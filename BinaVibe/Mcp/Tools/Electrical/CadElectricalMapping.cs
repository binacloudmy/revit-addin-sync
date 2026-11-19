#nullable enable
// CAD electrical mapping - phase 1 of the CAD electrical to BIM work (spec 2026-11-17 §5.3).
//
// The table the AI writes after reading the census: what each block class IS (role, Revit
// family type, mount height, host), what each layer is for, and which plan is which level.
// Every row carries its evidence. This file parses that table and checks it against the
// drawing and the model; it decides nothing itself.
//
// Errors reject the call and come back all at once. Incomplete rows (a placeable class with
// no family type, a wall device with no height) do not: they plan nothing and are reported
// as unmapped, so the drafter is asked one question instead of being refused.
//
// System and System.Text.Json only, so it is linked into Tests. It must also compile on net48.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal sealed class SymbolRow
    {
        /// <summary>The census block class key: a block name, or "anon:xxxxxxxx".</summary>
        public string Block = "";
        public string Role = "";
        public string Evidence = "";
        /// <summary>"Family : Type", resolved to the model's own spelling by Validate.</summary>
        public string? FamilyType;
        /// <summary>"wall" | "ceiling" | "none"; null only on a row that is not placeable.</summary>
        public string? Host;
        public double? MountHeightMm;
        /// <summary>True when the mapping left the host null and the role chose it.</summary>
        public bool HostDefaulted;
    }

    internal sealed class LayerRow
    {
        public string Layer = "";
        public string Role = "";
        /// <summary>Required on conduit and trunking rows; optional elsewhere.</summary>
        public string Evidence = "";
        /// <summary>A conduit row's "Family : Type", resolved to the model's spelling by Validate.</summary>
        public string? ConduitType;
        /// <summary>A trunking row's cable tray "Family : Type", resolved like the conduit type.</summary>
        public string? TrayType;
        public double? DiameterMm, WidthMm, HeightMm;
        /// <summary>The run height above the level for this layer; wins over the call's argument.</summary>
        public double? RunHeightMm;
    }

    /// <summary>A conduit or cable tray type of the model, as a mapping may name it.</summary>
    internal sealed class RunTypeInfo
    {
        /// <summary>"Family : Type", e.g. "Conduit with Fittings : PVC".</summary>
        public string Name = "";
        /// <summary>The type name alone.</summary>
        public string TypeName = "";
        /// <summary>A "with fittings" type: bends and elbows can be built.</summary>
        public bool WithFittings;
        /// <summary>Trade sizes (conduit) the type's standard offers, mm; empty when unknown.</summary>
        public List<double> SizesMm = new List<double>();

        /// <summary>The names matching a mapping's text: the full name first, else every type
        /// whose bare name matches (several: ambiguous).</summary>
        public static IReadOnlyList<string> Matches(IEnumerable<RunTypeInfo> types, string text)
        {
            string wanted = text.Trim();
            List<RunTypeInfo> all = types.ToList();
            List<string> full = all.Where(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name).Distinct(StringComparer.Ordinal).ToList();
            if (full.Count > 0) return full;
            return all.Where(t => string.Equals(t.TypeName, wanted, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
    }

    internal sealed class PlanRow
    {
        public int Cluster;
        public string? Level;
        public string Evidence = "";
    }

    internal sealed class MappingIssue
    {
        /// <summary>Where in the mapping, e.g. "symbols[2].role".</summary>
        public string Path = "";
        public string Code = "";
        public string Message = "";
        public List<string> Candidates = new List<string>();
    }

    /// <summary>What the mapping is checked against: the drawing (uncapped) and the model.</summary>
    internal sealed class MappingFacts
    {
        public ISet<string> BlockKeys = new HashSet<string>(StringComparer.Ordinal);
        public ISet<string> LayerNames = new HashSet<string>(StringComparer.Ordinal);
        public int PlanCount;
        public ISet<string> Levels = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>The model's "Family : Type" names matching a mapping's text: one is a
        /// match, none is not found, several are ambiguous.</summary>
        public Func<string, IReadOnlyList<string>> FamilyTypeMatches = _ => Array.Empty<string>();
        /// <summary>The model's conduit types matching a mapping's text (RunTypeInfo.Matches).</summary>
        public Func<string, IReadOnlyList<string>> ConduitTypeMatches = _ => Array.Empty<string>();
        /// <summary>The model's cable tray types matching a mapping's text.</summary>
        public Func<string, IReadOnlyList<string>> TrayTypeMatches = _ => Array.Empty<string>();
        /// <summary>A resolved conduit type's trade sizes, mm; empty when they cannot be read.</summary>
        public Func<string, IReadOnlyList<double>> ConduitSizesMm = _ => Array.Empty<double>();
    }

    internal sealed class CadElectricalMapping
    {
        public static readonly string[] Roles =
        {
            "socket", "switch", "isolator", "distribution_board", "light", "fan", "comms_outlet", "other",
            "ignore", "unknown",
        };

        public static readonly string[] Hosts = { "wall", "ceiling", "none" };

        public static readonly string[] LayerRoles = { "background", "conduit", "wiring_schematic", "ignore", "unknown" };

        /// <summary>Highest mount height accepted, mm. Above it is a typo, not a device.</summary>
        public const double MaxMountHeightMm = 4000;

        /// <summary>Largest conduit diameter accepted, mm.</summary>
        public const double MaxDiameterMm = 200;
        /// <summary>Run heights accepted, mm above the level: a run under the slab of the level
        /// below, up to one well above the ceiling.</summary>
        public const double MinRunHeightMm = -1000, MaxRunHeightMm = 10000;
        /// <summary>A diameter this close to one of the type's sizes is that size, mm.</summary>
        public const double SizeToleranceMm = 0.5;

        public List<SymbolRow> Symbols = new List<SymbolRow>();
        public List<LayerRow> Layers = new List<LayerRow>();
        public List<PlanRow> Plans = new List<PlanRow>();

        /// <summary>What the parse decided on the mapping's behalf, for the review: host_defaulted.</summary>
        public List<string> Notes = new List<string>();

        public static bool IsPlaceable(string role) =>
            Array.IndexOf(Roles, role) >= 0 && role != "ignore" && role != "unknown";

        /// <summary>The host a role takes when the mapping leaves it null.</summary>
        public static string DefaultHost(string role) => role == "light" || role == "fan" ? "ceiling" : "wall";

        /// <summary>Why a row plans no points, or null when it plans them (or is ignored):
        /// role_unknown, no_family_type, no_mount_height.</summary>
        public static string? UnplannedReason(SymbolRow row)
        {
            if (row.Role == "unknown") return "role_unknown";
            if (!IsPlaceable(row.Role)) return null;
            if (string.IsNullOrWhiteSpace(row.FamilyType)) return "no_family_type";
            if (row.Host == "wall" && !row.MountHeightMm.HasValue) return "no_mount_height";
            return null;
        }

        /// <summary>A layer whose linework is runs: conduit (and trunking, as cable tray).</summary>
        public static bool IsRunLayer(string role) => role == "conduit" || role == "trunking";

        /// <summary>The run height a run layer gets: its own row's, else the call's argument.</summary>
        public static double? EffectiveRunHeight(LayerRow row, double? argRunHeightMm) => row.RunHeightMm ?? argRunHeightMm;

        /// <summary>Why a run layer plans no runs, or null when it plans them (or is not a run
        /// layer): no_conduit_type, no_tray_type, no_diameter, no_tray_size, no_run_height.</summary>
        public static string? UnplannedReason(LayerRow row, double? argRunHeightMm)
        {
            if (row.Role == "conduit")
            {
                if (string.IsNullOrWhiteSpace(row.ConduitType)) return "no_conduit_type";
                if (!row.DiameterMm.HasValue) return "no_diameter";
            }
            else if (row.Role == "trunking")
            {
                if (string.IsNullOrWhiteSpace(row.TrayType)) return "no_tray_type";
                if (!row.WidthMm.HasValue || !row.HeightMm.HasValue) return "no_tray_size";
            }
            else
            {
                return null;
            }
            return EffectiveRunHeight(row, argRunHeightMm).HasValue ? null : "no_run_height";
        }

        /// <summary>The layers the mapping names as background, in its order.</summary>
        public List<string> BackgroundLayers() =>
            Layers.Where(row => row.Role == "background").Select(row => row.Layer).ToList();

        // ---------------------------------------------------------------- parse

        /// <summary>
        /// Reads the mapping. A JSON string holding an object is parsed first (a model often
        /// sends the table as text). Every problem found is added to <paramref name="issues"/>;
        /// the rows that parsed are kept either way.
        /// </summary>
        public static CadElectricalMapping Parse(JsonElement mapping, List<MappingIssue> issues)
        {
            var result = new CadElectricalMapping();

            if (mapping.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using (JsonDocument parsed = JsonDocument.Parse(mapping.GetString() ?? ""))
                    {
                        mapping = parsed.RootElement.Clone();
                    }
                }
                catch (JsonException)
                {
                    Add(issues, "", "not_an_object", "the mapping is text that is not JSON");
                    return result;
                }
            }

            if (mapping.ValueKind != JsonValueKind.Object)
            {
                Add(issues, "", "not_an_object", "the mapping must be an object with symbols, layers and plans");
                return result;
            }

            foreach ((JsonElement row, string path) in Rows(mapping, "symbols", issues))
            {
                result.Symbols.Add(ParseSymbol(row, path, issues, result.Notes));
            }

            foreach ((JsonElement row, string path) in Rows(mapping, "layers", issues))
            {
                var layer = new LayerRow
                {
                    Layer = Text(row, "layer") ?? "",
                    Role = Normal(Text(row, "role")),
                    Evidence = Text(row, "evidence") ?? "",
                };
                if (layer.Layer.Length == 0) Add(issues, path + ".layer", "layer_not_in_drawing", "the row names no layer");
                if (Array.IndexOf(LayerRoles, layer.Role) < 0)
                {
                    Add(issues, path + ".role", "unknown_role",
                        "'" + layer.Role + "' is not a layer role", LayerRoles);
                }
                if (IsRunLayer(layer.Role)) ParseRunLayer(row, path, layer, issues);
                result.Layers.Add(layer);
            }

            foreach ((JsonElement row, string path) in Rows(mapping, "plans", issues))
            {
                var plan = new PlanRow { Level = Text(row, "level"), Evidence = Text(row, "evidence") ?? "" };
                if (!row.TryGetProperty("cluster", out JsonElement cluster) ||
                    cluster.ValueKind != JsonValueKind.Number || !cluster.TryGetInt32(out plan.Cluster))
                {
                    plan.Cluster = -1;
                }
                if (plan.Evidence.Trim().Length == 0)
                {
                    Add(issues, path + ".evidence", "missing_evidence", "every plan row needs its evidence");
                }
                result.Plans.Add(plan);
            }

            return result;
        }

        private static SymbolRow ParseSymbol(JsonElement row, string path, List<MappingIssue> issues, List<string> notes)
        {
            var symbol = new SymbolRow
            {
                Block = Text(row, "block") ?? "",
                Role = Normal(Text(row, "role")),
                Evidence = Text(row, "evidence") ?? "",
                FamilyType = Blank(Text(row, "family_type")),
            };

            if (symbol.Block.Length == 0) Add(issues, path + ".block", "block_not_in_drawing", "the row names no block");

            if (Array.IndexOf(Roles, symbol.Role) < 0)
            {
                Add(issues, path + ".role", "unknown_role", "'" + symbol.Role + "' is not a symbol role", Roles);
            }

            if (symbol.Evidence.Trim().Length == 0)
            {
                Add(issues, path + ".evidence", "missing_evidence", "every symbol row needs its evidence");
            }

            string? host = Blank(Text(row, "host"));
            if (host != null)
            {
                symbol.Host = Normal(host);
                if (Array.IndexOf(Hosts, symbol.Host) < 0)
                {
                    Add(issues, path + ".host", "unknown_host", "'" + host + "' is not a host", Hosts);
                }
            }
            else if (IsPlaceable(symbol.Role))
            {
                symbol.Host = DefaultHost(symbol.Role);
                symbol.HostDefaulted = true;
                notes.Add("host_defaulted: " + symbol.Block + " -> " + symbol.Host);
            }

            if (row.TryGetProperty("mount_height_mm", out JsonElement height) && height.ValueKind != JsonValueKind.Null)
            {
                if (height.ValueKind != JsonValueKind.Number)
                {
                    Add(issues, path + ".mount_height_mm", "bad_mount_height", "the mount height must be a number of mm");
                }
                else
                {
                    double mm = height.GetDouble();
                    if (mm < 0 || mm > MaxMountHeightMm)
                    {
                        Add(issues, path + ".mount_height_mm", "bad_mount_height",
                            "the mount height must be 0 to " + MaxMountHeightMm.ToString(CultureInfo.InvariantCulture) + " mm");
                    }
                    else
                    {
                        symbol.MountHeightMm = mm;
                    }
                }
            }

            return symbol;
        }

        /// <summary>A conduit row's evidence, type, diameter and run height. A missing value is
        /// not an issue (the layer is then unplanned); a wrong one is.</summary>
        private static void ParseRunLayer(JsonElement row, string path, LayerRow layer, List<MappingIssue> issues)
        {
            if (layer.Evidence.Trim().Length == 0)
                Add(issues, path + ".evidence", "missing_evidence", "every " + layer.Role + " row needs its evidence");

            layer.RunHeightMm = Number(row, "run_height_mm", path, "bad_run_height",
                "the run height must be a number of mm from " + Mm(MinRunHeightMm) + " to " + Mm(MaxRunHeightMm),
                mm => mm >= MinRunHeightMm && mm <= MaxRunHeightMm, issues);

            if (layer.Role == "conduit")
            {
                layer.ConduitType = Blank(Text(row, "conduit_type"));
                layer.DiameterMm = Number(row, "diameter_mm", path, "bad_diameter",
                    "the diameter must be a number of mm above 0 and at most " + Mm(MaxDiameterMm),
                    mm => mm > 0 && mm <= MaxDiameterMm, issues);
            }
        }

        /// <summary>A number property: null when missing or null; an issue (and null) when it is
        /// not a number or fails <paramref name="valid"/>.</summary>
        private static double? Number(JsonElement row, string name, string path, string code, string message,
                                      Func<double, bool> valid, List<MappingIssue> issues)
        {
            if (!row.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.Number || !valid(value.GetDouble()))
            {
                Add(issues, path + "." + name, code, message);
                return null;
            }
            return value.GetDouble();
        }

        private static string Mm(double value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>The rows of one list, as (row, path). A missing list is empty; a list that
        /// is not one, or a row that is not an object, is an issue.</summary>
        private static List<(JsonElement Row, string Path)> Rows(JsonElement mapping, string name, List<MappingIssue> issues)
        {
            var rows = new List<(JsonElement Row, string Path)>();
            if (!mapping.TryGetProperty(name, out JsonElement list) || list.ValueKind == JsonValueKind.Null) return rows;

            if (list.ValueKind != JsonValueKind.Array)
            {
                Add(issues, name, "not_an_object", name + " must be a list of rows");
                return rows;
            }

            int i = 0;
            foreach (JsonElement row in list.EnumerateArray())
            {
                string path = name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (row.ValueKind == JsonValueKind.Object) rows.Add((row, path));
                else Add(issues, path, "not_an_object", "each row must be an object");
                i++;
            }
            return rows;
        }

        // ---------------------------------------------------------------- validate

        /// <summary>
        /// Checks the rows against the drawing and the model. A block, layer or level is
        /// matched exactly first, then by a unique case-insensitive match, whose spelling then
        /// replaces the mapping's. A family type takes the model's spelling too.
        /// </summary>
        public void Validate(MappingFacts facts, List<MappingIssue> issues)
        {
            for (int i = 0; i < Symbols.Count; i++)
            {
                SymbolRow row = Symbols[i];
                string path = "symbols[" + i.ToString(CultureInfo.InvariantCulture) + "]";

                if (row.Block.Length > 0)
                {
                    string? key = Resolve(row.Block, facts.BlockKeys, out List<string> near);
                    if (key == null) Add(issues, path + ".block", "block_not_in_drawing", "no block class '" + row.Block + "' in the drawing", near);
                    else row.Block = key;
                }

                if (row.FamilyType != null && IsPlaceable(row.Role))
                {
                    IReadOnlyList<string> matches = facts.FamilyTypeMatches(row.FamilyType);
                    if (matches.Count == 1) row.FamilyType = matches[0];
                    else if (matches.Count == 0)
                        Add(issues, path + ".family_type", "family_type_not_found", "no family type '" + row.FamilyType + "' in the model");
                    else
                        Add(issues, path + ".family_type", "family_type_ambiguous", "'" + row.FamilyType + "' matches several family types", matches);
                }
            }

            Duplicates(Symbols.Select(row => row.Block), "symbols", "block", "duplicate_block", issues);

            for (int i = 0; i < Layers.Count; i++)
            {
                LayerRow row = Layers[i];
                if (row.Layer.Length == 0) continue;
                string? name = Resolve(row.Layer, facts.LayerNames, out List<string> near);
                if (name == null)
                {
                    Add(issues, "layers[" + i.ToString(CultureInfo.InvariantCulture) + "].layer", "layer_not_in_drawing",
                        "no layer '" + row.Layer + "' in the drawing", near);
                }
                else
                {
                    row.Layer = name;
                }
            }

            for (int i = 0; i < Layers.Count; i++)
            {
                LayerRow row = Layers[i];
                string path = "layers[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (row.Role == "conduit" && row.ConduitType != null)
                {
                    row.ConduitType = ResolveType(row.ConduitType, facts.ConduitTypeMatches, path + ".conduit_type",
                                                  "conduit_type", "conduit type", issues);
                    if (row.ConduitType != null && row.DiameterMm.HasValue)
                    {
                        IReadOnlyList<double> sizes = facts.ConduitSizesMm(row.ConduitType);
                        double wanted = row.DiameterMm.Value;
                        if (sizes.Count > 0 && !sizes.Any(s => Math.Abs(s - wanted) <= SizeToleranceMm))
                        {
                            Add(issues, path + ".diameter_mm", "diameter_not_in_standard",
                                Mm(wanted) + " mm is not a size of " + row.ConduitType + "'s standard",
                                sizes.OrderBy(s => s).Select(s => Mm(Math.Round(s, 1))));
                        }
                    }
                }
            }

            Duplicates(Layers.Select(row => row.Layer), "layers", "layer", "duplicate_layer", issues);

            for (int i = 0; i < Plans.Count; i++)
            {
                PlanRow row = Plans[i];
                string path = "plans[" + i.ToString(CultureInfo.InvariantCulture) + "]";

                if (row.Cluster < 0 || row.Cluster >= facts.PlanCount)
                {
                    Add(issues, path + ".cluster", "cluster_out_of_range",
                        "the drawing has plans 0 to " + (facts.PlanCount - 1).ToString(CultureInfo.InvariantCulture));
                }

                if (row.Level != null)
                {
                    string? level = Resolve(row.Level, facts.Levels, out _);
                    if (level == null)
                    {
                        Add(issues, path + ".level", "level_not_found", "no level '" + row.Level + "' in the model",
                            facts.Levels.OrderBy(name => name, StringComparer.Ordinal).Take(20));
                    }
                    else
                    {
                        row.Level = level;
                    }
                }
            }
        }

        /// <summary>The exact name if there is one, else the only case-insensitive match, else
        /// null with the near names (several case-insensitive matches, or names containing it).</summary>
        private static string? Resolve(string wanted, ISet<string> names, out List<string> near)
        {
            near = new List<string>();
            if (names.Contains(wanted)) return wanted;

            List<string> folded = names.Where(name => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (folded.Count == 1) return folded[0];

            near = folded.Count > 1
                ? folded
                : names.Where(name => name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      wanted.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                       .ToList();
            near = near.OrderBy(name => name, StringComparer.Ordinal).Take(10).ToList();
            return null;
        }

        /// <summary>A conduit or tray type in the model's spelling; null, with an issue
        /// (code prefix_not_found or prefix_ambiguous), when it does not match exactly one.</summary>
        private static string? ResolveType(string text, Func<string, IReadOnlyList<string>> matches, string path,
                                           string codePrefix, string what, List<MappingIssue> issues)
        {
            IReadOnlyList<string> found = matches(text);
            if (found.Count == 1) return found[0];
            if (found.Count == 0)
                Add(issues, path, codePrefix + "_not_found", "no " + what + " '" + text + "' in the model");
            else
                Add(issues, path, codePrefix + "_ambiguous", "'" + text + "' matches several " + what + "s", found);
            return null;
        }

        private static void Duplicates(IEnumerable<string> values, string list, string field, string code, List<MappingIssue> issues)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int i = 0;
            foreach (string value in values)
            {
                if (value.Length > 0 && !seen.Add(value))
                {
                    Add(issues, list + "[" + i.ToString(CultureInfo.InvariantCulture) + "]." + field, code,
                        "'" + value + "' is mapped more than once");
                }
                i++;
            }
        }

        // ---------------------------------------------------------------- helpers

        private static void Add(List<MappingIssue> issues, string path, string code, string message,
                                IEnumerable<string>? candidates = null)
        {
            issues.Add(new MappingIssue
            {
                Path = path,
                Code = code,
                Message = message,
                Candidates = candidates == null ? new List<string>() : candidates.ToList(),
            });
        }

        /// <summary>A property as text: a string as is, null or missing as null, anything else
        /// as its JSON text.</summary>
        private static string? Text(JsonElement row, string name)
        {
            if (!row.TryGetProperty(name, out JsonElement value)) return null;
            switch (value.ValueKind)
            {
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Null:
                case JsonValueKind.Undefined: return null;
                default: return value.GetRawText();
            }
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        private static string Normal(string? value) => (value ?? "").Trim().ToLowerInvariant();
    }
}

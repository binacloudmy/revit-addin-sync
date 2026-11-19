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

        public List<SymbolRow> Symbols = new List<SymbolRow>();
        public List<LayerRow> Layers = new List<LayerRow>();
        public List<PlanRow> Plans = new List<PlanRow>();

        /// <summary>What the parse decided on the mapping's behalf, for the review:
        /// host_defaulted and conduit_deferred.</summary>
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
                };
                if (layer.Layer.Length == 0) Add(issues, path + ".layer", "layer_not_in_drawing", "the row names no layer");
                if (Array.IndexOf(LayerRoles, layer.Role) < 0)
                {
                    Add(issues, path + ".role", "unknown_role",
                        "'" + layer.Role + "' is not a layer role", LayerRoles);
                }
                if (layer.Role == "conduit") result.Notes.Add("conduit_deferred: layer " + layer.Layer + " (phase 2)");
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

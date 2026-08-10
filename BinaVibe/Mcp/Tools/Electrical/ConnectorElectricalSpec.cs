// Argument validation and vocabulary for get/set_connector_electrical_data —
// the Revit-FREE half, source-linked into Tests.csproj.
//
// NOTHING IN THIS FILE MAY NAME A REVIT TYPE, in any signature or any field.
// Tests.csproj takes the Revit API as a reference-only NuGet package, so a
// Revit type reachable during xUnit discovery makes the runner print
// "Skipping: Tests (could not find dependent assembly 'RevitAPI')" and run
// ZERO tests while exiting green. That is why the system-type map returns the
// Revit enum MEMBER NAME as a plain string and ConnectorElectricalTools does
// the Enum.TryParse — the mapping is the part worth testing, the parse is not.
//
// UNITS: voltage in volts, apparent load in volt-amperes, both as the drafter
// speaks them. Conversion to Revit internal units happens on the Revit side.
using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>A validated set_connector_electrical_data request. Every field
    /// is optional on its own; <see cref="Errors"/> non-empty means the caller
    /// must be refused before any family document is opened.</summary>
    internal sealed class ConnectorElectricalSpec
    {
        public double? VoltageV { get; set; }
        public int? Poles { get; set; }
        /// <summary>MEPSystemClassification member name, or null to leave the
        /// connector's system classification alone.</summary>
        public string? SystemClassificationName { get; set; }
        public string? LoadClassification { get; set; }
        public double? ApparentLoadVa { get; set; }
        /// <summary>Family parameter to bind the connector's Voltage to, or
        /// null for a direct write.</summary>
        public string? AssociateToParameter { get; set; }
        /// <summary>Zero-based index into the family's ELECTRICAL connectors,
        /// or null for every one of them.</summary>
        public int? ConnectorIndex { get; set; }
        public bool DryRun { get; set; }

        public List<string> Errors { get; } = new List<string>();

        public bool IsValid => Errors.Count == 0;

        /// <summary>True when at least one connector field was supplied. A
        /// request that names a family and nothing else is a no-op, and a
        /// silent no-op reported as ok:true is how an agent concludes it fixed
        /// something it never touched.</summary>
        public bool HasAnyWrite =>
            VoltageV.HasValue || Poles.HasValue || SystemClassificationName != null
            || LoadClassification != null || ApparentLoadVa.HasValue;
    }

    internal static class ConnectorElectricalData
    {
        // ─── system classification vocabulary ───────────────────────────
        //
        // Keys are what the agent says; values are MEPSystemClassification
        // member names, verified present in the 2023, 2025 and 2027 reference
        // assemblies. Note "Data" is DataCircuit in MEPSystemClassification but
        // plain Data in ElectricalSystemType — the two enums do NOT share
        // spellings, and this map is the classification one.
        private static readonly Dictionary<string, string> SystemClassMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["power_unbalanced"] = "PowerUnBalanced",
                ["power_unbal"]      = "PowerUnBalanced",
                ["single_phase"]     = "PowerUnBalanced",
                ["1ph"]              = "PowerUnBalanced",
                ["satu_fasa"]        = "PowerUnBalanced",

                ["power_balanced"]   = "PowerBalanced",
                ["power_bal"]        = "PowerBalanced",
                ["three_phase"]      = "PowerBalanced",
                ["3ph"]              = "PowerBalanced",
                ["tiga_fasa"]        = "PowerBalanced",

                ["power"]            = "PowerCircuit",
                ["power_circuit"]    = "PowerCircuit",
                ["kuasa"]            = "PowerCircuit",

                ["data"]             = "DataCircuit",
                ["data_circuit"]     = "DataCircuit",
                ["telephone"]        = "Telephone",
                ["security"]         = "Security",
                ["fire_alarm"]       = "FireAlarm",
                ["nurse_call"]       = "NurseCall",
                ["controls"]         = "Controls",
                ["communication"]    = "Communication",
            };

        /// <summary>Every accepted ``system_type`` word, for error messages and
        /// the tool docstring. Sorted so the message is stable.</summary>
        public static IReadOnlyList<string> AcceptedSystemTypes
        {
            get
            {
                var words = new List<string>(SystemClassMap.Keys);
                words.Sort(StringComparer.Ordinal);
                return words;
            }
        }

        /// <summary>Agent word to MEPSystemClassification member name, or null
        /// when the word is not one we accept. Null input is null output — an
        /// omitted system_type leaves the connector's classification alone.</summary>
        public static string? SystemClassificationName(string? word)
        {
            if (string.IsNullOrWhiteSpace(word)) return null;
            return SystemClassMap.TryGetValue(word!.Trim(), out var name) ? name : null;
        }

        /// <summary>True for the classifications that carry a voltage. Setting
        /// a voltage on a Data or FireAlarm connector is almost always a
        /// mis-typed system_type rather than an intent.</summary>
        public static bool IsPowerClassification(string? memberName) =>
            memberName == "PowerCircuit" || memberName == "PowerBalanced"
            || memberName == "PowerUnBalanced";

        // ─── family parameter names ─────────────────────────────────────

        // Revit rejects these outright in a parameter name. Checked here so the
        // refusal names the character instead of surfacing Revit's own
        // "The name is invalid" after the family document is already open.
        private static readonly char[] ForbiddenNameChars =
            { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>Null when the name is usable, else why it is not.</summary>
        public static string? ValidateParameterName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "associate_to_parameter is empty — omit it for a direct connector write";
            foreach (var c in ForbiddenNameChars)
                if (name!.IndexOf(c) >= 0)
                    return $"parameter name '{name}' contains '{c}', which Revit forbids in a parameter name";
            if (name!.Trim().Length != name.Length)
                return $"parameter name '{name}' has leading or trailing whitespace — Revit stores it "
                     + "verbatim and the parameter becomes impossible to match by name later";
            return null;
        }

        // ─── request assembly ───────────────────────────────────────────

        /// <summary>Validate raw argument values into a spec. Takes plain
        /// values, not JsonElement, so the rules are testable without the
        /// addin's arg helpers (which live in a Revit-heavy file).</summary>
        public static ConnectorElectricalSpec Build(
            double? voltageV, long? poles, string? systemType, string? loadClassification,
            double? apparentLoadVa, string? associateToParameter, long? connectorIndex, bool dryRun)
        {
            var spec = new ConnectorElectricalSpec { DryRun = dryRun };

            if (voltageV.HasValue)
            {
                var v = voltageV.Value;
                if (double.IsNaN(v) || double.IsInfinity(v))
                    spec.Errors.Add("voltage_v is not a finite number");
                else if (v <= 0)
                    // The whole reason this tool exists is a family sitting at
                    // 0 V. Writing another 0 would look like a successful fix.
                    spec.Errors.Add(
                        $"voltage_v must be greater than 0 (got {v}). A 0 V connector is exactly the "
                        + "state that makes Revit refuse every panel; Malaysian LV is 240 V "
                        + "line-to-ground or 415 V line-to-line.");
                else if (v > 1_000_000)
                    spec.Errors.Add($"voltage_v {v} is beyond any plausible building supply");
                else spec.VoltageV = v;
            }

            if (poles.HasValue)
            {
                var p = poles.Value;
                if (p != 1 && p != 2 && p != 3)
                    spec.Errors.Add($"poles must be 1, 2 or 3 (got {p})");
                else spec.Poles = (int)p;
            }

            if (!string.IsNullOrWhiteSpace(systemType))
            {
                var name = SystemClassificationName(systemType);
                if (name == null)
                    spec.Errors.Add(
                        $"system_type '{systemType}' is not recognised. Accepted: "
                        + string.Join(", ", AcceptedSystemTypes));
                else spec.SystemClassificationName = name;
            }

            if (apparentLoadVa.HasValue)
            {
                var va = apparentLoadVa.Value;
                if (double.IsNaN(va) || double.IsInfinity(va))
                    spec.Errors.Add("apparent_load_va is not a finite number");
                else if (va < 0)
                    spec.Errors.Add($"apparent_load_va must be 0 or greater (got {va})");
                else spec.ApparentLoadVa = va;
            }

            if (!string.IsNullOrWhiteSpace(loadClassification))
                spec.LoadClassification = loadClassification!.Trim();

            if (associateToParameter != null)
            {
                var problem = ValidateParameterName(associateToParameter);
                if (problem != null) spec.Errors.Add(problem);
                else spec.AssociateToParameter = associateToParameter;
            }

            if (connectorIndex.HasValue)
            {
                if (connectorIndex.Value < 0)
                    spec.Errors.Add($"connector_index must be 0 or greater (got {connectorIndex.Value})");
                else if (connectorIndex.Value > int.MaxValue)
                    spec.Errors.Add("connector_index is absurdly large");
                else spec.ConnectorIndex = (int)connectorIndex.Value;
            }

            // Association only ever binds the VOLTAGE parameter, so asking for
            // one without a voltage produces a bound-but-unset parameter and
            // the connector stays at whatever it was.
            if (spec.AssociateToParameter != null && !spec.VoltageV.HasValue)
                spec.Errors.Add(
                    "associate_to_parameter needs voltage_v — the association binds the connector's "
                    + "Voltage to that family parameter, and without a value the parameter is created "
                    + "empty and the connector keeps its old voltage.");

            if (spec.IsValid && !spec.HasAnyWrite)
                spec.Errors.Add(
                    "nothing to set — pass at least one of voltage_v, poles, system_type, "
                    + "load_classification or apparent_load_va.");

            // A voltage on a non-power connector is a mis-typed system_type far
            // more often than an intent, and Revit accepts it silently.
            if (spec.IsValid && spec.VoltageV.HasValue && spec.SystemClassificationName != null
                && !IsPowerClassification(spec.SystemClassificationName))
                spec.Errors.Add(
                    $"voltage_v was given with system_type '{systemType}', which maps to "
                    + $"{spec.SystemClassificationName} — not a power classification. Drop voltage_v, "
                    + "or use power_unbalanced (1-phase) / power_balanced (3-phase).");

            return spec;
        }

        // ─── read-side classification ───────────────────────────────────

        /// <summary>Where a voltage reading came from. ``Absent`` is the case
        /// the 0 V guard in CircuitDriver cannot see: it treats an unreadable
        /// voltage as "unknown, let it pass", so a family with no voltage
        /// parameter at all circuits happily and then fails panel assignment
        /// with an error that reads like a panel mismatch.</summary>
        public const string SourceInstance = "instance";
        public const string SourceSymbol = "symbol";
        public const string SourceAbsent = "absent";

        /// <summary>One line of plain guidance for a voltage reading, or null
        /// when the reading is fine. Returned by get_connector_electrical_data
        /// per connector so the agent does not have to infer the consequence.</summary>
        public static string? VoltageAdvice(string source, double? voltageV)
        {
            if (source == SourceAbsent)
                return "no voltage parameter on the instance or its type — Revit cannot build a circuit "
                     + "voltage from this connector. create_circuit will still succeed (its 0 V guard "
                     + "treats an unreadable value as unknown) and the panel assignment will fail "
                     + "afterwards. Fix with set_connector_electrical_data.";
            if (voltageV.HasValue && Math.Abs(voltageV.Value) < 1e-9)
                return "reports 0 V — no panel will accept a circuit built from this connector. "
                     + "Fix with set_connector_electrical_data.";
            return null;
        }
    }
}

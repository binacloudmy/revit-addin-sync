#nullable enable
// check rule sets and results (spec §21, plan §2.5; owned by W0, contracts).
// Revit-free mirror of contracts/copilot/ruleset.schema.json. The cloud
// rewrites check(rule_set: "<id>") into args.rule_set_content (a RuleSet);
// W6's CheckTool evaluates it and returns a CheckResult as the envelope data.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BinaVibe.Copilot.Contracts
{
    public static class AssertionKinds
    {
        // Generic kinds (Revit-free evaluation).
        public const string NotEmpty = "not_empty";
        public const string Matches = "matches";
        public const string InList = "in_list";
        public const string Between = "between";
        public const string EqualsParam = "equals_param";
        public const string Exists = "exists";
        public const string CountLe = "count_le";
        // Geometric / model kinds (implemented against the Revit API).
        public const string GridAlignedWithLink = "grid_aligned_with_link";
        public const string LevelMatchesLink = "level_matches_link";
        public const string ClearWidthGe = "clear_width_ge";
        public const string TurningCircleGe = "turning_circle_ge";
        public const string RoomEnclosed = "room_enclosed";
        public const string RoomPlaced = "room_placed";
        public const string DimensionOverridden = "dimension_overridden";
        public const string TaggedInView = "tagged_in_view";
        public const string DuplicateInstances = "duplicate_instances";
        public const string ImportedCadPresent = "imported_cad_present";
        public const string InPlaceFamily = "in_place_family";
        public const string WarningsGroup = "warnings_group";

        public static IReadOnlyList<string> All { get; } = new[]
        {
            NotEmpty, Matches, InList, Between, EqualsParam, Exists, CountLe,
            GridAlignedWithLink, LevelMatchesLink, ClearWidthGe, TurningCircleGe, RoomEnclosed, RoomPlaced,
            DimensionOverridden, TaggedInView, DuplicateInstances, ImportedCadPresent, InPlaceFamily, WarningsGroup,
        };
    }

    public static class RuleSeverity
    {
        public const string Error = "error";
        public const string Warning = "warning";
        public const string Info = "info";

        public static IReadOnlyList<string> All { get; } = new[] { Error, Warning, Info };
    }

    public static class SelectorLinks
    {
        public const string Host = "host";
        public const string All = "all";
    }

    public static class CheckLimits
    {
        /// <summary>Findings listed per rule (RuleResult.Top).</summary>
        public const int MaxTopFindings = 5;
    }

    public sealed class RuleSet
    {
        /// <summary>kebab-case, e.g. "model-health".</summary>
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("title_en")] public string TitleEn { get; set; } = "";
        [JsonPropertyName("title_ms")] public string TitleMs { get; set; } = "";
        [JsonPropertyName("source")] public RuleSource Source { get; set; } = new();
        /// <summary>At least one; ids unique within the set.</summary>
        [JsonPropertyName("rules")] public List<Rule> Rules { get; set; } = new();
    }

    public sealed class RuleSource
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("edition")] public string Edition { get; set; } = "";
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    public sealed class Rule
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title_en")] public string TitleEn { get; set; } = "";
        [JsonPropertyName("title_ms")] public string TitleMs { get; set; } = "";
        /// <summary>The clause this rule enforces.</summary>
        [JsonPropertyName("cite")] public string Cite { get; set; } = "";
        /// <summary>One of <see cref="RuleSeverity"/>.</summary>
        [JsonPropertyName("severity")] public string Severity { get; set; } = "";
        [JsonPropertyName("selector")] public RuleSelector Selector { get; set; } = new();
        [JsonPropertyName("assertion")] public RuleAssertion Assertion { get; set; } = new();
        /// <summary>The threshold is not confirmed from a primary source: report every outcome not_verified.</summary>
        [JsonPropertyName("unverified_value")] public bool? UnverifiedValue { get; set; }
        [JsonPropertyName("fix_hint_en")] public string FixHintEn { get; set; } = "";
        [JsonPropertyName("fix_hint_ms")] public string FixHintMs { get; set; } = "";
    }

    public sealed class RuleSelector
    {
        /// <summary>Revit category names; empty for document-wide kinds.</summary>
        [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
        [JsonPropertyName("levels")] public List<string>? Levels { get; set; }
        [JsonPropertyName("type_name_regex")] public string? TypeNameRegex { get; set; }
        [JsonPropertyName("has_param")] public string? HasParam { get; set; }
        /// <summary><see cref="SelectorLinks"/>: host | all. An unloaded link makes its outcomes not_verified.</summary>
        [JsonPropertyName("link")] public string? Link { get; set; }
    }

    /// <summary>Kind-specific requirements (enforced by the schema): not_empty needs
    /// Param; matches needs Regex; in_list needs Values; between needs Min or Max;
    /// equals_param needs Param and OtherParam; count_le needs Max;
    /// clear_width_ge and turning_circle_ge need Min.</summary>
    public sealed class RuleAssertion
    {
        /// <summary>One of <see cref="AssertionKinds"/>.</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("param")] public string? Param { get; set; }
        [JsonPropertyName("regex")] public string? Regex { get; set; }
        [JsonPropertyName("values")] public List<string>? Values { get; set; }
        [JsonPropertyName("min")] public double? Min { get; set; }
        [JsonPropertyName("max")] public double? Max { get; set; }
        [JsonPropertyName("unit")] public string? Unit { get; set; }
        [JsonPropertyName("other_param")] public string? OtherParam { get; set; }
        /// <summary>Link name to compare against.</summary>
        [JsonPropertyName("link")] public string? Link { get; set; }
        [JsonPropertyName("tolerance_mm")] public double? ToleranceMm { get; set; }
        [JsonPropertyName("tolerance_deg")] public double? ToleranceDeg { get; set; }
    }

    // ------------------------------------------------------------------
    // check result: the data of a check envelope
    // ------------------------------------------------------------------

    /// <summary>Counts are per (element, rule) outcome; Totals.Pass/Fail/NotVerified
    /// are the sums over Rules, and Totals.Checked = Pass + Fail + NotVerified.</summary>
    public sealed class CheckResult
    {
        [JsonPropertyName("rule_set_id")] public string RuleSetId { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("totals")] public CheckTotals Totals { get; set; } = new();
        [JsonPropertyName("rules")] public List<RuleResult> Rules { get; set; } = new();
    }

    public sealed class CheckTotals
    {
        [JsonPropertyName("checked")] public int Checked { get; set; }
        [JsonPropertyName("pass")] public int Pass { get; set; }
        [JsonPropertyName("fail")] public int Fail { get; set; }
        [JsonPropertyName("not_verified")] public int NotVerified { get; set; }
    }

    public sealed class RuleResult
    {
        [JsonPropertyName("rule_id")] public string RuleId { get; set; } = "";
        [JsonPropertyName("cite")] public string Cite { get; set; } = "";
        [JsonPropertyName("severity")] public string Severity { get; set; } = "";
        [JsonPropertyName("pass")] public int Pass { get; set; }
        [JsonPropertyName("fail")] public int Fail { get; set; }
        [JsonPropertyName("not_verified")] public int NotVerified { get; set; }
        /// <summary>At most <see cref="CheckLimits.MaxTopFindings"/>.</summary>
        [JsonPropertyName("top")] public List<RuleFinding> Top { get; set; } = new();
        /// <summary>Handle to every failing element.</summary>
        [JsonPropertyName("fail_handle")] public string? FailHandle { get; set; }
        [JsonPropertyName("not_verified_reason")] public string? NotVerifiedReason { get; set; }
    }

    public sealed class RuleFinding
    {
        [JsonPropertyName("ref")] public ElementRef Ref { get; set; } = new ElementRef(0, null);
        /// <summary>The value found, in display units.</summary>
        [JsonPropertyName("value")] public string? Value { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    }
}

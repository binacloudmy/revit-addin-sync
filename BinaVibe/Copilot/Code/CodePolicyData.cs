#nullable enable
// The run_revit_code policy as data (spec §7.8). Revit-free.
//
// Resources/copilot/code-policy.json is the source of truth. CodePolicyTable.cs
// holds the same data as C#, so the policy is compiled into the signed add-in
// and nothing at runtime (and nothing from the cloud) can change it.
// CodePolicyTests.Table_MatchesJson fails whenever the two differ.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BinaVibe.Copilot.Code
{
    public sealed class CodePolicyData
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("language_version")] public string LanguageVersion { get; set; } = "";
        [JsonPropertyName("budget")] public CodeBudgetLimits Budget { get; set; } = new CodeBudgetLimits();
        [JsonPropertyName("assemblies")] public CodePolicyAssemblies Assemblies { get; set; } = new CodePolicyAssemblies();

        /// <summary>BCL assemblies (simple names) the snippet is compiled against.</summary>
        [JsonPropertyName("references")] public List<string> References { get; set; } = new List<string>();

        /// <summary>The only System types the code may use (namespace + metadata name, e.g. System.Collections.Generic.List`1).
        /// A nested type is allowed when its outermost type is.</summary>
        [JsonPropertyName("system_types")] public List<string> SystemTypes { get; set; } = new List<string>();

        [JsonPropertyName("system_member_allow_only")] public List<CodeMemberAllowOnly> SystemMemberAllowOnly { get; set; } = new List<CodeMemberAllowOnly>();

        /// <summary>The only RevitAPIUI types the code may use.</summary>
        [JsonPropertyName("revit_ui_types")] public List<string> RevitUiTypes { get; set; } = new List<string>();

        [JsonPropertyName("categories")] public List<CodePolicyCategory> Categories { get; set; } = new List<CodePolicyCategory>();

        /// <summary>Checked in order; the first match names the category.</summary>
        [JsonPropertyName("rules")] public List<CodePolicyRule> Rules { get; set; } = new List<CodePolicyRule>();

        [JsonPropertyName("mode")] public CodeModePolicy Mode { get; set; } = new CodeModePolicy();
    }

    public sealed class CodeBudgetLimits
    {
        [JsonPropertyName("max_ticks")] public long MaxTicks { get; set; }
        [JsonPropertyName("max_seconds")] public int MaxSeconds { get; set; }
        [JsonPropertyName("max_depth")] public int MaxDepth { get; set; }
        [JsonPropertyName("max_output_bytes")] public int MaxOutputBytes { get; set; }
    }

    public sealed class CodePolicyAssemblies
    {
        [JsonPropertyName("revit")] public List<string> Revit { get; set; } = new List<string>();
        [JsonPropertyName("revit_ui")] public List<string> RevitUi { get; set; } = new List<string>();
        /// <summary>The JSON "system" list: BCL assemblies a System type may come from.</summary>
        [JsonPropertyName("system")] public List<string> Bcl { get; set; } = new List<string>();
    }

    public sealed class CodeMemberAllowOnly
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("members")] public List<string> Members { get; set; } = new List<string>();
    }

    public sealed class CodePolicyCategory
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        /// <summary>One line the model reads in a rejection.</summary>
        [JsonPropertyName("why")] public string Why { get; set; } = "";
    }

    public sealed class CodePolicyRule
    {
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        /// <summary>One of <see cref="CodeRuleKinds"/>.</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("match")] public List<string> Match { get; set; } = new List<string>();
    }

    public sealed class CodeModePolicy
    {
        [JsonPropertyName("write_namespaces")] public List<string> WriteNamespaces { get; set; } = new List<string>();
        /// <summary>"Namespace.Type.Member".</summary>
        [JsonPropertyName("write_members")] public List<string> WriteMembers { get; set; } = new List<string>();
        /// <summary>A method whose name starts with one of these (at a camel-case boundary) changes the document.</summary>
        [JsonPropertyName("write_verbs")] public List<string> WriteVerbs { get; set; } = new List<string>();
        /// <summary>Value objects that live outside the document: nothing done to them is a write.</summary>
        [JsonPropertyName("detached_types")] public List<string> DetachedTypes { get; set; } = new List<string>();
        /// <summary>Type-name suffixes of value objects; never applied to Element subclasses.</summary>
        [JsonPropertyName("detached_suffixes")] public List<string> DetachedSuffixes { get; set; } = new List<string>();

        /// <summary>"Namespace.Type.Member" UI requests (open a view, zoom, select) the host queues and applies
        /// after the run's rollback (ruling R59); the code calls them through host shims.</summary>
        [JsonPropertyName("ui_shim_members")] public List<string> UiShimMembers { get; set; } = new List<string>();
    }

    public static class CodeRuleKinds
    {
        public const string Syntax = "syntax";
        public const string Operation = "operation";
        public const string SystemNamespace = "system_namespace";
        public const string SystemType = "system_type";
        public const string SystemMember = "system_member";
        public const string SystemDefault = "system_default";
        public const string RevitType = "revit_type";
        public const string RevitNamespace = "revit_namespace";
        public const string RevitMember = "revit_member";
        public const string RevitSetter = "revit_setter";
        public const string RevitMemberKey = "revit_member_key";
        public const string RevitTypeSuffix = "revit_type_suffix";
        public const string PathMember = "path_member";
        public const string RevitUiDefault = "revit_ui_default";
        public const string AssemblyDefault = "assembly_default";

        public static IReadOnlyList<string> All { get; } = new[]
        {
            Syntax, Operation, SystemNamespace, SystemType, SystemMember, SystemDefault, RevitType, RevitNamespace,
            RevitMember, RevitSetter, RevitMemberKey, RevitTypeSuffix, PathMember, RevitUiDefault, AssemblyDefault,
        };
    }
}

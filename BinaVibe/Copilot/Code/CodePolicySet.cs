#nullable enable
// CodePolicyData compiled into lookups (sets, regexes) for CodePolicy and
// ModeClassifier. Revit-free. Default is built from CodePolicyTable, the
// compiled-in copy of Resources/copilot/code-policy.json.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;

namespace BinaVibe.Copilot.Code
{
    public sealed class CodePolicySet
    {
        private static readonly Lazy<CodePolicySet> DefaultSet = new Lazy<CodePolicySet>(() => new CodePolicySet(CodePolicyTable.Data));

        /// <summary>The policy compiled into this add-in build.</summary>
        public static CodePolicySet Default => DefaultSet.Value;

        private readonly Dictionary<string, string> _why;
        private readonly Dictionary<(string Kind, string Construct), string> _constructCategory;
        private readonly Dictionary<string, HashSet<string>> _allowOnly;

        public CodePolicySet(CodePolicyData data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            LanguageVersion = (LanguageVersion)Enum.Parse(typeof(LanguageVersion), data.LanguageVersion);

            RevitAssemblies = Set(data.Assemblies.Revit);
            RevitUiAssemblies = Set(data.Assemblies.RevitUi);
            BclAssemblies = Set(data.Assemblies.Bcl);
            SystemTypes = Set(data.SystemTypes);
            RevitUiTypes = Set(data.RevitUiTypes);

            _why = data.Categories.ToDictionary(c => c.Id, c => c.Why, StringComparer.Ordinal);
            _allowOnly = data.SystemMemberAllowOnly.ToDictionary(a => a.Type, a => Set(a.Members), StringComparer.Ordinal);

            _constructCategory = new Dictionary<(string, string), string>();
            var rules = new List<CompiledRule>();
            foreach (var rule in data.Rules)
            {
                if (!CodeRuleKinds.All.Contains(rule.Kind))
                    throw new ArgumentException("code policy rule kind '" + rule.Kind + "' is unknown");
                if (!_why.ContainsKey(rule.Category))
                    throw new ArgumentException("code policy rule category '" + rule.Category + "' has no categories entry");
                if (rule.Kind == CodeRuleKinds.Syntax || rule.Kind == CodeRuleKinds.Operation)
                    foreach (var m in rule.Match)
                        if (!_constructCategory.ContainsKey((rule.Kind, m))) _constructCategory[(rule.Kind, m)] = rule.Category;
                var regexes = rule.Kind == CodeRuleKinds.RevitMember || rule.Kind == CodeRuleKinds.RevitSetter
                    ? rule.Match.Select(m => new Regex(m, RegexOptions.CultureInvariant)).ToArray()
                    : Array.Empty<Regex>();
                rules.Add(new CompiledRule(rule.Category, rule.Kind, Set(rule.Match), rule.Match.ToArray(), regexes));
            }
            Rules = rules;

            WriteNamespaces = data.Mode.WriteNamespaces.ToArray();
            WriteMembers = Set(data.Mode.WriteMembers);
            WriteVerbs = data.Mode.WriteVerbs.ToArray();
            DetachedTypes = Set(data.Mode.DetachedTypes);
            DetachedSuffixes = data.Mode.DetachedSuffixes.ToArray();
        }

        public CodePolicyData Data { get; }
        public LanguageVersion LanguageVersion { get; }
        public CodeBudgetLimits Budget => Data.Budget;

        public HashSet<string> RevitAssemblies { get; }
        public HashSet<string> RevitUiAssemblies { get; }
        public HashSet<string> BclAssemblies { get; }
        public HashSet<string> SystemTypes { get; }
        public HashSet<string> RevitUiTypes { get; }
        public IReadOnlyList<CompiledRule> Rules { get; }

        public IReadOnlyList<string> WriteNamespaces { get; }
        public HashSet<string> WriteMembers { get; }
        public IReadOnlyList<string> WriteVerbs { get; }
        public HashSet<string> DetachedTypes { get; }
        public IReadOnlyList<string> DetachedSuffixes { get; }

        public IEnumerable<string> CategoryIds => Data.Categories.Select(c => c.Id);

        /// <summary>The category of a structural finding. Throws when the policy has no rule for it:
        /// the engine must never report something the policy file does not name.</summary>
        public string CategoryFor(string kind, string construct) =>
            _constructCategory.TryGetValue((kind, construct), out var category)
                ? category
                : throw new InvalidOperationException("code policy has no " + kind + " rule for '" + construct + "'");

        public bool HasConstruct(string kind, string construct) => _constructCategory.ContainsKey((kind, construct));

        public string Why(string category) => _why.TryGetValue(category, out var why) ? why : category;

        /// <summary>For a System type with an allow-only member list: whether this member is on it. True otherwise.</summary>
        public bool SystemMemberAllowed(string typeKey, string memberName) =>
            !_allowOnly.TryGetValue(typeKey, out var members) || members.Contains(memberName);

        private static HashSet<string> Set(IEnumerable<string> items) => new HashSet<string>(items, StringComparer.Ordinal);
    }

    public sealed class CompiledRule
    {
        public CompiledRule(string category, string kind, HashSet<string> matchSet, string[] match, Regex[] regexes)
        {
            Category = category;
            Kind = kind;
            MatchSet = matchSet;
            Match = match;
            Regexes = regexes;
        }

        public string Category { get; }
        public string Kind { get; }
        public HashSet<string> MatchSet { get; }
        public string[] Match { get; }
        public Regex[] Regexes { get; }

        public bool AnyRegex(string name)
        {
            foreach (var r in Regexes)
                if (r.IsMatch(name)) return true;
            return false;
        }
    }
}

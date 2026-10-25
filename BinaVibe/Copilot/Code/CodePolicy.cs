#nullable enable
// Deny-by-default code policy for run_revit_code (spec §7.8). Revit-free.
//
// Check() walks the bound tree of Run's body (SymbolUseCollector) and resolves
// every symbol and the assembly it comes from:
//   - a type is allowed when it is a Revit API type no rule denies, a
//     RevitAPIUI type on revit_ui_types, or a System type on system_types,
//     from an allowed assembly; everything else is denied (system_default,
//     revit_ui_default, assembly_default);
//   - a member is allowed when its type is, no member rule denies it
//     (revit_member, revit_setter, path_parameter, system_member), and every
//     type in its signature is allowed (System.Type only as a parameter of a
//     Revit member, for OfClass(typeof(Wall)) and friends);
//   - the host gives only doc, uidoc, view and Print: any other symbol of the
//     wrapper is host_internals.
// The first matching rule names the category; the I/O denials hold in every mode.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BinaVibe.Copilot.Code
{
    /// <summary>One reason the code may not run. Line and Column are 1-based, in the model's own text.</summary>
    public sealed class PolicyViolation
    {
        public PolicyViolation(string category, string construct, int line, int column, string symbol, string why)
        {
            Category = category;
            Construct = construct;
            Line = line;
            Column = column;
            Symbol = symbol;
            Why = why;
        }

        public string Category { get; }
        /// <summary>The rule kind or structural construct that matched.</summary>
        public string Construct { get; }
        public int Line { get; }
        public int Column { get; }
        public string Symbol { get; }
        public string Why { get; }

        public override string ToString() => "line " + Line + ": " + Symbol + " (" + Category + ": " + Why + ")";
    }

    public sealed class PolicyResult
    {
        public PolicyResult(IReadOnlyList<PolicyViolation> violations, IReadOnlyCollection<string> referenced,
            IReadOnlyCollection<string>? shimmed = null)
        {
            Violations = violations;
            Referenced = referenced;
            Shimmed = shimmed ?? Array.Empty<string>();
        }

        public IReadOnlyList<PolicyViolation> Violations { get; }
        public bool Allowed => Violations.Count == 0;

        /// <summary>Identities of every non-host symbol the code uses (to prove a rewrite changed nothing).</summary>
        public IReadOnlyCollection<string> Referenced { get; }

        /// <summary>The UI requests among them, which the rewrite routes to host shims (ruling R59).</summary>
        public IReadOnlyCollection<string> Shimmed { get; }
    }

    internal enum SymbolOrigin { Revit, RevitUi, Bcl, Source, Other }

    public static class CodePolicy
    {
        /// <summary>Every structural construct the engine can report; each needs a syntax or operation rule.</summary>
        public static IReadOnlyList<(string Kind, string Construct)> EngineConstructs { get; } = new[]
        {
            (CodeRuleKinds.Syntax, "wrapper_escape"), (CodeRuleKinds.Syntax, "member_or_type_declaration"),
            (CodeRuleKinds.Syntax, "unsafe"), (CodeRuleKinds.Syntax, "pointer"), (CodeRuleKinds.Syntax, "stackalloc"),
            (CodeRuleKinds.Syntax, "fixed"), (CodeRuleKinds.Syntax, "function_pointer"), (CodeRuleKinds.Syntax, "undocumented_keyword"),
            (CodeRuleKinds.Syntax, "async"), (CodeRuleKinds.Syntax, "await"), (CodeRuleKinds.Syntax, "lock"),
            (CodeRuleKinds.Syntax, "goto"), (CodeRuleKinds.Syntax, "label"), (CodeRuleKinds.Syntax, "yield"),
            (CodeRuleKinds.Syntax, "attribute"), (CodeRuleKinds.Syntax, "extern"), (CodeRuleKinds.Syntax, "directive"),
            (CodeRuleKinds.Syntax, "reserved_identifier"), (CodeRuleKinds.Syntax, "query_expression"),
            (CodeRuleKinds.Operation, "method_group"), (CodeRuleKinds.Operation, "conditional_linq"), (CodeRuleKinds.Operation, "conditional_ui"),
            (CodeRuleKinds.Operation, "exception_to_text"), (CodeRuleKinds.Operation, "exception_container"),
            (CodeRuleKinds.Operation, "host_member"), (CodeRuleKinds.Operation, "this_reference"),
            (CodeRuleKinds.Operation, "dispose_host_object"), (CodeRuleKinds.Operation, "dynamic"),
            (CodeRuleKinds.Operation, "event_reference"), (CodeRuleKinds.Operation, "unsupported_operation"),
            (CodeRuleKinds.Operation, "typeof_outside_revit_argument"),
        };

        public static PolicyResult Check(Compilation compilation, SyntaxTree tree) =>
            Check(compilation, StatementWrapper.FromTree(tree), CodePolicySet.Default);

        public static PolicyResult Check(Compilation compilation, WrappedCode wrapped, CodePolicySet? policy = null)
        {
            if (!wrapped.IsStatementsOnly || wrapped.Body == null)
                return new PolicyResult(wrapped.Violations, Array.Empty<string>());
            return CheckBoundTree(compilation, wrapped, policy);
        }

        /// <summary>The semantic pass alone, without the statement-only gate: for the budget-rewritten
        /// tree (whose hook calls the walker skips) and for defence-in-depth tests.</summary>
        public static PolicyResult CheckBoundTree(Compilation compilation, WrappedCode wrapped, CodePolicySet? policy = null)
        {
            policy ??= CodePolicySet.Default;
            if (wrapped.Body == null)
                return new PolicyResult(wrapped.Violations, Array.Empty<string>());

            var model = compilation.GetSemanticModel(wrapped.Tree);
            var uses = SymbolUseCollector.Collect(model, wrapped.Body);
            var evaluator = new Evaluator(compilation, wrapped, policy, model, wrapped.Body);
            foreach (var use in uses) evaluator.Evaluate(use);
            return new PolicyResult(evaluator.Violations, evaluator.Referenced, evaluator.Shimmed);
        }

        /// <summary>"Ns.Type.Member" against a key, where a trailing * matches any member-name suffix.</summary>
        public static bool KeyMatches(string key, string pattern) =>
            pattern.EndsWith("*", StringComparison.Ordinal)
                ? key.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal)
                : string.Equals(key, pattern, StringComparison.Ordinal);

        /// <summary>Structural path test (C2): the member takes, returns or holds a string (or strings)
        /// and its name or one of its parameter names has a path token. A path string is an I/O
        /// request (a UNC path makes Windows connect out), whatever the member is called.</summary>
        public static bool IsPathMember(ISymbol member, ICollection<string> tokens)
        {
            switch (member)
            {
                case IMethodSymbol m:
                    var name = SymbolKeys.MemberName(m);
                    var strings = m.Parameters.Any(p => Stringish(p.Type)) || (!m.ReturnsVoid && Stringish(m.ReturnType));
                    if (m.AssociatedSymbol is IPropertySymbol owner && Stringish(owner.Type)) strings = true;
                    return strings && (IsPathName(name, tokens) || m.Parameters.Any(p => IsPathName(p.Name, tokens)));
                case IPropertySymbol p:
                    var holds = Stringish(p.Type) || p.Parameters.Any(x => Stringish(x.Type));
                    return holds && (IsPathName(p.Name, tokens) || p.Parameters.Any(x => IsPathName(x.Name, tokens)));
                case IFieldSymbol f:
                    return Stringish(f.Type) && IsPathName(f.Name, tokens);
                default:
                    return false;
            }
        }

        /// <summary>string, string[] or any generic type over strings (IList&lt;string&gt;, IDictionary&lt;string, string&gt;...).</summary>
        public static bool Stringish(ITypeSymbol? type) => type switch
        {
            null => false,
            IArrayTypeSymbol a => Stringish(a.ElementType),
            INamedTypeSymbol n => n.SpecialType == SpecialType.System_String || n.TypeArguments.Any(Stringish),
            _ => false,
        };

        /// <summary>System.Exception or any type derived from it.</summary>
        public static bool IsException(INamedTypeSymbol? type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (SymbolKeys.TypeKey(t) == "System.Exception") return true;
            return false;
        }

        private static IEnumerable<string> SelfAndBaseNames(INamedTypeSymbol type)
        {
            for (var t = type.OriginalDefinition; t != null; t = t.BaseType?.OriginalDefinition) yield return t.Name;
        }

        /// <summary>The policy's verdict on one member used one way, outside any code: for the metadata
        /// scan that proves every path-like Revit member is denied or reviewed.</summary>
        public static IReadOnlyList<PolicyViolation> Judge(Compilation compilation, ISymbol member, MemberUsage usage, CodePolicySet? policy = null)
        {
            policy ??= CodePolicySet.Default;
            var wrapped = StatementWrapper.Build("", WrapperProfile.Revit, policy.LanguageVersion);
            var evaluator = new Evaluator(compilation, wrapped, policy, null, null);
            evaluator.Evaluate(new SymbolUse(SymbolUseKind.Member, member, usage, Location.None));
            return evaluator.Violations;
        }

        /// <summary>Camel-case tokens of a parameter name: "folderPath" is [folder, path], "profile" is [profile].</summary>
        public static bool IsPathName(string name, ICollection<string> tokens)
        {
            if (tokens.Contains(name.ToLowerInvariant())) return true;
            var start = 0;
            for (var i = 1; i <= name.Length; i++)
            {
                if (i == name.Length || char.IsUpper(name[i]) || name[i] == '_')
                {
                    var part = name.Substring(start, i - start).Trim('_').ToLowerInvariant();
                    if (part.Length > 0 && tokens.Contains(part)) return true;
                    start = i;
                }
            }
            return false;
        }

        private sealed class Evaluator
        {
            private readonly Compilation _compilation;
            private readonly WrappedCode _wrapped;
            private readonly CodePolicySet _p;
            private readonly HashSet<ISymbol> _hostParameters;
            private readonly IMethodSymbol? _print;
            private readonly HashSet<(string, int, int)> _seen = new HashSet<(string, int, int)>();

            public readonly List<PolicyViolation> Violations = new List<PolicyViolation>();
            public readonly HashSet<string> Referenced = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Shimmed = new HashSet<string>(StringComparer.Ordinal);

            public Evaluator(Compilation compilation, WrappedCode wrapped, CodePolicySet policy, SemanticModel? model, BlockSyntax? body)
            {
                _compilation = compilation;
                _wrapped = wrapped;
                _p = policy;
                var run = body?.Parent as MethodDeclarationSyntax;
                var runSymbol = run == null || model == null ? null : model.GetDeclaredSymbol(run);
                _hostParameters = new HashSet<ISymbol>(runSymbol?.Parameters ?? Enumerable.Empty<IParameterSymbol>(), SymbolEqualityComparer.Default);
                _print = runSymbol?.ContainingType.GetMembers(StatementWrapper.PrintMethod).OfType<IMethodSymbol>().FirstOrDefault();
            }

            public void Evaluate(SymbolUse use)
            {
                switch (use.Kind)
                {
                    case SymbolUseKind.Structural:
                        Add(_p.CategoryFor(CodeRuleKinds.Operation, use.Construct!), use.Construct!, use.Location,
                            use.Symbol != null ? SymbolKeys.Display(use.Symbol) : use.Detail ?? use.Construct!);
                        break;
                    case SymbolUseKind.Type:
                        CheckType((ITypeSymbol)use.Symbol!, use.Location);
                        break;
                    case SymbolUseKind.Member:
                        CheckMember(use.Symbol!, use.Usage, use.Location);
                        break;
                }
            }

            private SymbolOrigin Origin(IAssemblySymbol? assembly)
            {
                if (assembly == null) return SymbolOrigin.Other;
                if (SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly)) return SymbolOrigin.Source;
                var name = assembly.Name;
                if (_p.RevitAssemblies.Contains(name)) return SymbolOrigin.Revit;
                if (_p.RevitUiAssemblies.Contains(name)) return SymbolOrigin.RevitUi;
                if (_p.BclAssemblies.Contains(name)) return SymbolOrigin.Bcl;
                return SymbolOrigin.Other;
            }

            private static bool IsSystemNamespace(string ns) =>
                SymbolKeys.InNamespace(ns, "System") || SymbolKeys.InNamespace(ns, "Microsoft");

            // ---- types --------------------------------------------------------------------------

            private void CheckType(ITypeSymbol? type, Location at)
            {
                switch (type)
                {
                    case null:
                    case ITypeParameterSymbol _:
                    case IErrorTypeSymbol _:
                        return;
                    case IArrayTypeSymbol array:
                        if (IsException(array.ElementType as INamedTypeSymbol)) ExceptionContainer(at, type);
                        CheckType(array.ElementType, at);
                        return;
                    case IPointerTypeSymbol _:
                        Add(_p.CategoryFor(CodeRuleKinds.Syntax, "pointer"), "pointer", at, type.ToDisplayString());
                        return;
                    case IFunctionPointerTypeSymbol _:
                        Add(_p.CategoryFor(CodeRuleKinds.Syntax, "function_pointer"), "function_pointer", at, type.ToDisplayString());
                        return;
                }

                if (type.TypeKind == TypeKind.Dynamic)
                {
                    Add(_p.CategoryFor(CodeRuleKinds.Operation, "dynamic"), "dynamic", at, "dynamic");
                    return;
                }

                if (!(type is INamedTypeSymbol named)) return;
                if (named.IsAnonymousType)
                {
                    foreach (var p in named.GetMembers().OfType<IPropertySymbol>())
                    {
                        if (IsException(p.Type as INamedTypeSymbol)) ExceptionContainer(at, named);
                        CheckType(p.Type, at);
                    }
                    return;
                }
                foreach (var arg in named.TypeArguments)
                {
                    // List<Exception>, (Exception, int), Func<Exception>...: each one turns an exception into text
                    // somewhere (Join, Print, ToString of the container). Only the exception itself may be held.
                    if (IsException(arg as INamedTypeSymbol)) ExceptionContainer(at, named);
                    CheckType(arg, at);
                }
                if (named.IsTupleType)
                    foreach (var element in named.TupleElements) CheckType(element.Type, at);
                CheckNamedType(named.OriginalDefinition, at);
            }

            /// <summary>The category that denies this (generic definition) type, or null when allowed.</summary>
            private string? DeniedTypeCategory(INamedTypeSymbol def, out string construct)
            {
                construct = "";
                var origin = Origin(def.ContainingAssembly);
                if (origin == SymbolOrigin.Source)
                {
                    construct = "host_member";
                    return _p.CategoryFor(CodeRuleKinds.Operation, "host_member");
                }

                var chain = SymbolKeys.TypeKeyChain(def).ToList();
                var outermost = chain[chain.Count - 1];
                var ns = SymbolKeys.NamespaceOf(def);
                var systemLike = origin == SymbolOrigin.Bcl || (origin == SymbolOrigin.Other && IsSystemNamespace(ns));
                var revitLike = origin == SymbolOrigin.Revit || origin == SymbolOrigin.RevitUi;

                foreach (var rule in _p.Rules)
                {
                    construct = rule.Kind;
                    switch (rule.Kind)
                    {
                        case CodeRuleKinds.SystemType when (systemLike || origin == SymbolOrigin.Other) && chain.Any(rule.MatchSet.Contains):
                        case CodeRuleKinds.SystemNamespace when (systemLike || origin == SymbolOrigin.Other) && rule.Match.Any(m => SymbolKeys.InNamespace(ns, m)):
                        case CodeRuleKinds.RevitType when revitLike && chain.Any(rule.MatchSet.Contains):
                        case CodeRuleKinds.RevitNamespace when revitLike && rule.Match.Any(m => SymbolKeys.InNamespace(ns, m)):
                        case CodeRuleKinds.RevitTypeSuffix when revitLike && SelfAndBaseNames(def).Any(n => rule.Match.Any(sfx => n.EndsWith(sfx, StringComparison.Ordinal))):
                        case CodeRuleKinds.RevitUiDefault when origin == SymbolOrigin.RevitUi && !_p.RevitUiTypes.Contains(outermost):
                        case CodeRuleKinds.SystemDefault when systemLike && (!_p.SystemTypes.Contains(outermost) || origin != SymbolOrigin.Bcl):
                        case CodeRuleKinds.AssemblyDefault when origin == SymbolOrigin.Other && !systemLike:
                            return rule.Category;
                    }
                }
                construct = "";
                return null;
            }

            private void CheckNamedType(INamedTypeSymbol def, Location at)
            {
                var category = DeniedTypeCategory(def, out var construct);
                if (category != null) Add(category, construct, at, def.ToDisplayString());
                else Referenced.Add(SymbolKeys.Identity(def));
            }

            // ---- members ------------------------------------------------------------------------

            private void CheckMember(ISymbol member, MemberUsage usage, Location at)
            {
                var origin = Origin(member.ContainingAssembly);
                if (origin == SymbolOrigin.Source)
                {
                    CheckSourceMember(member, at);
                    return;
                }

                var containing = member.ContainingType;
                if (containing == null) return;

                var before = Violations.Count;
                CheckType(containing, at);
                if (Violations.Count > before) return; // the type itself is denied; that is the message

                var typeKey = SymbolKeys.TypeKey(containing);
                var name = SymbolKeys.MemberName(member);
                var revitLike = origin == SymbolOrigin.Revit || origin == SymbolOrigin.RevitUi;
                var isEnumConstant = member is IFieldSymbol f && f.ContainingType?.TypeKind == TypeKind.Enum;
                var display = SymbolKeys.Display(member);

                foreach (var rule in _p.Rules)
                {
                    var hit = rule.Kind switch
                    {
                        CodeRuleKinds.SystemMember => !revitLike
                            && (rule.MatchSet.Contains(typeKey + "." + name) || rule.MatchSet.Contains("*." + name)),
                        CodeRuleKinds.RevitMember => revitLike && !isEnumConstant && rule.AnyRegex(name),
                        CodeRuleKinds.RevitSetter => revitLike && usage == MemberUsage.Write && member is IPropertySymbol && rule.AnyRegex(name),
                        CodeRuleKinds.RevitMemberKey => revitLike && rule.Match.Any(m => KeyMatches(typeKey + "." + name, m)),
                        CodeRuleKinds.PathMember => revitLike && IsPathMember(member, rule.MatchSet),
                        CodeRuleKinds.ExceptionMember => rule.MatchSet.Contains(name) && IsException(containing),
                        _ => false,
                    };
                    if (hit)
                    {
                        Add(rule.Category, rule.Kind, at, display);
                        return;
                    }
                }

                if (!revitLike && !_p.SystemMemberAllowed(typeKey, name))
                {
                    var category = _p.Rules.First(r => r.Kind == CodeRuleKinds.SystemDefault).Category;
                    Add(category, CodeRuleKinds.SystemDefault, at, display);
                    return;
                }

                // Every type in the signature must be allowed too (MethodBase, Stream, Type...).
                before = Violations.Count;
                foreach (var t in SignatureTypes(member, revitLike)) CheckType(t, at);
                if (Violations.Count == before)
                {
                    Referenced.Add(SymbolKeys.Identity(member));
                    if (_p.UiShimMembers.Contains(typeKey + "." + name)) Shimmed.Add(SymbolKeys.Identity(member));
                }
            }

            private void CheckSourceMember(ISymbol member, Location at)
            {
                switch (member)
                {
                    case IMethodSymbol m when m.MethodKind == MethodKind.LocalFunction || m.MethodKind == MethodKind.AnonymousFunction:
                        foreach (var t in SignatureTypes(m, false)) CheckType(t, at);
                        return;
                    case IMethodSymbol m when _print != null && SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, _print):
                        Referenced.Add("host:Print");
                        return;
                    case IPropertySymbol p when p.ContainingType?.IsAnonymousType == true:
                        CheckType(p.Type, at);
                        return;
                    case IMethodSymbol m when m.ContainingType?.IsAnonymousType == true:
                        return; // anonymous type's own Equals/GetHashCode/ToString/get_
                    case IParameterSymbol ps when _hostParameters.Contains(ps):
                        return;
                }
                Add(_p.CategoryFor(CodeRuleKinds.Operation, "host_member"), "host_member", at, SymbolKeys.Display(member));
            }

            private static IEnumerable<ITypeSymbol> SignatureTypes(ISymbol member, bool revitMember)
            {
                switch (member)
                {
                    case IMethodSymbol m:
                        if (!m.ReturnsVoid) yield return m.ReturnType;
                        foreach (var p in m.Parameters)
                            if (!(revitMember && IsSystemType(p.Type))) yield return p.Type;
                        foreach (var t in m.TypeArguments) yield return t;
                        break;
                    case IPropertySymbol p:
                        yield return p.Type;
                        foreach (var ip in p.Parameters) yield return ip.Type;
                        break;
                    case IFieldSymbol f:
                        yield return f.Type;
                        break;
                    case IEventSymbol e:
                        yield return e.Type;
                        break;
                }
            }

            private static bool IsSystemType(ITypeSymbol t) =>
                t is INamedTypeSymbol n && n.ContainingType == null && n.Name == "Type"
                && SymbolKeys.NamespaceName(n.ContainingNamespace) == "System";

            private void ExceptionContainer(Location at, ITypeSymbol container) =>
                Add(_p.CategoryFor(CodeRuleKinds.Operation, "exception_container"), "exception_container", at, container.ToDisplayString());

            private void Add(string category, string construct, Location at, string symbol)
            {
                var pos = at.GetLineSpan().StartLinePosition;
                var line = _wrapped.ModelLine(pos.Line);
                if (!_seen.Add((category + "|" + symbol, line, 0))) return;
                Violations.Add(new PolicyViolation(category, construct, line, pos.Character + 1, symbol, _p.Why(category)));
            }
        }
    }
}

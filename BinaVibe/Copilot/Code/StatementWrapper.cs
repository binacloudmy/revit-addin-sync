#nullable enable
// Wraps the model's run_revit_code text in a fixed host class and proves the
// text is ONLY statements inside Run's body (spec §7.8). Revit-free: it only
// builds and inspects a syntax tree.
//
//   namespace BinaVibe.Copilot.Dynamic {
//   public static class Snippet {
//     <hook fields>  __Bind(...)  Print(object)
//     public static void Run(Document doc, UIDocument uidoc, View view) {
//   <the model's text, verbatim, from column 1>
//   } } }
//
// Build() rejects (policy_rejected) anything that escapes the body or declares
// a member or type, and any banned statement form: attributes, unsafe,
// pointers, stackalloc, fixed, extern, async/await, lock, goto/labels, yield,
// # directives, __arglist-style keywords and identifiers starting with "__"
// (reserved for the host hooks). Semantic checks are CodePolicy's job.
//
// The line offset is computed from the built tree (the line after Run's "{"),
// never hard-coded, so compile errors and stack frames map back to the
// model's own line numbers.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace BinaVibe.Copilot.Code
{
    /// <summary>What the wrapper hands the model's code.</summary>
    public enum WrapperProfile
    {
        /// <summary>doc, uidoc, view and Print(obj), with the Revit usings.</summary>
        Revit,
        /// <summary>Print(obj) and System usings only: lets tests compile and RUN code without Revit.</summary>
        Pure,
    }

    /// <summary>The wrapped tree plus where the model's text sits in it.</summary>
    public sealed class WrappedCode
    {
        internal WrappedCode(string modelCode, WrapperProfile profile, SyntaxTree tree, TextSpan modelSpan)
        {
            ModelCode = modelCode;
            Profile = profile;
            Tree = tree;
            ModelSpan = modelSpan;
        }

        public string ModelCode { get; }
        public WrapperProfile Profile { get; }
        public SyntaxTree Tree { get; }

        /// <summary>The model's text inside <see cref="Tree"/>.</summary>
        public TextSpan ModelSpan { get; }

        /// <summary>Run's body; null when the model's text broke the wrapper's structure.</summary>
        public BlockSyntax? Body { get; internal set; }

        /// <summary>Zero-based wrapper lines before the model's first line (computed from the tree).</summary>
        public int LineOffset { get; internal set; }

        /// <summary>Lines in the model's text (at least 1).</summary>
        public int ModelLineCount { get; internal set; }

        /// <summary>Structure and banned-syntax findings, in model line numbers. Empty = statements only.</summary>
        public IReadOnlyList<PolicyViolation> Violations { get; internal set; } = Array.Empty<PolicyViolation>();

        /// <summary>Syntax errors (compile errors the model must fix), reported only when there are no violations.</summary>
        public IReadOnlyList<Diagnostic> ParseErrors { get; internal set; } = Array.Empty<Diagnostic>();

        public bool IsStatementsOnly => Violations.Count == 0;

        /// <summary>Maps a zero-based line of <see cref="Tree"/> to a 1-based line of the model's text,
        /// clamped to the text (wrapper lines map to the nearest model line).</summary>
        public int ModelLine(int zeroBasedTreeLine)
        {
            var line = zeroBasedTreeLine - LineOffset + 1;
            if (line < 1) return 1;
            return line > ModelLineCount ? ModelLineCount : line;
        }

        /// <summary>True when the tree line is inside the model's text.</summary>
        public bool IsModelLine(int zeroBasedTreeLine)
        {
            var line = zeroBasedTreeLine - LineOffset + 1;
            return line >= 1 && line <= ModelLineCount;
        }
    }

    public static class StatementWrapper
    {
        public const string TreePath = "run_revit_code.cs";
        public const string Namespace = "BinaVibe.Copilot.Dynamic";
        public const string ClassName = "Snippet";
        public const string RunMethod = "Run";
        public const string BindMethod = "__Bind";
        public const string PrintMethod = "Print";

        /// <summary>The hook fields the budget rewriter calls (never callable by the model: "__" is reserved).</summary>
        public const string TickField = "__tick";
        public const string EnterField = "__enter";
        public const string ExitField = "__exit";
        public const string LiveField = "__live";
        public const string PrintField = "__print";

        public const string UiField = "__ui";

        /// <summary>Host shims for UI requests are named this plus the Revit member (__RequestViewChange...).</summary>
        public const string ShimPrefix = "__";

        /// <summary>The counting wrapper around lazy LINQ calls (and its iterator).</summary>
        public const string CountMethod = "__count";
        public const string CountIterator = "__countEach";

        /// <summary>Host parameter names per profile, in Run's parameter order.</summary>
        public static IReadOnlyList<string> HostParameters(WrapperProfile profile) =>
            profile == WrapperProfile.Revit ? new[] { "doc", "uidoc", "view" } : Array.Empty<string>();

        private static readonly string[] SystemUsings = { "System", "System.Collections.Generic", "System.Linq", "System.Text" };

        private static readonly string[] RevitUsings =
        {
            "Autodesk.Revit.DB", "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.DB.Structure",
            // MEP: Pipe/Duct resolve without full names. Electrical stays out: its Panel collides with DB.Panel.
            "Autodesk.Revit.DB.Plumbing", "Autodesk.Revit.DB.Mechanical",
            "Autodesk.Revit.UI", "Autodesk.Revit.UI.Selection",
        };


        public static CSharpParseOptions ParseOptions(LanguageVersion version) =>
            new CSharpParseOptions(version, DocumentationMode.None, SourceCodeKind.Regular);

        public static WrappedCode Build(string? code, WrapperProfile profile = WrapperProfile.Revit, LanguageVersion? version = null)
        {
            var model = code ?? "";
            var prefix = Prefix(profile);
            var text = prefix + model + "\n}\n}\n}\n";
            var tree = CSharpSyntaxTree.ParseText(
                SourceText.From(text, Encoding.UTF8),
                ParseOptions(version ?? CodePolicySet.Default.LanguageVersion),
                TreePath);
            return Analyze(tree, new TextSpan(prefix.Length, model.Length), profile);
        }

        /// <summary>Re-reads a tree this class built (or the budget rewriter produced from one):
        /// finds Run's body and the model's text between its braces, and runs the same checks.</summary>
        public static WrappedCode FromTree(SyntaxTree tree)
        {
            var root = (CompilationUnitSyntax)tree.GetRoot();
            foreach (var profile in new[] { WrapperProfile.Revit, WrapperProfile.Pure })
            {
                var run = FindRun(root, profile);
                if (run?.Body == null) continue;
                var start = run.Body.OpenBraceToken.Span.End + 1;            // after "{\n"
                var end = Math.Max(start, run.Body.CloseBraceToken.SpanStart - 1); // before "\n}"
                return Analyze(tree, TextSpan.FromBounds(start, end), profile);
            }
            return Analyze(tree, new TextSpan(0, 0), WrapperProfile.Revit);
        }

        private static WrappedCode Analyze(SyntaxTree tree, TextSpan span, WrapperProfile profile)
        {
            var model = tree.GetText().ToString(span);
            var wrapped = new WrappedCode(model, profile, tree, span)
            {
                ModelLineCount = Math.Max(1, CountLines(model)),
            };

            var root = (CompilationUnitSyntax)tree.GetRoot();
            var violations = new List<PolicyViolation>();
            var run = FindRun(root, profile);

            // Computed, not hard-coded: the model starts on the line after Run's "{".
            wrapped.LineOffset = run?.Body != null
                ? run.Body.OpenBraceToken.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                : tree.GetText().Lines.GetLineFromPosition(span.Start).LineNumber;

            // 1. Declarations anywhere in the model's text (inside or outside the body).
            foreach (var node in root.DescendantNodes(n => n.FullSpan.IntersectsWith(span)))
            {
                if (!span.Contains(node.Span.Start)) continue;
                if (node is MemberDeclarationSyntax || node is IncompleteMemberSyntax || node is GlobalStatementSyntax
                    || node is ExternAliasDirectiveSyntax || node is UsingDirectiveSyntax)
                {
                    violations.Add(Violation(wrapped, "member_or_type_declaration", node.GetLocation(), Describe(node)));
                    break;
                }
            }

            // 2. The wrapper must still be intact, with the whole text inside Run's body.
            if (violations.Count == 0 && !StructureIntact(root, run, span))
            {
                var at = EscapePoint(root, span) ?? Location.Create(tree, new TextSpan(span.Start, 0));
                violations.Add(Violation(wrapped, "wrapper_escape", at, "code outside the method body"));
            }

            if (violations.Count == 0 && run?.Body != null)
            {
                wrapped.Body = run.Body;
                var parseErrors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                if (parseErrors.Count > 0) wrapped.ParseErrors = parseErrors;
                else violations.AddRange(BannedSyntax(wrapped, run.Body, root));
            }

            wrapped.Violations = violations;
            return wrapped;
        }

        private static string Prefix(WrapperProfile profile)
        {
            var sb = new StringBuilder();
            foreach (var u in SystemUsings) sb.Append("using ").Append(u).Append(";\n");
            if (profile == WrapperProfile.Revit)
                foreach (var u in RevitUsings) sb.Append("using ").Append(u).Append(";\n");
            sb.Append("namespace ").Append(Namespace).Append("\n{\n");
            sb.Append("public static class ").Append(ClassName).Append("\n{\n");
            sb.Append("private static global::System.Action<object> ").Append(PrintField).Append(";\n");
            sb.Append("private static global::System.Action ").Append(TickField).Append(";\n");
            sb.Append("private static global::System.Action ").Append(EnterField).Append(";\n");
            sb.Append("private static global::System.Action ").Append(ExitField).Append(";\n");
            sb.Append("private static global::System.Func<bool> ").Append(LiveField).Append(";\n");
            sb.Append("private static global::System.Action<int, object> ").Append(UiField).Append(";\n");
            sb.Append("public static void ").Append(BindMethod)
              .Append("(global::System.Action<object> print, global::System.Action tick, global::System.Action enter, global::System.Action exit, global::System.Func<bool> live, global::System.Action<int, object> ui) { ")
              .Append(PrintField).Append(" = print; ").Append(TickField).Append(" = tick; ")
              .Append(EnterField).Append(" = enter; ").Append(ExitField).Append(" = exit; ")
              .Append(LiveField).Append(" = live; ").Append(UiField).Append(" = ui; }\n");
            sb.Append("private static void ").Append(PrintMethod).Append("(object value) { ")
              .Append(TickField).Append("(); ").Append(PrintField).Append("(value); }\n");
            // LoopBudgetRewriter wraps every lazy Enumerable call in __count: each MoveNext is an __enter (one
            // tick per element, plus the depth and stack check), so lazy fan-out and deep lazy chains hit
            // the step budget and the depth cap.
            sb.Append("private static global::System.Collections.Generic.IEnumerable<T> ").Append(CountMethod)
              .Append("<T>(global::System.Collections.Generic.IEnumerable<T> source) { return source == null ? null : ")
              .Append(CountIterator).Append("(source); }\n");
            sb.Append("private static global::System.Collections.Generic.IEnumerable<T> ").Append(CountIterator)
              .Append("<T>(global::System.Collections.Generic.IEnumerable<T> source) { using (var e = source.GetEnumerator()) { while (true) { ")
              .Append(EnterField).Append("(); bool more; try { more = e.MoveNext(); } finally { ").Append(ExitField).Append("(); } ")
              .Append("if (!more) yield break; yield return e.Current; } } }\n");
            if (profile == WrapperProfile.Revit)
            {
                // UI requests never run inside the code (ruling R59): the rewriter turns uidoc.RequestViewChange(v)
                // into __RequestViewChange(uidoc, v) and so on; the host queues each request and applies it
                // after the read's rollback (or the write's commit). Kinds: 0 view, 1 show, 2 select.
                const string UiDoc = "global::Autodesk.Revit.UI.UIDocument", Ids = "global::System.Collections.Generic.ICollection<global::Autodesk.Revit.DB.ElementId>";
                Shim(sb, "RequestViewChange", UiDoc, "global::Autodesk.Revit.DB.View", 0);
                Shim(sb, "ShowElements", UiDoc, "global::Autodesk.Revit.DB.Element", 1);
                Shim(sb, "ShowElements", UiDoc, "global::Autodesk.Revit.DB.ElementId", 1);
                Shim(sb, "ShowElements", UiDoc, Ids, 1);
                Shim(sb, "SetElementIds", "global::Autodesk.Revit.UI.Selection.Selection", Ids, 2);
            }
            sb.Append("public static void ").Append(RunMethod).Append('(');
            if (profile == WrapperProfile.Revit)
                sb.Append("global::Autodesk.Revit.DB.Document doc, global::Autodesk.Revit.UI.UIDocument uidoc, global::Autodesk.Revit.DB.View view");
            sb.Append(")\n{\n");
            return sb.ToString();
        }

        private static void Shim(StringBuilder sb, string member, string targetType, string argType, int kind) =>
            sb.Append("private static void ").Append(ShimPrefix).Append(member).Append('(').Append(targetType).Append(" target, ")
              .Append(argType).Append(" value) { ").Append(TickField).Append("(); ").Append(UiField).Append('(').Append(kind).Append(", value); }\n");

        /// <summary>Members the prefix declares: 6 hook fields, __Bind, Print, __count, __countEach, the UI shims
        /// (Revit profile), Run.</summary>
        private static int ClassMemberCount(WrapperProfile profile) => profile == WrapperProfile.Revit ? 16 : 11;

        private static MethodDeclarationSyntax? FindRun(CompilationUnitSyntax root, WrapperProfile profile)
        {
            if (root.Members.Count != 1 || !(root.Members[0] is NamespaceDeclarationSyntax ns)) return null;
            if (ns.Members.Count != 1 || !(ns.Members[0] is ClassDeclarationSyntax cls)) return null;
            var count = ClassMemberCount(profile);
            if (cls.Identifier.ValueText != ClassName || cls.Members.Count != count) return null;
            if (!(cls.Members[count - 1] is MethodDeclarationSyntax run)) return null;
            if (run.Identifier.ValueText != RunMethod || run.Body == null) return null;
            if (run.ParameterList.Parameters.Count != HostParameters(profile).Count) return null;
            return run;
        }

        private static bool StructureIntact(CompilationUnitSyntax root, MethodDeclarationSyntax? run, TextSpan span)
        {
            if (run?.Body == null) return false;
            if (root.AttributeLists.Count > 0 || root.Externs.Count > 0) return false;
            var body = run.Body;
            if (body.OpenBraceToken.IsMissing || body.CloseBraceToken.IsMissing) return false;
            // Run's own braces must enclose the model's text: an early "}" in the text would close the body.
            return body.OpenBraceToken.Span.End <= span.Start && body.CloseBraceToken.SpanStart >= span.End;
        }

        private static Location? EscapePoint(CompilationUnitSyntax root, TextSpan span)
        {
            // The first "}" in the model's text that closes a block the wrapper opened.
            var token = root.DescendantTokens().FirstOrDefault(t =>
                t.IsKind(SyntaxKind.CloseBraceToken) && span.Contains(t.SpanStart)
                && t.Parent != null && !span.Contains(t.Parent.SpanStart));
            return token.RawKind != 0 ? token.GetLocation() : null;
        }

        private static IEnumerable<PolicyViolation> BannedSyntax(WrappedCode wrapped, BlockSyntax body, CompilationUnitSyntax root)
        {
            var found = new List<PolicyViolation>();

            foreach (var node in body.DescendantNodes())
            {
                var construct = node switch
                {
                    AttributeListSyntax _ => "attribute",
                    UnsafeStatementSyntax _ => "unsafe",
                    PointerTypeSyntax _ => "pointer",
                    FunctionPointerTypeSyntax _ => "function_pointer",
                    PrefixUnaryExpressionSyntax p when p.IsKind(SyntaxKind.AddressOfExpression) || p.IsKind(SyntaxKind.PointerIndirectionExpression) => "pointer",
                    MemberAccessExpressionSyntax m when m.IsKind(SyntaxKind.PointerMemberAccessExpression) => "pointer",
                    FixedStatementSyntax _ => "fixed",
                    StackAllocArrayCreationExpressionSyntax _ => "stackalloc",
                    ImplicitStackAllocArrayCreationExpressionSyntax _ => "stackalloc",
                    LockStatementSyntax _ => "lock",
                    AwaitExpressionSyntax _ => "await",
                    GotoStatementSyntax _ => "goto",
                    LabeledStatementSyntax _ => "label",
                    YieldStatementSyntax _ => "yield",
                    QueryExpressionSyntax _ => "query_expression",
                    MakeRefExpressionSyntax _ => "undocumented_keyword",
                    RefTypeExpressionSyntax _ => "undocumented_keyword",
                    RefValueExpressionSyntax _ => "undocumented_keyword",
                    _ when node.IsKind(SyntaxKind.ArgListExpression) => "undocumented_keyword",
                    _ => null,
                };
                if (construct != null) found.Add(Violation(wrapped, construct, node.GetLocation(), Describe(node)));
            }

            foreach (var token in body.DescendantTokens())
            {
                string? construct = null;
                if (token.IsKind(SyntaxKind.UnsafeKeyword)) construct = "unsafe";
                else if (token.IsKind(SyntaxKind.ExternKeyword)) construct = "extern";
                else if (token.IsKind(SyntaxKind.AsyncKeyword)) construct = "async";
                else if (token.IsKind(SyntaxKind.AwaitKeyword)) construct = "await";
                else if (token.IsKind(SyntaxKind.ArgListKeyword)) construct = "undocumented_keyword";
                else if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText.StartsWith("__", StringComparison.Ordinal))
                    construct = "reserved_identifier";
                if (construct != null) found.Add(Violation(wrapped, construct, token.GetLocation(), "'" + token.Text + "'"));
            }

            // The wrapper itself has no directives, so any directive came from the model.
            foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
                if (trivia.IsDirective)
                {
                    found.Add(Violation(wrapped, "directive", trivia.GetLocation(), "'" + trivia.ToString().Trim() + "'"));
                    break;
                }

            // One finding per construct and line is enough for the model to rewrite.
            return found.GroupBy(v => (v.Construct, v.Line)).Select(g => g.First()).OrderBy(v => v.Line).ThenBy(v => v.Column);
        }

        private static PolicyViolation Violation(WrappedCode wrapped, string construct, Location location, string what)
        {
            var pos = location.GetLineSpan().StartLinePosition;
            var category = CodePolicySet.Default.CategoryFor(CodeRuleKinds.Syntax, construct);
            return new PolicyViolation(category, construct, wrapped.ModelLine(pos.Line), pos.Character + 1, what,
                CodePolicySet.Default.Why(category));
        }

        private static string Describe(SyntaxNode node)
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax t: return KindWord(t) + " '" + t.Identifier.ValueText + "'";
                case DelegateDeclarationSyntax d: return "delegate '" + d.Identifier.ValueText + "'";
                case MethodDeclarationSyntax m: return "method '" + m.Identifier.ValueText + "'";
                case PropertyDeclarationSyntax p: return "property '" + p.Identifier.ValueText + "'";
                case FieldDeclarationSyntax _: return "field declaration";
                case BaseNamespaceDeclarationSyntax n: return "namespace '" + n.Name + "'";
                case UsingDirectiveSyntax u: return "using directive '" + u.Name + "'";
                default:
                    var text = node.ToString();
                    var line = text.Split('\n')[0].Trim();
                    return "'" + (line.Length > 60 ? line.Substring(0, 60) + "…" : line) + "'";
            }
        }

        private static string KindWord(BaseTypeDeclarationSyntax t) => t switch
        {
            ClassDeclarationSyntax _ => "class",
            StructDeclarationSyntax _ => "struct",
            InterfaceDeclarationSyntax _ => "interface",
            RecordDeclarationSyntax _ => "record",
            EnumDeclarationSyntax _ => "enum",
            _ => "type",
        };

        internal static int CountLines(string s)
        {
            if (s.Length == 0) return 1;
            var n = 1;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\n') n++;
                else if (s[i] == '\r' && (i + 1 >= s.Length || s[i + 1] != '\n')) n++;
            }
            return n;
        }
    }
}

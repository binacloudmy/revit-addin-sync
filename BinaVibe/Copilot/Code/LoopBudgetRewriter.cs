#nullable enable
// Inserts the loop budget into the model's code (spec §7.8). Revit-free.
//
//   while/do/for/foreach body    -> { __tick(); <body> }                  every back-edge passes the body start
//   lambda / local function body -> { __enter(); try { <body> } finally { __exit(); } }
//                                   (an expression body becomes "return e;" or "e;" by the symbol's return type)
//   catch [(T e)] [when (f)]     -> catch [(T e)] when (__live() [&& (f)])  so a catch cannot swallow the stop
//   lazy Enumerable call         -> __count(<call>): each element is a tick and each nested MoveNext an
//                                   __enter, so lazy fan-out (Concat doubling, Range/Repeat) and deep lazy
//                                   chains hit the step budget and the depth cap (fix round 1, C1)
//
// goto, labels, yield and query syntax are banned by StatementWrapper, and method
// groups as delegates by CodePolicy, so loops, lambdas, local functions and lazy
// LINQ elements are the only places work can repeat. Inserted text never contains a newline: every original token
// keeps its line, and compile errors and stack frames map with the same
// offset. CodeRunner re-parses the result and proves (CodePolicy.CheckBoundTree)
// that it references exactly the symbols the checked original did.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BinaVibe.Copilot.Code
{
    public static class LoopBudgetRewriter
    {
        /// <summary>Rewrites Run's body in a tree StatementWrapper built; returns the new tree text.</summary>
        public static string Rewrite(WrappedCode wrapped, SemanticModel model)
        {
            if (wrapped.Body == null) throw new ArgumentException("the wrapped code has no Run body", nameof(wrapped));

            // Return types are read from the ORIGINAL nodes before any rewriting.
            var returnsVoid = new Dictionary<int, bool>();
            foreach (var node in wrapped.Body.DescendantNodes())
            {
                IMethodSymbol? symbol = node switch
                {
                    AnonymousFunctionExpressionSyntax f => model.GetSymbolInfo(f).Symbol as IMethodSymbol,
                    LocalFunctionStatementSyntax l => model.GetDeclaredSymbol(l) as IMethodSymbol,
                    _ => null,
                };
                if (symbol != null) returnsVoid[node.SpanStart * 4 + Kind(node)] = symbol.ReturnsVoid;
            }

            // Lazy LINQ calls whose elements must be counted (never one behind ?.: the policy rejects those).
            var counted = new HashSet<(int, int)>();
            foreach (var inv in wrapped.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (!(inv.Expression is MemberBindingExpressionSyntax)
                    && SymbolUseCollector.IsCountedLinq(model.GetSymbolInfo(inv).Symbol as IMethodSymbol))
                    counted.Add((inv.SpanStart, inv.Span.Length));

            var rewriter = new Rewriter(returnsVoid, counted);
            var newBody = (BlockSyntax)rewriter.Visit(wrapped.Body);
            var root = wrapped.Tree.GetRoot().ReplaceNode(wrapped.Body, newBody);
            return root.ToFullString();
        }

        private static int Kind(SyntaxNode node) => node is LocalFunctionStatementSyntax ? 1 : 2;

        private sealed class Rewriter : CSharpSyntaxRewriter
        {
            private readonly Dictionary<int, bool> _returnsVoid;
            private readonly HashSet<(int, int)> _counted;

            public Rewriter(Dictionary<int, bool> returnsVoid, HashSet<(int, int)> counted)
            {
                _returnsVoid = returnsVoid;
                _counted = counted;
            }

            // ---- lazy LINQ: every element is counted ------------------------------------------------

            public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                var n = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
                if (!_counted.Contains((node.SpanStart, node.Span.Length))) return n;
                // __count(<call>): same static type, same line, the call's trivia kept outside.
                return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.IdentifierName(StatementWrapper.CountMethod),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(n.WithoutTrivia()))))
                    .WithLeadingTrivia(n.GetLeadingTrivia())
                    .WithTrailingTrivia(n.GetTrailingTrivia());
            }

            private bool ReturnsVoid(SyntaxNode original) =>
                !_returnsVoid.TryGetValue(original.SpanStart * 4 + Kind(original), out var v) || v;

            // ---- loops ------------------------------------------------------------------------

            public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
            {
                var n = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
                return n.WithStatement(TickFirst(n.Statement));
            }

            public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
            {
                var n = (DoStatementSyntax)base.VisitDoStatement(node)!;
                return n.WithStatement(TickFirst(n.Statement));
            }

            public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
            {
                var n = (ForStatementSyntax)base.VisitForStatement(node)!;
                return n.WithStatement(TickFirst(n.Statement));
            }

            public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
            {
                var n = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
                return n.WithStatement(TickFirst(n.Statement));
            }

            public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
            {
                var n = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
                return n.WithStatement(TickFirst(n.Statement));
            }

            // ---- method and lambda entry --------------------------------------------------------

            public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
            {
                var voidReturn = ReturnsVoid(node);
                var n = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
                if (n.Body != null) return n.WithBody(Guard(n.Body));
                if (n.ExpressionBody == null) return n;
                // "=>" goes away with the expression body: keep its trivia (it may hold a newline).
                var arrow = n.ExpressionBody.ArrowToken;
                var block = Guard(n.ExpressionBody.Expression, voidReturn,
                    n.SemicolonToken.LeadingTrivia.AddRange(n.SemicolonToken.TrailingTrivia),
                    arrow.LeadingTrivia.AddRange(arrow.TrailingTrivia));
                return n.WithExpressionBody(null).WithSemicolonToken(default).WithBody(block);
            }

            public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            {
                var voidReturn = ReturnsVoid(node);
                var n = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
                return n.Block != null
                    ? n.WithBlock(Guard(n.Block))
                    : n.WithExpressionBody(null).WithBlock(Guard(n.ExpressionBody!, voidReturn, default, default));
            }

            public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            {
                var voidReturn = ReturnsVoid(node);
                var n = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
                return n.Block != null
                    ? n.WithBlock(Guard(n.Block))
                    : n.WithExpressionBody(null).WithBlock(Guard(n.ExpressionBody!, voidReturn, default, default));
            }

            public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
            {
                var n = (AnonymousMethodExpressionSyntax)base.VisitAnonymousMethodExpression(node)!;
                return n.WithBlock(Guard(n.Block));
            }

            // ---- catch ----------------------------------------------------------------------------

            public override SyntaxNode? VisitCatchClause(CatchClauseSyntax node)
            {
                var n = (CatchClauseSyntax)base.VisitCatchClause(node)!;
                ExpressionSyntax live = Call(StatementWrapper.LiveField);
                ExpressionSyntax condition = n.Filter == null
                    ? live
                    : SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, live,
                        SyntaxFactory.Token(SyntaxKind.AmpersandAmpersandToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space),
                        SyntaxFactory.ParenthesizedExpression(n.Filter.FilterExpression));
                var filter = SyntaxFactory.CatchFilterClause(
                    SyntaxFactory.Token(SyntaxKind.WhenKeyword).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.Token(SyntaxKind.OpenParenToken),
                    condition,
                    SyntaxFactory.Token(SyntaxKind.CloseParenToken).WithTrailingTrivia(SyntaxFactory.Space));
                if (n.Filter != null)
                    filter = filter.WithLeadingTrivia(n.Filter.GetLeadingTrivia()).WithTrailingTrivia(n.Filter.GetTrailingTrivia());
                return n.WithFilter(filter);
            }

            // ---- builders -------------------------------------------------------------------------

            private static StatementSyntax TickFirst(StatementSyntax body)
            {
                var tick = HookStatement(StatementWrapper.TickField);
                if (body is BlockSyntax block)
                    return block.WithStatements(block.Statements.Insert(0, tick));
                return SyntaxFactory.Block(
                        SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(body.GetLeadingTrivia()).WithTrailingTrivia(SyntaxFactory.Space),
                        SyntaxFactory.List(new[] { tick, body.WithoutLeadingTrivia().WithoutTrailingTrivia() }),
                        SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(body.GetTrailingTrivia()));
            }

            /// <summary>{ __enter(); try { body } finally { __exit(); } } keeping the block's own braces.</summary>
            private static BlockSyntax Guard(BlockSyntax body)
            {
                var inner = SyntaxFactory.Block(
                    SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(SyntaxFactory.Space),
                    body.Statements,
                    SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.Space));
                return body.WithStatements(SyntaxFactory.List(new StatementSyntax[] { HookStatement(StatementWrapper.EnterField), Try(inner) }));
            }

            /// <summary>The same for an expression body: "return e;" or "e;".</summary>
            private static BlockSyntax Guard(ExpressionSyntax expression, bool returnsVoid, SyntaxTriviaList trailing, SyntaxTriviaList leading)
            {
                var e = expression.WithoutLeadingTrivia().WithoutTrailingTrivia();
                StatementSyntax statement = returnsVoid
                    ? SyntaxFactory.ExpressionStatement(e, SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(SyntaxFactory.Space))
                    : SyntaxFactory.ReturnStatement(
                        SyntaxFactory.Token(SyntaxKind.ReturnKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                        e,
                        SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(SyntaxFactory.Space));
                var inner = SyntaxFactory.Block(
                    SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.SingletonList(statement),
                    SyntaxFactory.Token(SyntaxKind.CloseBraceToken));
                return SyntaxFactory.Block(
                    SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(leading.AddRange(expression.GetLeadingTrivia())).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.List(new StatementSyntax[] { HookStatement(StatementWrapper.EnterField), Try(inner) }),
                    SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.Space)
                        .WithTrailingTrivia(expression.GetTrailingTrivia().AddRange(trailing)));
            }

            private static TryStatementSyntax Try(BlockSyntax inner) =>
                SyntaxFactory.TryStatement(
                    SyntaxFactory.Token(SyntaxKind.TryKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    inner,
                    default,
                    SyntaxFactory.FinallyClause(
                        SyntaxFactory.Token(SyntaxKind.FinallyKeyword).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space),
                        SyntaxFactory.Block(
                            SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(SyntaxFactory.Space),
                            SyntaxFactory.SingletonList(HookStatement(StatementWrapper.ExitField)),
                            SyntaxFactory.Token(SyntaxKind.CloseBraceToken))))
                .WithTrailingTrivia(SyntaxFactory.Space);

            private static StatementSyntax HookStatement(string field) =>
                SyntaxFactory.ExpressionStatement(Call(field), SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(SyntaxFactory.Space));

            private static InvocationExpressionSyntax Call(string field) =>
                SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(field), SyntaxFactory.ArgumentList());
        }
    }
}

#nullable enable
// Collects every symbol the model's code touches, for CodePolicy (allowed?)
// and ModeClassifier (does it change the document?). Revit-free: Revit types
// are recognised by name, never referenced.
//
// Two passes over Run's body, so nothing binds unseen:
//   1. the bound IOperation tree: member calls, constructors, conversions and
//      operators with a method, property/field/event references (with read or
//      write usage), delegates and method groups, typeof, patterns, catch
//      types, declared locals, lambda and local-function signatures, the
//      result type of every operation, and structural findings (dynamic,
//      event subscription, `this`, unsupported constructs);
//   2. the syntax tree: GetSymbolInfo/GetTypeInfo of every expression and type
//      node, plus what IOperation keeps implicit (foreach enumerator methods,
//      Deconstruct, query clause methods).
// The host's own hook calls (identifiers starting with "__", which the model
// may not write) are skipped, so the budget-rewritten tree can be compared
// with the original.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace BinaVibe.Copilot.Code
{
    public enum SymbolUseKind { Type, Member, Structural }

    public enum MemberUsage { Read, Write, Invoke }

    public sealed class SymbolUse
    {
        public SymbolUse(SymbolUseKind kind, ISymbol? symbol, MemberUsage usage, Location location, string? construct = null, string? detail = null)
        {
            Kind = kind;
            Symbol = symbol;
            Usage = usage;
            Location = location;
            Construct = construct;
            Detail = detail;
        }

        public SymbolUseKind Kind { get; }
        public ISymbol? Symbol { get; }
        public MemberUsage Usage { get; }
        public Location Location { get; }

        /// <summary>For Structural uses: the operation construct name in the policy (e.g. "dynamic").</summary>
        public string? Construct { get; }
        public string? Detail { get; }
    }

    public static class SymbolKeys
    {
        /// <summary>"Namespace.Name`arity", nested as "Outer+Inner"; generic arguments dropped.</summary>
        public static string TypeKey(INamedTypeSymbol type)
        {
            var def = type.OriginalDefinition;
            if (def.ContainingType != null) return TypeKey(def.ContainingType) + "+" + def.MetadataName;
            var ns = NamespaceName(def.ContainingNamespace);
            return ns.Length == 0 ? def.MetadataName : ns + "." + def.MetadataName;
        }

        /// <summary>The type and every type it is nested in, innermost first.</summary>
        public static IEnumerable<string> TypeKeyChain(INamedTypeSymbol type)
        {
            for (var t = type.OriginalDefinition; t != null; t = t.ContainingType?.OriginalDefinition)
                yield return TypeKey(t);
        }

        public static INamedTypeSymbol Outermost(INamedTypeSymbol type)
        {
            var t = type.OriginalDefinition;
            while (t.ContainingType != null) t = t.ContainingType.OriginalDefinition;
            return t;
        }

        public static string NamespaceOf(INamedTypeSymbol type) => NamespaceName(Outermost(type).ContainingNamespace);

        public static string NamespaceName(INamespaceSymbol? ns) =>
            ns == null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString();

        /// <summary>True when ns is prefix or inside it ("System.IO" is in "System", "System.IOx" is not).</summary>
        public static bool InNamespace(string ns, string prefix) =>
            string.Equals(ns, prefix, StringComparison.Ordinal) || ns.StartsWith(prefix + ".", StringComparison.Ordinal);

        /// <summary>A property accessor answers with its property's name; everything else with its own.</summary>
        public static string MemberName(ISymbol member) =>
            member is IMethodSymbol m && m.AssociatedSymbol != null ? m.AssociatedSymbol.Name : member.Name;

        public static string MemberKey(ISymbol member) =>
            (member.ContainingType != null ? TypeKey(member.ContainingType) : "") + "." + MemberName(member);

        public static string Display(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

        /// <summary>A comparable identity for the rewrite check.</summary>
        public static string Identity(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                                         + "|" + symbol.Kind;
    }

    public static class SymbolUseCollector
    {
        public static IReadOnlyList<SymbolUse> Collect(SemanticModel model, BlockSyntax body)
        {
            var uses = new List<SymbolUse>();
            var operation = model.GetOperation(body);
            if (operation != null)
                foreach (var op in operation.DescendantsAndSelf())
                    if (!IsHook(op.Syntax)) VisitOperation(op, uses);

            foreach (var node in body.DescendantNodes())
                if (!IsHook(node)) VisitSyntax(model, node, uses);

            return uses;
        }

        /// <summary>One of the host's hook calls (__tick(), __live(), __count(...)) or the hook's own name.
        /// A hook's ARGUMENTS are the model's code (__count wraps a LINQ call) and are still visited.</summary>
        private static bool IsHook(SyntaxNode? node)
        {
            for (var n = node; n != null && !(n is BlockSyntax b && b.Parent is MethodDeclarationSyntax); n = n.Parent)
            {
                if (n is ArgumentSyntax && n.Parent?.Parent is InvocationExpressionSyntax owner && IsHookCall(owner)) return false;
                if (n is InvocationExpressionSyntax inv && IsHookCall(inv)) return true;
                if (n is IdentifierNameSyntax name && name.Identifier.ValueText.StartsWith("__", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool IsHookCall(InvocationExpressionSyntax inv) =>
            inv.Expression is IdentifierNameSyntax id && id.Identifier.ValueText.StartsWith("__", StringComparison.Ordinal);

        /// <summary>A UI request the host queues and applies after the run (policy ui_shim_members, ruling R59).</summary>
        public static bool IsUiShim(IMethodSymbol? method, CodePolicySet? policy = null) =>
            method?.ContainingType != null && (policy ?? CodePolicySet.Default).UiShimMembers.Contains(SymbolKeys.MemberKey(method));

        /// <summary>A System.Linq.Enumerable call that returns a lazy IEnumerable&lt;T&gt;: the rewriter counts
        /// its elements through __count.</summary>
        public static bool IsCountedLinq(IMethodSymbol? method)
        {
            if (method == null) return false;
            var m = method.ReducedFrom ?? method;
            return m.ContainingType != null && SymbolKeys.TypeKey(m.ContainingType) == "System.Linq.Enumerable"
                   && m.ReturnType is INamedTypeSymbol r && r.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T;
        }

        private static void VisitOperation(IOperation op, List<SymbolUse> uses)
        {
            var at = op.Syntax.GetLocation();

            // nameof's operand never runs and binds as OperationKind.None; the syntax pass still checks
            // the symbol it names, so nameof(System.IO.File) stays denied.
            if (op.Kind == OperationKind.None && !(op.Parent is INameOfOperation))
            {
                uses.Add(new SymbolUse(SymbolUseKind.Structural, null, MemberUsage.Read, at, "unsupported_operation", Short(op.Syntax)));
                return;
            }

            switch (op)
            {
                case IInvalidOperation _:
                    return;

                case IDynamicInvocationOperation _:
                case IDynamicMemberReferenceOperation _:
                case IDynamicObjectCreationOperation _:
                case IDynamicIndexerAccessOperation _:
                    uses.Add(new SymbolUse(SymbolUseKind.Structural, null, MemberUsage.Read, at, "dynamic", Short(op.Syntax)));
                    return;

                case IEventAssignmentOperation ea:
                    uses.Add(new SymbolUse(SymbolUseKind.Structural, (ea.EventReference as IEventReferenceOperation)?.Event, MemberUsage.Write, at, "event_reference", Short(op.Syntax)));
                    break;

                case IEventReferenceOperation er:
                    uses.Add(new SymbolUse(SymbolUseKind.Structural, er.Event, MemberUsage.Read, at, "event_reference", Short(op.Syntax)));
                    break;

                case IInstanceReferenceOperation ir when ir.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance:
                    // Implicit receivers (a local-function method group's closure) are the compiler's, not the model's.
                    if (!ir.IsImplicit)
                        uses.Add(new SymbolUse(SymbolUseKind.Structural, null, MemberUsage.Read, at, "this_reference", Short(op.Syntax)));
                    return;

                case ITypeOfOperation typeOf:
                    AddType(uses, typeOf.TypeOperand, at);
                    if (!TypeOfInRevitArgument(typeOf))
                        uses.Add(new SymbolUse(SymbolUseKind.Structural, typeOf.TypeOperand, MemberUsage.Read, at, "typeof_outside_revit_argument", Short(op.Syntax)));
                    return; // its own type (System.Type) is allowed only there; never checked as a type use

                case IInvocationOperation inv:
                    AddMember(uses, inv.TargetMethod, MemberUsage.Invoke, at);
                    // a?.Concat(b): the counting wrapper cannot go around a ?. binding without changing its meaning.
                    if (IsCountedLinq(inv.TargetMethod) && inv.Syntax is InvocationExpressionSyntax call && call.Expression is MemberBindingExpressionSyntax)
                        uses.Add(new SymbolUse(SymbolUseKind.Structural, inv.TargetMethod, MemberUsage.Invoke, at, "conditional_linq", Short(op.Syntax)));
                    // uidoc?.ShowElements(...): the host shim needs a plain receiver.call(...) to rewrite.
                    if (IsUiShim(inv.TargetMethod) && inv.Syntax is InvocationExpressionSyntax ui && !(ui.Expression is MemberAccessExpressionSyntax))
                        uses.Add(new SymbolUse(SymbolUseKind.Structural, inv.TargetMethod, MemberUsage.Invoke, at, "conditional_ui", Short(op.Syntax)));
                    break;

                case IObjectCreationOperation oc:
                    if (oc.Constructor != null) AddMember(uses, oc.Constructor, MemberUsage.Invoke, at);
                    break;

                case IPropertyReferenceOperation pr:
                    AddMember(uses, pr.Property, IsWriteTarget(pr) ? MemberUsage.Write : MemberUsage.Read, at);
                    break;

                case IFieldReferenceOperation fr:
                    AddMember(uses, fr.Field, IsWriteTarget(fr) ? MemberUsage.Write : MemberUsage.Read, at);
                    break;

                case IMethodReferenceOperation mr:
                    AddMember(uses, mr.Method, MemberUsage.Invoke, at);
                    // A method group run as a delegate (Select(Math.Abs)) enters no counted lambda; only the
                    // snippet's own local functions (counted on entry) and Print (counted) may be passed.
                    if (!(mr.Method.MethodKind == MethodKind.LocalFunction
                          || (mr.Method.Name == StatementWrapper.PrintMethod && mr.Method.ContainingType?.Name == StatementWrapper.ClassName)))
                        uses.Add(new SymbolUse(SymbolUseKind.Structural, mr.Method, MemberUsage.Invoke, at, "method_group", Short(op.Syntax)));
                    break;

                case IConversionOperation conv:
                    if (conv.OperatorMethod != null) AddMember(uses, conv.OperatorMethod, MemberUsage.Invoke, at);
                    // An exception that becomes object, string or an interface can become text (Print, + "", Format,
                    // Join...). Reference comparisons (ex != null, ReferenceEquals(ex, null)) produce no text and stay allowed.
                    if (CodePolicy.IsException(conv.Operand.Type as INamedTypeSymbol) && !CodePolicy.IsException(conv.Type as INamedTypeSymbol)
                        && !IsReferenceComparison(conv))
                        uses.Add(new SymbolUse(SymbolUseKind.Structural, conv.Operand.Type, MemberUsage.Read, at, "exception_to_text", Short(op.Syntax)));
                    break;

                case IInterpolationOperation interp when CodePolicy.IsException(interp.Expression.Type as INamedTypeSymbol):
                    uses.Add(new SymbolUse(SymbolUseKind.Structural, interp.Expression.Type, MemberUsage.Read, at, "exception_to_text", Short(op.Syntax)));
                    break;

                case IBinaryOperation bin when bin.OperatorMethod != null:
                    AddMember(uses, bin.OperatorMethod, MemberUsage.Invoke, at);
                    break;

                case IUnaryOperation un when un.OperatorMethod != null:
                    AddMember(uses, un.OperatorMethod, MemberUsage.Invoke, at);
                    break;

                case ICompoundAssignmentOperation ca when ca.OperatorMethod != null:
                    AddMember(uses, ca.OperatorMethod, MemberUsage.Invoke, at);
                    break;

                case IIncrementOrDecrementOperation inc when inc.OperatorMethod != null:
                    AddMember(uses, inc.OperatorMethod, MemberUsage.Invoke, at);
                    break;

                case IVariableDeclaratorOperation decl:
                    AddType(uses, decl.Symbol.Type, at);
                    break;

                case ICatchClauseOperation cc:
                    AddType(uses, cc.ExceptionType, at);
                    break;

                case IDeclarationPatternOperation dp:
                    AddType(uses, dp.MatchedType, at);
                    ExceptionNarrowing(uses, dp, at);
                    break;

                case ITypePatternOperation tp:
                    AddType(uses, tp.MatchedType, at);
                    ExceptionNarrowing(uses, tp, at);
                    break;

                case IRecursivePatternOperation rp:
                    AddType(uses, rp.MatchedType, at);
                    ExceptionNarrowing(uses, rp, at);
                    if (rp.DeconstructSymbol != null) AddMember(uses, rp.DeconstructSymbol, MemberUsage.Invoke, at);
                    break;

                case ISizeOfOperation so:
                    AddType(uses, so.TypeOperand, at);
                    break;

                case ILocalFunctionOperation lf:
                    AddSignature(uses, lf.Symbol, at);
                    break;

                case IAnonymousFunctionOperation af:
                    AddSignature(uses, af.Symbol, at);
                    break;

                case IUsingOperation u:
                    CheckUsingResource(u.Resources, uses, at);
                    break;

                case IUsingDeclarationOperation ud:
                    CheckUsingResource(ud.DeclarationGroup, uses, at);
                    break;

                case IWithOperation w when w.CloneMethod != null:
                    AddMember(uses, w.CloneMethod, MemberUsage.Invoke, at);
                    break;

                case IRangeOperation range when range.Method != null:
                    AddMember(uses, range.Method, MemberUsage.Invoke, at);
                    break;
            }

            if (op.Type != null) AddType(uses, op.Type, at);
        }

        private static void VisitSyntax(SemanticModel model, SyntaxNode node, List<SymbolUse> uses)
        {
            var at = node.GetLocation();

            switch (node)
            {
                case CommonForEachStatementSyntax fe:
                    var info = model.GetForEachStatementInfo(fe);
                    if (info.GetEnumeratorMethod != null) AddMember(uses, info.GetEnumeratorMethod, MemberUsage.Invoke, at);
                    if (info.MoveNextMethod != null) AddMember(uses, info.MoveNextMethod, MemberUsage.Invoke, at);
                    if (info.CurrentProperty != null) AddMember(uses, info.CurrentProperty, MemberUsage.Read, at);
                    // info.DisposeMethod is skipped: foreach disposes its own enumerator, never a host object.
                    AddType(uses, info.ElementType, at);
                    if (fe is ForEachVariableStatementSyntax fv) AddDeconstruction(uses, model.GetDeconstructionInfo(fv), at);
                    return;

                case AssignmentExpressionSyntax assign when assign.Left is TupleExpressionSyntax || assign.Left is DeclarationExpressionSyntax:
                    AddDeconstruction(uses, model.GetDeconstructionInfo(assign), at);
                    break;

                case QueryClauseSyntax clause:
                    var q = model.GetQueryClauseInfo(clause);
                    if (q.CastInfo.Symbol != null) AddMember(uses, q.CastInfo.Symbol, MemberUsage.Invoke, at);
                    if (q.OperationInfo.Symbol != null) AddMember(uses, q.OperationInfo.Symbol, MemberUsage.Invoke, at);
                    return;

                case TypeOfExpressionSyntax _:
                    return; // the operation pass decides where typeof may appear

                case OrderingSyntax _:
                case SelectOrGroupClauseSyntax _:
                    var s = model.GetSymbolInfo(node).Symbol;
                    if (s != null) AddMember(uses, s, MemberUsage.Invoke, at);
                    return;
            }

            if (!(node is ExpressionSyntax) && !(node is TypeSyntax)) return;

            var symbol = model.GetSymbolInfo(node).Symbol;
            switch (symbol)
            {
                case ITypeSymbol t: AddType(uses, t, at); break;
                case IMethodSymbol _:
                case IFieldSymbol _:
                case IEventSymbol _:
                    AddMember(uses, symbol, symbol is IMethodSymbol ? MemberUsage.Invoke : MemberUsage.Read, at);
                    break;
                case IPropertySymbol p:
                    AddMember(uses, p, IsWriteTarget(node) ? MemberUsage.Write : MemberUsage.Read, at);
                    break;
                case ILocalSymbol l: AddType(uses, l.Type, at); break;
                case IParameterSymbol ps: AddType(uses, ps.Type, at); break;
            }

            var typeInfo = model.GetTypeInfo(node);
            AddType(uses, typeInfo.Type, at);
            AddType(uses, typeInfo.ConvertedType, at);
        }

        /// <summary>The host owns doc, uidoc and view (and every Document, UIDocument and View wrapper):
        /// a using block on one would dispose it under the host. Recognised by name: this file is Revit-free.</summary>
        private static void CheckUsingResource(IOperation? resources, List<SymbolUse> uses, Location at)
        {
            if (resources == null) return;
            var types = new List<ITypeSymbol?>();
            if (resources is IVariableDeclarationGroupOperation group)
                types.AddRange(group.Declarations.SelectMany(d => d.Declarators).Select(d => (ITypeSymbol?)d.Symbol.Type));
            else
                types.Add(resources is IConversionOperation conv ? conv.Operand.Type : resources.Type);
            foreach (var t in types)
                if (t is INamedTypeSymbol n && IsHostOwned(n))
                    uses.Add(new SymbolUse(SymbolUseKind.Structural, n, MemberUsage.Write, at, "dispose_host_object", "using on " + n.Name));
        }

        private static bool IsHostOwned(INamedTypeSymbol type)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var key = SymbolKeys.TypeKey(t);
                if (key == "Autodesk.Revit.DB.Document" || key == "Autodesk.Revit.UI.UIDocument" || key == "Autodesk.Revit.DB.View")
                    return true;
            }
            return false;
        }

        /// <summary>ex == null, ex != other, or an argument of object.ReferenceEquals: a bool, never text.</summary>
        private static bool IsReferenceComparison(IConversionOperation conv)
        {
            if (conv.Parent is IBinaryOperation cmp)
                return cmp.OperatorKind == BinaryOperatorKind.Equals || cmp.OperatorKind == BinaryOperatorKind.NotEquals;
            return conv.Parent is IArgumentOperation arg && arg.Parent is IInvocationOperation call
                && call.TargetMethod.Name == nameof(ReferenceEquals) && call.TargetMethod.IsStatic
                && call.TargetMethod.ContainingType?.SpecialType == SpecialType.System_Object;
        }

        /// <summary>ex is object o / case object o: the exception escapes the exception types without a conversion.</summary>
        private static void ExceptionNarrowing(List<SymbolUse> uses, IPatternOperation pattern, Location at)
        {
            var declared = pattern switch
            {
                IDeclarationPatternOperation d => (d.DeclaredSymbol as ILocalSymbol)?.Type ?? d.MatchedType,
                IRecursivePatternOperation r => (r.DeclaredSymbol as ILocalSymbol)?.Type ?? r.MatchedType,
                ITypePatternOperation t => t.MatchedType,
                _ => null,
            };
            if (declared != null && CodePolicy.IsException(pattern.InputType as INamedTypeSymbol) && !CodePolicy.IsException(declared as INamedTypeSymbol))
                uses.Add(new SymbolUse(SymbolUseKind.Structural, pattern.InputType, MemberUsage.Read, at, "exception_to_text", Short(pattern.Syntax)));
        }

        private static void AddDeconstruction(List<SymbolUse> uses, DeconstructionInfo info, Location at)
        {
            if (info.Method != null) AddMember(uses, info.Method, MemberUsage.Invoke, at);
            if (info.Conversion is { } c && c.MethodSymbol != null) AddMember(uses, c.MethodSymbol, MemberUsage.Invoke, at);
            foreach (var nested in info.Nested) AddDeconstruction(uses, nested, at);
        }

        private static void AddSignature(List<SymbolUse> uses, IMethodSymbol method, Location at)
        {
            if (!method.ReturnsVoid) AddType(uses, method.ReturnType, at);
            foreach (var p in method.Parameters) AddType(uses, p.Type, at);
        }

        private static void AddType(List<SymbolUse> uses, ITypeSymbol? type, Location at)
        {
            if (type == null) return;
            uses.Add(new SymbolUse(SymbolUseKind.Type, type, MemberUsage.Read, at));
        }

        private static void AddMember(List<SymbolUse> uses, ISymbol member, MemberUsage usage, Location at)
        {
            if (member is IMethodSymbol m && m.ReducedFrom != null)
            {
                // Extension call in reduced form: check the static method on its class, with the same type arguments.
                var full = m.ReducedFrom;
                member = full.IsGenericMethod && m.TypeArguments.Length == full.TypeParameters.Length
                    ? full.Construct(m.TypeArguments.ToArray())
                    : full;
            }
            uses.Add(new SymbolUse(SymbolUseKind.Member, member, usage, at));
        }

        private static bool TypeOfInRevitArgument(ITypeOfOperation typeOf)
        {
            if (!(typeOf.Parent is IArgumentOperation arg)) return false;
            var target = arg.Parent switch
            {
                IInvocationOperation inv => inv.TargetMethod.ContainingType,
                IObjectCreationOperation oc => oc.Constructor?.ContainingType,
                _ => null,
            };
            return target != null && IsRevitNamespace(target);
        }

        internal static bool IsRevitNamespace(INamedTypeSymbol type) => SymbolKeys.InNamespace(SymbolKeys.NamespaceOf(type), "Autodesk.Revit");

        private static bool IsWriteTarget(IOperation op)
        {
            var target = op;
            var parent = op.Parent;
            while (parent is IConditionalAccessOperation || parent is IParenthesizedOperation)
            {
                target = parent;
                parent = parent.Parent;
            }
            // (w.Name, k) = ("X", 0): an element of a deconstruction target, at any tuple depth (fix round 1, I1).
            while (parent is ITupleOperation tuple)
            {
                target = tuple;
                parent = tuple.Parent;
            }
            return parent switch
            {
                IAssignmentOperation a => ReferenceEquals(a.Target, op) || ReferenceEquals(a.Target, target),
                IIncrementOrDecrementOperation i => ReferenceEquals(i.Target, op),
                IForEachLoopOperation f => ReferenceEquals(f.LoopControlVariable, target),
                IArgumentOperation arg => arg.Parameter?.RefKind == RefKind.Ref || arg.Parameter?.RefKind == RefKind.Out,
                _ => false,
            };
        }

        private static bool IsWriteTarget(SyntaxNode node)
        {
            var n = node;
            while (n.Parent is MemberAccessExpressionSyntax ma && ma.Name == n) n = ma;
            while (n.Parent is ParenthesizedExpressionSyntax paren) n = paren;
            // The same for the syntax pass: climb out of nested tuple elements to the deconstruction.
            while (n.Parent is ArgumentSyntax arg && arg.Parent is TupleExpressionSyntax tuple) n = tuple;
            return n.Parent switch
            {
                AssignmentExpressionSyntax a => a.Left == n,
                ForEachVariableStatementSyntax f => f.Variable == n,
                PrefixUnaryExpressionSyntax p => p.IsKind(SyntaxKind.PreIncrementExpression) || p.IsKind(SyntaxKind.PreDecrementExpression),
                PostfixUnaryExpressionSyntax p => p.IsKind(SyntaxKind.PostIncrementExpression) || p.IsKind(SyntaxKind.PostDecrementExpression),
                _ => false,
            };
        }

        internal static string Short(SyntaxNode node)
        {
            var text = node.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
            return "'" + (text.Length > 60 ? text.Substring(0, 60) + "…" : text) + "'";
        }
    }
}

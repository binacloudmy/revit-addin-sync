#nullable enable
// Read or write? Decided by the add-in from the code itself (spec §7.8), never
// by the request. Revit-free: Revit symbols are recognised by name.
//
// A RevitAPI member makes the call a write when it is
//   - anything in a write namespace (Autodesk.Revit.Creation: doc.Create.*),
//   - a listed write member (Document.Delete, Regenerate, Create, Parameter.Set, ...),
//   - a property or field assignment, or a method whose name starts with a write
//     verb (Set, Create, Delete, Move, Copy, Join, Hide, flipHand, ...),
// unless its type is a detached value object (geometry, filters, options,
// OverrideGraphicSettings, collector, ElementId...: changing those changes
// nothing in the document). Element subclasses are never detached.
//
// The heuristic is conservative and safe both ways: a read taken for a write
// only costs a confirm card whose preview shows no change (noop); a write taken
// for a read runs with no transaction open, so Revit refuses it
// (ModificationOutsideTransaction) and the mapped error tells the model to pass
// `expected`, which forces the write path (WithExpected).

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace BinaVibe.Copilot.Code
{
    public enum CodeMode { Read, Write }

    public sealed class ModeReason
    {
        public ModeReason(string symbol, int line, string why)
        {
            Symbol = symbol;
            Line = line;
            Why = why;
        }

        public string Symbol { get; }
        /// <summary>1-based model line; 0 when not tied to a line (expected changes).</summary>
        public int Line { get; }
        public string Why { get; }

        public override string ToString() => Symbol + (Line > 0 ? " (line " + Line + ")" : "") + ": " + Why;
    }

    public sealed class ModeDecision
    {
        public ModeDecision(CodeMode mode, IReadOnlyList<ModeReason> reasons, bool navigates = false)
        {
            Mode = mode;
            Reasons = reasons;
            Navigates = navigates;
        }

        public CodeMode Mode { get; }

        /// <summary>Why it is a write (empty for a read), first few by line.</summary>
        public IReadOnlyList<ModeReason> Reasons { get; }

        /// <summary>The code moves the active view or zoom (uidoc.RequestViewChange, ShowElements). Not a
        /// model write; a read that navigates runs outside the always-rolled-back group.</summary>
        public bool Navigates { get; }

        public static ModeDecision Read { get; } = new ModeDecision(CodeMode.Read, Array.Empty<ModeReason>());
    }

    /// <summary>The model's own estimate (run_revit_code "expected").</summary>
    public sealed class ExpectedChanges
    {
        public ExpectedChanges(int? adds, int? modifies, int? deletes)
        {
            Adds = adds;
            Modifies = modifies;
            Deletes = deletes;
        }

        public int? Adds { get; }
        public int? Modifies { get; }
        public int? Deletes { get; }

        public bool AnyChange => (Adds ?? 0) > 0 || (Modifies ?? 0) > 0 || (Deletes ?? 0) > 0;
    }

    public static class ModeClassifier
    {
        public const int MaxReasons = 5;

        public static ModeDecision Classify(Compilation compilation, WrappedCode wrapped, CodePolicySet? policy = null)
        {
            policy ??= CodePolicySet.Default;
            if (wrapped.Body == null) return ModeDecision.Read;

            var model = compilation.GetSemanticModel(wrapped.Tree);
            var reasons = new List<ModeReason>();
            var seen = new HashSet<(string, int)>();
            var navigates = false;
            foreach (var use in SymbolUseCollector.Collect(model, wrapped.Body))
            {
                if (use.Kind != SymbolUseKind.Member || use.Symbol == null) continue;
                if (use.Symbol.ContainingType != null && policy.NavigationMembers.Contains(SymbolKeys.MemberKey(use.Symbol))) navigates = true;
                var why = WriteReason(use.Symbol, use.Usage, policy);
                if (why == null) continue;
                var line = wrapped.ModelLine(use.Location.GetLineSpan().StartLinePosition.Line);
                var display = SymbolKeys.Display(use.Symbol);
                if (seen.Add((display, line))) reasons.Add(new ModeReason(display, line, why));
            }

            return reasons.Count == 0
                ? new ModeDecision(CodeMode.Read, Array.Empty<ModeReason>(), navigates)
                : new ModeDecision(CodeMode.Write, reasons.OrderBy(r => r.Line).Take(MaxReasons).ToList(), navigates);
        }

        /// <summary>A model that says it expects changes gets the write path (never the other way round).</summary>
        public static ModeDecision WithExpected(ModeDecision decision, ExpectedChanges? expected)
        {
            if (decision.Mode == CodeMode.Write || expected == null || !expected.AnyChange) return decision;
            return new ModeDecision(CodeMode.Write, new[] { new ModeReason("expected", 0, "the call says it expects changes") }, decision.Navigates);
        }

        /// <summary>Why this use changes the document, or null.</summary>
        public static string? WriteReason(ISymbol member, MemberUsage usage, CodePolicySet? policy = null)
        {
            policy ??= CodePolicySet.Default;
            var type = member.ContainingType;
            var assembly = type?.ContainingAssembly?.Name;
            if (type == null || assembly == null || !policy.RevitAssemblies.Contains(assembly)) return null;

            var ns = SymbolKeys.NamespaceOf(type);
            if (policy.WriteNamespaces.Any(w => SymbolKeys.InNamespace(ns, w))) return "uses the document creation API";
            if (policy.WriteMembers.Contains(SymbolKeys.MemberKey(member))) return "changes the document";
            if (IsDetached(type, policy)) return null;

            switch (member)
            {
                case IPropertySymbol _:
                    return usage == MemberUsage.Write ? "sets a property" : null;
                case IFieldSymbol f:
                    return usage == MemberUsage.Write && !f.IsConst ? "sets a field" : null;
                case IMethodSymbol m:
                    if (m.MethodKind == MethodKind.PropertySet) return "sets a property";
                    if (m.MethodKind != MethodKind.Ordinary) return null;
                    return StartsWithVerb(m.Name, policy.WriteVerbs) ? "calls a method that changes elements" : null;
                default:
                    return null;
            }
        }

        public static bool IsDetached(INamedTypeSymbol type, CodePolicySet? policy = null)
        {
            policy ??= CodePolicySet.Default;
            if (policy.DetachedTypes.Contains(SymbolKeys.TypeKey(type))) return true;
            if (DerivesFromElement(type)) return false;
            var name = type.OriginalDefinition.Name;
            return policy.DetachedSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));
        }

        private static bool DerivesFromElement(INamedTypeSymbol type)
        {
            for (var t = type.OriginalDefinition; t != null; t = t.BaseType?.OriginalDefinition)
                if (SymbolKeys.TypeKey(t) == "Autodesk.Revit.DB.Element") return true;
            return false;
        }

        /// <summary>"SetValueString" and "flipHand" start with a verb; "Settings" and "Address" do not.</summary>
        public static bool StartsWithVerb(string name, IEnumerable<string> verbs)
        {
            foreach (var verb in verbs)
            {
                if (name.Length < verb.Length || !name.StartsWith(verb, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Length == verb.Length) return true;
                var next = name[verb.Length];
                if (char.IsUpper(next) || char.IsDigit(next) || next == '_') return true;
            }
            return false;
        }
    }
}

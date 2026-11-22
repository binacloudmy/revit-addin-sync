#nullable enable
// AI-dc1: renames many types in one write without letting one bad type undo the others. Revit-free.
//
// Before: the first type Revit refused threw out of SetParametersTool.Execute, the runner rolled the whole transaction
// back, and the drafter saw "0/9 done · 8 failed (rolled back by Revit)". Now each rename runs on its own (the Revit side
// wraps each one in a SubTransaction), a type that fails is skipped with the real exception text, and the others stay.

using System;
using System.Collections.Generic;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>One planned rename.</summary>
    public readonly struct TypeRenameItem
    {
        public TypeRenameItem(long id, string before, string name) { Id = id; Before = before; Name = name; }
        public long Id { get; }
        public string Before { get; }
        public string Name { get; }
    }

    /// <summary>A rename Revit refused: which type, what name it was given, and why (exception type and message).</summary>
    public sealed class TypeRenameFailure
    {
        public TypeRenameFailure(TypeRenameItem item, string reason) { Item = item; Reason = reason; }
        public TypeRenameItem Item { get; }
        public string Reason { get; }

        /// <summary>One line for the receipt: "Type 'A' could not be renamed to 'B': reason".</summary>
        public string Text => "Type '" + Item.Before + "' could not be renamed to '" + Item.Name + "': " + Reason;
    }

    public sealed class TypeRenameBatchResult
    {
        public List<TypeRenameItem> Done { get; } = new();
        public List<TypeRenameFailure> Failed { get; } = new();
    }

    public static class TypeRenameBatch
    {
        /// <summary>Runs <paramref name="apply"/> for each item. A throw fails only that item. <paramref name="apply"/> must
        /// undo its own partial work before it throws (the Revit side rolls its SubTransaction back).</summary>
        public static TypeRenameBatchResult Run(IEnumerable<TypeRenameItem> items, Action<TypeRenameItem> apply)
        {
            var result = new TypeRenameBatchResult();
            foreach (var item in items)
            {
                try
                {
                    apply(item);
                    result.Done.Add(item);
                }
                catch (Exception ex)
                {
                    result.Failed.Add(new TypeRenameFailure(item, Describe(ex)));
                }
            }
            return result;
        }

        /// <summary>The real reason, with the exception type so "external exception" style errors say what they are.</summary>
        public static string Describe(Exception ex)
        {
            var msg = (ex.Message ?? "").Trim();
            var type = ex.GetType().Name;
            return msg.Length == 0 ? type : type + ": " + msg;
        }

        /// <summary>The summary clause for failures: the first reason and the count.</summary>
        public static string FailureSummary(IReadOnlyList<TypeRenameFailure> failed)
        {
            if (failed.Count == 0) return "";
            var first = failed[0].Text;
            return failed.Count == 1 ? first : first + " (and " + (failed.Count - 1) + " more)";
        }
    }
}

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
        /// <summary>Items never tried because an earlier one threw a native (SEH-class) exception that can leave the document
        /// unusable; what committed before it stays.</summary>
        public List<TypeRenameItem> NotAttempted { get; } = new();
        /// <summary>The native exception that stopped the batch, or null.</summary>
        public string? StoppedBy { get; set; }
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
                    if (IsPoisonous(ex))
                    {
                        // A native exception (SEHException, access violation) can leave Revit's document state corrupt:
                        // stop here, keep what already committed, and say so (never keep going on a poisoned document).
                        result.StoppedBy = Describe(ex);
                        var seen = false;
                        foreach (var rest in items)
                        {
                            if (seen) result.NotAttempted.Add(rest);
                            else if (rest.Id == item.Id) seen = true;
                        }
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>True for a native-code failure (SEH, access violation, Revit InternalException) that can poison the document.</summary>
        public static bool IsPoisonous(Exception ex) =>
            ex is System.Runtime.InteropServices.SEHException || ex is AccessViolationException ||
            ex.GetType().Name == "InternalException";

        /// <summary>The receipt line for items never tried after a native exception stopped the batch ("" when none).</summary>
        public static string StoppedText(TypeRenameBatchResult r) =>
            r.StoppedBy == null ? "" :
            "Stopped after a native Revit error (" + r.StoppedBy + "): " + r.NotAttempted.Count + " more type(s) were not tried so Revit stays safe; the renames before it are kept. Save, then re-check and run Fix all again for the rest.";

        /// <summary>Runs one item's change; when it throws, rolls back (a rollback that throws too is ignored) and rethrows the ORIGINAL
        /// exception, so its text, not the rollback's, reaches the receipt.</summary>
        public static void ApplyWithRollback(Action change, Action commit, Action rollback)
        {
            try
            {
                change();
                commit();
            }
            catch
            {
                try { rollback(); }
                catch (Exception) { /* the transaction is already invalid; the first exception is the one that matters */ }
                throw;
            }
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

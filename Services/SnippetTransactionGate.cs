using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Decides whether a generated snippet manages its own Revit
    /// <c>Transaction</c>/<c>TransactionGroup</c> (or changes the active view), so
    /// <see cref="CodeExecutor"/> must NOT wrap it in an auto-transaction — a nested
    /// transaction throws the "no open transaction" error this gate exists to avoid.
    ///
    /// Split out of CodeExecutor.cs, which references Revit + Roslyn, so the
    /// detection rules are unit-testable.
    /// </summary>
    public static class SnippetTransactionGate
    {
        // Matches Transaction and TransactionGroup as whole identifiers, and therefore
        // every declaration/construction form the model writes: `new Transaction(...)`,
        // `new Autodesk.Revit.DB.Transaction(...)`, the target-typed
        // `Transaction t = new(doc, ...)`, and `new TransactionGroup(...)`. A bare
        // `Contains("Transaction")` also matched those but additionally fired on
        // unrelated identifiers, while the old `new\s+Transaction\s*\(` regex missed
        // the qualified and target-typed forms.
        private static readonly Regex TransactionType =
            new Regex(@"\bTransaction(Group)?\b", RegexOptions.Compiled);

        private static readonly Regex ActiveViewSetter =
            new Regex(@"\.ActiveView\s*=", RegexOptions.Compiled);

        public static bool SelfManagesTransaction(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            return TransactionType.IsMatch(code)
                || code.Contains("OpenView")
                || code.Contains("RequestViewChange")
                || ActiveViewSetter.IsMatch(code);
        }
    }
}

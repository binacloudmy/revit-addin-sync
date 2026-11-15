// SnippetTransactionGate tests — the decision behind CodeExecutor.WrapCode that
// says "this generated snippet opens its own Revit transaction, so do NOT wrap it
// in an auto-transaction". A false negative here re-introduces the AI-56 bug: the
// auto-wrapper opens __tx and the snippet's own Transaction/TransactionGroup then
// throws the "no open transaction" error. Pure (Regex only) — no Revit, no Roslyn.

using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SnippetTransactionGateTests
    {
        [Theory]
        // The three forms the pre-PR `code.Contains("Transaction")` matched but the
        // `new\s+Transaction\s*\(` regex regressed on:
        [InlineData("var tx = new Transaction(doc, \"x\");")]
        [InlineData("var tx = new Autodesk.Revit.DB.Transaction(doc, \"x\");")]
        [InlineData("Transaction t = new(doc, \"x\");")]
        // TransactionGroup (also missed by the narrow regex):
        [InlineData("var tg = new TransactionGroup(doc, \"x\");")]
        [InlineData("var tg = new Autodesk.Revit.DB.TransactionGroup(doc, \"x\");")]
        // Fully-qualified declaration, no construction on the same line:
        [InlineData("Autodesk.Revit.DB.Transaction tx = GetTransaction();")]
        // The active-view changers that must never ride an open transaction:
        [InlineData("uidoc.ActiveView = view;")]
        [InlineData("uidoc.ActiveView=view;")]
        [InlineData("RequestViewChange(view);")]
        [InlineData("OpenView(view);")]
        public void SelfManagesTransaction_True_ForEveryManagerForm(string code)
        {
            Assert.True(SnippetTransactionGate.SelfManagesTransaction(code));
        }

        [Theory]
        [InlineData("var e = doc.GetElement(id); return e.Name;")]
        [InlineData("var ids = new List<ElementId>();")]
        // Whole-word match: an unrelated identifier containing the substring must not trip it.
        [InlineData("myTransactionCounter++;")]
        public void SelfManagesTransaction_False_ForReadOnlySnippets(string code)
        {
            Assert.False(SnippetTransactionGate.SelfManagesTransaction(code));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void SelfManagesTransaction_False_ForNullOrEmpty(string code)
        {
            Assert.False(SnippetTransactionGate.SelfManagesTransaction(code));
        }
    }
}

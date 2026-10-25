#nullable enable
// Stub created by W0; body owned by W1
// Marks which of our own callbacks is running against a document, so
// ChangeRecorder only records inside our scopes and ExternalRevision only
// counts what happens outside them.
//
//   using (CallbackScope.Enter(doc, CallbackKind.Preview)) { ... group, batch, RollBack ... }
//
// Enter and dispose on Revit's UI thread, around the whole callback including
// its rollback, so our own events never move the external revision. The rules
// live in the Revit-free CallbackScopeTable (CallbackKind is declared there).

using System;

namespace BinaVibe.Copilot.Guard
{
    public static class CallbackScope
    {
        private static readonly CallbackScopeTable Table = new CallbackScopeTable();

        public static IDisposable Enter(Autodesk.Revit.DB.Document doc, CallbackKind kind)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return Table.Enter(doc, kind);
        }

        public static CallbackKind? Current(Autodesk.Revit.DB.Document doc) =>
            doc == null ? null : Table.Current(doc);
    }
}

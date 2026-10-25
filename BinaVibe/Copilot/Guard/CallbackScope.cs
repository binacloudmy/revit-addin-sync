#nullable enable
// Stub created by W0; body owned by W1
// Marks which of our own callbacks is running against a document, so
// ChangeRecorder only records inside our scopes and ExternalRevision only
// counts what happens outside them.

namespace BinaVibe.Copilot.Guard
{
    using System;

    public enum CallbackKind { Preview, Read, Look, Commit }

    public static class CallbackScope
    {
        public static IDisposable Enter(Autodesk.Revit.DB.Document doc, CallbackKind kind) =>
            throw new NotImplementedException("W1");

        public static CallbackKind? Current(Autodesk.Revit.DB.Document doc) =>
            throw new NotImplementedException("W1");
    }
}

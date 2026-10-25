#nullable enable
// Stub created by W0; body owned by W1
// Records DocumentChanged for one document while a CallbackScope is set.

using System;

namespace BinaVibe.Copilot.Guard
{
    public sealed class ChangeRecording : IDisposable
    {
        public ChangeSet Stop() => throw new NotImplementedException("W1");

        public void Dispose() => throw new NotImplementedException("W1");
    }

    public static class ChangeRecorder
    {
        public static ChangeRecording Start(Autodesk.Revit.DB.Document doc) => throw new NotImplementedException("W1");
    }
}

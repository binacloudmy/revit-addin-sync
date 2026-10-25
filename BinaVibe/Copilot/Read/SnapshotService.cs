#nullable enable
// Stub created by W0; body owned by W2
// Keeps the context snapshot current from Revit events (SelectionChanged,
// ViewActivated, DocumentChanged, DocumentOpened/Closed) and fingerprints the
// active document so a write can fail doc_changed when it moved.

namespace BinaVibe.Copilot.Read
{
    using System;
    using BinaVibe.Copilot.Contracts;

    public static class SnapshotService
    {
        public static void Attach(Autodesk.Revit.UI.UIControlledApplication app) => throw new NotImplementedException("W2");

        public static SnapshotModel Current() => throw new NotImplementedException("W2");

        public static string DocFingerprint(Autodesk.Revit.DB.Document doc) => throw new NotImplementedException("W2");
    }
}

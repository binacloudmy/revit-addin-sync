#nullable enable
// Stub created by W0; body owned by W3
// One Undo per turn (spec §7.6): posts Revit's Undo for the turn's "BINA: …"
// group when nothing else committed on top of it.

using System;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Undo
{
    public static class UndoService
    {
        public static void Attach(Autodesk.Revit.UI.UIControlledApplication app) => throw new NotImplementedException("W3");

        public static bool CanUndoTurn(string turnId) => throw new NotImplementedException("W3");

        public static System.Threading.Tasks.Task<Envelope> UndoTurnAsync(string turnId, System.Threading.CancellationToken ct) =>
            throw new NotImplementedException("W3");
    }
}

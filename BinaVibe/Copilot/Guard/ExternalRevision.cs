#nullable enable
// Stub created by W0; body owned by W1
// Counts commits, undos and redos raised OUTSIDE CallbackScope (spec §7.4):
// if the count moved between preview and commit, the drafter changed the
// model in between and the preview no longer holds.

using System;

namespace BinaVibe.Copilot.Guard
{
    public static class ExternalRevision
    {
        public static void Attach(Autodesk.Revit.ApplicationServices.Application app) =>
            throw new NotImplementedException("W1");

        /// <summary>Commits/undos/redos on this document outside our scopes.</summary>
        public static long Get(Autodesk.Revit.DB.Document doc) => throw new NotImplementedException("W1");
    }
}

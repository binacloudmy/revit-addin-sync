#nullable enable
// Stub created by W0; body owned by W1
// Revit-free. Turns the existing BinaVibe.Mcp tools' Dictionary results into
// contract envelopes, so the new tools can reuse them: ok:false / error /
// status:not_implemented become failed, counts become count, the raw
// dictionary stays in data.

using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Envelopes
{
    public static class LegacyResultAdapter
    {
        public static Envelope ToEnvelope(Dictionary<string, object?> legacy, WritePhase? phase) =>
            throw new NotImplementedException("W1");
    }

    public static class ResultSemantics
    {
        public static bool IsFailure(Dictionary<string, object?> legacy) => throw new NotImplementedException("W1");
    }
}

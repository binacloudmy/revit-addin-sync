#nullable enable
// Stub created by W0; body owned by W1
// Revit-free. Turns the existing BinaVibe.Mcp tools' Dictionary results into
// contract envelopes, so the new tools can reuse them: ok:false / error /
// status:not_implemented become failed, counts become count, the raw
// dictionary stays in data.

namespace BinaVibe.Copilot.Envelope
{
    // Inside the namespace on purpose: this namespace is itself named
    // Envelope, which hides the Envelope type from usings at the top of a file.
    using System;
    using System.Collections.Generic;
    using BinaVibe.Copilot.Contracts;

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

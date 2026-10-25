#nullable enable
// Stub created by W0; body owned by W1
// Revit-free fluent builder for contract envelopes. Build() enforces the §2.1
// invariants (EnvelopeInvariants.Violations): throws in DEBUG, logs in RELEASE.

namespace BinaVibe.Copilot.Envelope
{
    // Inside the namespace on purpose: this namespace is itself named
    // Envelope, which hides the Envelope type from usings at the top of a file.
    using System;
    using System.Collections.Generic;
    using BinaVibe.Copilot.Contracts;

    public sealed class EnvelopeBuilder
    {
        public static EnvelopeBuilder Read(string summary) => throw new NotImplementedException("W1");

        public static EnvelopeBuilder Write(WritePhase phase, string summary) => throw new NotImplementedException("W1");

        public EnvelopeBuilder Status(string status) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithData(object? data) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithCount(int count) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithHandle(string handle) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithSearched(SearchedDto searched) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithChanges(ChangesDto touched, SideEffectsDto? sideEffects = null) =>
            throw new NotImplementedException("W1");

        public EnvelopeBuilder WithVerified(VerifiedDto verified) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithWarnings(IEnumerable<WarningDto> warnings) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithDialogs(IEnumerable<DialogDto> dialogs) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithHighlight(int selected, int visibleInView) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithImage(ImageDto image) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithPlanHash(string planHash) => throw new NotImplementedException("W1");

        public EnvelopeBuilder WithNext(string next) => throw new NotImplementedException("W1");

        /// <summary>Enforces the §2.1 invariants.</summary>
        public Envelope Build() => throw new NotImplementedException("W1");

        public static Envelope Fail(string code, string message, IEnumerable<string>? candidates = null, WritePhase? phase = null) =>
            throw new NotImplementedException("W1");
    }
}

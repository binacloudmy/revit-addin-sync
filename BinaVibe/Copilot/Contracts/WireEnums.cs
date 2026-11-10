#nullable enable
// Typed views of the envelope's enum-like strings (AI-298).
//
// The wire keeps plain strings (contracts/copilot/envelope.schema.json lists the
// allowed values; the EnvelopeStatus / EnvelopePhase / ErrorCodes constants
// stay as they are, and JSON does not change). These enums are the pane's
// way to branch on them: parse once, switch on the enum, and a value the add-in
// does not know yet becomes Unknown instead of a silent miss.
//
// Add, never rename: a new wire value gets a new member here, in the constants,
// and in the schema together; EnvelopeEnumTests pins the three against each other.

using System;

namespace BinaVibe.Copilot.Contracts
{
    /// <summary>Envelope <c>status</c>.</summary>
    public enum EnvelopeStatusKind { Unknown = 0, Done, Partial, Failed, Rejected, Noop, RevitBusy, Timeout }

    /// <summary>Envelope <c>phase</c>.</summary>
    public enum EnvelopePhaseKind { Unknown = 0, Read, Preview, Commit }

    /// <summary>Envelope <c>error.code</c>.</summary>
    public enum ErrorCodeKind
    {
        Unknown = 0,
        NotFound, NotAllowed, NotOwned, DocChanged, DiffDrift, RevitBusy, PolicyRejected,
        CompileError, RuntimeError, Timeout, InvalidArgs, RolledBack, MayHaveApplied, NoGpu, NotImplemented,
    }

    /// <summary>The <c>mode</c> a run_revit_code envelope reports in its data.</summary>
    public enum CodeMode { Unknown = 0, Read, Write }

    public static class WireEnums
    {
        public static EnvelopeStatusKind ParseStatus(string? wire) => wire switch
        {
            EnvelopeStatus.Done => EnvelopeStatusKind.Done,
            EnvelopeStatus.Partial => EnvelopeStatusKind.Partial,
            EnvelopeStatus.Failed => EnvelopeStatusKind.Failed,
            EnvelopeStatus.Rejected => EnvelopeStatusKind.Rejected,
            EnvelopeStatus.Noop => EnvelopeStatusKind.Noop,
            EnvelopeStatus.RevitBusy => EnvelopeStatusKind.RevitBusy,
            EnvelopeStatus.Timeout => EnvelopeStatusKind.Timeout,
            _ => EnvelopeStatusKind.Unknown,
        };

        public static string ToWire(EnvelopeStatusKind kind) => kind switch
        {
            EnvelopeStatusKind.Done => EnvelopeStatus.Done,
            EnvelopeStatusKind.Partial => EnvelopeStatus.Partial,
            EnvelopeStatusKind.Failed => EnvelopeStatus.Failed,
            EnvelopeStatusKind.Rejected => EnvelopeStatus.Rejected,
            EnvelopeStatusKind.Noop => EnvelopeStatus.Noop,
            EnvelopeStatusKind.RevitBusy => EnvelopeStatus.RevitBusy,
            EnvelopeStatusKind.Timeout => EnvelopeStatus.Timeout,
            _ => "",
        };

        public static EnvelopePhaseKind ParsePhase(string? wire) => wire switch
        {
            EnvelopePhase.Read => EnvelopePhaseKind.Read,
            EnvelopePhase.Preview => EnvelopePhaseKind.Preview,
            EnvelopePhase.Commit => EnvelopePhaseKind.Commit,
            _ => EnvelopePhaseKind.Unknown,
        };

        public static string ToWire(EnvelopePhaseKind kind) => kind switch
        {
            EnvelopePhaseKind.Read => EnvelopePhase.Read,
            EnvelopePhaseKind.Preview => EnvelopePhase.Preview,
            EnvelopePhaseKind.Commit => EnvelopePhase.Commit,
            _ => "",
        };

        public static ErrorCodeKind ParseError(string? wire) => wire switch
        {
            ErrorCodes.NotFound => ErrorCodeKind.NotFound,
            ErrorCodes.NotAllowed => ErrorCodeKind.NotAllowed,
            ErrorCodes.NotOwned => ErrorCodeKind.NotOwned,
            ErrorCodes.DocChanged => ErrorCodeKind.DocChanged,
            ErrorCodes.DiffDrift => ErrorCodeKind.DiffDrift,
            ErrorCodes.RevitBusy => ErrorCodeKind.RevitBusy,
            ErrorCodes.PolicyRejected => ErrorCodeKind.PolicyRejected,
            ErrorCodes.CompileError => ErrorCodeKind.CompileError,
            ErrorCodes.RuntimeError => ErrorCodeKind.RuntimeError,
            ErrorCodes.Timeout => ErrorCodeKind.Timeout,
            ErrorCodes.InvalidArgs => ErrorCodeKind.InvalidArgs,
            ErrorCodes.RolledBack => ErrorCodeKind.RolledBack,
            ErrorCodes.MayHaveApplied => ErrorCodeKind.MayHaveApplied,
            ErrorCodes.NoGpu => ErrorCodeKind.NoGpu,
            ErrorCodes.NotImplemented => ErrorCodeKind.NotImplemented,
            _ => ErrorCodeKind.Unknown,
        };

        public static string ToWire(ErrorCodeKind kind) => kind switch
        {
            ErrorCodeKind.NotFound => ErrorCodes.NotFound,
            ErrorCodeKind.NotAllowed => ErrorCodes.NotAllowed,
            ErrorCodeKind.NotOwned => ErrorCodes.NotOwned,
            ErrorCodeKind.DocChanged => ErrorCodes.DocChanged,
            ErrorCodeKind.DiffDrift => ErrorCodes.DiffDrift,
            ErrorCodeKind.RevitBusy => ErrorCodes.RevitBusy,
            ErrorCodeKind.PolicyRejected => ErrorCodes.PolicyRejected,
            ErrorCodeKind.CompileError => ErrorCodes.CompileError,
            ErrorCodeKind.RuntimeError => ErrorCodes.RuntimeError,
            ErrorCodeKind.Timeout => ErrorCodes.Timeout,
            ErrorCodeKind.InvalidArgs => ErrorCodes.InvalidArgs,
            ErrorCodeKind.RolledBack => ErrorCodes.RolledBack,
            ErrorCodeKind.MayHaveApplied => ErrorCodes.MayHaveApplied,
            ErrorCodeKind.NoGpu => ErrorCodes.NoGpu,
            ErrorCodeKind.NotImplemented => ErrorCodes.NotImplemented,
            _ => "",
        };

        public static CodeMode ParseMode(string? wire) =>
            string.Equals(wire, "write", StringComparison.OrdinalIgnoreCase) ? CodeMode.Write
            : string.Equals(wire, "read", StringComparison.OrdinalIgnoreCase) ? CodeMode.Read
            : CodeMode.Unknown;
    }

    public static class EnvelopeKinds
    {
        public static EnvelopeStatusKind StatusKind(this Envelope env) => WireEnums.ParseStatus(env?.Status);
        public static EnvelopePhaseKind PhaseKind(this Envelope env) => WireEnums.ParsePhase(env?.Phase);
        public static ErrorCodeKind CodeKind(this ErrorDto? error) => WireEnums.ParseError(error?.Code);

        /// <summary>The envelope's error code, or Unknown when it has no error.</summary>
        public static ErrorCodeKind ErrorKind(this Envelope env) => WireEnums.ParseError(env?.Error?.Code);
    }
}

#nullable enable
// Copilot tool-host contracts, Revit-free part (plan §4 Task 0.2; owned by W0).
// The Revit-bound part (ToolContext, ICopilotTool, IWriteTool) is in
// ToolContext.cs, so everything here links into Tests and the scratch harness.
//
// Signatures are normative for W1-W6: fill bodies elsewhere, never change these.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BinaVibe.Copilot.Contracts
{
    /// <summary>The 9 model-facing tools (spec §11), in tools.json order.</summary>
    public static class ToolNames
    {
        public const string ModelOverview = "model_overview";
        public const string FindElements = "find_elements";
        public const string Inspect = "inspect";
        public const string Check = "check";
        public const string SetParameters = "set_parameters";
        public const string Look = "look";
        public const string RunRevitCode = "run_revit_code";
        public const string Lookup = "lookup";
        public const string AskUser = "ask_user";

        public const int DescriptionMaxChars = 600;

        public static IReadOnlyList<string> All { get; } = new[]
        {
            ModelOverview, FindElements, Inspect, Check, SetParameters, Look, RunRevitCode, Lookup, AskUser,
        };

        /// <summary>The 7 tools the add-in runs (lookup runs in the cloud, ask_user in the pane).</summary>
        public static IReadOnlyList<string> AddIn { get; } = new[]
        {
            ModelOverview, FindElements, Inspect, Check, SetParameters, Look, RunRevitCode,
        };

        /// <summary>The tools.json kind: read | write | code | server | pane; null for any other name.</summary>
        public static string? KindOf(string? name) => name switch
        {
            ModelOverview or FindElements or Inspect or Check or Look => "read",
            SetParameters => "write",
            RunRevitCode => "code",
            Lookup => "server",
            AskUser => "pane",
            _ => null,
        };
    }

    public enum ToolKind { Read, Write, Code }

    public enum WritePhase { Preview, Commit }

    /// <summary>A write call's targets and intent, resolved BEFORE any change (W3 hashes these).</summary>
    public sealed class WritePlan
    {
        public string Tool = "";
        public string CanonicalArgs = "";
        public long[] TargetIds = Array.Empty<long>();
        public Dictionary<string, string> IntendedValues = new();
        public Dictionary<string, int> ExpectedAddedByCategory = new();
        public Dictionary<string, int> ExpectedDeletedByCategory = new();
        public string? CodeText;
        public bool Previewable = true;
    }

    public interface IHandleStore
    {
        string Put(IReadOnlyList<ElementRef> refs, string label);
        IReadOnlyList<ElementRef>? Get(string handle);
    }

    /// <summary>Runs a protocol-3 read request on Revit's thread. Returns one envelope
    /// per call, in call order.</summary>
    public interface IReadExecutor
    {
        Task<Envelope[]> RunAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct);
    }

    /// <summary>Previews, confirms and commits a protocol-3 write request. Returns one
    /// envelope per call, in call order. The runner owns every transaction.</summary>
    public interface IWriteBatchRunner
    {
        Task<Envelope[]> RunAsync(WriteBatchRequest req, CancellationToken ct);
    }

    public sealed class WriteBatchRequest
    {
        public string TurnId = "";
        public string RequestId = "";
        public IReadOnlyList<ToolCallDto> Calls = Array.Empty<ToolCallDto>();
        public string? Description;
        public SnapshotModel Snapshot = new();
    }

    /// <summary>Shows the add-in's own confirm card (spec §7.5); the cloud cannot answer it.</summary>
    public interface IConfirmPresenter
    {
        Task<ConfirmDecision> AskAsync(ConfirmRequest req, CancellationToken ct);
    }

    public sealed class ConfirmRequest
    {
        public string BatchId = "";
        public Envelope[] Preview = Array.Empty<Envelope>();
        public string PlanHash = "";
        public string? AssistantDescription;
        public bool OfferApproveThisBuild;
    }

    public sealed class ConfirmDecision
    {
        public bool Approved;
        public bool ApproveThisBuild;
        public string? Reason;
    }

    /// <summary>Failure envelopes for refusals that happen before a tool runs.
    /// Phase is read: nothing was previewed or committed.</summary>
    public static class EnvelopeFactory
    {
        public static Envelope NotImplemented(string tool) =>
            Failure(ErrorCodes.NotImplemented, Shorten(tool, 64) + " is not implemented in this add-in build.", null);

        public static Envelope NotAllowed(string tool) =>
            Failure(ErrorCodes.NotAllowed, "'" + Shorten(tool, 64) + "' is not an add-in tool that may run here.", null);

        /// <summary>A failed envelope for any <see cref="ErrorCodes"/> code. A not_found
        /// failure always carries candidates (empty when none are given).</summary>
        /// <exception cref="ArgumentException">code is not a contract error code.</exception>
        public static Envelope Failed(string code, string message, string[]? candidates = null)
        {
            if (!ErrorCodes.All.Contains(code))
                throw new ArgumentException("'" + code + "' is not one of " + string.Join("|", ErrorCodes.All), nameof(code));
            return Failure(code, message, candidates);
        }

        private static Envelope Failure(string code, string message, string[]? candidates)
        {
            var text = string.IsNullOrWhiteSpace(message) ? "Failed: " + code + "." : message;
            var list = candidates?.ToList() ?? (code == ErrorCodes.NotFound ? new List<string>() : null);
            return new Envelope
            {
                Ok = false,
                Status = EnvelopeStatus.Failed,
                Phase = EnvelopePhase.Read,
                Summary = Shorten(text, EnvelopeLimits.SummaryMaxChars),
                Error = new ErrorDto { Code = code, Message = text, Candidates = list },
            };
        }

        /// <summary>Cuts to at most max code points, ending in "…" when cut.</summary>
        internal static string Shorten(string? s, int max)
        {
            s ??= "";
            if (EnvelopeInvariants.CodePoints(s) <= max) return s;
            var kept = 0;
            var end = 0;
            while (end < s.Length && kept < max - 1)
            {
                end += char.IsHighSurrogate(s[end]) && end + 1 < s.Length ? 2 : 1;
                kept++;
            }
            return s.Substring(0, end) + "…";
        }
    }
}

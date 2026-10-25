#nullable enable
// Copilot tool-result envelope (spec §10, plan §2.1; owned by W0, contracts).
// Revit-free mirror of contracts/copilot/envelope.schema.json: change both
// together, through the orchestrator. EnvelopeInvariants checks the §2.1 rules.
//
// Wire conventions (contracts/copilot/README.md): snake_case keys via
// [JsonPropertyName]; null means absent (CopilotJson omits null fields);
// enum-like fields are plain strings with the allowed values listed below.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BinaVibe.Copilot.Contracts
{
    public static class EnvelopeStatus
    {
        public const string Done = "done";
        public const string Partial = "partial";
        public const string Failed = "failed";
        public const string Rejected = "rejected";
        public const string Noop = "noop";
        public const string RevitBusy = "revit_busy";
        public const string Timeout = "timeout";

        public static IReadOnlyList<string> All { get; } = new[] { Done, Partial, Failed, Rejected, Noop, RevitBusy, Timeout };
    }

    public static class EnvelopePhase
    {
        public const string Read = "read";
        public const string Preview = "preview";
        public const string Commit = "commit";

        public static IReadOnlyList<string> All { get; } = new[] { Read, Preview, Commit };

        public static string Of(WritePhase phase) => phase == WritePhase.Commit ? Commit : Preview;
    }

    public static class ErrorCodes
    {
        public const string NotFound = "not_found";
        public const string NotAllowed = "not_allowed";
        public const string NotOwned = "not_owned";
        public const string DocChanged = "doc_changed";
        public const string DiffDrift = "diff_drift";
        public const string RevitBusy = "revit_busy";
        public const string PolicyRejected = "policy_rejected";
        public const string CompileError = "compile_error";
        public const string RuntimeError = "runtime_error";
        public const string Timeout = "timeout";
        public const string InvalidArgs = "invalid_args";
        public const string RolledBack = "rolled_back";
        public const string NoGpu = "no_gpu";
        public const string NotImplemented = "not_implemented";

        public static IReadOnlyList<string> All { get; } = new[]
        {
            NotFound, NotAllowed, NotOwned, DocChanged, DiffDrift, RevitBusy, PolicyRejected,
            CompileError, RuntimeError, Timeout, InvalidArgs, RolledBack, NoGpu, NotImplemented,
        };
    }

    public static class VerifyMethods
    {
        public const string ParamReadback = "param_readback";
        public const string Exists = "exists";
        public const string CountDelta = "count_delta";
        public const string None = "none";

        public static IReadOnlyList<string> All { get; } = new[] { ParamReadback, Exists, CountDelta, None };
    }

    public static class WarningSeverity
    {
        public const string Warning = "warning";
        public const string Error = "error";

        public static IReadOnlyList<string> All { get; } = new[] { Warning, Error };
    }

    public static class EnvelopeLimits
    {
        public const int SummaryMaxChars = 300;
        public const int NextMaxChars = 200;
        /// <summary>Rows per list in an envelope; more go behind count + handle.</summary>
        public const int MaxRows = 50;
        /// <summary>Long edge of any image, in pixels.</summary>
        public const int ImageMaxPx = 1024;
        public const string ImageMediaType = "image/png";
    }

    /// <summary>What every add-in tool returns (spec §10).</summary>
    public sealed class Envelope
    {
        /// <summary>Reads: ran without error. Writes: committed and verified.</summary>
        [JsonPropertyName("ok")] public bool Ok { get; set; }

        /// <summary>One of <see cref="EnvelopeStatus"/>.</summary>
        [JsonPropertyName("status")] public string Status { get; set; } = "";

        /// <summary>One of <see cref="EnvelopePhase"/>.</summary>
        [JsonPropertyName("phase")] public string Phase { get; set; } = "";

        /// <summary>1–300 characters.</summary>
        [JsonPropertyName("summary")] public string Summary { get; set; } = "";

        /// <summary>Tool-specific JSON object or array.</summary>
        [JsonPropertyName("data")] public JsonElement? Data { get; set; }

        [JsonPropertyName("count")] public int? Count { get; set; }
        [JsonPropertyName("handle")] public string? Handle { get; set; }
        [JsonPropertyName("searched")] public SearchedDto? Searched { get; set; }
        [JsonPropertyName("changes")] public ChangeReportDto? Changes { get; set; }
        [JsonPropertyName("verified")] public VerifiedDto? Verified { get; set; }

        /// <summary>"sha256:" + 64 lowercase hex; same in a batch's preview and commit.</summary>
        [JsonPropertyName("plan_hash")] public string? PlanHash { get; set; }

        [JsonPropertyName("highlight")] public HighlightDto? Highlight { get; set; }
        [JsonPropertyName("warnings")] public List<WarningDto>? Warnings { get; set; }
        [JsonPropertyName("dialogs")] public List<DialogDto>? Dialogs { get; set; }
        [JsonPropertyName("error")] public ErrorDto? Error { get; set; }

        /// <summary>At most 200 characters.</summary>
        [JsonPropertyName("next")] public string? Next { get; set; }

        [JsonPropertyName("image")] public ImageDto? Image { get; set; }
    }

    /// <summary>The envelope <c>ref</c>: an element id, plus the RevitLinkInstance id
    /// when the element lives in a link (linked ids collide with host ids).
    /// Link is null (omitted on the wire) for the host model.</summary>
    public sealed record ElementRef(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("link")] long? Link);

    public sealed class SearchedDto
    {
        /// <summary>What was searched, e.g. "host", "link: STR_v3".</summary>
        [JsonPropertyName("sources")] public List<string> Sources { get; set; } = new();
        [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
        /// <summary>The applied filter in words; null when there was none.</summary>
        [JsonPropertyName("filter")] public string? Filter { get; set; }
        /// <summary>What could not be searched and why, e.g. "link: STR_v3 (unloaded)".</summary>
        [JsonPropertyName("not_searched")] public List<string> NotSearched { get; set; } = new();
    }

    /// <summary>The elements a call meant to change (envelope changes.touched).</summary>
    public sealed class ChangesDto
    {
        [JsonPropertyName("added")] public List<ElementRef> Added { get; set; } = new();
        [JsonPropertyName("modified")] public List<FieldChangeDto> Modified { get; set; } = new();
        [JsonPropertyName("deleted")] public List<ElementRef> Deleted { get; set; } = new();
    }

    public sealed class FieldChangeDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("field")] public string Field { get; set; } = "";
        /// <summary>Display value with unit; null when empty.</summary>
        [JsonPropertyName("before")] public string? Before { get; set; }
        /// <summary>Display value with unit; null when empty.</summary>
        [JsonPropertyName("after")] public string? After { get; set; }
    }

    /// <summary>What Revit changed beyond the touched elements, per category name.</summary>
    public sealed class SideEffectsDto
    {
        [JsonPropertyName("added_by_category")] public Dictionary<string, int> AddedByCategory { get; set; } = new();
        [JsonPropertyName("modified_by_category")] public Dictionary<string, int> ModifiedByCategory { get; set; } = new();
        [JsonPropertyName("deleted_by_category")] public Dictionary<string, int> DeletedByCategory { get; set; } = new();
    }

    /// <summary>The envelope <c>changes</c> block.</summary>
    public sealed class ChangeReportDto
    {
        [JsonPropertyName("touched")] public ChangesDto Touched { get; set; } = new();
        [JsonPropertyName("side_effects")] public SideEffectsDto? SideEffects { get; set; }
        /// <summary>Per-category counts of the touched elements (same shape as side effects);
        /// the confirm card reads categories from here.</summary>
        [JsonPropertyName("touched_by_category")] public SideEffectsDto? TouchedByCategory { get; set; }
    }

    public sealed class VerifiedDto
    {
        /// <summary>One of <see cref="VerifyMethods"/>.</summary>
        [JsonPropertyName("method")] public string Method { get; set; } = "";
        [JsonPropertyName("checked")] public int Checked { get; set; }
        [JsonPropertyName("mismatches")] public List<MismatchDto> Mismatches { get; set; } = new();
    }

    public sealed class MismatchDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("field")] public string Field { get; set; } = "";
        [JsonPropertyName("expected")] public string? Expected { get; set; }
        [JsonPropertyName("actual")] public string? Actual { get; set; }
    }

    public sealed class HighlightDto
    {
        [JsonPropertyName("selected")] public int Selected { get; set; }
        [JsonPropertyName("visible_in_view")] public int VisibleInView { get; set; }
    }

    public sealed class WarningDto
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        /// <summary>One of <see cref="WarningSeverity"/>.</summary>
        [JsonPropertyName("severity")] public string Severity { get; set; } = "";
        /// <summary>Revit FailureDefinitionId GUID, when known.</summary>
        [JsonPropertyName("definition_id")] public string? DefinitionId { get; set; }
        [JsonPropertyName("ids")] public List<long> Ids { get; set; } = new();
    }

    public sealed class DialogDto
    {
        /// <summary>Revit dialog id.</summary>
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        /// <summary>The safe answer given; null when the dialog was left to the drafter.</summary>
        [JsonPropertyName("answered_with")] public string? AnsweredWith { get; set; }
    }

    public sealed class ErrorDto
    {
        /// <summary>One of <see cref="ErrorCodes"/>.</summary>
        [JsonPropertyName("code")] public string Code { get; set; } = "";
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        /// <summary>Near matches. Required (possibly empty) when Code is not_found.</summary>
        [JsonPropertyName("candidates")] public List<string>? Candidates { get; set; }
    }

    public sealed class ImageDto
    {
        [JsonPropertyName("media_type")] public string MediaType { get; set; } = EnvelopeLimits.ImageMediaType;
        [JsonPropertyName("base64")] public string Base64 { get; set; } = "";
        /// <summary>1–1024 px.</summary>
        [JsonPropertyName("width")] public int Width { get; set; }
        /// <summary>1–1024 px.</summary>
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("caption")] public string Caption { get; set; } = "";
    }
}

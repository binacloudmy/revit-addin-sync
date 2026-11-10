#nullable enable
// Revit-free fluent builder for contract envelopes. Build() enforces the §2.1
// invariants (EnvelopeInvariants.Violations): throws in DEBUG, logs in RELEASE.
//
//   EnvelopeBuilder.Read("Found 34 ceilings on Level 2.").WithCount(34).WithSearched(s).Build();
//   EnvelopeBuilder.Write(WritePhase.Commit, "...").WithChanges(t, se).WithVerified(v).Build();
//   EnvelopeBuilder.Fail(ErrorCodes.NotFound, "No level 'L9'.", nearNames);
//
// ok follows status: failed, revit_busy and timeout are not ok; done, partial,
// noop and rejected are (rejected is the drafter's decision, not a failure).
// A failure always carries an error. not_found needs candidates: pass an empty
// list only when nothing similar exists; null is a bug and fails in DEBUG.
//
// RELEASE never throws at the drafter: it logs the violations through Trace,
// repairs what it safely can (lengths, a missing error or candidates list, an
// empty read's unknown scope, an unverified commit downgraded to ok:false)
// and returns the envelope. A builder is spent after Build().

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Envelopes
{
    public sealed class EnvelopeBuilder
    {
        /// <summary>DEBUG builds throw on an invalid envelope; RELEASE logs and repairs.</summary>
        internal static readonly bool StrictByDefault =
#if DEBUG
            true;
#else
            false;
#endif

        internal const string ScopeNotReported = "scope not reported by this tool";

        private static readonly JsonSerializerOptions DataJson = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            MaxDepth = 64,
        };

        private Envelope? _e;

        private EnvelopeBuilder(string phase, string summary)
        {
            _e = new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = phase, Summary = summary ?? "" };
        }

        private Envelope E => _e ?? throw new InvalidOperationException("This EnvelopeBuilder was already built; start a new one.");

        public static EnvelopeBuilder Read(string summary) => new EnvelopeBuilder(EnvelopePhase.Read, summary);

        public static EnvelopeBuilder Write(WritePhase phase, string summary) => new EnvelopeBuilder(EnvelopePhase.Of(phase), summary);

        /// <summary>Sets the status; ok follows it (false for failed, revit_busy, timeout).</summary>
        public EnvelopeBuilder Status(string status)
        {
            E.Status = status;
            E.Ok = !IsFailingStatus(status);
            return this;
        }

        /// <summary>Any JSON object or array: a dictionary, a list, an anonymous or
        /// DTO object, or a JsonElement. Null clears it.</summary>
        public EnvelopeBuilder WithData(object? data)
        {
            E.Data = data == null ? (JsonElement?)null : ToJson(data);
            return this;
        }

        public EnvelopeBuilder WithCount(int count)
        {
            E.Count = count;
            return this;
        }

        public EnvelopeBuilder WithHandle(string handle)
        {
            E.Handle = handle;
            return this;
        }

        public EnvelopeBuilder WithSearched(SearchedDto searched)
        {
            E.Searched = searched;
            return this;
        }

        public EnvelopeBuilder WithChanges(ChangesDto touched, SideEffectsDto? sideEffects = null)
        {
            E.Changes = new ChangeReportDto { Touched = touched ?? new ChangesDto(), SideEffects = sideEffects };
            return this;
        }

        public EnvelopeBuilder WithVerified(VerifiedDto verified)
        {
            E.Verified = verified;
            return this;
        }

        /// <summary>Appends warnings, e.g. <c>FailureLog.Current.Snapshot()</c>. None leaves the field absent.</summary>
        public EnvelopeBuilder WithWarnings(IEnumerable<WarningDto> warnings)
        {
            var list = warnings?.Where(w => w != null).ToList() ?? new List<WarningDto>();
            if (list.Count > 0) (E.Warnings ??= new List<WarningDto>()).AddRange(list);
            return this;
        }

        /// <summary>Appends answered or left dialogs. None leaves the field absent.</summary>
        public EnvelopeBuilder WithDialogs(IEnumerable<DialogDto> dialogs)
        {
            var list = dialogs?.Where(d => d != null).ToList() ?? new List<DialogDto>();
            if (list.Count > 0) (E.Dialogs ??= new List<DialogDto>()).AddRange(list);
            return this;
        }

        public EnvelopeBuilder WithHighlight(int selected, int visibleInView)
        {
            E.Highlight = new HighlightDto { Selected = selected, VisibleInView = visibleInView };
            return this;
        }

        public EnvelopeBuilder WithImage(ImageDto image)
        {
            E.Image = image;
            return this;
        }

        public EnvelopeBuilder WithPlanHash(string planHash)
        {
            E.PlanHash = planHash;
            return this;
        }

        public EnvelopeBuilder WithNext(string next)
        {
            E.Next = next;
            return this;
        }

        /// <summary>Enforces the §2.1 invariants.</summary>
        public Envelope Build() => Build(StrictByDefault, null);

        public static Envelope Fail(string code, string message, IEnumerable<string>? candidates = null, WritePhase? phase = null) =>
            Failure(code, message, candidates, phase).Build();

        /// <summary>A failure that can still take data, count or searched before Build().
        /// Status is revit_busy or timeout for those codes, failed otherwise; the summary
        /// is the message cut to 300 characters, the error keeps it whole.</summary>
        internal static EnvelopeBuilder Failure(string code, string message, IEnumerable<string>? candidates, WritePhase? phase)
        {
            var text = string.IsNullOrWhiteSpace(message) ? "Failed: " + code + "." : message.Trim();
            var b = new EnvelopeBuilder(phase.HasValue ? EnvelopePhase.Of(phase.Value) : EnvelopePhase.Read,
                EnvelopeFactory.Shorten(text, EnvelopeLimits.SummaryMaxChars));
            b.E.Status = code == ErrorCodes.RevitBusy ? EnvelopeStatus.RevitBusy
                : code == ErrorCodes.Timeout ? EnvelopeStatus.Timeout
                : EnvelopeStatus.Failed;
            b.E.Ok = false;
            b.E.Error = new ErrorDto { Code = code, Message = text, Candidates = Candidates(candidates) };
            return b;
        }

        /// <summary>Marks the envelope not ok with an error, keeping its status (an
        /// unverified commit is partial, not failed).</summary>
        internal EnvelopeBuilder Fault(string code, string message, string? status = null)
        {
            if (status != null) E.Status = status;
            E.Ok = false;
            E.Error = new ErrorDto { Code = code, Message = message };
            return this;
        }

        internal Envelope Build(bool strict, Action<string>? log)
        {
            var e = E;
            _e = null;
            var violations = EnvelopeInvariants.Violations(e);
            if (violations.Count == 0) return e;

            var text = "envelope (" + e.Phase + "/" + e.Status + ") breaks the contract: " + string.Join("; ", violations);
            if (strict) throw new InvalidOperationException(text);

            log ??= DefaultLog;
            log(text);
            Repair(e);
            var left = EnvelopeInvariants.Violations(e);
            if (left.Count > 0) log("envelope still invalid after repair: " + string.Join("; ", left));
            return e;
        }

        private static void DefaultLog(string text) => Trace.TraceWarning("[BinaVibe][envelope] " + text);

        private static bool IsFailingStatus(string? status) =>
            status == EnvelopeStatus.Failed || status == EnvelopeStatus.RevitBusy || status == EnvelopeStatus.Timeout;

        private static List<string>? Candidates(IEnumerable<string>? candidates) =>
            candidates?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.Ordinal)
                .Take(EnvelopeLimits.MaxRows).ToList();

        // RELEASE only: the smallest change that makes the envelope honest and valid.
        private static void Repair(Envelope e)
        {
            if (!EnvelopeStatus.All.Contains(e.Status)) e.Status = e.Ok ? EnvelopeStatus.Done : EnvelopeStatus.Failed;
            if (string.IsNullOrWhiteSpace(e.Summary)) e.Summary = e.Error?.Message is { Length: > 0 } m ? m : "(no summary)";
            e.Summary = EnvelopeFactory.Shorten(e.Summary, EnvelopeLimits.SummaryMaxChars);
            if (e.Next != null) e.Next = EnvelopeFactory.Shorten(e.Next, EnvelopeLimits.NextMaxChars);
            if (e.Data is JsonElement d && d.ValueKind != JsonValueKind.Object && d.ValueKind != JsonValueKind.Array)
                e.Data = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["value"] = d }, DataJson);
            if (e.Count < 0) e.Count = null;

            if (e.Error != null)
            {
                if (!ErrorCodes.All.Contains(e.Error.Code)) e.Error.Code = ErrorCodes.RuntimeError;
                e.Error.Message ??= "";
                if (e.Error.Code == ErrorCodes.NotFound && e.Error.Candidates == null) e.Error.Candidates = new List<string>();
            }
            if (!e.Ok && e.Error == null)
                e.Error = new ErrorDto { Code = ErrorCodes.RuntimeError, Message = "The tool failed without saying why." };

            if (e.Phase == EnvelopePhase.Commit && e.Ok && e.Status != EnvelopeStatus.Noop
                && (e.Verified == null || e.Verified.Method == VerifyMethods.None))
            {
                e.Ok = false;
                e.Status = EnvelopeStatus.Partial;
                e.Verified ??= new VerifiedDto { Method = VerifyMethods.None };
                e.Error ??= new ErrorDto
                {
                    Code = ErrorCodes.RuntimeError,
                    Message = "The change was committed but not verified; inspect the elements before relying on it.",
                };
            }

            if (e.Phase == EnvelopePhase.Read && e.Count == 0 && e.Searched == null)
                e.Searched = new SearchedDto { NotSearched = { ScopeNotReported } };
        }

        // ---- data serialisation -------------------------------------------------

        private static JsonElement ToJson(object data)
        {
            if (data is JsonElement je) return je.Clone();
            if (data is JsonDocument jd) return jd.RootElement.Clone();
            try
            {
                return JsonSerializer.SerializeToElement(data, data.GetType(), DataJson);
            }
            catch (Exception)
            {
                // A value System.Text.Json rejects (a Revit object, a throwing
                // property): keep the shape, stringify what cannot be written.
                return JsonSerializer.SerializeToElement(Plain(data, 0), DataJson);
            }
        }

        private static object? Plain(object? v, int depth)
        {
            switch (v)
            {
                case null: return null;
                case string or bool or char: return v;
                case JsonElement je: return je.Clone();
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? d.ToString(CultureInfo.InvariantCulture) : (object)d;
                case float f: return float.IsNaN(f) || float.IsInfinity(f) ? f.ToString(CultureInfo.InvariantCulture) : (object)f;
                case IConvertible c when v.GetType().IsPrimitive || v is decimal: return c;
                case Enum en: return en.ToString();
            }
            if (depth >= 16) return v.ToString();
            if (v is IDictionary dict)
            {
                var o = new Dictionary<string, object?>();
                foreach (DictionaryEntry kv in dict) o[Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? ""] = Plain(kv.Value, depth + 1);
                return o;
            }
            if (v is IEnumerable seq)
            {
                var list = new List<object?>();
                foreach (var item in seq) list.Add(Plain(item, depth + 1));
                return list;
            }
            try
            {
                return JsonSerializer.SerializeToElement(v, v.GetType(), DataJson);
            }
            catch (Exception)
            {
                return v.ToString();
            }
        }
    }
}

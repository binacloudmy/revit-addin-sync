using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// What to do about the lineage stamp of a model that was just downloaded
    /// from BINA. Revit-free so the rules are testable without a Revit session.
    ///
    /// The stamp is the document GUID the add-in sends as `docGuid` on Sync and
    /// that bina-be matches on `sourceDocumentGuid`. A model uploaded through the
    /// web has no such GUID, so there is nothing valid to write for it: writing
    /// bina-be's own lineageId would not resolve and would split the model's
    /// listing on the next Sync.
    /// </summary>
    public enum StampAction
    {
        /// <summary>No stamp yet and the row has a docGuid: write it.</summary>
        Write,
        /// <summary>The file already carries this same GUID.</summary>
        AlreadyStamped,
        /// <summary>The row carries no docGuid (web upload, promotion copy).</summary>
        SkipNoDocGuid,
        /// <summary>The file carries a different GUID; never overwritten.</summary>
        SkipDifferentStamp,
        /// <summary>The stamp could not be read; writing blind could fork identity.</summary>
        SkipUnreadable
    }

    public static class DownloadStampPolicy
    {
        public static StampAction Decide(bool stampReadable, string existingLineageId, string rowDocGuid)
        {
            if (string.IsNullOrWhiteSpace(rowDocGuid)) return StampAction.SkipNoDocGuid;
            if (!stampReadable) return StampAction.SkipUnreadable;
            if (string.IsNullOrWhiteSpace(existingLineageId)) return StampAction.Write;

            return string.Equals(existingLineageId.Trim(), rowDocGuid.Trim(),
                    System.StringComparison.OrdinalIgnoreCase)
                ? StampAction.AlreadyStamped
                : StampAction.SkipDifferentStamp;
        }
    }

    public enum AutoPullKind
    {
        /// <summary>AutoSyncParameters is false.</summary>
        Disabled,
        /// <summary>The model could not be opened as a document, so nothing was pulled.</summary>
        NoDocument,
        /// <summary>BINA holds no parameters for this model.</summary>
        NothingToWrite,
        Written,
        Failed
    }

    /// <summary>Result of the automatic parameter step after Download Model.</summary>
    public sealed class AutoPullOutcome
    {
        public AutoPullKind Kind { get; set; }
        public int Applied { get; set; }
        public int ElementsTouched { get; set; }
        public int Skipped { get; set; }
        public int Total { get; set; }
        /// <summary>BINA returned a first batch only (no paging on the endpoint).</summary>
        public bool Truncated { get; set; }
        /// <summary>Server/exception text for logs only; never shown to the user.</summary>
        public string Error { get; set; }
        public PullFailure Failure { get; set; }
        /// <summary>The write was undone as a whole; the model holds none of the pulled values.</summary>
        public bool RolledBack { get; set; }
        /// <summary>The failure happened while writing into the model (not while fetching): one fixed text, no network wording.</summary>
        public bool WriteFailed { get; set; }
        public string ModelName { get; set; }
        public int? VersionNumber { get; set; }
        public StampAction Stamp { get; set; }
        /// <summary>Set when writing the stamp threw; the model is unaffected.</summary>
        public bool StampFailed { get; set; }
    }

    /// <summary>One summary per download, in plain words. Null means stay silent.</summary>
    public static class AutoPullMessages
    {
        public sealed class Message
        {
            public string Title { get; set; }
            public string Instruction { get; set; }
            public string Content { get; set; }
        }

        public static Message Build(AutoPullOutcome o)
        {
            if (o == null) return null;
            string model = string.IsNullOrEmpty(o.ModelName) ? "this model" : "\"" + o.ModelName + "\"";
            string ver = o.VersionNumber.HasValue ? " (v" + o.VersionNumber.Value + ")" : "";

            var body = new StringBuilder();
            string instruction;

            switch (o.Kind)
            {
                case AutoPullKind.Written:
                    instruction = o.Applied == 0
                        ? "No parameters could be written"
                        : $"{o.Applied} parameter{(o.Applied == 1 ? "" : "s")} written onto " +
                          $"{o.ElementsTouched} element{(o.ElementsTouched == 1 ? "" : "s")}";
                    body.Append(o.Applied > 0
                        ? $"From BINA, for {model}{ver}. Save the model to keep them."
                        : $"Nothing from {model}{ver} reached the model.");
                    if (o.Skipped > 0)
                        body.Append($"\n\n{o.Skipped} of {o.Total} were not written (element not in this version, read-only in Revit, or a measurable value such as a length). Use Sync Parameters for the full list.");
                    if (o.Truncated)
                        body.Append("\n\nBINA returned only the first batch of parameters for this model. Some were not pulled; the rest cannot be fetched automatically yet.");
                    break;

                case AutoPullKind.Failed:
                    instruction = o.Failure == PullFailure.Cancelled
                        ? "Model opened, BINA parameters not pulled"
                        : "Model opened, but BINA parameters could not be applied";
                    if (o.WriteFailed)
                    {
                        // The write phase is not a network call: one fixed text, which
                        // already says the transaction was rolled back.
                        body.Append(PullWriteFailure.Text);
                    }
                    else
                    {
                        body.Append(PullFailureText.For(o.Failure));
                        if (o.RolledBack)
                            body.Append(" No parameter changes were kept in the model.");
                    }
                    body.Append("\n\nThe model itself is fine. Click Sync Parameters to try again.");
                    break;

                case AutoPullKind.NoDocument:
                    instruction = "Model downloaded, parameters not applied";
                    body.Append("Revit could not open the downloaded file in this session, so BINA parameters were not pulled. Open it, then click Sync Parameters.");
                    break;

                default:
                    // Disabled / NothingToWrite: nothing to say, unless the stamp
                    // itself failed (handled below).
                    instruction = null;
                    break;
            }

            if (o.StampFailed)
            {
                if (instruction == null) instruction = "Model opened";
                body.Append("\n\nBINA could not record this model's identity inside the file. Sync, Issues and Sync Parameters may ask which folder it came from.");
            }

            if (instruction == null) return null;
            return new Message
            {
                Title = "BINA parameters",
                Instruction = instruction,
                Content = body.ToString()
            };
        }
    }
}

namespace RevitWebAppSync.Services
{
    /// <summary>Why the automatic pull did not complete, in terms a drafter acts on.</summary>
    public enum PullFailure
    {
        None,
        Cancelled,
        Timeout,
        NotFound,
        AccessDenied,
        SessionExpired,
        Server,
        Network,
        Unexpected
    }

    /// <summary>The element-parameters call answered with a non-success status.</summary>
    public sealed class ParameterPullHttpException : InvalidOperationException
    {
        public int StatusCode { get; }
        public ParameterPullHttpException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>
    /// A request came back 401 and refreshing the token failed: the user has to
    /// log in again. Still an InvalidOperationException with the same message, so
    /// the other commands that show the message behave as before; the pull maps
    /// it to <see cref="PullFailure.SessionExpired"/>.
    /// </summary>
    public sealed class SessionExpiredException : InvalidOperationException
    {
        public SessionExpiredException()
            : base("Your Cloud Docs session has expired. Click 'Login to CDE' and try again.") { }
    }

    public static class PullPolicy
    {
        /// <summary>Longest the automatic pull waits for BINA before giving up.</summary>
        public const int TimeoutSeconds = 90;

        public static PullFailure Classify(Exception ex, bool userCancelled)
        {
            if (userCancelled) return PullFailure.Cancelled;
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is AggregateException agg && agg.InnerExceptions.Count > 0)
                    return Classify(agg.InnerExceptions[0], false);

                if (e is SessionExpiredException) return PullFailure.SessionExpired;

                if (e is ParameterPullHttpException http)
                {
                    switch (http.StatusCode)
                    {
                        case 401: return PullFailure.SessionExpired;
                        case 403: return PullFailure.AccessDenied;
                        case 404: return PullFailure.NotFound;
                        case 408: return PullFailure.Timeout;
                        default:
                            return http.StatusCode >= 500 ? PullFailure.Server : PullFailure.Unexpected;
                    }
                }

                // TaskCanceledException is an OperationCanceledException: when
                // the user did not press Cancel, it is the timeout token.
                if (e is OperationCanceledException) return PullFailure.Timeout;
                if (e is System.Net.Http.HttpRequestException || e is System.Net.WebException
                    || e is System.IO.IOException)
                    return PullFailure.Network;
            }
            return PullFailure.Unexpected;
        }
    }

    public static class PullFailureDialog
    {
        /// <summary>
        /// What the manual Sync Parameters button shows when the pull throws:
        /// plain text by cause, never the exception message (it can carry the
        /// raw server body). Null means the user cancelled, so stay silent. The
        /// 90 second timeout surfaces as a cancellation too, which is why
        /// <paramref name="userCancelled"/> (the Cancel click) decides.
        /// </summary>
        public static string TextFor(Exception ex, bool userCancelled)
        {
            var failure = PullPolicy.Classify(ex, userCancelled);
            return failure == PullFailure.Cancelled ? null : PullFailureText.For(failure);
        }
    }

    public static class PullWriteFailure
    {
        /// <summary>
        /// What both paths (automatic and the manual Sync Parameters button) say
        /// when the WRITE throws. Never the exception message, and never sorted
        /// by network cause: the write is a Revit transaction, which AtomicBatch
        /// rolled back, so the model holds none of the pulled values.
        /// </summary>
        public const string Text =
            "Something went wrong while applying the parameters. No changes were kept in the model.";
    }

    public static class PullFailureText
    {
        public static string For(PullFailure f)
        {
            switch (f)
            {
                case PullFailure.Cancelled:
                    return "The pull was cancelled.";
                case PullFailure.Timeout:
                    return $"BINA did not answer within {PullPolicy.TimeoutSeconds} seconds.";
                case PullFailure.NotFound:
                    return "BINA has no parameters record for this model (it may have been moved or deleted).";
                case PullFailure.AccessDenied:
                    return "You do not have access to read this model's parameters.";
                case PullFailure.SessionExpired:
                    return "Your Cloud Docs session has expired. Click 'Login to CDE', then Sync Parameters.";
                case PullFailure.Server:
                    return "BINA had a problem answering. Try again in a moment.";
                case PullFailure.Network:
                    return "BINA could not be reached. Check the connection.";
                default:
                    // A load failure the classifier cannot sort (for example an answer
                    // that is not valid JSON). The write phase has its own text
                    // (PullWriteFailure.Text), so this one speaks of loading.
                    return "Something went wrong while loading the parameters from BINA.";
            }
        }
    }

    /// <summary>
    /// What an apply batch is made of, decided without Revit: which rows are
    /// usable and how they group by element. Revit-side code applies the plan.
    /// </summary>
    public static class ParameterApplyPlan
    {
        public sealed class Plan
        {
            public List<IGrouping<string, BinaElementParameter>> Groups { get; set; } =
                new List<IGrouping<string, BinaElementParameter>>();
            public int Usable { get; set; }
            /// <summary>Rows with no element id or no parameter name.</summary>
            public int Rejected { get; set; }
            public int Elements => Groups.Count;
        }

        public static Plan Build(IEnumerable<BinaElementParameter> parameters)
        {
            var all = (parameters ?? Enumerable.Empty<BinaElementParameter>()).ToList();
            var usable = all
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.ElementExternalId)
                            && !string.IsNullOrWhiteSpace(p.ParameterName))
                .ToList();
            return new Plan
            {
                Groups = usable.GroupBy(p => p.ElementExternalId).ToList(),
                Usable = usable.Count,
                Rejected = all.Count - usable.Count
            };
        }
    }

    public enum CoerceKind { String, Integer, Double, Unsupported }

    /// <summary>
    /// Decides from Revit's spec whether an existing parameter is measurable
    /// (has a unit). The "Number" spec (SpecTypeId.Number, what BINA's own Number
    /// parameters use) is NOT measurable for our purpose even though Revit lets it
    /// carry units: BINA writes a plain number to it, which is what its owner
    /// meant. Length, Area, Volume, Angle, Percentage, Currency and the rest stay
    /// skipped. Revit-free: the caller passes what the API said.
    /// </summary>
    public static class SpecPolicy
    {
        public static bool IsMeasurable(bool apiSaysMeasurable, bool isNumberSpec) =>
            apiSaysMeasurable && !isNumberSpec;

        /// <summary>
        /// True for the identifier of the unitless Number spec ("autodesk.spec.aec:number-2.0.0").
        /// Matched on the spec's name part so a version bump does not break it; a
        /// spec whose name merely contains "number" (such as a numbered quantity)
        /// is not matched.
        /// </summary>
        public static bool IsNumberTypeId(string typeId)
        {
            if (string.IsNullOrWhiteSpace(typeId)) return false;
            int colon = typeId.LastIndexOf(':');
            string name = colon >= 0 ? typeId.Substring(colon + 1) : typeId;
            int dash = name.IndexOf('-');
            if (dash >= 0) name = name.Substring(0, dash);
            return string.Equals(name, "number", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The text-to-Revit-value rules for one parameter, free of the Revit API.
    /// Numbers use the invariant culture: BINA sends "1200.5", and a machine
    /// set to a comma-decimal locale must not read that as 12005 or fail.
    /// </summary>
    public static class ValueCoercion
    {
        public sealed class Result
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
            public string Text { get; set; }
            public int Int { get; set; }
            public double Double { get; set; }
        }

        public static Result Coerce(CoerceKind kind, string value)
        {
            value = value ?? "";
            switch (kind)
            {
                case CoerceKind.String:
                    return new Result { Ok = true, Text = value };

                case CoerceKind.Integer:
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                        return new Result { Ok = true, Int = i };
                    var v = value.Trim().ToLowerInvariant();
                    if (v == "true" || v == "yes") return new Result { Ok = true, Int = 1 };
                    if (v == "false" || v == "no") return new Result { Ok = true, Int = 0 };
                    return new Result { Error = $"'{value}' is not a whole number or a yes/no value" };

                case CoerceKind.Double:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                        && !double.IsNaN(d) && !double.IsInfinity(d))
                        return new Result { Ok = true, Double = d };
                    return new Result { Error = $"'{value}' is not a number" };

                default:
                    return new Result { Error = "unsupported storage type" };
            }
        }
    }

    /// <summary>A started-and-committed-or-rolled-back unit of work (a Revit Transaction in production).</summary>
    public interface IBatchScope
    {
        void Start();
        void Commit();
        void RollBack();
    }

    /// <summary>
    /// Runs a write batch so that it is either kept whole or not at all, and
    /// puts application state back afterwards. A failure inside the batch, a
    /// cancel, or a batch that wrote nothing rolls everything back — including
    /// parameter bindings, which live in the document transaction.
    /// </summary>
    public static class AtomicBatch
    {
        public sealed class Outcome<T>
        {
            public T Value { get; set; }
            public bool Committed { get; set; }
        }

        /// <param name="shouldCommit">Asked after the body returns; false rolls back.</param>
        /// <param name="restore">Always runs last, whatever happened (e.g. SharedParametersFilename).</param>
        public static Outcome<T> Run<T>(
            IBatchScope scope,
            Func<T> body,
            Func<T, bool> shouldCommit,
            Action restore,
            CancellationToken cancel = default)
        {
            bool started = false;
            try
            {
                scope.Start();
                started = true;

                T value = body();
                cancel.ThrowIfCancellationRequested();

                if (shouldCommit != null && !shouldCommit(value))
                {
                    scope.RollBack();
                    started = false;
                    return new Outcome<T> { Value = value, Committed = false };
                }

                scope.Commit();
                started = false;
                return new Outcome<T> { Value = value, Committed = true };
            }
            catch
            {
                if (started)
                {
                    try { scope.RollBack(); } catch { /* the original failure matters more */ }
                }
                throw;
            }
            finally
            {
                try { restore?.Invoke(); } catch { /* restore is best effort */ }
            }
        }
    }
}

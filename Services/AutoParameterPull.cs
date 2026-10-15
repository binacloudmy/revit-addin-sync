using System.Text;

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
        public string Error { get; set; }
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
                        body.Append($"\n\n{o.Skipped} of {o.Total} were not written (element not in this version, or read-only in Revit). Use Sync Parameters for the full list.");
                    if (o.Truncated)
                        body.Append("\n\nBINA returned only the first batch of parameters for this model. Some were not pulled; the rest cannot be fetched automatically yet.");
                    break;

                case AutoPullKind.Failed:
                    instruction = "Model opened, but BINA parameters could not be applied";
                    body.Append(string.IsNullOrWhiteSpace(o.Error) ? "Unknown error." : o.Error.Trim());
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

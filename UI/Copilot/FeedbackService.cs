using System;
using System.IO;
using Newtonsoft.Json;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>One feedback record — a star rating or a bug report — appended to
    /// the local feedback log. Kept deliberately small; the backend upload is a
    /// future concern (this stub just persists locally so nothing is lost).</summary>
    public class FeedbackEntry
    {
        public string Kind { get; set; }        // "rating" | "bug"
        public int Stars { get; set; }           // 1–5 for ratings; 0 for bugs
        public string Message { get; set; }      // optional comment / bug description
        public string ModelName { get; set; }    // active Revit model, if known
        public string UserId { get; set; }       // BinaConfig user, if signed in
        public string AddinVersion { get; set; } // "Copilot x.y.z" — from AppInfo
        public string RevitVersion { get; set; } // "Revit a.b" — from AppInfo
        public string AtUtc { get; set; }        // ISO-8601 timestamp
    }

    /// <summary>Sink for in-app feedback (ratings + bug reports). The panel calls
    /// this from the Rate / Report sheets; the implementation decides where it
    /// goes. Today: a local JSONL log. Later: a backend endpoint.</summary>
    public interface IFeedbackService
    {
        void SubmitRating(int stars, string comment);
        void ReportBug(string description);
    }

    /// <summary>Default implementation — appends each entry as one JSON line to
    /// %APPDATA%\RevitWebAppSync\feedback.jsonl. Best-effort: a failed write is
    /// swallowed (feedback must never break the panel).</summary>
    public class LocalFeedbackService : IFeedbackService
    {
        private readonly Func<string> _modelName;
        private readonly Func<string> _userId;

        public LocalFeedbackService(Func<string> modelName = null, Func<string> userId = null)
        {
            _modelName = modelName;
            _userId = userId;
        }

        public void SubmitRating(int stars, string comment) =>
            Append(new FeedbackEntry { Kind = "rating", Stars = stars, Message = comment });

        public void ReportBug(string description) =>
            Append(new FeedbackEntry { Kind = "bug", Message = description });

        private void Append(FeedbackEntry e)
        {
            try
            {
                e.ModelName = SafeGet(_modelName);
                e.UserId = SafeGet(_userId);
                e.AddinVersion = RevitWebAppSync.AppInfo.AddinLabel;
                e.RevitVersion = RevitWebAppSync.AppInfo.RevitVersion;
                e.AtUtc = DateTime.UtcNow.ToString("o");
                File.AppendAllText(FilePath, JsonConvert.SerializeObject(e) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Feedback write failed: {ex.Message}");
            }
        }

        private static string SafeGet(Func<string> f)
        {
            try { return f?.Invoke(); } catch { return null; }
        }

        private static string FilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitWebAppSync");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "feedback.jsonl");
            }
        }
    }
}

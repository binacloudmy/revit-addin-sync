#nullable enable
// ImportDialogGuard - answers the one Revit dialog a far-flung DWG raises while it is linked
// for reading, so a read never sits behind a modal box nobody is looking at.
//
// Linking a drawing whose geometry reaches past 1E9 drawing units raises a message box,
// auto-converted to a TaskDialog with OK and Cancel: "Geometry in the file ... has extents
// greater than 1E9. Data exceeding that range will be truncated...". It blocks the Idling pump
// until a person clicks it. OK links the drawing (the far stray geometry is cut off, which a
// read does not need); Cancel aborts the link, and the fallback link raises the same box again.
//
// Two mechanisms, two handlers: this guard answers the dialog from App's DialogBoxShowing
// subscription, only while a scope is open (Begin ... Dispose) and only for that dialog. The
// failure warning the same link posts ("Imported model is out of range") is a transaction
// failure, deleted by TxGuard.StartSwallowing on the link's transaction.
//
// Revit-free: App passes the dialog's id and text and how to answer it, so the rule is
// unit-tested. The match is on English text; a localised Revit still shows the dialog.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace BinaVibe.Mcp.Tools
{
    internal sealed class ImportDialogGuard : IDisposable
    {
        /// <summary>IDOK.</summary>
        internal const int Ok = 1;

        // Dialogs are raised on Revit's UI thread, where the link runs too.
        private static ImportDialogGuard? _active;

        /// <summary>Off in the unit tests, which have no Revit log folder to write to.</summary>
        internal static bool WriteLog = true;

        private readonly ImportDialogGuard? _outer;
        private readonly List<string> _seen = new List<string>();
        private bool _disposed;

        private ImportDialogGuard(ImportDialogGuard? outer)
        {
            _outer = outer;
        }

        /// <summary>Opens a scope: dialogs raised until Dispose are seen, and the extents dialog
        /// is answered OK.</summary>
        public static ImportDialogGuard Begin()
        {
            var guard = new ImportDialogGuard(_active);
            _active = guard;
            return guard;
        }

        /// <summary>"id | message" of every dialog raised inside the scope, for the log.</summary>
        public IReadOnlyList<string> Seen => _seen;

        /// <summary>
        /// Called for every dialog Revit raises. Outside a scope it does nothing. Inside, it notes
        /// the dialog and answers it when <see cref="AnswerFor"/> knows the answer. True when it
        /// answered: the dialog never shows.
        /// </summary>
        public static bool OnDialog(string? dialogId, string? message, Func<int, bool> answer)
        {
            ImportDialogGuard? guard = _active;
            if (guard == null) return false;

            guard._seen.Add((dialogId ?? "") + " | " + (message ?? ""));
            int? result = AnswerFor(dialogId, message);
            if (!result.HasValue) return false;
            try
            {
                return answer(result.Value);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>IDOK for the extents dialog; null for any other, which is left alone.</summary>
        internal static int? AnswerFor(string? dialogId, string? message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            bool extents = message!.IndexOf("extents greater than", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           message.IndexOf("truncated", StringComparison.OrdinalIgnoreCase) >= 0;
            return extents ? Ok : (int?)null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(_active, this)) _active = _outer;
            if (_seen.Count > 0 && WriteLog) Log(_seen);
        }

        /// <summary>The dialog ids are not in Revit's journal; the first live run reads them here.</summary>
        private static void Log(IEnumerable<string> seen)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitWebAppSync");
                Directory.CreateDirectory(dir);
                var lines = new List<string>();
                foreach (string line in seen)
                {
                    string cut = line.Length > 300 ? line.Substring(0, 300) : line;
                    Debug.WriteLine("[BinaVibe][import-dialog] " + cut);
                    lines.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + cut.Replace("\r", " ").Replace("\n", " "));
                }
                File.AppendAllLines(Path.Combine(dir, "import-dialogs.log"), lines);
            }
            catch
            {
                // Logging never breaks a read.
            }
        }
    }
}

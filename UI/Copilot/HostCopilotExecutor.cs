#nullable enable
// The library's tool forms and /open-view on CopilotToolHost (lean-harness
// spec §7.2, §17; W5 Task 5). Replaces RevitCopilotExecutor, which ran
// synthesized C# through CodeExecutionHandler with an AI self-heal loop.
//
// Each vetted tool becomes one host call (VettedCalls): reads run with no
// confirmation; writes preview and show the pane's confirm card, then commit
// in one named group with read-back, exactly like the model's calls. The
// result is the envelope's own summary; nothing here claims more than it says.
// AI (tier 2) commands are not run here: the view model sends them to chat.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    public sealed class HostCopilotExecutor : ICopilotExecutor
    {
        private readonly PaneToolRunner _tools;

        public HostCopilotExecutor(PaneToolRunner tools)
        {
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        }

        public async void Run(ToolDef tool, IDictionary<string, object> values, string code, Action<ExecOutcome> onDone)
        {
            ExecOutcome outcome;
            try
            {
                outcome = await RunAsync(tool, values).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                outcome = new ExecOutcome { Success = false, Error = "The tool could not run (" + ex.GetType().Name + ": " + ex.Message + ")." };
            }
            try { onDone?.Invoke(outcome); } catch { /* a UI hiccup must not crash Revit */ }
        }

        private async Task<ExecOutcome> RunAsync(ToolDef tool, IDictionary<string, object> values)
        {
            if (tool == null || tool.Tier != 1)
                return new ExecOutcome { Success = false, Error = "AI commands run in the chat." };

            var form = (values ?? new Dictionary<string, object>()).ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            // One snapshot for the whole run: its document is what the run checks against.
            var snapshot = _tools.Snapshot();
            var vc = VettedCalls.Build(tool.BackendName, form, snapshot.Selection, out var error);
            if (vc == null) return new ExecOutcome { Success = false, Error = error };

            var turnId = "lib-" + Guid.NewGuid().ToString("N");
            var calls = new[] { vc.Call };
            var envelopes = vc.Write
                ? await _tools.RunWritesAsync(turnId, "req-" + turnId, calls, vc.Label, snapshot, CancellationToken.None).ConfigureAwait(true)
                : await _tools.RunReadsAsync(turnId, calls, snapshot, CancellationToken.None).ConfigureAwait(true);
            var env = envelopes.FirstOrDefault() ?? EnvelopeFactory.Failed(ErrorCodes.RuntimeError, "The add-in returned no result.");

            if (vc.ExportSchedule && env.Ok) return Export(env, vc, turnId);

            var ok = env.Ok && env.Status != EnvelopeStatus.Rejected;
            return new ExecOutcome
            {
                Success = ok,
                Message = env.Summary,
                Error = ok ? null : (env.Error?.Message ?? env.Summary),
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(new { kind = "plain", headline = env.Summary, sub = env.Next ?? "" }),
                TurnId = turnId,
            };
        }

        /// <summary>The schedule's rows to a file the drafter picks (pane-owned picker).</summary>
        private static ExecOutcome Export(Envelope env, VettedCall vc, string turnId)
        {
            var evidence = new TurnEvidence();
            evidence.Add(new ToolRequestDto { Kind = ToolRequestKinds.Read, Calls = new List<ToolCallDto> { vc.Call } }, new[] { env });
            var table = evidence.Tables.FirstOrDefault();
            if (table == null)
                return new ExecOutcome { Success = false, Error = "The schedule returned no rows to export.", TurnId = turnId };

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export schedule",
                Filter = vc.Format == "csv" ? "CSV (*.csv)|*.csv" : "Excel workbook (*.xlsx)|*.xlsx",
                FileName = (vc.Call.Args.TryGetProperty("schedule", out var s) ? s.GetString() : "schedule") + "." + vc.Format,
                AddExtension = true,
            };
            if (dlg.ShowDialog() != true)
                return new ExecOutcome { Success = false, Error = "Export cancelled.", TurnId = turnId };

            TableXlsx.Write(table, dlg.FileName);
            try
            {
                if (vc.AfterExport.StartsWith("Open", StringComparison.OrdinalIgnoreCase))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
                else if (vc.AfterExport.StartsWith("Show", StringComparison.OrdinalIgnoreCase))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + dlg.FileName + "\"");
            }
            catch { /* the file is written; opening it is a convenience */ }

            return new ExecOutcome
            {
                Success = true,
                Message = "Exported " + table.Rows.Count + " rows",
                Data = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    kind = "file",
                    headline = Path.GetFileName(dlg.FileName),
                    sub = table.Rows.Count + " rows · " + env.Summary,
                    path = Path.GetDirectoryName(dlg.FileName),
                }),
                TurnId = turnId,
            };
        }
    }
}

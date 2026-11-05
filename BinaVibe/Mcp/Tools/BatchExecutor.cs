// BatchExecutor — runs an ordered list of MUTATE steps inside ONE
// TransactionGroup (single Ctrl+Z), resolving "$<index>.<field>" references
// to earlier steps' outputs. Reuses the existing per-tool Mutators (each keeps
// its own inner transaction; the group assimilates them into one undo).
//
// This is the safe build path: bounded typed mutators only — no generated C#,
// so it can neither hallucinate the API nor hang Revit hard. A failed step
// rolls back the whole group.
using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    public static class BatchExecutor
    {
        /// <summary>One entry per step, same order. A step the card cannot preview
        /// (no dry run of its own, or it refers to an earlier step's output) is
        /// {skipped:true} and is NOT run. A previewable step runs with dry_run=true.</summary>
        private static Dictionary<string, object?> Preview(UIApplication app, JsonElement stepsEl)
        {
            var stepOut = new List<object?>();
            foreach (var step in stepsEl.EnumerateArray())
            {
                var dry = RevitWebAppSync.Services.CardPreview.StepDryRunArgs(step);
                var tool = step.ValueKind == JsonValueKind.Object && step.TryGetProperty("tool", out var t) ? (t.GetString() ?? "") : "";
                if (dry == null)
                {
                    stepOut.Add(new Dictionary<string, object?> { ["ok"] = true, ["skipped"] = true, ["tool"] = tool });
                    continue;
                }
                try { stepOut.Add(ToolRegistry.Invoke(app, tool, dry.Value) ?? new Dictionary<string, object?>()); }
                catch (Exception ex) { stepOut.Add(new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.Message }); }
            }
            return new() { ["ok"] = true, ["dry_run"] = true, ["steps"] = stepOut };
        }

        public static Dictionary<string, object?> Run(UIApplication app, JsonElement args)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return new() { ["ok"] = false, ["error"] = "execute_revit_batch: no active document" };
            if (!args.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array)
                return new() { ["ok"] = false, ["error"] = "execute_revit_batch: missing steps[]" };

            // AI-220: the approval card previews a batch before Allow. dry_run on the
            // batch runs ONLY the steps whose tool has its own dry run (no transaction,
            // nothing written); every other step is skipped, never executed.
            if (ArgsHelp.GetBool(args, "dry_run") ?? false)
                return Preview(app, stepsEl);

            var results = new List<Dictionary<string, object?>>();
            var stepOut = new List<object?>();
            using var group = new TransactionGroup(doc, "BinaVibe: batch build");
            group.Start();
            try
            {
                foreach (var step in stepsEl.EnumerateArray())
                {
                    var tool = step.TryGetProperty("tool", out var t) ? (t.GetString() ?? "") : "";
                    var stepArgs = step.TryGetProperty("args", out var a) ? a : default;
                    var resolved = BatchRefResolver.ResolveRefs(stepArgs, results);
                    // Re-serialize resolved args back to a JsonElement for ToolRegistry.
                    var resolvedJson = JsonSerializer.SerializeToElement(resolved);
                    var r = ToolRegistry.Invoke(app, tool, resolvedJson) ?? new Dictionary<string, object?>();
                    results.Add(r);
                    stepOut.Add(r);
                    if (r.TryGetValue("ok", out var ok) && ok is bool b && !b)
                    {
                        group.RollBack();
                        return new()
                        {
                            ["ok"] = false,
                            ["error"] = $"step {results.Count - 1} ({tool}) failed: {r.GetValueOrDefault("error")}",
                            ["steps"] = stepOut,
                        };
                    }
                }
                group.Assimilate();   // one undo for the whole build
                return new() { ["ok"] = true, ["steps"] = stepOut };
            }
            catch (Exception ex)
            {
                try { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); } catch { }
                return new() { ["ok"] = false, ["error"] = ex.Message, ["steps"] = stepOut };
            }
        }
    }
}

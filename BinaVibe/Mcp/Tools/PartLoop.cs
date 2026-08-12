// PartLoop — the VibeCAD loop, in-process (spec 2026-08-10).
// Build one part, measure it against the prediction the backend shipped,
// rebuild once on mismatch, mark blocked dependents instead of cascading
// failures. The scorecard this emits is the ONLY thing the model narrates
// from — which is exactly why it must never claim what wasn't measured.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class PartLoop
    {
        public static JsonArray Run(Document doc, JsonElement partsJson,
                                    Func<string, JsonElement, List<ElementId>> buildPart)
        {
            var scorecard = new JsonArray();
            var status = new Dictionary<string, string>();

            foreach (var part in partsJson.EnumerateArray())
            {
                string id = "<unknown>";
                try
                {
                    id = part.GetProperty("id").GetString()!;
                    var expected = part.GetProperty("expected");
                    var deps = part.TryGetProperty("deps", out var d)
                        ? d.EnumerateArray().Select(x => x.GetString()!).ToList()
                        : new List<string>();

                    var badDep = deps.FirstOrDefault(dep =>
                        status.TryGetValue(dep, out var s) && (s == "failed" || s == "blocked"));
                    if (badDep != null)
                    {
                        status[id] = "blocked";
                        var depStatus = status[badDep];
                        scorecard.Add(new JsonObject {
                            ["part"] = id, ["status"] = "blocked",
                            ["predicted"] = $"dep {badDep} {depStatus}", ["measured"] = "" });
                        continue;
                    }

                    var result = BuildAndMeasure(doc, id, expected, buildPart, out var owned);
                    // Best-effort cleanup on a failed attempt is never silent
                    // now — a survivor count from EITHER delete rides through
                    // to the row this loop finally writes, so a part reported
                    // "failed" never quietly leaves orphaned geometry behind it.
                    string undoNote = "";
                    if (result.Status == "failed")
                    {
                        var survivors = DeleteOwned(doc, id, owned);
                        if (survivors > 0)
                            undoNote += $"; undo incomplete: {survivors} elements remain";
                        result = BuildAndMeasure(doc, id, expected, buildPart, out owned);
                        if (result.Status == "failed")
                        {
                            var survivors2 = DeleteOwned(doc, id, owned);   // wrong geometry never ships
                            if (survivors2 > 0)
                                undoNote += $"; undo incomplete: {survivors2} elements remain";
                        }
                    }
                    status[id] = result.Status;
                    scorecard.Add(new JsonObject {
                        ["part"] = id, ["status"] = result.Status,
                        ["predicted"] = result.Predicted, ["measured"] = result.Measured + undoNote });
                }
                catch (Exception e)
                {
                    scorecard.Add(new JsonObject {
                        ["part"] = id, ["status"] = "failed",
                        ["predicted"] = "", ["measured"] = $"malformed part entry: {e.Message}" });
                    if (id != "<unknown>")
                        status[id] = "failed";
                }
            }
            return scorecard;
        }

        private static PartResult BuildAndMeasure(
            Document doc, string id, JsonElement expected,
            Func<string, JsonElement, List<ElementId>> buildPart,
            out List<ElementId> owned)
        {
            owned = new List<ElementId>();
            try
            {
                // TxGuard, not a bare Start/Commit: a part that trips a Revit
                // warning would otherwise block the UI thread on a modal dialog
                // nobody can see, and a part Revit rolled back would report
                // success. CommitOrThrow turns that rollback into a failed line.
                using var t = new Transaction(doc, $"BINA part {id}");
                TxGuard.StartSwallowing(t);
                owned = buildPart(id, expected);
                TxGuard.CommitOrThrow(t);
            }
            catch (Exception e)
            {
                return new PartResult { Status = "failed",
                    Predicted = expected.ToString(),
                    Measured = $"build threw: {e.Message}" };
            }
            try
            {
                return PartMeasure.Measure(doc, id, expected, owned);
            }
            catch (Exception e)
            {
                return new PartResult { Status = "unverified",
                    Predicted = expected.ToString(),
                    Measured = $"measurement threw: {e.Message}" };
            }
        }

        /// <summary>Best-effort delete of a failed part's own elements. Returns
        /// the count of survivors — elements that outlived the attempt — so a
        /// caller can say so instead of the old behaviour, which swallowed the
        /// failure and reported nothing: the scorecard said "failed" while
        /// orphaned geometry sat in the model with no note anywhere that undo
        /// itself had not fully landed.</summary>
        private static int DeleteOwned(Document doc, string id, List<ElementId> owned)
        {
            if (owned.Count == 0) return 0;
            try
            {
                using var t = new Transaction(doc, $"BINA undo part {id}");
                TxGuard.StartSwallowing(t);
                doc.Delete(owned);
                TxGuard.CommitOrThrow(t);
                return 0;
            }
            catch { return owned.Count; }   // best-effort, but COUNTED now
        }
    }
}

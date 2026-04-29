using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Applies JKR compliance fixes (renames + parameter sets) via ExternalEvent.
    /// Runs on Revit's main thread.
    /// </summary>
    public class JkrRenameHandler : IExternalEventHandler
    {
        /// <summary>
        /// List of (ElementId, newName) pairs to rename.
        /// </summary>
        public List<(int ElementId, string NewName)> RenameQueue { get; set; } = new List<(int, string)>();

        /// <summary>
        /// List of parameter fixes to apply after renames.
        /// </summary>
        public List<JkrFixAction> ParamFixQueue { get; set; } = new List<JkrFixAction>();

        public Action<RenameResult> OnCompleted { get; set; }

        public void Execute(UIApplication app)
        {
            var result = new RenameResult();
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    result.Error = "No active document.";
                    OnCompleted?.Invoke(result);
                    return;
                }

                // Phase 1: Renames
                if (RenameQueue.Any())
                {
                    using (var tx = new Transaction(doc, "JKR Auto-Rename Elements"))
                    {
                        tx.Start();
                        foreach (var (elemId, newName) in RenameQueue)
                        {
                            try
                            {
                                var elem = doc.GetElement(new ElementId(elemId));
                                if (elem == null) { result.Skipped++; result.FailedElementIds.Add(elemId); continue; }

                                // Grids and Levels are Elements (not ElementTypes) — rename
                                // via their own Name property. Family/loadable types keep
                                // the elemType.Name path so shared instances all update.
                                if (elem is Grid || elem is Level)
                                {
                                    elem.Name = newName;
                                    result.Renamed++;
                                    continue;
                                }

                                ElementId typeId = elem.GetTypeId();
                                var elemType = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) as ElementType : null;

                                if (elemType != null)
                                {
                                    elemType.Name = newName;
                                    result.Renamed++;
                                }
                                else
                                {
                                    // Last-ditch: if the element itself has a settable Name, try it.
                                    try { elem.Name = newName; result.Renamed++; }
                                    catch { result.Skipped++; }
                                }
                            }
                            catch (Autodesk.Revit.Exceptions.ArgumentException)
                            {
                                result.Failed++;
                                result.FailedElementIds.Add(elemId);
                            }
                            catch (Exception)
                            {
                                result.Failed++;
                                result.FailedElementIds.Add(elemId);
                            }
                        }
                        tx.Commit();
                    }
                }

                // Phase 2: Parameter fixes (sorted by priority — classification before material before renames)
                if (ParamFixQueue.Any())
                {
                    var applicator = new JkrFixApplicator(doc);
                    var failReasons = new List<string>();
                    foreach (var fix in ParamFixQueue.OrderBy(f => f.Priority))
                    {
                        var fixResult = applicator.ApplyFix(fix);
                        if (fixResult.Success)
                            result.ParamFixed++;
                        else
                        {
                            result.Failed++;
                            result.FailedElementIds.Add(fix.ElementId);
                            failReasons.Add($"{fix.ParameterName} on {fix.ElementId}: {fixResult.Message}");
                        }
                    }
                    if (failReasons.Count > 0)
                        result.FailDetails = string.Join("\n", failReasons.Take(5));
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            finally
            {
                OnCompleted?.Invoke(result);
                RenameQueue.Clear();
                ParamFixQueue.Clear();
            }
        }

        public string GetName() => "JKR Auto-Fix Handler";

        // JKR category prefix mapping (from JKR Doc 03/09 spec)
        private static readonly Dictionary<string, string> CategoryPrefixMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Walls", "wll" }, { "Doors", "dor" }, { "Windows", "wdw" },
            { "Floors", "flr" }, { "Ceilings", "clg" }, { "Roofs", "rof" },
            { "Stairs", "str" }, { "Columns", "col" }, { "Structural Columns", "col" },
            { "Railings", "rln" }, { "Specialty Equipment", "seq" },
            { "Mechanical Equipment", "meq" }, { "Electrical Equipment", "eeq" },
            { "Electrical Fixtures", "efx" }, { "Lighting Fixtures", "lfx" },
            { "Plumbing Fixtures", "pfx" }, { "Furniture", "fur" },
            { "Curtain Panels", "cpn" }, { "Curtain Wall Mullions", "cwm" },
            { "Generic Models", "gen" }, { "Structural Framing", "sfr" },
            { "Structural Foundations", "sfd" }, { "Duct Accessories", "dac" },
            { "Pipe Accessories", "pac" }, { "Sprinklers", "spk" },
        };

        // Default subtypes per category (first/most common)
        private static readonly Dictionary<string, string> DefaultSubtype = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Walls", "b" }, { "Doors", "k" }, { "Windows", "b" },
            { "Floors", "k" }, { "Roofs", "k" }, { "Stairs", "k" },
            { "Columns", "k" }, { "Structural Columns", "k" },
        };

        /// <summary>
        /// Generate a JKR-compliant name from category and current type name.
        /// Format: jkr{Disc}_{prefix}-{subtype}_{description}
        /// Example: jkrAR_wll-b_(bb02) Batu Bata
        /// </summary>
        public static string GenerateJkrName(string discipline, string category, string currentTypeName)
        {
            string prefix;
            if (!CategoryPrefixMap.TryGetValue(category, out prefix))
            {
                // Fallback: clean category name
                prefix = Regex.Replace(category.Trim(), @"\s+", "").ToLower();
                if (prefix.Length > 3) prefix = prefix.Substring(0, 3);
            }

            // Subtype
            string subtype = "";
            if (DefaultSubtype.TryGetValue(category, out string sub))
                subtype = $"-{sub}";

            // Clean description from current name
            string desc = currentTypeName ?? "";
            // Strip common non-JKR prefixes
            foreach (var strip in new[] { "Basic Wall", "Basic", "Generic", "Default", "Standard" })
                desc = desc.Replace(strip, "").Trim();

            // Extract material code if present in parentheses like (bb02)
            string matPart = "";
            var matMatch = Regex.Match(desc, @"\(([a-z]{2}\d{0,2})\)", RegexOptions.IgnoreCase);
            if (matMatch.Success)
                matPart = $"_({matMatch.Groups[1].Value.ToLower()})";

            // Clean remaining description
            desc = Regex.Replace(desc, @"\([^)]*\)", "").Trim(); // remove parenthesized parts
            if (desc.Length > 30) desc = desc.Substring(0, 30).Trim();
            if (!string.IsNullOrWhiteSpace(desc))
                desc = $" {desc}";

            return $"jkr{discipline.ToUpper()}_{prefix}{subtype}{matPart}{desc}".Trim();
        }
    }

    public class RenameResult
    {
        public int Renamed { get; set; }
        public int ParamFixed { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public string Error { get; set; }
        public string FailDetails { get; set; } = "";
        /// <summary>Element IDs that failed/skipped — UI marks these as not auto-fixable.</summary>
        public HashSet<int> FailedElementIds { get; set; } = new HashSet<int>();
    }
}

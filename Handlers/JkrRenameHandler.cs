using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Renames element types to follow JKR naming convention via ExternalEvent.
    /// Runs on Revit's main thread.
    /// </summary>
    public class JkrRenameHandler : IExternalEventHandler
    {
        /// <summary>
        /// List of (ElementId, newName) pairs to rename.
        /// Set before raising the ExternalEvent.
        /// </summary>
        public List<(int ElementId, string NewName)> RenameQueue { get; set; } = new List<(int, string)>();

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

                using (var tx = new Transaction(doc, "JKR Auto-Rename Elements"))
                {
                    tx.Start();
                    foreach (var (elemId, newName) in RenameQueue)
                    {
                        try
                        {
                            var elem = doc.GetElement(new ElementId(elemId));
                            if (elem == null) { result.Skipped++; continue; }

                            // Get the element type (we rename the type, not the instance)
                            ElementId typeId = elem.GetTypeId();
                            var elemType = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) as ElementType : null;

                            if (elemType != null)
                            {
                                elemType.Name = newName;
                                result.Renamed++;
                            }
                            else
                            {
                                result.Skipped++;
                            }
                        }
                        catch (Autodesk.Revit.Exceptions.ArgumentException)
                        {
                            // Name already in use or invalid chars
                            result.Failed++;
                        }
                        catch (Exception)
                        {
                            result.Failed++;
                        }
                    }
                    tx.Commit();
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
            }
        }

        public string GetName() => "JKR Auto-Rename Handler";

        /// <summary>
        /// Generate a JKR-compliant name from category and current type name.
        /// Format: jkr{Discipline}_{category}_{originalName}
        /// </summary>
        public static string GenerateJkrName(string discipline, string category, string currentTypeName)
        {
            // Clean category: "Walls" → "wall", "Structural Columns" → "structural_column"
            string catClean = Regex.Replace(category.Trim(), @"\s+", "_").ToLower();
            // Remove trailing 's' for plural (simple)
            if (catClean.EndsWith("s") && !catClean.EndsWith("ss"))
                catClean = catClean.Substring(0, catClean.Length - 1);

            // Clean current name: keep alphanumeric, dots, hyphens, underscores
            string nameClean = Regex.Replace(currentTypeName.Trim(), @"[^\w.\-]", "_");
            nameClean = Regex.Replace(nameClean, @"_+", "_").Trim('_');

            // If name already starts with jkr, strip it to avoid jkr_jkr
            if (nameClean.StartsWith("jkr", StringComparison.OrdinalIgnoreCase))
                nameClean = nameClean.Substring(3).TrimStart('_');

            return $"jkr{discipline.ToUpper()}_{catClean}_{nameClean}";
        }
    }

    public class RenameResult
    {
        public int Renamed { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public string Error { get; set; }
    }
}

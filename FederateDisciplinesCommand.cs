using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class FederateDisciplinesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[BINA] Federation command started");

                // Get the current Revit document
                Document doc = commandData.Application.ActiveUIDocument.Document;
                
                if (doc == null)
                {
                    TaskDialog.Show("Error", "No active Revit document found.");
                    return Result.Failed;
                }

                // Check if the document is saved
                if (string.IsNullOrEmpty(doc.PathName))
                {
                    TaskDialog.Show("Error", "Please save your Revit document before federating discipline files.");
                    return Result.Failed;
                }

                // Find the BINA Downloads directory
                string downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "BINA_Downloads");
                
                if (!Directory.Exists(downloadDir))
                {
                    TaskDialog.Show("No Downloads Found", $"BINA Downloads directory not found at:\n{downloadDir}\n\nPlease download discipline files first using the 'Download BIM Disciplines' button.");
                    return Result.Failed;
                }

                // Find all .rvt files in the downloads directory
                var allRvtFiles = Directory.GetFiles(downloadDir, "*.rvt", SearchOption.TopDirectoryOnly).ToList();
                System.Diagnostics.Debug.WriteLine($"[BINA] Found {allRvtFiles.Count} .rvt files in {downloadDir}");
                
                // Log all files found for debugging
                foreach (var file in allRvtFiles)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Found file: {Path.GetFileName(file)}");
                }
                
                // Strict filtering - files must start with specific discipline prefixes
                var revitFiles = allRvtFiles
                    .Where(file => Path.GetFileName(file).StartsWith("Architecture_") ||
                                   Path.GetFileName(file).StartsWith("Structure_") ||
                                   Path.GetFileName(file).StartsWith("HVAC_") ||
                                   Path.GetFileName(file).StartsWith("Electrical_"))
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"[BINA] Filtered to {revitFiles.Count} discipline files");

                if (revitFiles.Count == 0)
                {
                    // Show all files found for user debugging
                    string allFilesInfo = allRvtFiles.Count > 0 ? 
                        $"\n\nFiles found in directory:\n{string.Join("\n", allRvtFiles.Select(Path.GetFileName))}\n\nExpected naming: Architecture_*, Structure_*, HVAC_*, Electrical_*" :
                        "\n\nNo .rvt files found in directory.";
                    
                    TaskDialog.Show("No Discipline Files", 
                        $"No discipline files found in:\n{downloadDir}\n\nExpected prefixes: Architecture_, Structure_, HVAC_, Electrical_{allFilesInfo}\n\nPlease download discipline files first using the 'Download BIM Disciplines' button.");
                    return Result.Failed;
                }

                // Show confirmation dialog with files to be linked
                string filesList = string.Join("\n", revitFiles.Select(Path.GetFileName));
                TaskDialog confirmDialog = new TaskDialog("Confirm Federation");
                confirmDialog.MainInstruction = $"Link {revitFiles.Count} discipline files to current document?";
                confirmDialog.MainContent = $"The following files will be linked as Revit Links:\n\n{filesList}\n\nThis will create federated references that can be managed in the Project Browser.";
                confirmDialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                confirmDialog.DefaultButton = TaskDialogResult.Yes;

                var confirmResult = confirmDialog.Show();
                if (confirmResult != TaskDialogResult.Yes)
                {
                    return Result.Cancelled;
                }

                // Start linking process
                var linkedFiles = new List<string>();
                var skippedFiles = new List<string>();
                var errorFiles = new List<string>();

                using (Transaction trans = new Transaction(doc, "Link BIM Discipline Files"))
                {
                    trans.Start();

                    try
                    {
                        foreach (string filePath in revitFiles)
                        {
                            try
                            {
                                string fileName = Path.GetFileNameWithoutExtension(filePath);
                                
                                // Check if a link with similar name already exists
                                var existingLinks = new List<RevitLinkType>();
                                var collector = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType));
                                foreach (RevitLinkType linkType in collector)
                                {
                                    if (linkType.Name.Contains(fileName.Split('_')[0])) // Match discipline name
                                    {
                                        existingLinks.Add(linkType);
                                    }
                                }

                                if (existingLinks.Count > 0)
                                {
                                    // Ask user if they want to reload the existing link or skip
                                    TaskDialog reloadDialog = new TaskDialog("Link Already Exists");
                                    reloadDialog.MainInstruction = $"A link for {fileName.Split('_')[0]} discipline already exists.";
                                    reloadDialog.MainContent = $"Existing link: {existingLinks[0].Name}\nNew file: {fileName}\n\nReload with new file?";
                                    reloadDialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                                    reloadDialog.DefaultButton = TaskDialogResult.Yes;

                                    var reloadResult = reloadDialog.Show();
                                    if (reloadResult == TaskDialogResult.No)
                                    {
                                        skippedFiles.Add($"{fileName} (link already exists)");
                                        continue;
                                    }

                                    // Reload the existing link with the new file
                                    var existingLink = existingLinks[0];
                                    ModelPath newModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                                    existingLink.LoadFrom(newModelPath, new WorksetConfiguration());
                                    linkedFiles.Add($"{fileName} (reloaded existing link)");
                                }
                                else
                                {
                                    // Create new link
                                    ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                                    
                                    try
                                    {
                                        // Create the link type
                                        RevitLinkOptions linkOptions = new RevitLinkOptions(false); // false = don't create new workset
                                        LinkLoadResult linkLoadResult = RevitLinkType.Create(doc, modelPath, linkOptions);
                                        
                                        if (linkLoadResult != null)
                                        {
                                            // Find the created RevitLinkType
                                            var linkTypes = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType));
                                            RevitLinkType createdLinkType = null;
                                            
                                            foreach (RevitLinkType linkType in linkTypes)
                                            {
                                                if (linkType.Name.Contains(Path.GetFileNameWithoutExtension(filePath)))
                                                {
                                                    createdLinkType = linkType;
                                                    break;
                                                }
                                            }
                                            
                                            if (createdLinkType != null)
                                            {
                                                // Create the link instance at origin
                                                RevitLinkInstance.Create(doc, createdLinkType.Id);
                                                linkedFiles.Add(fileName);
                                            }
                                            else
                                            {
                                                errorFiles.Add($"{fileName} (could not find created link type)");
                                            }
                                        }
                                        else
                                        {
                                            errorFiles.Add($"{fileName} (failed to create link type)");
                                        }
                                    }
                                    catch (Exception linkEx)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[BINA] Link creation error for {fileName}: {linkEx.Message}");
                                        errorFiles.Add($"{fileName} (link creation failed: {linkEx.Message})");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[BINA] Error linking file {filePath}: {ex.Message}");
                                errorFiles.Add($"{Path.GetFileName(filePath)} ({ex.Message})");
                            }
                        }

                        trans.Commit();
                    }
                    catch (Exception ex)
                    {
                        trans.RollBack();
                        TaskDialog.Show("Transaction Error", $"Failed to complete linking transaction: {ex.Message}");
                        return Result.Failed;
                    }
                }

                // Show results
                string resultMessage = "";
                
                if (linkedFiles.Count > 0)
                {
                    resultMessage += $"✅ Successfully linked {linkedFiles.Count} files:\n";
                    resultMessage += string.Join("\n", linkedFiles.Select(f => $"• {f}"));
                    resultMessage += "\n\n";
                }

                if (skippedFiles.Count > 0)
                {
                    resultMessage += $"⏭️ Skipped {skippedFiles.Count} files:\n";
                    resultMessage += string.Join("\n", skippedFiles.Select(f => $"• {f}"));
                    resultMessage += "\n\n";
                }

                if (errorFiles.Count > 0)
                {
                    resultMessage += $"❌ Failed to link {errorFiles.Count} files:\n";
                    resultMessage += string.Join("\n", errorFiles.Select(f => $"• {f}"));
                    resultMessage += "\n\n";
                }

                resultMessage += "Linked files are now available in the Project Browser under 'Revit Links'.\n\n";
                resultMessage += "💡 Tips:\n";
                resultMessage += "• Use Manage Links to control link visibility\n";
                resultMessage += "• Links will update automatically when source files change\n";
                resultMessage += "• Use 3D view to see all disciplines together";

                TaskDialog.Show("Federation Complete!", resultMessage);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred during federation: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[BINA] Federation error: {ex}");
                return Result.Failed;
            }
        }
    }
}
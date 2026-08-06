using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.FamilyLibrary;

namespace RevitWebAppSync
{
    /// <summary>
    /// Ribbon entry point for the Family Library: opens the browse dialog and,
    /// if the drafter picks something, loads it into the open document.
    ///
    /// The dialog only chooses; the load happens here. That keeps the Revit API
    /// work inside the command's context — where a Transaction is legal — and
    /// lets this reuse <c>Mutators.LoadFamily</c>, the same loader the copilot's
    /// load_family tool calls. A family therefore lands in the model identically
    /// whether the drafter picked it or the AI did: same overwrite handling,
    /// same .rvt-container extraction, same idempotency.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class FamilyLibraryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                var uiApp = commandData.Application;
                if (uiApp?.ActiveUIDocument?.Document == null)
                {
                    TaskDialog.Show("BINA Family Library",
                        "Open a project first — a family needs somewhere to load into.");
                    return Result.Cancelled;
                }

                var config = BinaConfig.Load();
                if (!config.IsLoggedIn())
                {
                    TaskDialog.Show("Not Logged In",
                        "Please log in with the 'Login' button before browsing the family library.");
                    return Result.Cancelled;
                }

                // Drives the grid's version filter: families authored in a newer
                // Revit than this one cannot be loaded, so they're greyed out.
                int? revitVersion = null;
                if (int.TryParse(uiApp.Application.VersionNumber, out var parsed))
                    revitVersion = parsed;

                var dialog = new FamilyLibraryWindow(config.AccessToken, revitVersion);
                if (dialog.ShowDialog() != true || dialog.SelectedFamily == null)
                    return Result.Cancelled;

                return LoadSelected(uiApp, dialog.SelectedFamily, config.AccessToken, ref message);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BINA Family Library", $"Something went wrong: {ex.Message}");
                return Result.Failed;
            }
        }

        private Result LoadSelected(
            UIApplication uiApp, FamilyLibraryItem family, string accessToken, ref string message)
        {
            FamilyDownloadTicket ticket;
            try
            {
                // Fetched now rather than when the grid was drawn: the link is
                // short-lived, and a drafter may browse for a while first.
                // GetAwaiter().GetResult() rather than .Result so the original
                // exception surfaces instead of an AggregateException wrapper.
                ticket = Task.Run(() => FamilyLibraryApi
                        .GetDownloadTicketAsync(accessToken, family.LibraryId))
                    .GetAwaiter().GetResult();
            }
            catch (FamilyLibraryException fex)
            {
                // Already phrased for the user by the API client.
                TaskDialog.Show("BINA Family Library", fex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BINA Family Library",
                    $"Could not get a download link for {family.FamilyName}.\n\n{ex.Message}");
                return Result.Failed;
            }

            if (ticket == null || string.IsNullOrEmpty(ticket.DownloadUrl))
            {
                TaskDialog.Show("BINA Family Library",
                    $"No download is available for {family.FamilyName} right now.");
                return Result.Failed;
            }

            try
            {
                var args = BuildLoadArgs(ticket);
                var result = BinaVibe.Mcp.Tools.Mutators.LoadFamily(uiApp, args);

                var loadedTypes = result.TryGetValue("loaded_types", out var t)
                    ? t as List<string>
                    : null;
                var alreadyLoaded = result.TryGetValue("already_loaded", out var a)
                                    && a is bool b && b;

                TaskDialog.Show("BINA Family Library", alreadyLoaded
                    ? $"{ticket.FamilyName} is already in this project.\n\n" +
                      $"{Describe(loadedTypes)}"
                    : $"Loaded {ticket.FamilyName}.\n\n{Describe(loadedTypes)}\n\n" +
                      "It's now in the Project Browser, ready to place.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BINA Family Library",
                    $"Could not load {ticket.FamilyName}.\n\n{ex.Message}");
                return Result.Failed;
            }
        }

        /// <summary>
        /// Shape the ticket into the argument object Mutators.LoadFamily reads.
        /// Going through JSON rather than a typed overload is what lets the
        /// manual path reuse the tool loader untouched.
        /// </summary>
        private static JsonElement BuildLoadArgs(FamilyDownloadTicket ticket)
        {
            var payload = new
            {
                download_url = ticket.DownloadUrl,
                file_type = ticket.FileType,
                family_name = ticket.FamilyName,
                source_names = ticket.SourceNames ?? new List<string>(),
            };
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            // Clone: the element is only valid while the document lives, and
            // this one is disposed on return.
            return doc.RootElement.Clone();
        }

        private static string Describe(List<string> loadedTypes)
        {
            if (loadedTypes == null || loadedTypes.Count == 0)
                return "No types reported.";
            if (loadedTypes.Count <= 6)
                return "Types: " + string.Join(", ", loadedTypes);
            return $"{loadedTypes.Count} types, including: " +
                   string.Join(", ", loadedTypes.GetRange(0, 6)) + "…";
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace BinaConnector
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class App : IExternalApplication
    {
        private const string TabName = "BINA";
        private const string PanelName = "Cloud Sync";

        public Result OnStartup(UIControlledApplication application)
        {
            // Ribbon initialization must never fail loudly. If something goes wrong, log it and
            // return Succeeded so we don't surface a Revit dialog every launch.
            try
            {
                CreateRibbon(application);
            }
            catch (Exception ex)
            {
                TryWriteStartupLog(ex);
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        private void CreateRibbon(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }

            RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            var uploadData = new PushButtonData(
                "BinaUpload", "Upload\nto BINA", assemblyPath,
                "BinaConnector.Commands.UploadCommand")
            {
                ToolTip = "Upload the active Revit model to BINA Cloud",
                LongDescription = "Sends the current Revit document to BINA Cloud (BIMCloudX). " +
                                  "You will be asked to choose a discipline if no default is set in Project Settings.",
                Image = LoadEmbedded("BinaConnector.Resources.upload_16.png", 16),
                LargeImage = LoadEmbedded("BinaConnector.Resources.upload_32.png", 32)
            };

            var settingsData = new PushButtonData(
                "BinaProjectSettings", "Project\nSettings", assemblyPath,
                "BinaConnector.Commands.ProjectSettingsCommand")
            {
                ToolTip = "Choose the active BINA project and upload preferences",
                LongDescription = "Switch which BINA Cloud project this connector uploads to, set a default " +
                                  "discipline, and toggle the upload confirmation dialog.",
                Image = LoadEmbedded("BinaConnector.Resources.settings_16.png", 16),
                LargeImage = LoadEmbedded("BinaConnector.Resources.settings_32.png", 32)
            };

            var accountData = new PushButtonData(
                "BinaAccount", "Sign In /\nAccount", assemblyPath,
                "BinaConnector.Commands.AccountCommand")
            {
                ToolTip = "Sign in to BINA Cloud or manage your account",
                LongDescription = "Sign in with your BINA Cloud credentials. If already signed in, view your " +
                                  "account, switch project, or sign out.",
                Image = LoadEmbedded("BinaConnector.Resources.account_16.png", 16),
                LargeImage = LoadEmbedded("BinaConnector.Resources.account_32.png", 32)
            };

            // Layout: large Upload on the left; Settings + Account stacked on the right.
            var uploadButton = panel.AddItem(uploadData) as PushButton;
            IList<RibbonItem> stacked = panel.AddStackedItems(settingsData, accountData);
            var settingsButton = stacked[0] as PushButton;
            var accountButton = stacked[1] as PushButton;

            ContextualHelp help = TryBuildContextualHelp(assemblyPath);
            if (help != null)
            {
                uploadButton?.SetContextualHelp(help);
                settingsButton?.SetContextualHelp(help);
                accountButton?.SetContextualHelp(help);
            }
        }

        private static ContextualHelp TryBuildContextualHelp(string assemblyPath)
        {
            try
            {
                // In the App Store bundle layout, help lives at ../Resources/help/index.html
                // relative to the addin DLL. ContextualHelpType.ChmFile accepts HTML files.
                string addinDir = Path.GetDirectoryName(assemblyPath);
                if (string.IsNullOrEmpty(addinDir)) return null;
                string helpPath = Path.GetFullPath(Path.Combine(addinDir, "..", "Resources", "help", "index.html"));
                return new ContextualHelp(ContextualHelpType.ChmFile, helpPath);
            }
            catch
            {
                return null;
            }
        }

        private static BitmapImage LoadEmbedded(string resourceName, int size)
        {
            try
            {
                using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null) return null;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.DecodePixelWidth = size;
                bitmap.DecodePixelHeight = size;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static void TryWriteStartupLog(Exception ex)
        {
            try
            {
                Paths.EnsureDirectories();
                string logPath = Path.Combine(Paths.LogDirectory, "startup.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] OnStartup failed: {ex}{Environment.NewLine}");
            }
            catch
            {
                // If even logging fails, swallow — we must not throw from OnStartup.
            }
        }
    }
}

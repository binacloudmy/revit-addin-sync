namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Test-only stand-in for the add-in's ReportExporter.EnsureQuestPdfReady (the
    /// real one adds Revit's native-DLL search path; in the test host NuGet puts
    /// QuestPDF's native engine next to the test assembly).
    /// </summary>
    internal static class ReportExporter
    {
        internal static void EnsureQuestPdfReady() =>
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }
}

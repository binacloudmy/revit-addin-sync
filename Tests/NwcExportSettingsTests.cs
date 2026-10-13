using RevitWebAppSync.Services;
using Xunit;

// ClickUp 86d49v9ak — the policy half of the NWC export.
//
// NwcExporter.cs is Revit's own Navisworks exporter wrapped in the transactions it
// needs, and it cannot be reached from here: every meaningful call takes a live
// Document. So the decisions live in NwcExportSettings.cs instead — the checklist's
// ticks, the file-name rule, which Revit years can purge — and this file pins them.
// If a rule moves into the exporter, it stops being tested; keep it here.
public class NwcExportSettingsTests
{
    // ---------------------------------------------------------------- file naming

    [Theory]
    [InlineData("Tower.rvt", "Tower.nwc")]
    [InlineData("Tower A.rvt", "Tower A.nwc")]
    [InlineData("C:\\Projects\\Tower A.rvt", "Tower A.nwc")]
    [InlineData("C:/Projects/JKR/Bangunan 1.rvt", "Bangunan 1.nwc")]
    [InlineData("Tower", "Tower.nwc")]
    [InlineData("Tower.rvt.rvt", "Tower.rvt.nwc")]
    public void BuildFileNameNamesTheNwcAfterTheModel(string rvt, string expected)
    {
        Assert.Equal(expected, NwcExportSettings.BuildFileName(rvt));
    }

    // A Cloud Docs download is "Model-v3.rvt"; the version suffix is not part of the
    // model's real name, and an NWC called Model-v3.nwc beside a design called Model
    // reads as a different model to everyone federating it.
    [Theory]
    [InlineData("Tower-v3.rvt", "Tower.nwc")]
    [InlineData("Tower-v12.rvt", "Tower.nwc")]
    [InlineData("Tower_v2.rvt", "Tower.nwc")]
    [InlineData("Tower-V7.rvt", "Tower.nwc")]
    public void BuildFileNameDropsTheCloudDocsVersionSuffix(string rvt, string expected)
    {
        Assert.Equal(expected, NwcExportSettings.BuildFileName(rvt));
    }

    // "-v" mid-name is a real part of a name; only a trailing suffix is a version.
    [Fact]
    public void BuildFileNameLeavesVersionLikeTextAloneWhenItIsNotTrailing()
    {
        Assert.Equal("Tower-v3-east.nwc", NwcExportSettings.BuildFileName("Tower-v3-east.rvt"));
        Assert.Equal("v3.nwc", NwcExportSettings.BuildFileName("v3.rvt"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildFileNameFallsBackToModelWhenThereIsNoName(string rvt)
    {
        Assert.Equal("Model.nwc", NwcExportSettings.BuildFileName(rvt));
    }

    // ------------------------------------------------------------- name sanitising

    // Listed explicitly rather than Path.GetInvalidFileNameChars(), because that answer
    // depends on the OS the caller runs on — a name legal on Linux (Model:2) is not legal
    // on the Windows box doing the export.
    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData(':')]
    [InlineData('"')]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    public void SanitizeReplacesEveryWindowsIllegalCharacter(char illegal)
    {
        Assert.Equal("Mo_el", NwcExportSettings.SanitizeFileStem($"Mo{illegal}el"));
    }

    [Fact]
    public void SanitizeReplacesNulAndControlCharacters()
    {
        Assert.Equal("Mo_el", NwcExportSettings.SanitizeFileStem("Mo\0el"));
        Assert.Equal("Mo_el", NwcExportSettings.SanitizeFileStem("Mo\tel"));
        Assert.Equal("Mo_el", NwcExportSettings.SanitizeFileStem("Mo\nel"));
    }

    [Fact]
    public void SanitizeKeepsOrdinaryCharactersIncludingSpacesAndDashes()
    {
        Assert.Equal("Bangunan 1 - Blok A (rev2)", NwcExportSettings.SanitizeFileStem("Bangunan 1 - Blok A (rev2)"));
    }

    // Windows strips these itself, which would make the file the exporter writes differ
    // from the name handed to the link API — the link is by name.
    [Fact]
    public void SanitizeTrimsTrailingDotsAndSpaces()
    {
        Assert.Equal("Tower", NwcExportSettings.SanitizeFileStem("Tower. "));
        Assert.Equal("Tower", NwcExportSettings.SanitizeFileStem("Tower..."));
        Assert.Equal("Tower", NwcExportSettings.SanitizeFileStem("Tower   "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("???")]
    public void SanitizeFallsBackToModelWhenNothingIsLeft(string stem)
    {
        Assert.Equal("Model", NwcExportSettings.SanitizeFileStem(stem));
    }

    [Fact]
    public void SanitizedNamesAreAlwaysNonEmptyTrimmedAndFreeOfIllegalCharacters()
    {
        string[] hostile =
        {
            "a:b*c?d", "  ..  ", "AUX", "NUL.rvt", "totally/illegal\\name", new string('x', 300)
        };

        foreach (var stem in hostile)
        {
            string cleaned = NwcExportSettings.SanitizeFileStem(stem);

            Assert.False(string.IsNullOrWhiteSpace(cleaned));
            Assert.Equal(cleaned.TrimEnd(' ', '.'), cleaned);

            foreach (char illegal in System.IO.Path.GetInvalidFileNameChars())
            {
                Assert.Equal(-1, cleaned.IndexOf(illegal));
            }
        }
    }

    // ------------------------------------------------------------- purge capability

    // Purge calls Document.GetUnusedElements, which is Revit 2024 API, while the add-in
    // is compiled once against 2023 references for both 2023 and 2024 — so the year has
    // to decide, and 2023 has to get an honest "not available" rather than a no-op.
    [Theory]
    [InlineData(2023, false)]
    [InlineData(2024, true)]
    [InlineData(2025, true)]
    [InlineData(2027, true)]
    [InlineData(0, false)]
    public void PurgeNeedsRevit2024OrNewer(int year, bool expected)
    {
        Assert.Equal(expected, NwcExportSettings.IsPurgeAvailable(year));
    }

    // ------------------------------------------------------------------- defaults

    // The two checklists disagree: "Revit to NWC rev a" says Shared, the older TCDR01
    // slides say Project Internal. A federated model only lines up if every discipline
    // exported the same system, and rev a is what coordinators tick today.
    [Fact]
    public void DefaultsAreSharedCoordinatesWithoutPurging()
    {
        var settings = NwcExportSettings.Default;

        Assert.Equal(NwcCoordinates.Shared, settings.Coordinates);
        Assert.False(settings.PurgeUnused);
    }

    // ------------------------------------------------------------------- the ticks

    [Fact]
    public void OptionsSpecMatchesTheChecklist()
    {
        var spec = NwcExportSettings.Default.ToOptionsSpec();

        // Export = Current view, with the Element IDs tick.
        Assert.True(spec.ExportScopeIsView);
        Assert.True(spec.ExportElementIds);

        // "Convert linked files" unticked: each discipline exports its own NWC and the
        // coordinator federates them, so converting links here would double-count.
        Assert.False(spec.ExportLinks);

        // Linked CAD still converts — only Revit links are excluded.
        Assert.True(spec.ConvertLinkedCADFormats);

        Assert.True(spec.ExportRoomAsAttribute);
        Assert.True(spec.ExportRoomGeometry);
        Assert.True(spec.DivideFileIntoLevels);
        Assert.True(spec.ExportUrls);

        // "Convert construction parts" unticked.
        Assert.False(spec.ExportParts);

        Assert.False(spec.ConvertElementProperties);
        Assert.False(spec.ConvertLights);

        // Faceting 1 — finer faceting makes a huge NWC for no visible gain.
        Assert.Equal(1, spec.FacetingFactor);

        Assert.True(spec.FindMissingMaterials);
        Assert.True(spec.ExportAllParameters);
    }

    [Theory]
    [InlineData(NwcCoordinates.Shared)]
    [InlineData(NwcCoordinates.Internal)]
    public void OptionsSpecCarriesTheChosenCoordinateSystem(NwcCoordinates coordinates)
    {
        var settings = new NwcExportSettings { Coordinates = coordinates, PurgeUnused = true };

        Assert.Equal(coordinates, settings.ToOptionsSpec().Coordinates);
    }

    [Fact]
    public void CategoryRulesHideEverythingThatIsNotTheModel()
    {
        var rules = NwcExportSettings.Default.CategoryRules;

        Assert.True(rules.ModelVisible);
        Assert.True(rules.HideAnnotationCategories);
        Assert.True(rules.HideAnalyticalCategories);
        Assert.True(rules.HideImportedCategories);

        // Links hidden for the same reason the export options exclude them.
        Assert.True(rules.HideRevitLinks);

        // The checklist never asks for filters to be cleared, and clearing them would
        // change what v1 exports versus v2 without anyone asking.
        Assert.True(rules.LeavesFiltersAlone);
    }

    // The temporary view is created inside a rolled-back transaction group, so its name
    // has to be one no coordinator would ever give a real view — and recognisable if it
    // ever does leak into a saved model.
    [Fact]
    public void TempViewNameAnnouncesItselfAsTemporary()
    {
        Assert.Contains("temporary", NwcExportSettings.TempViewName, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BINA", NwcExportSettings.TempViewName);
    }

    // Autodesk ships the exporter separately, one build per Revit year, so the message
    // for a missing one has to be actionable on its own.
    [Fact]
    public void ExporterDownloadUrlIsAnAutodeskHttpsPage()
    {
        Assert.StartsWith("https://www.autodesk.com/", NwcExportSettings.ExporterDownloadUrl);
    }

    // ------------------------------------------------------------------ failure types

    [Theory]
    [InlineData(NwcExportFailure.ExporterMissing, false)]
    [InlineData(NwcExportFailure.FolderNotWritable, false)]
    [InlineData(NwcExportFailure.ExportFailed, true)]
    public void OnlyAnExporterFailureIsWorthRetrying(NwcExportFailure kind, bool retryable)
    {
        var ex = new NwcExportException(kind, "some message");

        Assert.Equal(kind, ex.Kind);
        Assert.Equal("some message", ex.Message);
        Assert.Equal(retryable, ex.IsRetryable);
    }
}

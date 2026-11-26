using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    /// <summary>Cases from the Revit-Test-v14 comparison of the picker with the Project Browser's Families rows (26 Nov 2026).</summary>
    public class BrowserFamiliesTests
    {
        [Theory]
        [InlineData("OST_Doors", "Model", "FamilySymbol")]
        [InlineData("OST_Walls", "Model", "WallType")]
        [InlineData("OST_PipeCurves", "Model", "PipeType")]
        [InlineData("OST_PipingSystem", "Model", "PipingSystemType")]
        [InlineData("OST_TilePatterns", "Internal", "TilePattern")]
        [InlineData("OST_MEPSystemZone", "AnalyticalModel", "SystemZoneElementType")]
        [InlineData("OST_LinksAnalytical", "AnalyticalModel", "AnalyticalLinkType")]
        [InlineData("OST_DoorTags", "Annotation", "FamilySymbol")]
        [InlineData("OST_GenericAnnotation", "Annotation", "AnnotationSymbolType")]
        [InlineData("OST_RoomTags", "Annotation", "RoomTagType")]
        public void The_browser_lists(string key, string type, string cls) => Assert.True(BrowserFamilies.Listed(key, type, cls));

        [Theory]
        [InlineData("OST_Fluids", "Model", "FluidType")]
        [InlineData("OST_PipeMaterials", "Model", "ElementType")]
        [InlineData("OST_WireInsulations", "Model", "ElementType")]
        [InlineData("OST_RoofSoffit", "Model", "CeilingType")]
        [InlineData("OST_StackedWalls", "Model", "WallType")]
        [InlineData("OST_StairsRailingBaluster", "Model", "FamilySymbol")]
        [InlineData("OST_RailingHandRail", "Model", "HandRailType")]
        [InlineData("OST_MassWallsAll", "Model", "ConceptualConstructionType")]
        [InlineData("OST_RvtLinks", "Model", "RevitLinkType")]
        [InlineData("", "Model", "CADLinkType")]
        [InlineData("OST_RasterImages", "Model", "ImageType")]
        [InlineData("OST_Grids", "Annotation", "GridType")]
        [InlineData("OST_Levels", "Annotation", "LevelType")]
        [InlineData("OST_AnalyticalPipeConnections", "AnalyticalModel", "MEPAnalyticalConnectionType")]
        public void The_browser_does_not_list(string key, string type, string cls) => Assert.False(BrowserFamilies.Listed(key, type, cls));

        [Fact]
        public void Annotation_families_sit_under_one_category()
        {
            Assert.Equal("Annotation Symbols", BrowserFamilies.CategoryLabel("Door Tags", "Annotation"));
            Assert.Equal("Doors", BrowserFamilies.CategoryLabel("Doors", "Model"));
        }
    }
}

// r18 audit: the force path's annotation exemption is an explicit allow-list (NonModelPolicy). Class chains are Revit's own type
// names (element class first, then its bases). Revit-free.
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class NonModelPolicyTests
    {
        [Theory]
        [InlineData("IndependentTag", "SpatialElementTag")]
        [InlineData("RoomTag", "SpatialElementTag")]
        [InlineData("SpaceTag", "SpatialElementTag")]
        [InlineData("Dimension", "Element")]
        [InlineData("SpotDimension", "Dimension")]
        [InlineData("TextNote", "TextElement")]
        [InlineData("AnnotationSymbol", "FamilyInstance")]
        public void AllowListedAnnotation_IsExempt(string cls, string baseCls)
        {
            Assert.True(NonModelPolicy.IsExempt(new[] { cls, baseCls, "Element" }, added: false));
        }

        [Theory]
        [InlineData("Level", "DatumPlane")]
        [InlineData("Grid", "DatumPlane")]
        [InlineData("ReferencePlane", "Element")]
        [InlineData("Element", "Element")]                // a scope box is a plain Element of OST_VolumeOfInterest
        [InlineData("ViewPlan", "View")]
        [InlineData("ViewSheet", "View")]
        [InlineData("ViewSchedule", "TableView")]
        [InlineData("AnalyticalMember", "AnalyticalElement")]
        [InlineData("DetailLine", "DetailCurve")]
        [InlineData("FilledRegion", "Element")]
        [InlineData("FamilyInstance", "Instance")]       // detail components and other view-specific content
        [InlineData("Duct", "MEPCurve")]
        public void EverythingElse_StillDrifts(string cls, string baseCls)
        {
            Assert.False(NonModelPolicy.IsExempt(new[] { cls, baseCls, "Element" }, added: false));
            Assert.False(NonModelPolicy.IsExempt(new[] { cls, baseCls, "Element" }, added: true));
        }

        [Fact]
        public void AnElementType_IsExemptOnlyWhenTheWriteAddedIt()
        {
            var ductType = new[] { "DuctType", "MEPCurveType", "HostObjAttributes", "ElementType", "Element" };
            var symbol = new[] { "FamilySymbol", "InsertableObject", "ElementType", "Element" };
            Assert.True(NonModelPolicy.IsExempt(ductType, added: true));
            Assert.True(NonModelPolicy.IsExempt(symbol, added: true));
            Assert.False(NonModelPolicy.IsExempt(ductType, added: false));
            Assert.False(NonModelPolicy.IsExempt(symbol, added: false));
        }
    }
}

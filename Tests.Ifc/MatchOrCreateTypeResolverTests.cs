// Tests.Ifc/MatchOrCreateTypeResolverTests.cs
using System.Collections.Generic;
using BinaVibe.Mcp.Tools.IfcConvert;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class MatchOrCreateTypeResolverTests
    {
        readonly ITypeResolver _r = new MatchOrCreateTypeResolver(toleranceMm: 2.0);

        [Fact]
        public void Resolve_ThicknessWithinTolerance_MatchesExistingType()
        {
            var existing = new List<ExistingType> { new("Generic - 200mm", 200.0), new("Generic - 100mm", 100.0) };
            var res = _r.Resolve(IfcEntity.Wall, 201.0, "Batu Bata", null, existing);
            Assert.False(res.NeedsCreate);
            Assert.Equal("Generic - 200mm", res.TypeName);
        }

        [Fact]
        public void Resolve_ExactNameMatch_PreferredOverThicknessOnly()
        {
            var existing = new List<ExistingType> { new("Batu Bata", 200.0), new("Generic - 200mm", 200.0) };
            var res = _r.Resolve(IfcEntity.Wall, 200.0, "Batu Bata", null, existing);
            Assert.Equal("Batu Bata", res.TypeName);
        }

        [Fact]
        public void Resolve_NoMatch_ReturnsDeterministicCreateSpec()
        {
            var existing = new List<ExistingType> { new("Generic - 100mm", 100.0) };
            var res = _r.Resolve(IfcEntity.Wall, 250.0, "Concrete", "Concrete", existing);
            Assert.True(res.NeedsCreate);
            Assert.Equal("IFC Wall 250mm", res.TypeName);
            Assert.Equal(250.0, res.CreateThicknessMm);
            Assert.Equal("Concrete", res.CreateMaterial);
        }
    }
}

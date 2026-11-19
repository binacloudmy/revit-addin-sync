#nullable enable
// Issue 7 (door list, 0.0.144): find_elements grouped rows by the type name alone, so size-named door types of different
// families ("750 x 2100" in "Single Flush" and in "Double Glass") merged into one group with the wrong count. A type group of
// family instances now keys on "Family: Type"; a system type (walls, floors, ceilings) keeps its bare name, and each row's
// own "type" stays the bare type name.
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FamilyTypeKey0145Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void FamilyInstance_KeysOnFamilyAndType()
        {
            Assert.Equal("Single Flush: 750 x 2100", FoundRow.TypeKeyOf("750 x 2100", "Single Flush", familyInstance: true));
            Assert.Equal("Double Glass: 750 x 2100", FoundRow.TypeKeyOf("750 x 2100", "Double Glass", familyInstance: true));
        }

        [Fact]
        public void SystemType_KeepsTheBareName()
        {
            // RevitValues.FamilyName gives a system family too ("Basic Wall"): it is not part of the key.
            Assert.Equal("Generic - 200mm", FoundRow.TypeKeyOf("Generic - 200mm", "Basic Wall", familyInstance: false));
            Assert.Equal("CL-20 Plasterboard", FoundRow.TypeKeyOf("CL-20 Plasterboard", "Compound Ceiling", familyInstance: false));
        }

        [Fact]
        public void NoFamily_OrSameName_OrNoType_StaysAsIs()
        {
            Assert.Equal("D1", FoundRow.TypeKeyOf("D1", null, true));
            Assert.Equal("D1", FoundRow.TypeKeyOf("D1", " ", true));
            Assert.Equal("D1", FoundRow.TypeKeyOf("D1", "D1", true));
            Assert.Null(FoundRow.TypeKeyOf(null, "Single Flush", true));
        }

        private static FoundRow Door(long id, string family, string type) => new FoundRow
        {
            Ref = new ElementRef(id, null), Category = "Doors", Level = "Level 1", Type = type, Family = family, FamilyInstance = true,
        };

        [Fact]
        public void Summary_SplitsSameSizeTypesOfTwoFamilies()
        {
            var outcome = new FindOutcome { Sources = { "host" }, Categories = { "Doors" }, Handle = "h_00000001" };
            for (var i = 0; i < 3; i++) outcome.Rows.Add(Door(i + 1, "Single Flush", "750 x 2100"));
            for (var i = 0; i < 2; i++) outcome.Rows.Add(Door(i + 10, "Double Glass", "750 x 2100"));
            outcome.Queries.Add(new QueryOutcome { Count = 5, Filter = "category = Doors", Subject = "Doors" });
            Assert.True(QueryParser.TryParse(System.Text.Json.JsonDocument.Parse("{\"queries\":[{\"category\":\"Doors\"}]}").RootElement.Clone(), out var plan, out var error), error);
            var env = FindResultBuilder.Build(plan, outcome);
            Assert.Contains("3 Single Flush: 750 x 2100", env.Summary);
            Assert.Contains("2 Double Glass: 750 x 2100", env.Summary);
            // Each row still names its bare type.
            var rows = env.Data!.Value.GetProperty("rows");
            Assert.Equal("750 x 2100", rows[0].GetProperty("type").GetString());
        }

        [Fact]
        public void Executor_GroupsOnTheTypeKey_AndMarksFamilyInstances()
        {
            var s = Src("BinaVibe/Copilot/Read/FindExecutor.cs");
            Assert.Contains("? new List<string?> { h.Row.TypeKey }", s);
            Assert.Contains("case \"type\": return h.Row.TypeKey ?? \"(no type)\";", s);
            Assert.Contains("FamilyInstance = el is FamilyInstance,", s);
        }
    }
}

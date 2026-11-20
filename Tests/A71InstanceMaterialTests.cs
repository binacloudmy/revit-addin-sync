using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-419: A7.1 must flag placed elements with no material of their own even when the
    /// type has one through a Material-typed parameter. Fixture only (no Revit).</summary>
    public class A71InstanceMaterialTests
    {
        private static SpecType GenericModelType(int n, long firstId, params string[] materials)
        {
            var t = new SpecType { Category = "Generic Models", FamilyName = "AKs001a", TypeName = "AKs001a", InstanceCount = n, TypeId = firstId + 10000 };
            for (var i = 0; i < n; i++) t.InstanceIds.Add(firstId + i);
            t.Materials.AddRange(materials);
            return t;
        }

        private static List<SpecResult> A71(SpecModel m) => SpecChecks.Run(m).Where(r => r.CheckId == "A7.1").ToList();

        [Fact]
        public void Type_without_any_material_is_still_flagged_with_all_instances()
        {
            var f = Assert.Single(A71(new SpecModel { Types = { GenericModelType(3, 1839886) } }).Where(r => SpecReport.IsFail(r.Status)));
            Assert.Equal(3, f.ElementIds.Count);
        }

        [Fact]
        public void Type_material_from_parameter_does_not_hide_instances_with_no_material()
        {
            var t = GenericModelType(3, 1839886, "jkrAR_LT-f_(LTq02)-3 2 Lapisan Polyurethene Coating");
            t.NoMaterialInstanceIds.AddRange(t.InstanceIds); t.NoMaterialInstanceCount = 3;
            var f = Assert.Single(A71(new SpecModel { Types = { t } }).Where(r => SpecReport.IsFail(r.Status)));
            Assert.Equal(new long[] { 1839886, 1839887, 1839888 }, f.ElementIds.OrderBy(x => x).ToArray());
            Assert.Contains("3 elemen", f.Note);
        }

        [Fact]
        public void Instances_with_their_own_material_pass()
        {
            var t = GenericModelType(3, 1839886, "Some Material");
            var rs = A71(new SpecModel { Types = { t } });
            Assert.Empty(rs.Where(r => SpecReport.IsFail(r.Status)));
            Assert.Contains(rs, r => r.Status == "pass");
        }

        [Fact]
        public void Only_the_instances_without_material_are_listed()
        {
            var t = GenericModelType(3, 1839886, "Some Material");
            t.NoMaterialInstanceIds.Add(1839887); t.NoMaterialInstanceCount = 1;
            var f = Assert.Single(A71(new SpecModel { Types = { t } }).Where(r => SpecReport.IsFail(r.Status)));
            Assert.Equal(new long[] { 1839887 }, f.ElementIds.ToArray());
        }
    }
}

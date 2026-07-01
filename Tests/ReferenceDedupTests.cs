using System.Collections.Generic;
using System.Linq;
using Xunit;
using RevitWebAppSync.Services;

namespace Tests
{
    public class ReferenceDedupTests
    {
        [Fact]
        public void DedupBySimpleName_same_name_different_location_keeps_first()
        {
            // The exact bug: Revit's copy and a co-installed add-in's copy share the
            // manifest simple name 'Autodesk.Http.JsonApi'. Roslyn rejects two refs with
            // one simple name, so only the first (Revit's) must survive.
            var candidates = new List<(string simpleName, string location)>
            {
                ("Autodesk.Http.JsonApi", @"C:\Revit 2027\Autodesk.Http.JsonApi.dll"),
                ("Autodesk.Http.JsonApi", @"D:\WORK\AddIns\IssuesManagement\Autodesk.Http.JsonApi.dll"),
            };

            var kept = ReferenceDedup.DedupBySimpleName(candidates, out var skipped);

            Assert.Single(kept);
            Assert.Equal(@"C:\Revit 2027\Autodesk.Http.JsonApi.dll", kept[0].location);
            Assert.Single(skipped);
            Assert.Equal(@"D:\WORK\AddIns\IssuesManagement\Autodesk.Http.JsonApi.dll", skipped[0].location);
        }

        [Fact]
        public void DedupBySimpleName_distinct_names_all_kept()
        {
            var candidates = new List<(string simpleName, string location)>
            {
                ("RevitAPI", @"C:\RevitAPI.dll"),
                ("RevitAPIUI", @"C:\RevitAPIUI.dll"),
                ("ClosedXML", @"C:\ClosedXML.dll"),
            };

            var kept = ReferenceDedup.DedupBySimpleName(candidates, out var skipped);

            Assert.Equal(3, kept.Count);
            Assert.Empty(skipped);
        }

        [Fact]
        public void DedupBySimpleName_is_case_insensitive()
        {
            // Assembly simple names compare case-insensitively; a case-variant copy is
            // still a duplicate as far as Roslyn is concerned.
            var candidates = new List<(string simpleName, string location)>
            {
                ("Autodesk.Http.DevPortal", @"C:\a\Autodesk.Http.DevPortal.dll"),
                ("autodesk.http.devportal", @"C:\b\Autodesk.Http.DevPortal.dll"),
            };

            var kept = ReferenceDedup.DedupBySimpleName(candidates, out var skipped);

            Assert.Single(kept);
            Assert.Equal(@"C:\a\Autodesk.Http.DevPortal.dll", kept[0].location);
            Assert.Single(skipped);
        }

        [Fact]
        public void DedupBySimpleName_preserves_load_order()
        {
            var candidates = new List<(string simpleName, string location)>
            {
                ("A", "a.dll"),
                ("B", "b.dll"),
                ("A", "a2.dll"), // dup of A
                ("C", "c.dll"),
            };

            var kept = ReferenceDedup.DedupBySimpleName(candidates, out _);

            Assert.Equal(new[] { "A", "B", "C" }, kept.Select(k => k.simpleName).ToArray());
        }
    }
}

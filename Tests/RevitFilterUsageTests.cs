// Some Revit API classes exist only in the API and not in Revit's native object
// model, so FilteredElementCollector.OfClass(typeof(X)) throws at run time
// ("...exists in the API, but not in Revit's native object model"). That broke
// tag_all_in_view (A-09) on every call. These classes must be collected by
// category and cast in memory instead. Revit-bound code has no harness here,
// so this pins the source fact across the add-in.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RevitFilterUsageTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        // API-only classes Revit rejects in an ElementClassFilter.
        private static readonly Regex Unsupported = new Regex(
            @"OfClass\(\s*typeof\(\s*(?:\w+\.)*(RoomTag|Room|Area|AreaTag|Space|SpaceTag|" +
            @"Mullion|Panel|SlabEdge|Fascia|Gutter|HostedSweep|RoomTagType|AreaTagType|SpaceTagType)\s*\)\s*\)");

        [Fact]
        public void NoOfClassOnApiOnlyTypes()
        {
            var root = RepoRoot();
            var hits = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !p.Contains(Path.DirectorySeparatorChar + "Tests" + Path.DirectorySeparatorChar))
                .SelectMany(p => File.ReadLines(p).Select((line, i) => (p, i, line)))
                .Where(x => Unsupported.IsMatch(x.line))
                .Select(x => $"{Path.GetRelativePath(root, x.p)}:{x.i + 1}: {x.line.Trim()}")
                .ToList();
            Assert.True(hits.Count == 0,
                "Use OfCategory(...) + OfType<T>() for these classes:\n" + string.Join("\n", hits));
        }
    }
}

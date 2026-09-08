// The Cad2Bim engine (Cad2Bim/cad2bim/*) is linked into all three add-in
// TFMs. net48 (Revit 2023/2024) has no Math.Clamp, no string.EndsWith(char),
// no System.Index and no System.Runtime.Intrinsics, and the add-in does not
// enable ImplicitUsings the way cad2bim.csproj does. The Mac build proves
// all of that once; these tests pin it so a later upstream sync of the
// Cad2Bim folder cannot quietly bring one back and break the Revit 2024
// build on the release box.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class Cad2BimNet48Tests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        // Every engine file the add-in links (csproj paths, backslashes as written there).
        private static readonly string[] Linked =
        {
            @"Cad2Bim\cad2bim\Geometry.cs",
            @"Cad2Bim\cad2bim\ModelSource.cs",
            @"Cad2Bim\cad2bim\Openings.cs",
            @"Cad2Bim\cad2bim\Plans.cs",
            @"Cad2Bim\cad2bim\Outlines.cs",
            @"Cad2Bim\cad2bim\Spaces.cs",
            @"Cad2Bim\cad2bim\SpatialIndex.cs",
            @"Cad2Bim\cad2bim\Topology.cs",
            @"Cad2Bim\cad2bim\Units.cs",
            @"Cad2Bim\cad2bim\Services\CadRenderSource.cs",
            @"Cad2Bim\cad2bim\Services\ClassificationService.cs",
            @"Cad2Bim\cad2bim\ViewModels\ViewModelBase.cs",
            @"Cad2Bim\cad2bim\ViewModels\RelayCommand.cs",
            @"Cad2Bim\cad2bim\ViewModels\LayerViewModel.cs",
            @"Cad2Bim\cad2bim\ViewModels\SettingsViewModel.cs",
            @"Cad2Bim\cad2bim\ViewModels\Shapes\ArcShape.cs",
            @"Cad2Bim\cad2bim\ViewModels\Shapes\PolylineShape.cs",
            @"Cad2Bim\cad2bim\ViewModels\Shapes\SegmentShape.cs",
            @"Cad2Bim\cad2bim\ViewModels\Shapes\WallShape.cs",
            @"Cad2Bim\cad2bim\Views\Rendering\CadViewport.cs",
            @"Cad2Bim\cad2bim\Views\Controls\NumericScrubBox.cs",
        };

        private static string Src(string csprojPath) =>
            File.ReadAllText(Path.Combine(RepoRoot(), csprojPath.Replace('\\', Path.DirectorySeparatorChar)));

        [Fact]
        public void Csproj_LinksEveryEngineFile_UnconditionallyForAllTfms()
        {
            var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "RevitWebAppSync.csproj"));

            foreach (var f in Linked)
                Assert.Contains($"<Compile Include=\"{f}\"", csproj);

            // The headless CLI's IFC writer never enters the add-in (the csproj
            // comment may NAME the file; only a Compile link is banned).
            Assert.DoesNotContain(@"<Compile Include=""Cad2Bim\cad2bim\IfcExporter.cs""", csproj);
            Assert.Contains("<PackageReference Include=\"ACadSharp\" Version=\"3.6.51\" />", csproj);

            // The link group must not be gated off net48 any more.
            var group = csproj.Split(new[] { "<ItemGroup" }, StringSplitOptions.None)
                .Single(chunk => chunk.Contains(@"Cad2Bim\cad2bim\Geometry.cs"));
            var openTag = group.Substring(0, group.IndexOf('>'));
            Assert.DoesNotContain("Condition", openTag);
        }

        [Fact]
        public void LinkedSources_UseNoApiThatNet48Lacks()
        {
            var banned = new[]
            {
                "Math.Clamp(",                 // .NET Core 2.0+
                "System.Runtime.Intrinsics",   // .NET Core 3.0+
                "[^1]",                        // System.Index
                "EndsWith('",                  // string.EndsWith(char)
                "StartsWith('",                // string.StartsWith(char)
                ".Contains('",                 // string.Contains(char)
            };
            foreach (var f in Linked)
            {
                var src = Src(f);
                foreach (var b in banned)
                    Assert.False(src.Contains(b), $"{f} uses `{b}`, which net48 does not have");
            }
        }

        [Fact]
        public void LinkedSources_CarryTheUsingsImplicitUsingsUsedToSupply()
        {
            // cad2bim.csproj enables ImplicitUsings; RevitWebAppSync.csproj does not.
            var needSystem = new[]
            {
                @"Cad2Bim\cad2bim\ViewModels\RelayCommand.cs",
                @"Cad2Bim\cad2bim\ViewModels\SettingsViewModel.cs",
                @"Cad2Bim\cad2bim\ViewModels\LayerViewModel.cs",
                @"Cad2Bim\cad2bim\Views\Rendering\CadViewport.cs",
                @"Cad2Bim\cad2bim\Views\Controls\NumericScrubBox.cs",
            };
            var needGenerics = new[]
            {
                @"Cad2Bim\cad2bim\Services\ClassificationService.cs",
                @"Cad2Bim\cad2bim\ViewModels\ViewModelBase.cs",
                @"Cad2Bim\cad2bim\ViewModels\LayerViewModel.cs",
                @"Cad2Bim\cad2bim\Views\Rendering\CadViewport.cs",
            };
            foreach (var f in needSystem)
                Assert.True(Src(f).Contains("using System;"), f + " needs `using System;`");
            foreach (var f in needGenerics)
                Assert.True(Src(f).Contains("using System.Collections.Generic;"), f + " needs `using System.Collections.Generic;`");
            Assert.True(Src(@"Cad2Bim\cad2bim\Views\Rendering\CadViewport.cs").Contains("using System.Linq;"),
                "CadViewport.cs needs `using System.Linq;` (Skip/Select/ToList)");
        }
    }
}

#nullable enable
// The pure electrical files compile into all three add-in TFMs, net48 (Revit 2023/2024)
// included, and into the headless CLI with Nullable and ImplicitUsings on. These tests pin the
// rules once, in the style of Cad2BimNet48Tests, so an API net48 lacks cannot creep in.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tests
{
    public class ElectricalNet48Tests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        /// <summary>The Revit-free electrical files new in phase 1, and the census they build on.</summary>
        private static readonly string[] Files =
        {
            "ElectricalCensus.cs",
            "CadElectricalMapping.cs",
            "CadModelFit.cs",
            "SocketPlanCache.cs",
            "SocketPlanSelect.cs",
            "CadSymbolResolve.cs",
            "CadElectricalReview.cs",
            "PlacementCompare.cs",
            "OverheadPlacement.cs",
            "CadRunChain.cs",
            "CadRunPlan.cs",
            "RunCompare.cs",
        };

        /// <summary>The phase 1 files on the Revit side: not linked into the tests, but held to
        /// the same net48 rules.</summary>
        private static readonly string[] RevitFiles =
        {
            "CadElectricalTools.cs",
            "CadElectricalResolver.cs",
        };

        private static string Src(string file) =>
            File.ReadAllText(Path.Combine(RepoRoot(), "BinaVibe", "Mcp", "Tools", "Electrical", file));

        /// <summary>Pattern, and what it stands for.</summary>
        private static readonly (string Pattern, string Why)[] Banned =
        {
            (@"Math\.Clamp\(", "Math.Clamp"),
            (@"double\.IsFinite\(", "double.IsFinite (use ElectricalCensus.IsFinite)"),
            (@"\bMathF\.", "MathF"),
            (@"\[\^\d", "System.Index ([^1])"),
            (@"\[[^\]\[]*\.\.[^\]\[]*\]", "ranges ([a..b])"),
            (@"(StartsWith|EndsWith|Contains)\('", "char overloads of StartsWith/EndsWith/Contains"),
            (@"\.Contains\([^;]*StringComparison\.", "string.Contains(string, StringComparison) (use IndexOf)"),
            (@"Join\('", "string.Join(char, ...)"),
            (@"\.Split\(""", "string.Split(string)"),
            (@"\.(MinBy|MaxBy|DistinctBy|Chunk)\(", "MinBy/MaxBy/DistinctBy/Chunk"),
            (@"\.TryAdd\(", "Dictionary.TryAdd"),
            (@"new ElementId\(", "new ElementId(long) (use ElemIds.From)"),
        };

        [Fact]
        public void Pure_electrical_files_use_no_api_that_net48_lacks()
        {
            foreach (string file in Files.Concat(RevitFiles))
            {
                string src = Src(file);
                foreach ((string pattern, string why) in Banned)
                {
                    Match hit = Regex.Match(src, pattern);
                    Assert.False(hit.Success, file + " uses " + why + ": `" + hit.Value + "`");
                }
            }
        }

        [Fact]
        public void Pure_electrical_files_enable_nullable_and_carry_their_usings()
        {
            foreach (string file in Files.Concat(RevitFiles))
            {
                string src = Src(file);
                Assert.StartsWith("#nullable enable", src.TrimStart('﻿'), StringComparison.Ordinal);
                Assert.True(src.Contains("using System;"), file + " needs `using System;`");
            }
        }

        [Fact]
        public void Every_pure_file_is_linked_into_the_tests()
        {
            string csproj = File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "Tests.csproj"));
            foreach (string file in Files.Where(f => f != "ElectricalCensus.cs"))
                Assert.Contains(@"<Compile Include=""..\BinaVibe\Mcp\Tools\Electrical\" + file + @"""", csproj);
        }
    }
}

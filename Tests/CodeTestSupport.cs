#nullable enable
// Shared helpers for the run_revit_code tests (W4). No Revit runtime: snippets
// bind against the Nice3point Revit reference assemblies, which the W4 block
// of Tests.csproj copies to <bin>/revit-ref (they are compile-only packages).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BinaVibe.Copilot.Code;
using Microsoft.CodeAnalysis;

namespace RevitWebAppSync.Tests
{
    internal static class CodeTestSupport
    {
        private static readonly Lazy<IReadOnlyList<MetadataReference>> Broad = new Lazy<IReadOnlyList<MetadataReference>>(BuildBroad);

        public static IReadOnlyList<string> RevitRefPaths()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "revit-ref");
            var paths = new[] { Path.Combine(dir, "RevitAPI.dll"), Path.Combine(dir, "RevitAPIUI.dll") };
            foreach (var p in paths)
                if (!File.Exists(p))
                    throw new InvalidOperationException("Missing " + p + ": the W4 block of Tests.csproj copies the Nice3point reference assemblies there.");
            return paths;
        }

        /// <summary>The add-in's own narrow reference set (CodeReferences.Build), with the 2027 Revit refs.</summary>
        public static IReadOnlyList<MetadataReference> ProductionRefs() => CodeReferences.Build(RevitRefPaths());

        /// <summary>Every platform assembly plus the Revit refs and this test assembly (which holds the
        /// add-in's Code sources). Lets denied types BIND, so the policy alone must reject them.</summary>
        public static IReadOnlyList<MetadataReference> BroadRefs() => Broad.Value;

        private static IReadOnlyList<MetadataReference> BuildBroad()
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator);
            foreach (var f in tpa.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                files[Path.GetFileNameWithoutExtension(f)] = f;
            foreach (var f in RevitRefPaths()) files[Path.GetFileNameWithoutExtension(f)] = f;
            files[typeof(CodeTestSupport).Assembly.GetName().Name!] = typeof(CodeTestSupport).Assembly.Location;
            var refs = new List<MetadataReference>();
            foreach (var f in files.Values)
            {
                try { refs.Add(MetadataReference.CreateFromFile(f)); }
                catch (BadImageFormatException) { }
            }
            return refs;
        }

        public static CodeAnalysis AnalyzeBroad(string code, WrapperProfile profile = WrapperProfile.Revit) =>
            CodeGate.Analyze(code, BroadRefs(), profile);

        public static CodeAnalysis AnalyzeProduction(string code, WrapperProfile profile = WrapperProfile.Revit) =>
            CodeGate.Analyze(code, ProductionRefs(), profile);

        public static string Describe(CodeAnalysis a) =>
            a.Stage + ": " + string.Join(" | ", a.Violations.Select(v => v.ToString()))
            + string.Join(" | ", a.Errors.Select(e => e.ToString()));

        /// <summary>A repo file: the copy the W4 block puts next to the test assembly, else found by
        /// walking up from this source file (the scratch harness links sources by absolute path).</summary>
        public static string RepoFile(string relativePath, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
        {
            var copy = Path.Combine(AppContext.BaseDirectory, "w4", Path.GetFileName(relativePath));
            if (File.Exists(copy)) return copy;
            for (var dir = Path.GetDirectoryName(here); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                var candidate = Path.Combine(dir!, relativePath);
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("Cannot find " + relativePath);
        }
    }
}

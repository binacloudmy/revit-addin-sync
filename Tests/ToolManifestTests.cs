// Installed tool manifest — GENERATED from ToolRegistry.cs, never hand-written.
//
// Spec: bina-ai docs/superpowers/specs/2026-08-14-control-plane-rewrite-design.md §8.2.
// The backend computes effective(session) = backend ∩ installed ∩ entitlement;
// "installed" is what this add-in actually dispatches. These tests pin that
// the generated manifest (BinaVibe/Mcp/Tools/ToolManifest.g.cs) matches the
// `tool switch` arms in ToolRegistry.cs, so a new arm without a regenerate
// (scripts/gen-tool-manifest.py) fails the build's tests instead of shipping
// a stale capability list.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BinaVibe.Mcp.Tools;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ToolManifestTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        /// <summary>Names of the arms of the FIRST switch in ToolRegistry.Invoke
        /// (`return tool switch { "name" => ..., _ => NotImplemented(tool) }`).
        /// Nested `category switch` blocks further down are not tools.</summary>
        private static List<string> ArmsFromSource()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "BinaVibe", "Mcp", "Tools", "ToolRegistry.cs"));
            var start = src.IndexOf("return tool switch", StringComparison.Ordinal);
            var end = src.IndexOf("_ => NotImplemented(tool)", start, StringComparison.Ordinal);
            Assert.True(start > 0 && end > start, "ToolRegistry.Invoke switch block not found");
            var block = src.Substring(start, end - start);
            return Regex.Matches(block, "^\\s*\"([a-z][a-z0-9_]+)\"\\s*=>", RegexOptions.Multiline)
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        }

        [Fact]
        public void Manifest_NamesMatchRegistryArms_Exactly()
        {
            var fromSource = ArmsFromSource();
            Assert.True(fromSource.Count > 100, "unexpectedly few arms parsed: " + fromSource.Count);
            Assert.Equal(fromSource.OrderBy(n => n, StringComparer.Ordinal),
                         InstalledToolManifest.Names.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void Manifest_HasNoDuplicates_AndIsSorted()
        {
            Assert.Equal(InstalledToolManifest.Names.Length, InstalledToolManifest.Names.Distinct().Count());
            Assert.Equal(InstalledToolManifest.Names.OrderBy(n => n, StringComparer.Ordinal), InstalledToolManifest.Names);
        }

        [Fact]
        public void Manifest_VersionIsHashOfSortedNames()
        {
            var joined = string.Join("\n", InstalledToolManifest.Names.OrderBy(n => n, StringComparer.Ordinal));
            var hex = BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))
                .Replace("-", "").ToLowerInvariant().Substring(0, 12);
            Assert.Equal(hex, InstalledToolManifest.Version);
        }

        [Fact]
        public void Manifest_SpeaksProtocolV2()
        {
            Assert.Equal(2, InstalledToolManifest.ProtocolVersion);
        }
    }
}

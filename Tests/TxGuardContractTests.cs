// Truth-core fence (spec §7.3.1, plan A01, W1 Task 1): every transaction under
// BinaVibe/Mcp/Tools and BinaVibe/Copilot commits through TxGuard, never with a
// bare Commit(). Revit's Commit() returns RolledBack after a swallowed error
// instead of throwing; a site that ignores that status reports "done" over an
// unchanged model. Source scans, like AboutRibbonTests: a tripwire, not a parser.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TxGuardContractTests
    {
        private static readonly string[] ScanRoots = { "BinaVibe/Mcp/Tools", "BinaVibe/Copilot" };

        // Files W1 does not own that still commit or roll back the old way. They
        // are the orchestrator's to convert (W1 report). Ratchet: each entry must
        // still offend, so the fence tightens the moment one is fixed.
        private static readonly string[] CommitPendingOwner =
        {
            "BinaVibe/Mcp/Tools/BatchLink.cs",
            "BinaVibe/Mcp/Tools/DwgScratchCache.cs",
        };

        private static readonly string[] RollbackPendingOwner =
        {
            "BinaVibe/Mcp/Tools/Electrical/SocketPlacement.cs",
        };

        private static readonly Regex CommitCall = new Regex(@"\bCommit\s*\(", RegexOptions.CultureInvariant);

        // catch { x.RollBack(); throw; } — single- or multi-line, no GetStatus() guard.
        private static readonly Regex UnguardedRollback =
            new Regex(@"catch\s*(\([^)]*\))?\s*\{\s*\w+\.RollBack\(\);\s*throw;", RegexOptions.CultureInvariant);

        [Fact]
        public void NoBareCommitInSwallowingFiles()
        {
            var offenders = new List<string>();
            foreach (var (rel, lines) in SourceFiles())
            {
                if (Path.GetFileName(rel) == "TxGuard.cs" || CommitPendingOwner.Contains(rel)) continue;
                for (int i = 0; i < lines.Length; i++)
                    if (!IsComment(lines[i]) && CommitCall.IsMatch(lines[i]))
                        offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
            Assert.True(offenders.Count == 0,
                "Commit( outside TxGuard.cs; use TxGuard.CommitOrThrow:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void PendingCommitFiles_StillOffend_ElseDropThemFromTheList()
        {
            foreach (var rel in CommitPendingOwner)
            {
                var lines = ReadLines(rel);
                Assert.True(lines.Any(l => !IsComment(l) && CommitCall.IsMatch(l)),
                    $"{rel} no longer calls Commit(: remove it from CommitPendingOwner");
            }
        }

        [Fact]
        public void NoUnguardedRollbackInCatch()
        {
            var offenders = new List<string>();
            foreach (var (rel, lines) in SourceFiles())
            {
                if (Path.GetFileName(rel) == "TxGuard.cs" || RollbackPendingOwner.Contains(rel)) continue;
                var text = string.Join("\n", lines);
                if (!text.Contains("StartSwallowing") && !text.Contains("CommitOrThrow")) continue;
                foreach (Match m in UnguardedRollback.Matches(text))
                    offenders.Add($"{rel}:{LineOf(text, m.Index)}: {m.Value.Replace("\n", " ")}");
            }
            Assert.True(offenders.Count == 0,
                "unguarded catch rollback; use `if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();`:\n"
                + string.Join("\n", offenders));
        }

        [Fact]
        public void PendingRollbackFiles_StillOffend_ElseDropThemFromTheList()
        {
            foreach (var rel in RollbackPendingOwner)
                Assert.True(UnguardedRollback.IsMatch(string.Join("\n", ReadLines(rel))),
                    $"{rel} has no unguarded catch rollback left: remove it from RollbackPendingOwner");
        }

        [Fact]
        public void CommitOrThrow_FailsOnAnyStatusButCommitted()
        {
            var src = string.Join("\n", ReadLines("BinaVibe/Mcp/Tools/TxGuard.cs"));
            Assert.Contains("if (status != TransactionStatus.Committed)", src);
            Assert.DoesNotContain("if (status == TransactionStatus.RolledBack)", src);
        }

        [Fact]
        public void Scan_SeesAssignedCommitToo()
        {
            Assert.Matches(CommitCall, "var s = tx.Commit();");
            Assert.Matches(CommitCall, "try { view.IsolateElementsTemporary(ids); tx.Commit(); }");
            Assert.Matches(CommitCall, "scope.Commit(new SwallowWarnings());");
            Assert.DoesNotMatch(CommitCall, "TxGuard.CommitOrThrow(tx);");
            Assert.Matches(UnguardedRollback, "catch { tx.RollBack(); throw; }");
            Assert.Matches(UnguardedRollback, "catch\n{\n    txA.RollBack();\n    throw;\n}");
            Assert.DoesNotMatch(UnguardedRollback,
                "catch { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); throw; }");
        }

        private static bool IsComment(string line)
        {
            var t = line.TrimStart();
            return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)
                || t.StartsWith("/*", StringComparison.Ordinal);
        }

        private static int LineOf(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

        private static IEnumerable<(string rel, string[] lines)> SourceFiles()
        {
            var root = RepoRoot();
            foreach (var scan in ScanRoots)
            {
                var dir = Path.Combine(root, scan);
                if (!Directory.Exists(dir)) continue;
                foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                {
                    var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                    yield return (rel, ReadLines(rel));
                }
            }
        }

        private static string[] ReadLines(string rel) =>
            File.ReadAllText(Path.Combine(RepoRoot(), rel)).Replace("\r\n", "\n").Split('\n');

        private static string RepoRoot([CallerFilePath] string thisFile = "")
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) ?? "" })
            {
                var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                    dir = dir.Parent;
                if (dir != null) return dir.FullName;
            }
            Assert.Fail("could not locate the repo root from " + AppContext.BaseDirectory);
            return "";
        }
    }
}

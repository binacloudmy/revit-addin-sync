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

        private static readonly Regex CommitCall = new Regex(@"\bCommit\s*\(", RegexOptions.CultureInvariant);

        // A catch block whose first statement is an unguarded x.RollBack(): single- or
        // multi-line, any exception filter, with line or block comments in between.
        private static readonly Regex UnguardedRollback = new Regex(
            @"catch\s*(\([^)]*\))?\s*\{(?:\s*(?://[^\n]*|/\*[\s\S]*?\*/))*\s*\w+\.RollBack\(\);",
            RegexOptions.CultureInvariant);

        [Fact]
        public void NoBareCommitInSwallowingFiles()
        {
            var offenders = new List<string>();
            foreach (var (rel, lines) in SourceFiles())
            {
                if (Path.GetFileName(rel) == "TxGuard.cs") continue;
                for (int i = 0; i < lines.Length; i++)
                    if (!IsComment(lines[i]) && CommitCall.IsMatch(lines[i]))
                        offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
            Assert.True(offenders.Count == 0,
                "Commit( outside TxGuard.cs; use TxGuard.CommitOrThrow:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void NoUnguardedRollbackInCatch()
        {
            var offenders = new List<string>();
            foreach (var (rel, lines) in SourceFiles())
            {
                if (Path.GetFileName(rel) == "TxGuard.cs") continue;
                var text = string.Join("\n", lines);
                foreach (Match m in UnguardedRollback.Matches(text))
                    offenders.Add($"{rel}:{LineOf(text, m.Index)}: {m.Value.Replace("\n", " ")}");
            }
            Assert.True(offenders.Count == 0,
                "unguarded catch rollback; use `if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();`:\n"
                + string.Join("\n", offenders));
        }

        [Fact]
        public void GuardedTransactions_StartWithStartSwallowing()
        {
            var offenders = new List<string>();
            foreach (var (rel, lines) in SourceFiles())
            {
                if (Path.GetFileName(rel) == "TxGuard.cs") continue;
                foreach (var (line, text) in PlainStartsOfGuardedTransactions(lines))
                    offenders.Add($"{rel}:{line}: {text}");
            }
            Assert.True(offenders.Count == 0,
                "a transaction committed through TxGuard starts with a plain Start(), so a warning can raise a modal; "
                + "use TxGuard.StartSwallowing(tx):\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void CommitOrThrow_FailsOnAnyStatusButCommitted()
        {
            var src = string.Join("\n", ReadLines("BinaVibe/Mcp/Tools/TxGuard.cs"));
            Assert.Contains("if (status != TransactionStatus.Committed)", src);
            Assert.DoesNotContain("if (status == TransactionStatus.RolledBack)", src);
            // Errors are this transaction's only: cleared right before Commit(), so a
            // plain-started transaction never reports an earlier one's errors.
            Assert.Contains("_lastErrors = null;\n            var status = tx.Commit();", src);
        }

        [Fact]
        public void Preprocessor_LogsEveryFailure_BeforeDeletingWarnings()
        {
            var src = string.Join("\n", ReadLines("BinaVibe/Mcp/Tools/TxGuard.cs"));
            Assert.True(src.Contains("FailureLog.Current.Add("), "SwallowWarnings must record failures into FailureLog.Current");
            int record = src.IndexOf("Record(f, text, isWarning);", StringComparison.Ordinal);
            int delete = src.IndexOf("a.DeleteWarning(f)", StringComparison.Ordinal);
            Assert.True(record > 0 && delete > record, "the failure must be logged before the warning is deleted");
            Assert.Contains("PreprocessFailures(FailuresAccessor a) => Process(a)", src);
        }

        [Fact]
        public void FailureScope_BackstopSubscribesAndUnsubscribes()
        {
            var src = string.Join("\n", ReadLines("BinaVibe/Copilot/Guard/FailureScope.cs"));
            Assert.Contains("public static FailureScope Begin(Autodesk.Revit.ApplicationServices.Application app)", src);
            Assert.Contains("FailuresProcessing += OnFailuresProcessing", src);
            Assert.Contains("FailuresProcessing -= OnFailuresProcessing", src);
            Assert.Contains("SwallowWarnings.Process(", src);
        }

        [Fact]
        public void PlainStartScan_FlagsOnlyGuardedTransactions()
        {
            string[] L(string src) => src.Split('\n');
            Assert.Single(PlainStartsOfGuardedTransactions(L(
                "using var tx = new Transaction(doc, \"x\");\ntx.Start();\nTxGuard.CommitOrThrow(tx);")));
            Assert.Single(PlainStartsOfGuardedTransactions(L(
                "using (var txA = new Autodesk.Revit.DB.Transaction(doc, \"x\"))\n{\n    txA.Start();\n    TxGuard.CommitOrThrow( txA );\n}")));
            Assert.Single(PlainStartsOfGuardedTransactions(L(
                "Transaction? t = null;\nTransaction t2 = new(doc, \"x\");\nt2.Start();\nTxGuard.CommitOrThrow(t2);")));
            // Swallowing start, a group, a commented line, and a transaction never committed through TxGuard: not flagged.
            Assert.Empty(PlainStartsOfGuardedTransactions(L(
                "using var tx = new Transaction(doc, \"x\");\nTxGuard.StartSwallowing(tx);\nTxGuard.CommitOrThrow(tx);")));
            Assert.Empty(PlainStartsOfGuardedTransactions(L(
                "using var group = new TransactionGroup(doc, \"g\");\ngroup.Start();\ngroup.Assimilate();")));
            Assert.Empty(PlainStartsOfGuardedTransactions(L(
                "using var tx = new Transaction(doc, \"x\");\n// tx.Start();\nTxGuard.StartSwallowing(tx);\nTxGuard.CommitOrThrow(tx);")));
            Assert.Empty(PlainStartsOfGuardedTransactions(L(
                "using var tx = new Transaction(doc, \"x\");\ntx.Start();\ntx.RollBack();")));
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
            Assert.Matches(UnguardedRollback,
                "catch (Autodesk.Revit.Exceptions.InvalidOperationException)\n{\n    // cannot be joined\n    tx.RollBack();\n    throw;\n}");
            Assert.Matches(UnguardedRollback, "catch (Exception ex) { /* note */ group.RollBack(); return Fail(ex); }");
            Assert.DoesNotMatch(UnguardedRollback,
                "catch { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); throw; }");
            Assert.DoesNotMatch(UnguardedRollback,
                "catch\n{\n    // already closed\n    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();\n    throw;\n}");
        }

        // `x = new Transaction(` / `Transaction x = new(` declare a transaction variable;
        // `x.Start()` on one the same file commits with TxGuard.CommitOrThrow(x) is a plain start.
        private static readonly Regex TransactionDecl = new Regex(
            @"\b(\w+)\s*=\s*new\s+(?:Autodesk\.Revit\.DB\.)?Transaction\s*\(|\bTransaction\??\s+(\w+)\s*=\s*new\s*\(",
            RegexOptions.CultureInvariant);

        private static readonly Regex PlainStart = new Regex(@"\b(\w+)\.Start\(\s*\)", RegexOptions.CultureInvariant);

        internal static List<(int line, string text)> PlainStartsOfGuardedTransactions(string[] lines)
        {
            var text = string.Join("\n", lines);
            var names = new HashSet<string>(TransactionDecl.Matches(text).Cast<Match>()
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
            var found = new List<(int, string)>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i])) continue;
                foreach (Match m in PlainStart.Matches(lines[i]))
                {
                    var name = m.Groups[1].Value;
                    if (names.Contains(name) && Regex.IsMatch(text, @"TxGuard\.CommitOrThrow\(\s*" + Regex.Escape(name) + @"\s*\)"))
                        found.Add((i + 1, lines[i].Trim()));
                }
            }
            return found;
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

        internal static string ReadSource(string rel) => string.Join("\n", ReadLines(rel));

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

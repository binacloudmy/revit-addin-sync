#nullable enable
// W3 fix round 1 (rulings R53/R17, R34; no-bare-commit fence). Source-text
// tripwires for the Revit-bound engine, which cannot run outside Revit:
//   - the read-mode path opens no transaction, group or callback scope of its
//     own (W4's tool owns the always-rolled-back group under CallbackKind.Read);
//   - the preview and commit groups run inside W1's FailureScope;
//   - nothing under BinaVibe/Copilot/Writes or Undo calls Commit( directly.
// Revit-free: reads the source files next to this test.

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class WriteEngineSourceTests
    {
        private static string Root([CallerFilePath] string thisFile = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

        private static string Engine() => File.ReadAllText(Path.Combine(Root(), "BinaVibe", "Copilot", "Writes", "RevitWriteEngine.cs"));

        /// <summary>The body of a method, from its signature to the next member's comment banner,
        /// without comment lines.</summary>
        private static string Method(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " not found");
            var depth = 0;
            var open = source.IndexOf('{', start);
            var i = open;
            for (; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) break;
            }
            var body = source.Substring(open, i - open + 1);
            return string.Join("\n", body.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        [Fact]
        public void ReadModeOpensNoTransactionGroupOrScope()
        {
            var body = Method(Engine(), "private static Envelope RunRead(");
            foreach (var forbidden in new[] { "new Transaction", "TransactionGroup", "StartSwallowing", "CommitOrThrow", "CallbackScope.Enter", "RunCall(" })
                Assert.DoesNotContain(forbidden, body);
            Assert.Contains("WritePhase.Commit", body);   // its one and only run
        }

        [Fact]
        public void PreviewAndCommitGroupsRunInsideFailureScope()
        {
            var source = Engine();
            Assert.Contains("FailureScope.Begin(", Method(source, "private PreviewOutcome PreviewBatch("));
            Assert.Contains("FailureScope.Begin(", Method(source, "private CommitOutcome CommitBatch("));
        }

        [Fact]
        public void NoBareCommitInTheWriteEngine()
        {
            // Same rule as W1's TxGuardContractTests: \bCommit( on a non-comment line.
            var bare = new Regex(@"\bCommit\s*\(", RegexOptions.CultureInvariant);
            foreach (var dir in new[] { Path.Combine("BinaVibe", "Copilot", "Writes"), Path.Combine("BinaVibe", "Copilot", "Undo") })
                foreach (var file in Directory.GetFiles(Path.Combine(Root(), dir), "*.cs"))
                    foreach (var line in File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))
                        Assert.False(bare.IsMatch(line), Path.GetFileName(file) + ": " + line.Trim());
        }
    }
}

#nullable enable
// Loop budget (W4 Task 2, spec §7.8 "Budget"): an inserted counter and clock
// check at every loop back-edge and every method or lambda entry, with a depth
// cap, is the only real limit on Revit's UI thread. These compile AND RUN pure
// C# snippets (WrapperProfile.Pure: no Revit types), so they need no Revit.

using System;
using System.Linq;
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class LoopBudgetTests
    {
        private static CodeRunResult Run(string code, LoopBudget? budget = null, OutputBuffer? output = null)
        {
            var prepared = CodeRunner.Prepare(code, CodeTestSupport.ProductionRefs(), WrapperProfile.Pure);
            Assert.True(prepared.Snippet != null, prepared.Failure?.Message);
            return CodeRunner.Execute(prepared.Snippet!, Array.Empty<object?>(), budget ?? LoopBudget.FromPolicy(), output ?? new OutputBuffer());
        }

        private static BudgetExceededException Exceeded(CodeRunResult r)
        {
            Assert.False(r.Ok, "the run should have been stopped");
            return Assert.IsType<BudgetExceededException>(r.Exception);
        }

        [Fact]
        public void InfiniteLoopThrowsBudgetExceeded()
        {
            var r = Run("while (true) { }");
            var ex = Exceeded(r);
            Assert.Equal(BudgetReason.Steps, ex.Reason);
            Assert.Equal(2_000_000, LoopBudget.FromPolicy().MaxTicks);
            Assert.True(r.Ticks > 2_000_000);
            Assert.Equal("timeout", r.Failure!.Code);
            Assert.Equal(1, r.Failure.Lines.First());
        }

        [Theory]
        [InlineData("for (;;) ;")]
        [InlineData("for (int i = 0; ; i++) { if (i < 0) break; }")]
        [InlineData("do { } while (true);")]
        [InlineData("while (true) continue;")]
        [InlineData("var xs = new List<int> { 1 };\nforeach (var x in Enumerable.Repeat(1, int.MaxValue)) { }")]
        [InlineData("int i = 0;\nwhile (i >= 0) { i = (i + 1) % 10; }")]
        public void EveryLoopFormIsCounted(string code)
        {
            Assert.Equal(BudgetReason.Steps, Exceeded(Run(code)).Reason);
        }

        [Fact]
        public void LambdasCountTowardTheBudget()
        {
            // No loop in the model's code: the lambda entry is the only counted point.
            var r = Run("var n = Enumerable.Range(0, int.MaxValue).Select(i => i + 1).Count();\nPrint(n);");
            Assert.Equal(BudgetReason.Steps, Exceeded(r).Reason);
        }

        [Fact]
        public void RecursionStopsAtTheDepthCapNotAStackOverflow()
        {
            var r = Run("int Down(int n) => Down(n + 1);\nPrint(Down(0));");
            var ex = Exceeded(r);
            Assert.Equal(BudgetReason.Depth, ex.Reason);
            Assert.Equal(64, LoopBudget.FromPolicy().MaxDepth);
        }

        [Fact]
        public void RecursiveLambdaStopsAtTheDepthCap()
        {
            var r = Run("Func<int, int> f = null;\nf = n => f(n + 1);\nPrint(f(0));");
            Assert.Equal(BudgetReason.Depth, Exceeded(r).Reason);
        }

        [Theory]
        // Each ends with a statement that does not tick (Print ticks, so it would throw anyway):
        // without the catch filter these runs would finish "ok" after being stopped.
        [InlineData("try { while (true) { } }\ncatch { }\nvar after = 1;")]
        [InlineData("try { while (true) { } }\ncatch (Exception) { }\nvar after = 1;")]
        [InlineData("var seen = 0;\ntry { for (;;) { } }\ncatch (Exception e) when (e != null) { seen = 1; }\nvar after = seen;")]
        [InlineData("int F(int n) { try { return F(n + 1); } catch { return 0; } }\nvar x = F(0);")]
        public void ACatchInTheModelsCodeCannotSwallowTheStop(string code)
        {
            Exceeded(Run(code));
        }

        [Fact]
        public void EveryCatchGetsTheLiveFilter()
        {
            var prepared = CodeRunner.Prepare("try { Print(1); } catch { }\ntry { Print(2); } catch (Exception e) when (e.Message != null) { }",
                CodeTestSupport.ProductionRefs(), WrapperProfile.Pure);
            var text = prepared.Snippet!.Wrapped.ModelCode;
            Assert.Contains("catch  when (__live()) ", text.Replace("catch when", "catch  when"));
            Assert.Contains("when (__live() && (e.Message != null))", text);
        }

        // ---- fix round 1, C1: work the old instrumentation never counted (the reviewer's probes) ----

        [Fact]
        public void QuerySyntaxIsRejected()
        {
            // Probe 1: the compiler's own lambdas for query clauses carried no tick.
            var p = CodeRunner.Prepare("var n = (from a in Enumerable.Range(0, 2000) from b in Enumerable.Range(0, 2000) select a).Count();\nPrint(n);",
                CodeTestSupport.ProductionRefs(), WrapperProfile.Pure);
            Assert.Null(p.Snippet);
            Assert.Equal("policy_rejected", p.Failure!.Code);
            Assert.Contains(p.Analysis.Violations, v => v.Category == "uncounted_work");
        }

        [Fact]
        public void MethodGroupsAsDelegatesAreRejected()
        {
            // Probe 2: BCL method groups run as delegates without entering any counted lambda.
            var p = CodeRunner.Prepare("var n = Enumerable.Range(0, 2000).Join(Enumerable.Range(0, 2000), Math.Abs, Math.Abs, Math.Max).Count();\nPrint(n);",
                CodeTestSupport.ProductionRefs(), WrapperProfile.Pure);
            Assert.Null(p.Snippet);
            Assert.Contains(p.Analysis.Violations, v => v.Category == "uncounted_work");
        }

        [Fact]
        public void LazyFanOutIsCountedPerElement()
        {
            // Probe 3: 22 Concat doublings build a 4,194,304-element sequence in 22 ticks; Max() walked it uncounted.
            var r = Run("var e = Enumerable.Range(0, 1);\nfor (int i = 0; i < 22; i++) e = e.Concat(e);\nPrint(e.Max());");
            Assert.Equal(BudgetReason.Steps, Exceeded(r).Reason);

            var small = new LoopBudget(50, TimeSpan.FromSeconds(30), 64);
            var r2 = Run("var n = Enumerable.Range(0, 1_000_000).Count(x => true);\nPrint(n);", small);
            Assert.Equal(BudgetReason.Steps, Exceeded(r2).Reason);
            var r3 = Run("var n = Enumerable.Repeat(1, 1_000_000).Sum();\nPrint(n);", new LoopBudget(50, TimeSpan.FromSeconds(30), 64));
            Assert.Equal(BudgetReason.Steps, Exceeded(r3).Reason);
        }

        [Fact]
        public void DeepLazyChainsStopAtTheDepthCapNotAStackOverflow()
        {
            // My old residual risk: a lazy chain thousands deep recursed inside the BCL with no counted frame.
            var r = Run("var e = Enumerable.Range(0, 3);\nfor (int i = 0; i < 10000; i++) e = e.Skip(0);\nPrint(e.Count());");
            Assert.Equal(BudgetReason.Depth, Exceeded(r).Reason);
        }

        [Fact]
        public void LinqBehindConditionalAccessIsRejected()
        {
            var p = CodeRunner.Prepare("IEnumerable<int> e = Enumerable.Range(0, 1);\nfor (int i = 0; i < 22; i++) e = e?.Concat(e);\nPrint(e.Max());",
                CodeTestSupport.ProductionRefs(), WrapperProfile.Pure);
            Assert.Null(p.Snippet);
            Assert.Contains(p.Analysis.Violations, v => v.Category == "uncounted_work");
        }

        [Fact]
        public void LocalFunctionAndPrintMethodGroupsStayAllowedAndCounted()
        {
            var output = new OutputBuffer();
            var r = Run("int Twice(int x) => x * 2;\nPrint(new[] { 1, 2, 3 }.Select(Twice).Sum());\nnew List<string> { \"a\", \"b\" }.ForEach(Print);", output: output);
            Assert.True(r.Ok, r.Failure?.Message);
            Assert.Equal("12\na\nb", output.Text);
        }

        [Fact]
        public void CountingKeepsResultsAndNulls()
        {
            var output = new OutputBuffer();
            var r = Run("var xs = new List<int> { 3, 1, 2 };\n" +
                        "Print(string.Join(\",\", xs.Where(x => x > 1).OrderBy(x => x).Select(x => x * 10)));\n" +
                        "Print(xs.GroupBy(x => x % 2).Count());\n" +
                        "IEnumerable<int> none = null;\nPrint(none == null);\n" +
                        "Print(Enumerable.Range(1, 4).Concat(Enumerable.Empty<int>()).Sum());", output: output);
            Assert.True(r.Ok, r.Failure?.Message);
            Assert.Equal("20,30\n2\ntrue\n10", output.Text);
        }

        [Fact]
        public void ExceptionTextIsPrintedWithoutStackFrames()
        {
            // "" + ex and $"{ex}" call ToString() inside the BCL: the output drops frame lines (fix round 1, M2).
            var output = new OutputBuffer();
            var r = Run("try { int.Parse(\"x\"); }\ncatch (Exception ex)\n{\n    Print(\"\" + ex);\n    Print($\"{ex}\");\n    Print(ex);\n    Print(ex.Message);\n}", output: output);
            Assert.True(r.Ok, r.Failure?.Message);
            Assert.DoesNotContain("   at ", output.Text);
            Assert.DoesNotContain(":line ", output.Text);
            Assert.DoesNotContain("BinaVibe.", output.Text);
            Assert.Contains("FormatException", output.Text);
        }

        [Fact]
        public void OutputDropsLinesThatLookLikeStackFrames()
        {
            var output = new OutputBuffer();
            output.Print("boom\n   at Autodesk.Revit.DB.Document.Regenerate()\n   at BinaVibe.X.Y(Int32 a) in C:\\src\\x.cs:line 4\n--- End of inner exception stack trace ---\nnext line");
            output.Print(new InvalidOperationException("bad"));
            Assert.Equal("boom\nnext line\nInvalidOperationException: bad", output.Text);
        }

        [Fact]
        public void CompileCapStopsNewSnippetsWithAClearError()
        {
            // Snippet assemblies are never unloaded, so a session compiles at most max_compiles of them (M3).
            var data = CodePolicyTable.Data;
            Assert.Equal(200, data.Budget.MaxCompiles);
            data.Budget.MaxCompiles = 2;
            var policy = new CodePolicySet(data);
            var refs = CodeTestSupport.ProductionRefs();
            Assert.NotNull(CodeRunner.Prepare("Print(1);", refs, WrapperProfile.Pure, policy).Snippet);
            Assert.NotNull(CodeRunner.Prepare("Print(2);", refs, WrapperProfile.Pure, policy).Snippet);
            var third = CodeRunner.Prepare("Print(3);", refs, WrapperProfile.Pure, policy);
            Assert.Null(third.Snippet);
            Assert.Equal("not_allowed", third.Failure!.Code);
            Assert.Contains("restart Revit", third.Failure.Message);
            Assert.NotNull(CodeRunner.Prepare("Print(1);", refs, WrapperProfile.Pure, policy).Snippet); // cached: no new compile
        }

        [Fact]
        public void TheClockStopsLongRunsToo()
        {
            var clock = TimeSpan.Zero;
            var budget = new LoopBudget(long.MaxValue, TimeSpan.FromSeconds(30), 64, () => clock += TimeSpan.FromSeconds(1));
            var r = Run("while (true) { }", budget);
            Assert.Equal(BudgetReason.Time, Exceeded(r).Reason);
            Assert.Equal(30, LoopBudget.FromPolicy().MaxTime.TotalSeconds);
        }

        [Fact]
        public void BoundedWorkFinishesAndKeepsItsOutput()
        {
            var output = new OutputBuffer();
            var r = Run("var total = 0;\nfor (int i = 0; i < 1000; i++) total += i;\nint Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);\nPrint(total);\nPrint(Fact(10));\nPrint(new[] { 3, 1, 2 }.OrderBy(x => x).First());", output: output);
            Assert.True(r.Ok, r.Failure?.Message);
            Assert.Equal("499500\n3628800\n1", output.Text);
            Assert.InRange(r.Ticks, 1000, 10_000);
        }

        [Fact]
        public void PrintOutputIsCappedAt16KbWithANarrowNote()
        {
            var output = new OutputBuffer();
            var r = Run("for (int i = 0; i < 100000; i++) Print(\"row \" + i + \" some padding text\");", output: output);
            Assert.True(r.Ok, r.Failure?.Message);
            Assert.True(output.Truncated);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(output.Text) <= 16 * 1024);
            Assert.Contains("narrow your query", output.Text);
            Assert.StartsWith("row 0 some padding text\nrow 1 ", output.Text);
        }

        [Fact]
        public void PrintOfACollectionIsCappedToo()
        {
            var output = new OutputBuffer();
            var r = Run("Print(Enumerable.Range(0, 1_000_000).ToList());", output: output);
            Assert.True(r.Ok, r.Failure?.Message);
            Assert.True(output.Truncated);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(output.Text) <= 16 * 1024);
        }

        [Fact]
        public void RewriteKeepsLinesAndMeaning()
        {
            var prepared = CodeRunner.Prepare(
                "var xs = new List<int>();\nfor (int i = 0; i < 3; i++)\n    xs.Add(i);\nxs.ForEach(x => xs.Count.ToString());\nvar sum = xs.Sum(x => x);\nPrint(sum);",
                CodeTestSupport.ProductionRefs(), WrapperProfile.Pure);
            Assert.True(prepared.Snippet != null, prepared.Failure?.Message);
            var original = prepared.Analysis.Wrapped;
            var rewritten = prepared.Snippet!.Wrapped;
            Assert.Equal(original.Tree.GetText().Lines.Count, rewritten.Tree.GetText().Lines.Count);
            Assert.Equal(original.LineOffset, rewritten.LineOffset);
            Assert.Contains("__tick()", rewritten.ModelCode);
            Assert.Contains("__enter()", rewritten.ModelCode);
        }
    }
}

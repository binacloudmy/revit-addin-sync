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

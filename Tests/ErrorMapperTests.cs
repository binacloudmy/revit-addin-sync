#nullable enable
// ErrorMapper (W4 Task 2, spec §7.8 "Errors"): compile errors carry line numbers
// relative to the model's code (offset computed, not hard-coded); runtime
// exceptions are unwrapped from TargetInvocationException with type, message
// and the top frames in the model's code; policy rejections name the symbol
// and the line. Every failure becomes a valid contract envelope.

using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class ErrorMapperTests
    {
        [Fact]
        public void LineNumbersRelativeToModelCode()
        {
            const string code = "var a = 1;\nvar b = 2;\nvar c = a + undefinedName;\nPrint(c);";
            foreach (var profile in new[] { WrapperProfile.Revit, WrapperProfile.Pure })
            {
                var analysis = CodeGate.Analyze(code, CodeTestSupport.ProductionRefs(), profile);
                Assert.Equal(CodeStage.CompileError, analysis.Stage);
                var f = ErrorMapper.FromAnalysis(analysis);
                Assert.Equal(ErrorCodes.CompileError, f.Code);
                var line = Assert.Single(f.Lines);
                Assert.Equal(3, line); // the model's line 3, whatever the wrapper's prefix length
                Assert.Contains("Line 3", f.Message);
                Assert.Contains("CS0103", f.Message);
                Assert.Contains("undefinedName", f.Summary);
            }
        }

        [Fact]
        public void SyntaxErrorsMapToo()
        {
            var f = ErrorMapper.FromAnalysis(CodeGate.Analyze("Print(1);\nvar x = ;\nPrint(2);", CodeTestSupport.ProductionRefs()));
            Assert.Equal(ErrorCodes.CompileError, f.Code);
            Assert.Equal(2, f.Lines.First());
        }

        [Fact]
        public void AMissingBraceAtTheEndPointsAtTheLastLine()
        {
            var f = ErrorMapper.FromAnalysis(CodeGate.Analyze("if (true)\n{\n    Print(1);", CodeTestSupport.ProductionRefs()));
            Assert.Equal(ErrorCodes.CompileError, f.Code);
            Assert.All(f.Lines, l => Assert.InRange(l, 1, 3));
        }

        [Fact]
        public void CompileErrorsAreCappedAndTheSummaryFits()
        {
            var code = string.Join("\n", Enumerable.Range(1, 40).Select(i => "var v" + i + " = missing" + i + ";"));
            var f = ErrorMapper.FromAnalysis(CodeGate.Analyze(code, CodeTestSupport.ProductionRefs()));
            Assert.InRange(f.Lines.Count, 1, ErrorMapper.MaxErrors);
            Assert.Contains("more", f.Message);
            Assert.True(EnvelopeInvariants.CodePoints(f.Summary) <= EnvelopeLimits.SummaryMaxChars);
        }

        [Fact]
        public void PolicyRejectionNamesSymbolAndLine()
        {
            var f = ErrorMapper.FromAnalysis(CodeTestSupport.AnalyzeBroad("Print(1);\nvar t = System.IO.File.ReadAllText(\"x\");"));
            Assert.Equal(ErrorCodes.PolicyRejected, f.Code);
            Assert.Equal(2, f.Lines.First());
            Assert.Contains("System.IO.File", f.Message);
            Assert.Contains("line 2", f.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("file_io", f.Message);
        }

        [Fact]
        public void RuntimeErrorIsUnwrappedWithModelLines()
        {
            var prepared = CodeRunner.Prepare(
                "var xs = new List<int>();\nPrint(\"before\");\nint Pick(int i) => xs[i];\nPrint(Pick(5));",
                CodeTestSupport.ProductionRefs(), WrapperProfile.Pure);
            Assert.NotNull(prepared.Snippet);
            var r = CodeRunner.Execute(prepared.Snippet!, Array.Empty<object?>(), LoopBudget.FromPolicy(), new OutputBuffer());
            Assert.False(r.Ok);
            Assert.IsType<ArgumentOutOfRangeException>(r.Exception); // not TargetInvocationException
            var f = r.Failure!;
            Assert.Equal(ErrorCodes.RuntimeError, f.Code);
            Assert.Equal(new[] { 3, 4 }, f.Lines.Take(2).ToArray()); // innermost first: Pick, then its caller
            Assert.Contains("ArgumentOutOfRangeException", f.Message);
            Assert.Contains("line 3", f.Summary, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TargetInvocationExceptionIsUnwrapped()
        {
            var inner = new InvalidOperationException("boom");
            var wrapped = new TargetInvocationException(new TargetInvocationException(inner));
            Assert.Same(inner, ErrorMapper.Unwrap(wrapped));
            Assert.Same(inner, ErrorMapper.Unwrap(new AggregateException(inner)));
        }

        [Fact]
        public void TheModificationOutsideTransactionHintSaysHowToRouteAWrite()
        {
            var f = ErrorMapper.FromException(new FakeModificationOutsideTransactionException(), null, readMode: true);
            Assert.Equal(ErrorCodes.RuntimeError, f.Code);
            Assert.Contains("expected", f.Next);
        }

        [Fact]
        public void BudgetStopIsATimeout()
        {
            var f = ErrorMapper.FromException(new BudgetExceededException(BudgetReason.Steps, 2_000_001, TimeSpan.FromSeconds(1), 64), null, readMode: true);
            Assert.Equal(ErrorCodes.Timeout, f.Code);
            Assert.Equal(EnvelopeStatus.Timeout, f.Status);
            Assert.Contains("2,000,000", f.Message);
        }

        [Fact]
        public void EveryFailureIsAValidEnvelope()
        {
            var failures = new[]
            {
                ErrorMapper.FromAnalysis(CodeGate.Analyze("var x = ;", CodeTestSupport.ProductionRefs())),
                ErrorMapper.FromAnalysis(CodeTestSupport.AnalyzeBroad("System.IO.File.Delete(\"x\");")),
                ErrorMapper.FromAnalysis(CodeGate.Analyze("class X { }", CodeTestSupport.ProductionRefs())),
                ErrorMapper.FromException(new InvalidOperationException(new string('x', 5000)), null, readMode: false),
                ErrorMapper.FromException(new BudgetExceededException(BudgetReason.Time, 10, TimeSpan.FromSeconds(31), 64), null, readMode: false),
            };
            foreach (var f in failures)
                foreach (var phase in new[] { EnvelopePhase.Read, EnvelopePhase.Preview, EnvelopePhase.Commit })
                {
                    var e = f.ToEnvelope(phase);
                    Assert.Empty(EnvelopeInvariants.Violations(e));
                    Assert.False(e.Ok);
                    Assert.Equal(phase, e.Phase);
                    Assert.Equal(f.Code, e.Error!.Code);
                    Assert.Equal(JsonValueKind.Object, e.Data!.Value.ValueKind);
                    Assert.True(e.Next == null || EnvelopeInvariants.CodePoints(e.Next) <= EnvelopeLimits.NextMaxChars);
                }
        }

        private sealed class FakeModificationOutsideTransactionException : Exception
        {
            public FakeModificationOutsideTransactionException() : base("Attempt to modify the model outside of transaction.") { }
        }
    }
}

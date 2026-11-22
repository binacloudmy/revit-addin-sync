#nullable enable
// StatementWrapper (W4 Task 2, spec §7.8): the model's text must be method-body
// statements only, and lines map back to the model's own numbering. Pure
// syntax; no Revit, nothing compiled except where a test says so.

using System.Linq;
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class StatementWrapperTests
    {
        [Fact]
        public void RejectsClassDeclaration()
        {
            var w = StatementWrapper.Build("var n = 1;\nclass Evil { public static void Run() { } }\nPrint(n);");
            Assert.False(w.IsStatementsOnly);
            var v = Assert.Single(w.Violations);
            Assert.Equal("declaration_outside_body", v.Category);
            Assert.Equal(2, v.Line);
            Assert.Contains("Evil", v.Symbol);
            Assert.Null(w.Body);
        }

        [Theory]
        [InlineData("struct S { }")]
        [InlineData("interface I { }")]
        [InlineData("enum E { A }")]
        [InlineData("public int Field;")]
        [InlineData("namespace N { }")]
        public void RejectsOtherDeclarations(string code)
        {
            var w = StatementWrapper.Build(code);
            Assert.False(w.IsStatementsOnly);
            Assert.All(w.Violations, v => Assert.Equal("declaration_outside_body", v.Category));
        }

        [Theory]
        [InlineData("delegate void D();")]
        [InlineData("record R(int A);")]
        [InlineData("using System.IO;")]
        public void OtherNonStatementsAreRejectedOrDoNotCompile(string code)
        {
            // In a method body these are not declarations: they fail to parse or to compile, and never run.
            Assert.NotEqual(CodeStage.Allowed, CodeGate.Analyze(code, CodeTestSupport.ProductionRefs()).Stage);
        }

        [Fact]
        public void RejectsClosingTheBodyEarly()
        {
            var w = StatementWrapper.Build("Print(1);\n}\n}\nnamespace Evil { class X { } }\nnamespace Pad {\nclass Y {\nvoid Z() {");
            Assert.False(w.IsStatementsOnly);
            Assert.Equal("declaration_outside_body", w.Violations[0].Category);
        }

        [Fact]
        public void RejectsAStrayClosingBraceAtItsLine()
        {
            var w = StatementWrapper.Build("Print(1);\nPrint(2);\n}");
            var v = Assert.Single(w.Violations);
            Assert.Equal("wrapper_escape", v.Construct);
            Assert.Equal(3, v.Line);
        }

        [Fact]
        public void AcceptsStatementsLocalFunctionsAndLambdas()
        {
            var w = StatementWrapper.Build(
                "int Twice(int x) => x * 2;\nvar list = new List<int> { 1, 2 };\nforeach (var i in list.Select(i => Twice(i))) Print(i);\nif (list.Count > 1) { Print(\"many\"); } else return;");
            Assert.True(w.IsStatementsOnly, string.Join("; ", w.Violations));
            Assert.Empty(w.ParseErrors);
            Assert.NotNull(w.Body);
        }

        [Theory]
        [InlineData("unsafe { }", "unsafe")]
        [InlineData("int x = 1; var p = &x;", "pointer")]
        [InlineData("fixed (char* c = \"x\") { }", "fixed")]
        [InlineData("var s = stackalloc int[2];", "stackalloc")]
        [InlineData("lock (doc) { }", "lock")]
        [InlineData("async System.Threading.Tasks.Task F() { } ", "async")]
        [InlineData("var f = async () => 1;", "async")]
        [InlineData("a: Print(1);", "label")]
        [InlineData("for (;;) goto end; ", "goto")]
        [InlineData("IEnumerable<int> G() { yield break; }", "yield")]
        [InlineData("var f = [System.Obsolete] () => 1;", "attribute")]
        [InlineData("extern static void X();", "extern")]
        [InlineData("#if DEBUG\nPrint(1);\n#endif", "directive")]
        [InlineData("#line 1 \"other.cs\"\nPrint(1);", "directive")]
        [InlineData("var __x = 1;", "reserved_identifier")]
        [InlineData("__tick = null;", "reserved_identifier")]
        [InlineData("var x = __makeref(doc);", "undocumented_keyword")]
        public void RejectsBannedStatementForms(string code, string construct)
        {
            var w = StatementWrapper.Build(code);
            Assert.False(w.IsStatementsOnly, "expected " + construct);
            Assert.Contains(w.Violations, v => v.Construct == construct);
        }

        [Fact]
        public void SyntaxErrorsAreCompileErrorsNotRejections()
        {
            var w = StatementWrapper.Build("var n = 1\nPrint(n);");
            Assert.True(w.IsStatementsOnly);
            Assert.NotEmpty(w.ParseErrors);
            Assert.Equal(1, w.ModelLine(w.ParseErrors[0].Location.GetLineSpan().StartLinePosition.Line));
        }

        [Fact]
        public void LineOffsetIsComputedFromTheTree()
        {
            var revit = StatementWrapper.Build("Print(1);\nPrint(2);");
            var pure = StatementWrapper.Build("Print(1);\nPrint(2);", WrapperProfile.Pure);
            // The Revit profile has more usings, so its offset is larger: proof it is not a constant.
            Assert.True(revit.LineOffset > pure.LineOffset);
            foreach (var w in new[] { revit, pure })
            {
                var text = w.Tree.GetText();
                Assert.Equal("Print(1);", text.Lines[w.LineOffset].ToString());
                Assert.Equal("Print(2);", text.Lines[w.LineOffset + 1].ToString());
                Assert.Equal(2, w.ModelLineCount);
                Assert.Equal(1, w.ModelLine(w.LineOffset));
                Assert.Equal(2, w.ModelLine(w.LineOffset + 1));
                Assert.Equal(1, w.ModelLine(0));        // wrapper lines clamp to the text
                Assert.Equal(2, w.ModelLine(w.LineOffset + 40));
            }
        }

        [Fact]
        public void ModelTextIsKeptVerbatim()
        {
            const string code = "  var s = \"tab\\there\";\r\n\tPrint(s); // trailing comment";
            var w = StatementWrapper.Build(code);
            Assert.Equal(code, w.ModelCode);
            Assert.Equal(code, w.Tree.GetText().ToString(w.ModelSpan));
        }

        [Fact]
        public void TrailingLineCommentCannotSwallowTheWrapper()
        {
            var w = StatementWrapper.Build("Print(1); // }");
            Assert.True(w.IsStatementsOnly, string.Join("; ", w.Violations));
            Assert.Empty(w.ParseErrors);
        }

        [Fact]
        public void UnterminatedCommentIsASyntaxError()
        {
            var w = StatementWrapper.Build("Print(1); /* the rest of the wrapper");
            Assert.False(w.IsStatementsOnly && w.ParseErrors.Count == 0);
        }

        [Fact]
        public void FromTree_FindsTheSameText()
        {
            var w = StatementWrapper.Build("Print(1);\nPrint(2);");
            var again = StatementWrapper.FromTree(w.Tree);
            Assert.Equal(w.ModelSpan, again.ModelSpan);
            Assert.Equal(w.LineOffset, again.LineOffset);
            Assert.Equal(WrapperProfile.Revit, again.Profile);
        }
    }
}

#nullable enable
// AI-327: CrashGuard swallows an unhandled exception on Revit's UI dispatcher
// only when the add-in's own code (or a run_revit_code snippet) is on its
// stack; everything else is left to Revit.

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CrashGuardPolicyTests
    {
        private static readonly Assembly Here = typeof(CrashGuardPolicyTests).Assembly;
        private static readonly Assembly NotOurs = typeof(Assert).Assembly;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception Thrown(Exception? inner = null)
        {
            try { throw new InvalidOperationException("from our code", inner); }
            catch (Exception ex) { return ex; }
        }

        [Fact]
        public void An_exception_thrown_by_our_code_is_ours()
        {
            Assert.True(CrashGuardPolicy.ShouldHandle(Thrown(), Here));
        }

        [Fact]
        public void An_exception_with_none_of_our_frames_is_left_to_revit()
        {
            Assert.False(CrashGuardPolicy.ShouldHandle(Thrown(), NotOurs));
            // Never thrown: no stack at all, so nothing says it is ours.
            Assert.False(CrashGuardPolicy.ShouldHandle(new InvalidOperationException("no trace"), Here));
            Assert.False(CrashGuardPolicy.ShouldHandle(null, Here));
        }

        [Fact]
        public void Our_frame_in_an_inner_exception_counts()
        {
            var wrapper = new System.Reflection.TargetInvocationException(Thrown());
            Assert.True(CrashGuardPolicy.ShouldHandle(wrapper, Here));
        }

        [Fact]
        public void Our_frame_in_an_aggregated_exception_counts()
        {
            var agg = new AggregateException(new InvalidOperationException("other"), Thrown());
            Assert.True(CrashGuardPolicy.ShouldHandle(agg, Here));
            Assert.False(CrashGuardPolicy.ShouldHandle(agg, NotOurs));
        }

        [Fact]
        public void Snippet_assemblies_are_ours_by_name()
        {
            var snippet = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("BinaSnippet_0123456789abcdef_1"), AssemblyBuilderAccess.Run);
            var other = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("SomeoneElse"), AssemblyBuilderAccess.Run);
            Assert.True(CrashGuardPolicy.IsOurAssembly(snippet, NotOurs));
            Assert.False(CrashGuardPolicy.IsOurAssembly(other, NotOurs));
            Assert.True(CrashGuardPolicy.IsOurAssembly(Here, Here));
            Assert.False(CrashGuardPolicy.IsOurAssembly(null, Here));
        }

        [Fact]
        public void A_compiled_snippet_carries_the_prefix_and_its_exceptions_are_ours()
        {
            var prepared = BinaVibe.Copilot.Code.CodeRunner.Prepare("Print(1 / int.Parse(\"0\"));",
                RevitWebAppSync.Tests.CodeTestSupport.ProductionRefs(), BinaVibe.Copilot.Code.WrapperProfile.Pure);
            Assert.NotNull(prepared.Snippet);
            var snippet = prepared.Snippet!.Assembly;
            Assert.StartsWith(CrashGuardPolicy.SnippetAssemblyPrefix, snippet.GetName().Name);

            var run = BinaVibe.Copilot.Code.CodeRunner.Execute(prepared.Snippet, Array.Empty<object?>(),
                BinaVibe.Copilot.Code.LoopBudget.FromPolicy(), new BinaVibe.Copilot.Code.OutputBuffer());
            Assert.False(run.Ok);
            Assert.True(CrashGuardPolicy.HasOurFrame(run.Exception, a => CrashGuardPolicy.IsOurAssembly(a, NotOurs)));
        }

        [Fact]
        public void A_very_deep_chain_stops_cleanly()
        {
            // AggregateException nesting deeper than the walk's cap stops cleanly.
            Exception e = new InvalidOperationException("leaf");
            for (var i = 0; i < 100; i++) e = new AggregateException(e);
            Assert.False(CrashGuardPolicy.ShouldHandle(e, Here));
        }
    }
}

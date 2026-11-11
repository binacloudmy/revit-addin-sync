#nullable enable
// AI-327: which unhandled exceptions CrashGuard may swallow. Revit-free and
// WPF-free (Tests.csproj links it). Ours = a frame of this add-in's assembly,
// or of a run_revit_code snippet assembly, anywhere in the exception chain
// (inner and aggregated exceptions included).

using System;
using System.Diagnostics;
using System.Reflection;

namespace RevitWebAppSync.Services
{
    public static class CrashGuardPolicy
    {
        /// <summary>Snippet assemblies are named BinaSnippet_&lt;hash&gt;_&lt;n&gt; (CodeRunner).</summary>
        public const string SnippetAssemblyPrefix = "BinaSnippet_";

        private const int MaxChain = 32;

        /// <summary>Whether <paramref name="assembly"/> is ours: the add-in's own, or a compiled snippet.</summary>
        public static bool IsOurAssembly(Assembly? assembly, Assembly ours)
        {
            if (assembly == null) return false;
            if (ReferenceEquals(assembly, ours)) return true;
            string? name;
            try { name = assembly.GetName().Name; }
            catch (Exception) { return false; }
            return name != null && name.StartsWith(SnippetAssemblyPrefix, StringComparison.Ordinal);
        }

        /// <summary>True when a frame of <paramref name="ex"/>, or of an exception inside it
        /// (inner or aggregated), runs code from an assembly <paramref name="isOurs"/> accepts.</summary>
        public static bool HasOurFrame(Exception? ex, Func<Assembly?, bool> isOurs)
        {
            if (ex == null || isOurs == null) return false;
            var seen = 0;
            return Walk(ex, isOurs, ref seen);
        }

        /// <summary>Whether an unhandled exception is ours to swallow: our code is on its stack.</summary>
        public static bool ShouldHandle(Exception? ex, Assembly ours) =>
            HasOurFrame(ex, a => IsOurAssembly(a, ours));

        private static bool Walk(Exception ex, Func<Assembly?, bool> isOurs, ref int seen)
        {
            if (++seen > MaxChain) return false;   // a pathological chain: stop looking
            try
            {
                var frames = new StackTrace(ex, false).GetFrames();
                if (frames != null)
                    foreach (var f in frames)
                        if (isOurs(f?.GetMethod()?.DeclaringType?.Assembly)) return true;
            }
            catch (Exception) { /* an unreadable trace says nothing */ }

            if (ex is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                    if (inner != null && Walk(inner, isOurs, ref seen)) return true;
                return false;
            }
            return ex.InnerException != null && Walk(ex.InnerException, isOurs, ref seen);
        }
    }
}

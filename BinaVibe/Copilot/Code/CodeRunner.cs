#nullable enable
// Prepares and runs run_revit_code snippets. Revit-free: the Revit-bound tool
// passes doc/uidoc/view in as plain objects, so tests compile AND run pure C#
// (WrapperProfile.Pure) through exactly the same path.
//
// Prepare(): CodeGate.Analyze (statements only, compile, policy) -> stop with a
// CodeFailure, or LoopBudgetRewriter -> re-parse -> recompile -> prove the
// rewrite kept every line and referenced exactly the symbols the policy
// checked (CodePolicy.CheckBoundTree; anything else fails closed) -> emit with
// an embedded PDB so stack frames carry line numbers -> load. Prepared code is
// cached by its text, so a write's preview and commit compile once.
// Execute(): binds the hooks (Print, budget), invokes Run, maps any exception.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Code
{
    public sealed class PreparedCode
    {
        internal PreparedCode(CodeAnalysis analysis) => Analysis = analysis;

        public CodeAnalysis Analysis { get; }

        /// <summary>Ready to run; null when <see cref="Failure"/> says why not.</summary>
        public CompiledSnippet? Snippet { get; internal set; }

        public CodeFailure? Failure { get; internal set; }

        public string CodeHash { get; internal set; } = "";
    }

    /// <summary>A loaded, budget-instrumented snippet.</summary>
    public sealed class CompiledSnippet
    {
        private readonly MethodInfo _bind;
        private readonly MethodInfo _run;

        internal CompiledSnippet(Assembly assembly, WrappedCode wrapped, CodeBudgetLimits budget, string codeHash)
        {
            Assembly = assembly;
            Wrapped = wrapped;
            Budget = budget;
            CodeHash = codeHash;
            var type = assembly.GetType(StatementWrapper.Namespace + "." + StatementWrapper.ClassName, throwOnError: true)!;
            _bind = type.GetMethod(StatementWrapper.BindMethod, BindingFlags.Public | BindingFlags.Static)!;
            _run = type.GetMethod(StatementWrapper.RunMethod, BindingFlags.Public | BindingFlags.Static)!;
        }

        public Assembly Assembly { get; }

        /// <summary>The rewritten tree's line mapping (same lines as the model's text).</summary>
        public WrappedCode Wrapped { get; }

        public CodeBudgetLimits Budget { get; }
        public string CodeHash { get; }
        public WrapperProfile Profile => Wrapped.Profile;

        /// <summary>Runs Run(hostArgs) with the hooks bound. Exceptions propagate (possibly wrapped
        /// in TargetInvocationException); CodeRunner.Execute maps them.</summary>
        public void Invoke(object?[] hostArgs, LoopBudget budget, Action<object?> print, Action<int, object?>? ui = null)
        {
            var expected = StatementWrapper.HostParameters(Profile).Count;
            if (hostArgs.Length != expected)
                throw new ArgumentException("Run takes " + expected + " host arguments", nameof(hostArgs));
            Action<object> printHook = o => print(o);
            Action tick = budget.Tick;
            Action enter = budget.Enter;
            Action exit = budget.Exit;
            Func<bool> live = () => budget.Live;
            Action<int, object> uiHook = (kind, value) => (ui ?? ((k, v) => { }))(kind, value);
            _bind.Invoke(null, new object?[] { printHook, tick, enter, exit, live, uiHook });
            try
            {
                _run.Invoke(null, hostArgs);
            }
            finally
            {
                // Anything the snippet left behind that runs later fails cleanly instead of with a null hook.
                Action dead = () => throw new InvalidOperationException("run_revit_code has finished; its code cannot run any more.");
                Action<object> deadPrint = _ => dead();
                Func<bool> notLive = () => false;
                Action<int, object> deadUi = (k, v) => dead();
                _bind.Invoke(null, new object?[] { deadPrint, dead, dead, (Action)(() => { }), notLive, deadUi });
            }
        }
    }

    public sealed class CodeRunResult
    {
        public bool Ok { get; internal set; }
        public CodeFailure? Failure { get; internal set; }

        /// <summary>The unwrapped exception, when the run failed.</summary>
        public Exception? Exception { get; internal set; }

        public long Ticks { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }
    }

    public static class CodeRunner
    {
        private const int CacheSize = 32;
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, LinkedListNode<(string Key, PreparedCode Value)>> CacheIndex =
            new Dictionary<string, LinkedListNode<(string, PreparedCode)>>(StringComparer.Ordinal);
        private static readonly LinkedList<(string Key, PreparedCode Value)> CacheOrder = new LinkedList<(string, PreparedCode)>();
        private static int _assemblyCounter;

        public static PreparedCode Prepare(string? code, IReadOnlyList<MetadataReference> references,
            WrapperProfile profile = WrapperProfile.Revit, CodePolicySet? policy = null)
        {
            policy ??= CodePolicySet.Default;
            var text = code ?? "";
            var hash = CodeGate.Hash(text);
            var key = profile + ":" + RuntimeHelpers.GetHashCode(references) + ":" + RuntimeHelpers.GetHashCode(policy) + ":" + hash;
            lock (CacheGate)
            {
                if (CacheIndex.TryGetValue(key, out var hit))
                {
                    CacheOrder.Remove(hit);
                    CacheOrder.AddFirst(hit);
                    return hit.Value.Value;
                }
            }

            var prepared = PrepareUncached(text, hash, references, profile, policy);
            lock (CacheGate)
            {
                if (!CacheIndex.ContainsKey(key))
                {
                    CacheIndex[key] = CacheOrder.AddFirst((key, prepared));
                    while (CacheOrder.Count > CacheSize)
                    {
                        CacheIndex.Remove(CacheOrder.Last!.Value.Key);
                        CacheOrder.RemoveLast();
                    }
                }
            }
            return prepared;
        }

        public static CodeRunResult Execute(CompiledSnippet snippet, object?[] hostArgs, LoopBudget budget, OutputBuffer output,
            bool readMode = true, Action<int, object?>? ui = null)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                snippet.Invoke(hostArgs, budget, output.Print, ui);
                if (!budget.Live)
                {
                    // Belt and braces: the catch filters already stop a catch from swallowing the stop,
                    // but a run that returns after the budget tripped is still a stopped run.
                    var stop = new BudgetExceededException(budget.Reason ?? BudgetReason.Steps, budget.Ticks, budget.Elapsed, budget.Depth);
                    return new CodeRunResult
                    {
                        Ok = false,
                        Exception = stop,
                        Failure = ErrorMapper.FromException(stop, snippet, readMode),
                        Ticks = budget.Ticks,
                        Elapsed = watch.Elapsed,
                    };
                }
                return new CodeRunResult { Ok = true, Ticks = budget.Ticks, Elapsed = watch.Elapsed };
            }
            catch (Exception ex)
            {
                // A tripped budget is the cause even when the BCL swallowed and rethrew something else.
                var cause = !budget.Live && !(ErrorMapper.Unwrap(ex) is BudgetExceededException)
                    ? new BudgetExceededException(budget.Reason ?? BudgetReason.Steps, budget.Ticks, budget.Elapsed, budget.Depth)
                    : ex;
                return new CodeRunResult
                {
                    Ok = false,
                    Exception = ErrorMapper.Unwrap(cause),
                    Failure = ErrorMapper.FromException(cause, snippet, readMode),
                    Ticks = budget.Ticks,
                    Elapsed = watch.Elapsed,
                };
            }
        }

        private static PreparedCode PrepareUncached(string code, string hash, IReadOnlyList<MetadataReference> references,
            WrapperProfile profile, CodePolicySet policy)
        {
            var analysis = CodeGate.Analyze(code, references, profile, policy);
            var prepared = new PreparedCode(analysis) { CodeHash = hash };
            if (!analysis.Allowed)
            {
                prepared.Failure = ErrorMapper.FromAnalysis(analysis);
                return prepared;
            }

            try
            {
                var original = analysis.Compilation!;
                var wrapped = analysis.Wrapped;
                var rewrittenText = LoopBudgetRewriter.Rewrite(wrapped, original.GetSemanticModel(wrapped.Tree), policy);
                var tree = CSharpSyntaxTree.ParseText(
                    SourceText.From(rewrittenText, Encoding.UTF8),
                    StatementWrapper.ParseOptions(policy.LanguageVersion),
                    StatementWrapper.TreePath);

                if (tree.GetText().Lines.Count != wrapped.Tree.GetText().Lines.Count)
                    return Internal(prepared, "the budget rewrite moved lines");

                var name = "BinaSnippet_" + hash.Substring(0, 16) + "_" + System.Threading.Interlocked.Increment(ref _assemblyCounter);
                var compilation = original.ReplaceSyntaxTree(wrapped.Tree, tree).WithAssemblyName(name);
                var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                if (errors.Count > 0)
                    return Internal(prepared, "the budget rewrite does not compile: " + errors[0].GetMessage());

                var rewritten = StatementWrapper.FromTree(tree);
                var check = CodePolicy.CheckBoundTree(compilation, rewritten, policy);
                if (!check.Allowed)
                    return Internal(prepared, "the budget rewrite added a symbol the policy denies: " + check.Violations[0]);
                // UI requests are rewritten to host shims (ruling R59): they are expected to disappear.
                var before = new HashSet<string>(analysis.Policy!.Referenced.Except(analysis.Policy.Shimmed), StringComparer.Ordinal);
                if (!before.SetEquals(check.Referenced))
                    return Internal(prepared, "the budget rewrite changed what the code calls ("
                        + string.Join(", ", check.Referenced.Except(before).Concat(before.Except(check.Referenced)).Take(3)) + ")");

                // Snippet assemblies are never unloaded: a session loads at most max_compiles of them (M3).
                if (policy.Compiled >= policy.Budget.MaxCompiles)
                    return Capped(prepared, policy.Budget.MaxCompiles);

                using var pe = new MemoryStream();
                var emit = compilation.Emit(pe, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
                if (!emit.Success)
                    return Internal(prepared, "emit failed: " + emit.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error)?.GetMessage());

                prepared.Snippet = new CompiledSnippet(Load(pe), rewritten, policy.Budget, hash);
                policy.CountCompile();
                return prepared;
            }
            catch (Exception ex)
            {
                return Internal(prepared, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static Assembly Load(MemoryStream pe)
        {
#if NETFRAMEWORK
            // Never unloadable on .NET Framework: one small assembly per distinct snippet per session.
            return Assembly.Load(pe.ToArray());
#else
            // Default context: snippets resolve RevitAPI from the host. Never unloaded (unloading a
            // context whose types are still referenced crashed Revit, see CodeExecutor history).
            pe.Position = 0;
            return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(pe);
#endif
        }

        private static PreparedCode Capped(PreparedCode prepared, int max)
        {
            prepared.Failure = new CodeFailure
            {
                Code = ErrorCodes.NotAllowed,
                Summary = "This Revit session has compiled " + max + " different snippets, the most it keeps loaded. The code was not run.",
                Message = "This Revit session has already compiled " + max + " different run_revit_code snippets, the most it keeps loaded "
                          + "(compiled code is never unloaded). The code was not run. Reuse code that already ran, or ask the drafter to restart Revit to compile more.",
                Next = "Reuse code that already ran this session, or ask the drafter to restart Revit.",
                Data = new Dictionary<string, object?> { ["max_compiles"] = max },
            };
            return prepared;
        }

        /// <summary>Our own failure, never the model's: fail closed and say so.</summary>
        private static PreparedCode Internal(PreparedCode prepared, string why)
        {
            prepared.Failure = new CodeFailure
            {
                Code = ErrorCodes.RuntimeError,
                Summary = "The add-in could not prepare this code (" + why + "). It was not run.",
                Message = "Internal error preparing run_revit_code: " + why + ". The code was not run.",
                Next = "Try a simpler form of the same code; report this if it repeats.",
                Data = new Dictionary<string, object?> { ["internal"] = why },
            };
            return prepared;
        }
    }
}

#nullable enable
// Turns everything that can stop run_revit_code into one CodeFailure the model
// can act on (spec §7.8 "Errors"). Revit-free.
//
//   compile errors     -> compile_error, "Line N, col C: CSxxxx message" with N in the
//                         model's own numbering (WrappedCode.ModelLine: offset computed)
//   policy rejections  -> policy_rejected, naming the symbol, the line and the reason
//   runtime exceptions -> runtime_error, unwrapped from TargetInvocationException, with
//                         type, message and the top frames that are in the model's code
//   budget stop        -> timeout (status timeout)
// ToEnvelope() makes a contract envelope that passes EnvelopeInvariants.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using Microsoft.CodeAnalysis;

namespace BinaVibe.Copilot.Code
{
    public sealed class CodeFailure
    {
        /// <summary>One of <see cref="ErrorCodes"/>.</summary>
        public string Code { get; set; } = ErrorCodes.RuntimeError;

        /// <summary>One of <see cref="EnvelopeStatus"/> (failed or timeout).</summary>
        public string Status { get; set; } = EnvelopeStatus.Failed;

        private string _summary = "";

        /// <summary>At most 300 characters (longer text is cut with "…").</summary>
        public string Summary
        {
            get => _summary;
            set => _summary = EnvelopeFactory.Shorten(value, EnvelopeLimits.SummaryMaxChars);
        }

        /// <summary>Everything the model needs to fix it (several lines).</summary>
        public string Message { get; set; } = "";

        /// <summary>At most 200 characters, or null.</summary>
        public string? Next { get; set; }

        /// <summary>Model lines involved (1-based), most relevant first.</summary>
        public List<int> Lines { get; set; } = new List<int>();

        /// <summary>The envelope's data object.</summary>
        public Dictionary<string, object?> Data { get; set; } = new Dictionary<string, object?>();

        /// <summary>The failed envelope, through W1's builder (invariants enforced; status from the code:
        /// timeout and revit_busy keep theirs, everything else is failed).</summary>
        public Envelope ToEnvelope(string phase)
        {
            var builder = EnvelopeBuilder.Failure(Code, Message, null, CodeEnvelopes.WritePhaseOf(phase)).WithData(Data);
            if (Next != null) builder.WithNext(EnvelopeFactory.Shorten(Next, EnvelopeLimits.NextMaxChars));
            var e = builder.Build();
            if (Summary.Length > 0) e.Summary = Summary; // already at most 300 characters
            return e;
        }
    }

    public static class ErrorMapper
    {
        public const int MaxErrors = 10;
        public const int MaxFrames = 3;
        private const int MaxMessageChars = 2000;

        public static CodeFailure FromAnalysis(CodeAnalysis analysis)
        {
            switch (analysis.Stage)
            {
                case CodeStage.CompileError:
                    return FromCompileErrors(analysis.Errors, analysis.Wrapped);
                case CodeStage.Rejected:
                case CodeStage.PolicyRejected:
                    return FromPolicy(analysis.Violations);
                default:
                    throw new ArgumentException("the analysis allowed the code; there is no failure to map", nameof(analysis));
            }
        }

        public static CodeFailure FromCompileErrors(IReadOnlyList<Diagnostic> diagnostics, WrappedCode wrapped)
        {
            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d =>
                {
                    var pos = d.Location.GetLineSpan().StartLinePosition;
                    var inText = wrapped.IsModelLine(pos.Line);
                    return new
                    {
                        Line = wrapped.ModelLine(pos.Line),
                        Column = inText ? pos.Character + 1 : 0,
                        d.Id,
                        Text = d.GetMessage(CultureInfo.InvariantCulture),
                        AtEnd = !inText,
                    };
                })
                .GroupBy(e => (e.Line, e.Id, e.Text)).Select(g => g.First())
                .OrderBy(e => e.Line).ThenBy(e => e.Column)
                .ToList();

            var shown = errors.Take(MaxErrors).ToList();
            var lines = shown.Select(e =>
                "Line " + e.Line + (e.AtEnd ? " (end of code)" : ", col " + e.Column) + ": " + e.Id + ": " + e.Text).ToList();
            if (errors.Count > shown.Count) lines.Add("(" + (errors.Count - shown.Count) + " more errors)");

            var first = shown.FirstOrDefault();
            return new CodeFailure
            {
                Code = ErrorCodes.CompileError,
                Summary = first == null ? "The code does not compile." : "Compile error at line " + first.Line + ": " + first.Text,
                Message = "The code does not compile:\n" + string.Join("\n", lines),
                Next = "Fix the lines above and retry; the line numbers are your code's.",
                Lines = shown.Select(e => e.Line).Distinct().ToList(),
                Data = new Dictionary<string, object?>
                {
                    ["errors"] = shown.Select(e => new Dictionary<string, object?>
                    {
                        ["line"] = e.Line,
                        ["column"] = e.Column,
                        ["id"] = e.Id,
                        ["message"] = e.Text,
                    }).ToList(),
                    ["error_count"] = errors.Count,
                },
            };
        }

        public static CodeFailure FromPolicy(IReadOnlyList<PolicyViolation> violations)
        {
            var shown = violations.OrderBy(v => v.Line).ThenBy(v => v.Column).Take(MaxErrors).ToList();
            var lines = shown.Select(v => "Line " + v.Line + ": " + v.Symbol + " is not allowed (" + v.Category + ": " + v.Why + ").").ToList();
            if (violations.Count > shown.Count) lines.Add("(" + (violations.Count - shown.Count) + " more)");
            var first = shown.FirstOrDefault();
            return new CodeFailure
            {
                Code = ErrorCodes.PolicyRejected,
                Summary = first == null
                    ? "The code is not allowed."
                    : "Not allowed at line " + first.Line + ": " + first.Symbol + " (" + first.Why + ").",
                Message = "The code was not run:\n" + string.Join("\n", lines),
                Next = "Rewrite those lines with allowed Revit API and basic System calls, then retry.",
                Lines = shown.Select(v => v.Line).Distinct().ToList(),
                Data = new Dictionary<string, object?>
                {
                    ["violations"] = shown.Select(v => new Dictionary<string, object?>
                    {
                        ["line"] = v.Line,
                        ["column"] = v.Column,
                        ["symbol"] = v.Symbol,
                        ["category"] = v.Category,
                        ["why"] = v.Why,
                    }).ToList(),
                },
            };
        }

        /// <summary>A runtime exception or budget stop. readMode adds the hint for a write that ran as a read.</summary>
        public static CodeFailure FromException(Exception exception, CompiledSnippet? snippet, bool readMode)
        {
            var ex = Unwrap(exception);
            var lines = snippet == null ? new List<int>() : ModelFrames(ex, snippet);
            var at = lines.Count > 0 ? " at line " + lines[0] : "";

            if (ex is BudgetExceededException budget)
            {
                var limit = budget.Reason switch
                {
                    BudgetReason.Steps => (snippet?.Budget.MaxTicks ?? CodePolicySet.Default.Budget.MaxTicks).ToString("N0", CultureInfo.InvariantCulture) + " loop steps",
                    BudgetReason.Time => (snippet?.Budget.MaxSeconds ?? CodePolicySet.Default.Budget.MaxSeconds) + " s",
                    _ => (snippet?.Budget.MaxDepth ?? CodePolicySet.Default.Budget.MaxDepth) + " nested calls",
                };
                return new CodeFailure
                {
                    Code = ErrorCodes.Timeout,
                    Status = EnvelopeStatus.Timeout,
                    Summary = "Stopped" + at + ": the code reached the limit of " + limit + ".",
                    Message = "The code was stopped" + at + " after reaching the limit of " + limit
                              + ". Nothing it did was kept. Loops over the whole model must filter first.",
                    Next = budget.Reason == BudgetReason.Depth
                        ? "Remove the runaway recursion, or make it iterative with an explicit stack."
                        : "Filter with FilteredElementCollector first, or stop the loop early, then retry.",
                    Lines = lines,
                    Data = new Dictionary<string, object?>
                    {
                        ["limit"] = budget.Reason.ToString().ToLowerInvariant(),
                        ["steps"] = budget.Ticks,
                        ["seconds"] = Math.Round(budget.Elapsed.TotalSeconds, 1),
                        ["lines"] = lines,
                    },
                };
            }

            var typeName = ex.GetType().FullName ?? ex.GetType().Name;
            var shortType = ex.GetType().Name;
            var message = Clip(ex.Message, MaxMessageChars);
            var modificationOutside = shortType.EndsWith("ModificationOutsideTransactionException", StringComparison.Ordinal);
            var frames = lines.Count > 0 ? "\nIn your code: " + string.Join(" <- ", lines.Select(l => "line " + l)) : "";
            return new CodeFailure
            {
                Code = ErrorCodes.RuntimeError,
                Summary = "Runtime error" + at + ": " + shortType + ": " + message,
                Message = shortType + at + ": " + message + frames,
                Next = modificationOutside && readMode
                    ? "This code changes the model but ran as a read. Pass expected (adds, modifies or deletes) so it is previewed as a change."
                    : "Fix the code at that line and retry.",
                Lines = lines,
                Data = new Dictionary<string, object?>
                {
                    ["exception"] = typeName,
                    ["message"] = message,
                    ["lines"] = lines,
                },
            };
        }

        /// <summary>TargetInvocationException (and single-inner AggregateException) unwrapped to the real error.</summary>
        public static Exception Unwrap(Exception exception)
        {
            var ex = exception;
            for (var i = 0; i < 16; i++)
            {
                if (ex is TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
                else if (ex is AggregateException ae && ae.InnerExceptions.Count == 1) ex = ae.InnerExceptions[0];
                else break;
            }
            return ex;
        }

        /// <summary>The model lines of the top frames that are in the snippet's own code, innermost first.</summary>
        public static List<int> ModelFrames(Exception ex, CompiledSnippet snippet)
        {
            var result = new List<int>();
            StackFrame[] frames;
            try
            {
                frames = new StackTrace(ex, true).GetFrames() ?? Array.Empty<StackFrame>();
            }
            catch
            {
                return result;
            }
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method == null || method.Module.Assembly != snippet.Assembly) continue;
                var line = frame.GetFileLineNumber();
                if (line <= 0 || !snippet.Wrapped.IsModelLine(line - 1)) continue;
                var modelLine = snippet.Wrapped.ModelLine(line - 1);
                if (result.Count == 0 || result[result.Count - 1] != modelLine) result.Add(modelLine);
                if (result.Count >= MaxFrames) break;
            }
            return result;
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}

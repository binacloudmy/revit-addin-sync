#nullable enable
// The run_revit_code front end, in a fixed order (spec §7.8). Revit-free.
//
//   1. StatementWrapper.Build   statements only (escape, declarations)    -> policy_rejected
//   2.   parse errors                                                      -> compile_error
//   3.   banned statement forms (unsafe, async, goto, "__" names, ...)    -> policy_rejected
//   4. compile against the narrow reference set                            -> compile_error
//   5. CodePolicy.Check on the bound tree                                   -> policy_rejected
//   6. ModeClassifier                                                      -> read | write
// Nothing runs unless every step passes. Prepare() (CodeRunner) adds the loop
// budget rewrite and emit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BinaVibe.Copilot.Code
{
    public enum CodeStage
    {
        /// <summary>The text is not statements only, or uses a banned statement form.</summary>
        Rejected,
        /// <summary>Syntax or compile errors.</summary>
        CompileError,
        /// <summary>It compiles, but a symbol is not allowed.</summary>
        PolicyRejected,
        /// <summary>Allowed: ready to rewrite and emit.</summary>
        Allowed,
    }

    public sealed class CodeAnalysis
    {
        internal CodeAnalysis(WrappedCode wrapped, CodeStage stage)
        {
            Wrapped = wrapped;
            Stage = stage;
        }

        public WrappedCode Wrapped { get; }
        public CodeStage Stage { get; internal set; }
        public CSharpCompilation? Compilation { get; internal set; }

        /// <summary>Parse or compile errors (Stage == CompileError).</summary>
        public IReadOnlyList<Diagnostic> Errors { get; internal set; } = Array.Empty<Diagnostic>();

        /// <summary>Why the code may not run (Stage == Rejected or PolicyRejected).</summary>
        public IReadOnlyList<PolicyViolation> Violations { get; internal set; } = Array.Empty<PolicyViolation>();

        public PolicyResult? Policy { get; internal set; }

        /// <summary>Read or write, from the code itself (only when Allowed).</summary>
        public ModeDecision? Mode { get; internal set; }

        public bool Allowed => Stage == CodeStage.Allowed;
    }

    public static class CodeGate
    {
        public static CSharpCompilationOptions CompilationOptions { get; } = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: false,
            nullableContextOptions: NullableContextOptions.Disable,
            concurrentBuild: false);

        public static CodeAnalysis Analyze(string? code, IReadOnlyList<MetadataReference> references,
            WrapperProfile profile = WrapperProfile.Revit, CodePolicySet? policy = null)
        {
            policy ??= CodePolicySet.Default;
            var wrapped = StatementWrapper.Build(code, profile, policy.LanguageVersion);

            if (!wrapped.IsStatementsOnly)
                return new CodeAnalysis(wrapped, CodeStage.Rejected) { Violations = wrapped.Violations };
            if (wrapped.ParseErrors.Count > 0)
                return new CodeAnalysis(wrapped, CodeStage.CompileError) { Errors = wrapped.ParseErrors };

            var compilation = CSharpCompilation.Create(
                "BinaSnippet_" + Hash(code ?? "").Substring(0, 16),
                new[] { wrapped.Tree },
                references,
                CompilationOptions);

            var analysis = new CodeAnalysis(wrapped, CodeStage.Allowed) { Compilation = compilation };
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                analysis.Stage = CodeStage.CompileError;
                analysis.Errors = errors;
                return analysis;
            }

            var result = CodePolicy.Check(compilation, wrapped, policy);
            analysis.Policy = result;
            if (!result.Allowed)
            {
                analysis.Stage = CodeStage.PolicyRejected;
                analysis.Violations = result.Violations;
                return analysis;
            }

            analysis.Mode = ModeClassifier.Classify(compilation, wrapped, policy);
            return analysis;
        }

        /// <summary>Lowercase hex SHA-256 of the UTF-8 text.</summary>
        public static string Hash(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}

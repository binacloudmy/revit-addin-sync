#nullable enable
// run_revit_code arguments as a typed DTO (ruling R14: the add-in does not load
// tools.json; it parses args itself and answers invalid_args on bad input).
// Revit-free. Shape (contracts/copilot/tools.json):
//   { code: string (required), intent: string <=120 (required),
//     expected?: { adds?: int>=0, modifies?: int>=0, deletes?: int>=0 } }
// Unknown keys are ignored (contracts README: readers ignore what they don't know).

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;

namespace BinaVibe.Copilot.Code
{
    public sealed class RunRevitCodeArgs
    {
        public const int IntentMaxChars = 120;

        /// <summary>A bound on what the add-in will even try to compile.</summary>
        public const int CodeMaxChars = 60_000;

        private RunRevitCodeArgs(string code, string intent, ExpectedChanges? expected)
        {
            Code = code;
            Intent = intent;
            Expected = expected;
        }

        /// <summary>For hosts that already have the code (the legacy CodeExecutor path).</summary>
        internal static RunRevitCodeArgs Create(string code, string intent, ExpectedChanges? expected = null) =>
            new RunRevitCodeArgs(code ?? "", string.IsNullOrWhiteSpace(intent) ? "run code" : intent, expected);

        public string Code { get; }
        public string Intent { get; }
        public ExpectedChanges? Expected { get; }

        public static bool TryParse(JsonElement args, out RunRevitCodeArgs? parsed, out string error)
        {
            parsed = null;
            if (args.ValueKind != JsonValueKind.Object)
            {
                error = "run_revit_code takes an object with code and intent.";
                return false;
            }

            if (!args.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(code.GetString()))
            {
                error = "code is required: C# method-body statements as a string.";
                return false;
            }
            var codeText = code.GetString()!;
            if (codeText.Length > CodeMaxChars)
            {
                error = "code is longer than " + CodeMaxChars.ToString("N0", CultureInfo.InvariantCulture) + " characters; split the job into smaller calls.";
                return false;
            }

            if (!args.TryGetProperty("intent", out var intent) || intent.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(intent.GetString()))
            {
                error = "intent is required: one line, shown to the drafter.";
                return false;
            }
            var intentText = intent.GetString()!.Trim();
            if (EnvelopeInvariants.CodePoints(intentText) > IntentMaxChars)
            {
                error = "intent must be at most " + IntentMaxChars + " characters.";
                return false;
            }

            ExpectedChanges? expected = null;
            if (args.TryGetProperty("expected", out var exp) && exp.ValueKind != JsonValueKind.Null)
            {
                if (exp.ValueKind != JsonValueKind.Object)
                {
                    error = "expected must be an object: {adds, modifies, deletes}.";
                    return false;
                }
                if (!Count(exp, "adds", out var adds, out error) || !Count(exp, "modifies", out var modifies, out error)
                    || !Count(exp, "deletes", out var deletes, out error))
                    return false;
                expected = new ExpectedChanges(adds, modifies, deletes);
            }

            parsed = new RunRevitCodeArgs(codeText, intentText, expected);
            error = "";
            return true;
        }

        private static bool Count(JsonElement expected, string name, out int? value, out string error)
        {
            value = null;
            error = "";
            if (!expected.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return true;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < 0)
            {
                error = "expected." + name + " must be a whole number, 0 or more.";
                return false;
            }
            value = n;
            return true;
        }

        /// <summary>The invalid_args envelope (phase read: nothing ran).</summary>
        public static Envelope Invalid(string error) =>
            EnvelopeBuilder.Fail(ErrorCodes.InvalidArgs, "run_revit_code: " + error);

        /// <summary>Sorted keys, no unknown keys, no whitespace: the same call always hashes the same.</summary>
        public string Canonical()
        {
            var sb = new StringBuilder();
            sb.Append("{\"code\":").Append(Quote(Code));
            if (Expected != null)
            {
                sb.Append(",\"expected\":{");
                var parts = new List<string>();
                if (Expected.Adds != null) parts.Add("\"adds\":" + Expected.Adds.Value.ToString(CultureInfo.InvariantCulture));
                if (Expected.Deletes != null) parts.Add("\"deletes\":" + Expected.Deletes.Value.ToString(CultureInfo.InvariantCulture));
                if (Expected.Modifies != null) parts.Add("\"modifies\":" + Expected.Modifies.Value.ToString(CultureInfo.InvariantCulture));
                sb.Append(string.Join(",", parts)).Append('}');
            }
            sb.Append(",\"intent\":").Append(Quote(Intent)).Append('}');
            return sb.ToString();
        }

        private static string Quote(string s) => JsonSerializer.Serialize(s);
    }
}

#nullable enable
// The one switch for Copilot's safety checks (AI-423). Revit-free.
//
//   Default (flag unset)         : safety OFF. The run_revit_code code policy
//                                  (CodePolicyTable / code-policy.json, banned
//                                  statement forms, write-only clock/Random/Guid
//                                  denial) is bypassed: nothing is rejected on
//                                  policy grounds. Compile errors, runtime errors,
//                                  and the structural "statements only" wrapper
//                                  stay, because without them the code cannot run.
//   BINA_COPILOT_SAFETY=1 (or true/on/yes) : safety ON. Today's behaviour, exactly.
//
// Read once per process. The checks are not deleted; each is bypassed at one
// site, behind CodePolicySet.Enforce, which defaults to this flag.

using System;

namespace BinaVibe.Copilot.Code
{
    public static class CopilotSafety
    {
        public const string EnvVar = "BINA_COPILOT_SAFETY";

        /// <summary>True when the safety checks are enforced. Read once from the environment.</summary>
        public static bool Enabled { get; } = Parse(Environment.GetEnvironmentVariable(EnvVar));

        public static bool Parse(string? value)
        {
            var v = (value ?? "").Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}

using System;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// TEST-ONLY switch for running the JKR pane in Revit without a login or the
    /// backend. Off unless the user sets environment variables:
    ///
    ///   BINA_DEV_UNLOCK=1            enables the JKR Compliance ribbon button
    ///                                (otherwise "Coming soon") and test mode.
    ///   BINA_JKR_FIXTURE=&lt;path.json&gt;  with unlock on: Run audit / Re-scan read
    ///                                issues from this fixture and the open model
    ///                                instead of calling the backend. A missing
    ///                                file is generated from the open model.
    ///
    /// Only the exact value "1" unlocks. With the variables absent nothing
    /// changes: same ribbon, same HTTP calls. Read once at Revit startup; restart
    /// Revit after changing them. Turn off:
    ///   [Environment]::SetEnvironmentVariable('BINA_DEV_UNLOCK', $null, 'User')
    ///   [Environment]::SetEnvironmentVariable('BINA_JKR_FIXTURE', $null, 'User')
    /// </summary>
    public sealed class JkrDevMode
    {
        public const string UnlockVar = "BINA_DEV_UNLOCK";
        public const string FixtureVar = "BINA_JKR_FIXTURE";

        /// <summary>BINA_DEV_UNLOCK is exactly "1".</summary>
        public bool Unlocked { get; }

        /// <summary>Fixture path; null unless unlocked and BINA_JKR_FIXTURE is set.</summary>
        public string FixturePath { get; }

        /// <summary>Scans read the fixture instead of calling the backend.</summary>
        public bool UsesFixture => FixturePath != null;

        private JkrDevMode(bool unlocked, string fixturePath)
        {
            Unlocked = unlocked;
            FixturePath = unlocked && !string.IsNullOrWhiteSpace(fixturePath) ? fixturePath.Trim().Trim('"') : null;
        }

        /// <summary>Decide from raw variable values (testable without touching the
        /// real environment).</summary>
        public static JkrDevMode From(Func<string, string> getVariable)
        {
            if (getVariable == null) throw new ArgumentNullException(nameof(getVariable));
            var unlocked = string.Equals((getVariable(UnlockVar) ?? "").Trim(), "1", StringComparison.Ordinal);
            return new JkrDevMode(unlocked, getVariable(FixtureVar));
        }

        private static readonly Lazy<JkrDevMode> _current =
            new Lazy<JkrDevMode>(() => From(Environment.GetEnvironmentVariable));

        /// <summary>The mode for this Revit session (process environment, which
        /// Revit inherits from the user's variables at start).</summary>
        public static JkrDevMode Current => _current.Value;
    }
}

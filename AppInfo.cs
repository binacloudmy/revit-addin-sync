using System.Reflection;

namespace RevitWebAppSync
{
    /// <summary>Single source of truth for the add-in version. Everything that
    /// displays a version — the kebab menu line, the Rate / Report sheet chips and
    /// the bug-report payload — reads from here, so bumping <c>&lt;Version&gt;</c>
    /// in the csproj (or a CI <c>-p:Version=</c> tag) flows to all of them with no
    /// other edit. The number is never hardcoded in the UI.</summary>
    public static class AppInfo
    {
        private static readonly System.Version Asm =
            Assembly.GetExecutingAssembly().GetName().Version ?? new System.Version(0, 0, 0);

        /// <summary>major.minor.patch, e.g. "2.4.1" — the sheet chips.</summary>
        public static string Version { get; } = Asm.ToString(3);

        /// <summary>major.minor, e.g. "2.4" — the kebab menu line.</summary>
        public static string ShortVersion { get; } = Asm.Major + "." + Asm.Minor;

        /// <summary>"Copilot {major.minor.patch}" — the sheet chip prefix.</summary>
        public static string AddinLabel => "Copilot " + Version;

        /// <summary>Revit host version ("Revit 2024.2"), set from the Revit API at
        /// pane init. Defaults to a stub so the UiHarness (outside Revit) still
        /// shows a plausible value.</summary>
        public static string RevitVersion { get; set; } = "Revit 2024.2";

        /// <summary>"Copilot x.y.z · Revit a.b" — the sheet context chip.</summary>
        public static string ShortLabel => AddinLabel + " · " + RevitVersion;
    }
}

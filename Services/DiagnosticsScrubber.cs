using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Client-side scrubbing of every text file in a diagnostics zip (OTA
    /// self-heal F6), applied before zipping:
    /// - lines containing token / password / authorization / bearer / cookie
    ///   (case-insensitive) are dropped;
    /// - the Bina folder becomes &lt;bina&gt;, %USERPROFILE% &lt;home&gt;;
    /// - full paths of files outside the Bina folder keep only the extension
    ///   (&lt;path&gt;\.rvt): model and drawing names are client data;
    /// - e-mail addresses, the machine name and the Windows user name become
    ///   &lt;email&gt;, &lt;machine&gt;, &lt;user&gt;.
    /// Versions, timestamps and stack traces (class/method names) survive.
    /// Revit-free; never throws.
    /// </summary>
    public sealed class DiagnosticsScrubber
    {
        private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(1);

        private static readonly string[] SecretWords = { "token", "password", "authorization", "bearer", "cookie" };

        // Drive-letter, UNC or <home>-rooted path ending in a file name with an
        // extension. Segments may contain spaces ("Hospital Kajang\L01 Central.rvt");
        // the extension must start with a letter (so 0.0.80 is not one) and be
        // followed by the end of the line or a delimiter.
        private static readonly Regex FilePath = new Regex(
            @"(?<![A-Za-z0-9])(?:[A-Za-z]:|\\\\[^\\/\s""<>|]+|<home>)" +
            @"(?:[\\/][^\\/:*?""<>|\r\n]+)*?[\\/][^\\/:*?""<>|\r\n]*?" +
            @"\.(?<ext>[A-Za-z][A-Za-z0-9]{0,7})(?=$|[\s""'<>|,;:)\]}])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexBudget);

        private static readonly Regex Email = new Regex(
            @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexBudget);

        private static readonly Regex LineBreak = new Regex(@"(\r?\n)", RegexOptions.Compiled);

        private readonly Regex _binaRoot;
        private readonly Regex _home;
        private readonly Regex _machine;
        private readonly Regex _user;

        public DiagnosticsScrubber(string userName, string userProfile, string machineName, string binaRoot)
        {
            _binaRoot = FolderPattern(binaRoot);
            _home = FolderPattern(userProfile);
            _machine = WordPattern(machineName);
            _user = WordPattern(userName);
        }

        /// <summary>The scrubber for this machine: current user, profile,
        /// machine name and %LocalAppData%\Bina.</summary>
        public static DiagnosticsScrubber ForCurrentMachine()
        {
            string Safe(Func<string> f) { try { return f(); } catch { return null; } }
            var local = Safe(() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return new DiagnosticsScrubber(
                Safe(() => Environment.UserName),
                Safe(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                Safe(() => Environment.MachineName),
                string.IsNullOrEmpty(local) ? null : System.IO.Path.Combine(local, "Bina"));
        }

        public string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var parts = LineBreak.Split(text);   // line, break, line, break, …, line
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < parts.Length; i += 2)
            {
                var line = parts[i];
                var lineBreak = i + 1 < parts.Length ? parts[i + 1] : "";
                if (IsSecret(line)) continue;   // the line and its break go
                sb.Append(ScrubLine(line)).Append(lineBreak);
            }
            return sb.ToString();
        }

        private static bool IsSecret(string line) =>
            SecretWords.Any(w => line.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        private string ScrubLine(string line)
        {
            try
            {
                // Bina first: it lives under the profile and keeps its tail.
                if (_binaRoot != null) line = _binaRoot.Replace(line, "<bina>");
                if (_home != null) line = _home.Replace(line, "<home>");
                line = FilePath.Replace(line, m => @"<path>\." + m.Groups["ext"].Value);
                line = Email.Replace(line, "<email>");
                // Machine before user: "PC-ALI" must not become "PC-<user>".
                if (_machine != null) line = _machine.Replace(line, "<machine>");
                if (_user != null) line = _user.Replace(line, "<user>");
                return line;
            }
            catch (RegexMatchTimeoutException)
            {
                return "[line removed: could not be scrubbed]";
            }
        }

        /// <summary>A folder and everything under it, either slash, any case,
        /// stopping at a name boundary (…\Bina never matches …\BinaX).</summary>
        private static Regex FolderPattern(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var segments = folder.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return null;   // never scrub a bare drive
            var body = string.Join(@"[\\/]+", segments.Select(Regex.Escape));
            if (folder.StartsWith(@"\\")) body = @"\\\\" + body;
            return new Regex(body + @"(?![A-Za-z0-9._\-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
        }

        /// <summary>A whole name (not inside a longer word or a &lt;placeholder&gt;).</summary>
        private static Regex WordPattern(string word)
        {
            if (string.IsNullOrWhiteSpace(word) || word.Trim().Length < 2) return null;
            return new Regex(@"(?<![A-Za-z0-9<_\-])" + Regex.Escape(word.Trim()) + @"(?![A-Za-z0-9>_\-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
        }
    }
}

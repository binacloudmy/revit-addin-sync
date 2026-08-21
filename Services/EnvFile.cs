using System;
using System.Collections.Generic;
using System.IO;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Parser for the channel .env files (.env.local / .env.staging /
    /// .env.production) that the csproj embeds as resources.
    ///
    /// Split out of BinaConfig.LoadEnv so the same parser can lint the files
    /// themselves: BinaConfig cannot be linked into the test project (it pulls
    /// in the Revit-dependent half of the add-in) and the embedded resources
    /// live in an assembly the tests do not reference, so EnvChannelTests reads
    /// the repo files from disk and parses them with THIS code. A second copy
    /// of the parser in the tests would have let the two drift, which is
    /// exactly what a lint test is supposed to prevent.
    /// </summary>
    public static class EnvFile
    {
        /// <summary>
        /// Key=value lines; `#` comments and blanks skipped, surrounding
        /// double quotes stripped, keys compared case-insensitively. A line
        /// with no `=` (or an empty key) is ignored rather than throwing — a
        /// malformed env file must not take the add-in down at startup.
        /// </summary>
        public static Dictionary<string, string> Parse(TextReader reader)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (reader == null) return map;

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                var eq = t.IndexOf('=');
                if (eq <= 0) continue;
                var k = t.Substring(0, eq).Trim();
                var val = t.Substring(eq + 1).Trim().Trim('"');
                map[k] = val;
            }
            return map;
        }

        public static Dictionary<string, string> Parse(string text) =>
            Parse(new StringReader(text ?? ""));
    }
}

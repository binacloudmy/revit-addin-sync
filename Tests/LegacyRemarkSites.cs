using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Finds every statement of the BIM010 checker sources that writes a Remark (AuditCheckers.cs,
    /// AuditTools.cs), as normalised source text. AuditCheckers needs a live Revit Document and cannot
    /// run in the test project, so the sources themselves are the only way to see a new BM remark.
    /// </summary>
    internal static class LegacyRemarkSites
    {
        public static string SourceOf(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
            if (dir == null) throw new FileNotFoundException("source file not found: " + relative);
            return File.ReadAllText(Path.Combine(dir.FullName, relative));
        }

        /// <summary>(hash, normalised statement) per `Remark = "..."` / `.Remark = ...` statement, in file order.</summary>
        public static List<(string Hash, string Text)> In(string source)
        {
            var lines = source.Replace("\r\n", "\n").Split('\n');
            var found = new List<(string, string)>();
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                var initializer = t.StartsWith("Remark = ", StringComparison.Ordinal);
                if (!initializer && t.IndexOf(".Remark = ", StringComparison.Ordinal) < 0) continue;
                var sb = new StringBuilder();
                var j = i;
                for (; j < lines.Length; j++)
                {
                    var l = lines[j].Trim();
                    sb.Append(l).Append(' ');
                    if (l.EndsWith(";", StringComparison.Ordinal) || (initializer && l.EndsWith(",", StringComparison.Ordinal))) break;
                }
                var text = sb.ToString().Trim();
                if (text.IndexOf('"') < 0) { i = j; continue; }   // not a literal: `rec.Remark = outcome.Remark;`
                found.Add((Hash(text), text));
                i = j;
            }
            return found;
        }

        private static string Hash(string s)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Take(5).Select(b => b.ToString("x2")));
        }
    }
}

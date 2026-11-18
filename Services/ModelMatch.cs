using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Which model in a folder the file being synced most likely is, so the
    /// Sync dialog can start on "new version of that model" instead of making
    /// the drafter find it. Identity beats names: a lineage or document match is
    /// certain, a name match is a guess, and a guess is only acted on when it is
    /// the only one.
    /// </summary>
    public static class ModelMatch
    {
        public enum Kind
        {
            None,
            Lineage,
            Design,
            DocGuid,
            ExactName,
            SimilarName,
        }

        public sealed class Candidate
        {
            public string Name { get; set; }
            public string LineageId { get; set; }
            public int? DesignId { get; set; }
            public string DocGuid { get; set; }
        }

        public sealed class Known
        {
            public string FileName { get; set; }
            public string LineageId { get; set; }
            public int? DesignId { get; set; }
            public string DocGuid { get; set; }
        }

        public sealed class Result
        {
            /// <summary>Index of the model to pre-select, or -1.</summary>
            public int Index { get; set; } = -1;
            public Kind Kind { get; set; }
            /// <summary>Indexes worth a "Suggested" badge when no single pick was made.</summary>
            public List<int> Suggested { get; set; } = new List<int>();
        }

        public static Result Pick(IList<Candidate> candidates, Known known)
        {
            var result = new Result();
            if (candidates == null || candidates.Count == 0 || known == null) return result;

            int Unique(Func<Candidate, bool> test)
            {
                int found = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!test(candidates[i])) continue;
                    if (found >= 0) return -2;   // more than one: not a pick
                    found = i;
                }
                return found;
            }

            bool Pick1(Func<Candidate, bool> test, Kind kind)
            {
                int i = Unique(test);
                if (i < 0) return false;
                result.Index = i;
                result.Kind = kind;
                return true;
            }

            if (!string.IsNullOrEmpty(known.LineageId) &&
                Pick1(c => Same(c.LineageId, known.LineageId), Kind.Lineage)) return result;

            if (known.DesignId.HasValue &&
                Pick1(c => c.DesignId == known.DesignId, Kind.Design)) return result;

            if (!string.IsNullOrEmpty(known.DocGuid) &&
                Pick1(c => Same(c.DocGuid, known.DocGuid), Kind.DocGuid)) return result;

            // The server files a version by name, so an exact name is the model
            // this lands in whatever is picked: always take it when unique.
            if (!string.IsNullOrEmpty(known.FileName) &&
                Pick1(c => Same(c.Name, known.FileName), Kind.ExactName)) return result;

            string mine = Normalize(known.FileName);
            if (mine.Length == 0) return result;

            var similar = Enumerable.Range(0, candidates.Count)
                .Where(i => Normalize(candidates[i].Name) == mine)
                .ToList();

            if (similar.Count == 1)
            {
                result.Index = similar[0];
                result.Kind = Kind.SimilarName;
            }
            else
            {
                result.Suggested = similar;
            }
            return result;
        }

        /// <summary>
        /// A name with the noise a copy or a download adds taken off: extension,
        /// "Copy of", " (2)", "-v3", and the difference between _ - and spaces.
        /// "Copy of Tower_A (2)-v3.rvt" and "tower a.rvt" normalise the same.
        /// </summary>
        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = name.Trim();
            if (s.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);

            string previous;
            do
            {
                previous = s;
                s = Regex.Replace(s, @"^copy of\s+", "", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\s*\(\d+\)$", "");
                s = Regex.Replace(s, @"[-_\s]v\d+$", "", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"\s*[-–]?\s*copy$", "", RegexOptions.IgnoreCase);
                s = s.Trim();
            } while (s != previous);

            s = Regex.Replace(s, @"[_\-\s]+", " ");
            return s.Trim().ToLowerInvariant();
        }

        private static bool Same(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

// AuditNaming — conservative rename proposal for naming-convention checkers.
//
// The naming checkers (file_naming, family_category) tell a drafter a name
// broke ">=N segments separated by - or _" but historically never proposed a
// corrected value. Suggest() offers one where a DETERMINISTIC transform is
// obvious: whitespace becomes the separator, repeated separators collapse, ends
// are trimmed. Nothing more — no character stripping, no case changes. When the
// result still misses the segment count (a single-word name like "Wall" has
// nothing to split), it returns null and the checker says so out loud rather
// than omitting the point.
//
// No Autodesk.Revit.DB dependency: this file is linked into Tests.csproj like
// AuditFormParser.cs / PdfReader.cs, so the transform is unit-tested without
// Revit.

using System.Linq;

namespace BinaVibe.Mcp.Tools.Audit
{
    public static class AuditNaming
    {
        private static readonly char[] SeparatorChars = { '-', '_' };

        /// <summary>Best-effort rename to reach <paramref name="minSegments"/>
        /// segments separated by '-' or '_'. Returns null when no confident
        /// transform exists (result still too few segments, or unchanged from
        /// the input) — "no suggestion" is a real answer, never a guess.</summary>
        public static string? Suggest(string current, int minSegments, char sep = '-')
        {
            var candidate = Normalise(current ?? "", sep);
            if (candidate.Length == 0) return null;
            if (SegmentCount(candidate) < minSegments) return null;
            if (candidate == (current ?? "").Trim()) return null;   // already what it is
            return candidate;
        }

        /// <summary>Whitespace runs → sep, runs of '-'/'_' → one sep, ends
        /// trimmed of separators. Whitespace and existing separators are the
        /// only things touched.</summary>
        private static string Normalise(string s, char sep)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            bool lastWasSep = false;
            foreach (var ch in s)
            {
                bool isSep = char.IsWhiteSpace(ch) || ch == '-' || ch == '_';
                if (isSep)
                {
                    if (!lastWasSep) sb.Append(sep);
                    lastWasSep = true;
                }
                else
                {
                    sb.Append(ch);
                    lastWasSep = false;
                }
            }
            return sb.ToString().Trim(SeparatorChars);
        }

        private static int SegmentCount(string s) =>
            s.Split(SeparatorChars).Count(seg => seg.Trim().Length > 0);
    }
}

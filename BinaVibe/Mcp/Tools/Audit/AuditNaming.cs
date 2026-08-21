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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BinaVibe.Mcp.Tools.Audit
{
    public static class AuditNaming
    {
        private static readonly char[] SeparatorChars = { '-', '_' };

        // JKR component code: "(TKh281a)", "(AKs002a)", "(LSc096a)", "(PT2p600a)",
        // "(TKk400b)" — parentheses, two upper-case discipline letters, then a
        // letter/digit run that contains at least one digit. Plain bracketed
        // words "(Brick)" or bare numbers "(123)" are NOT codes.
        private static readonly Regex JkrCode = new(@"\([A-Z]{2}[A-Za-z0-9]*\d[A-Za-z0-9]*\)",
                                                    RegexOptions.CultureInvariant);

        // JKR discipline prefix on a whole name: "jkrAR_", "jkrAR24_5a_…",
        // "jkrST-", "jkrME_". Lower-case "jkr" followed by two upper-case
        // discipline letters, anchored at the start — "Jkrafter" is not one.
        private static readonly Regex JkrPrefix = new(@"^jkr[A-Z]{2}", RegexOptions.CultureInvariant);

        // Material names that are compliant Section D type names on their own
        // (live model: 88 placed "UPVC" window/door types). Deliberately a
        // closed list: Revit defaults like "Generic", "Solid", "Glazed" are NOT
        // materials and must keep failing.
        private static readonly HashSet<string> Materials = new(StringComparer.OrdinalIgnoreCase)
        {
            "UPVC", "PVC", "HDPE", "GRC", "GRP", "FRP", "MDF",
            "Brick", "Brickwork", "Blockwork", "Masonry", "Concrete", "Cement", "Mortar",
            "Plaster", "Gypsum", "Render", "Terrazzo", "Tile", "Tiles", "Ceramic",
            "Granite", "Marble", "Stone", "Clay", "Sand",
            "Timber", "Wood", "Plywood", "Chipboard", "Laminate",
            "Steel", "Metal", "Aluminium", "Aluminum", "Copper", "Bronze", "Brass", "Zinc", "Lead",
            "Glass", "Vinyl", "Rubber", "Asphalt", "Bitumen",
        };

        /// <summary>True when the name carries a JKR component code
        /// "(XXnnn…)" anywhere, or starts with a JKR discipline prefix
        /// "jkrAR…". Invisible format characters (BOM, zero-width space) are
        /// ignored — they render as nothing and are not part of the name.</summary>
        public static bool IsJkrName(string? name)
        {
            var s = Clean(name);
            if (s.Length == 0) return false;
            return JkrCode.IsMatch(s) || JkrPrefix.IsMatch(s);
        }

        /// <summary>True when the whole name is one recognised building
        /// material ("UPVC", "Brick"). Closed list, case-insensitive.</summary>
        public static bool IsMaterialName(string? name) => Materials.Contains(Clean(name));

        /// <summary>Section D "standard component naming": JKR-coded, JKR-prefixed,
        /// or a bare material name. Anything else (Revit defaults, free text) is
        /// non-compliant.</summary>
        public static bool IsSectionDCompliant(string? name) => IsJkrName(name) || IsMaterialName(name);

        private static string Clean(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder(name!.Length);
            foreach (var ch in name)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                    != System.Globalization.UnicodeCategory.Format)
                    sb.Append(ch);
            return sb.ToString().Trim();
        }

        /// <summary>Best-effort rename to reach <paramref name="minSegments"/>
        /// segments separated by '-' or '_'. Returns null when no confident
        /// transform exists (result still too few segments, or unchanged from
        /// the input) — "no suggestion" is a real answer, never a guess.
        /// JKR-coded names are never rewritten: "(TKh281a) 600 x 1800" is the
        /// convention itself, and hyphenating its spaces would corrupt it.</summary>
        public static string? Suggest(string current, int minSegments, char sep = '-')
        {
            if (IsJkrName(current)) return null;
            var candidate = Normalise(current ?? "", sep);
            if (candidate.Length == 0) return null;
            if (SegmentCount(candidate) < minSegments) return null;
            if (candidate == (current ?? "").Trim()) return null;   // already what it is
            return candidate;
        }

        /// <summary>Whitespace runs → sep, runs of '-'/'_' → one sep, ends
        /// trimmed of separators. Whitespace and existing separators are the
        /// only things touched. Invisible Unicode format characters (zero-width
        /// space U+200B, ZWNJ/ZWJ, BOM — category Cf) count as whitespace: they
        /// are boundaries the author typed (usually pasted) that render as a
        /// stray gap in a PDF, never as part of a token. A token with no such
        /// boundary is never split.</summary>
        private static string Normalise(string s, char sep)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            bool lastWasSep = false;
            foreach (var ch in s)
            {
                bool isSep = IsBoundary(ch) || ch == '-' || ch == '_';
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

        private static bool IsBoundary(char ch) =>
            char.IsWhiteSpace(ch)
            || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
               == System.Globalization.UnicodeCategory.Format;

        private static int SegmentCount(string s) =>
            s.Split(SeparatorChars).Count(seg => seg.Trim().Length > 0);
    }
}

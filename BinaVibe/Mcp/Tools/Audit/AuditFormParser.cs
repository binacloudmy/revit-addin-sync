// AuditFormParser — positioned-word table extraction for audit checklist PDFs
// (BIM 010 Model Audit Form family; layout varies, so nothing is hardcoded to
// one revision: sections, column x-anchors and row refs are all detected from
// the page itself).
//
// Deterministic by design: same PDF in, same rows out. No LLM anywhere.
//
// Row assignment: the row-number ("No.") word is vertically CENTERED in its
// cell while a multi-line description spreads above and below it, so reading
// order lies about cell boundaries. Every content line is therefore assigned
// to the row marker NEAREST by vertical distance within its section+page —
// the rule a human eye applies to a ruled table. Verified against the real
// BIM 010 form (all 50 rows, refs and order exact; known limit: where a tall
// cell abuts a short one, a boundary line can bleed into the neighbouring
// row's description — row identity, references and the keyword core stay
// correct, which is what checker matching needs. The drawn table rulings were
// tried as exact boundaries and rejected: this form family draws borders
// cell-by-cell and per-column, so recovered rulings are both incomplete and
// polluted with sub-cell noise.)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BinaVibe.Mcp.Tools.Audit
{
    public static class AuditFormParser
    {
        // Row refs as printed on JKR forms: "1." "1.1" "3.0" "21."
        private static readonly Regex RowRef = new(@"^\d{1,3}(\.\d{1,2})?\.?$", RegexOptions.Compiled);
        // "A. MODEL INTEGRITY AND QUALITY" — letter, dot, mostly-uppercase title.
        private static readonly Regex SectionHead = new(@"^([A-Z])\.\s+(.{4,})$", RegexOptions.Compiled);
        // Document-control header box + form title noise, EN/BM.
        private static readonly string[] NoiseTokens =
        {
            "KOD DOKUMEN", "PINDAAN", "TARIKH", "MUKA SURAT", "MODEL AUDIT FORM",
            "BORANG AUDIT MODEL",
        };
        // Table-header vocabulary — lines made (mostly) of these are column
        // headers, not data. Includes the section-D matrix sub-headers.
        private static readonly HashSet<string> HeaderWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "No.", "No", "Description", "Reference", "Compliance", "Remarks",
            "(Yes)", "(No)", "Yes", "X", "√", "Components", "and", "System",
            "Families", "Category", "Standard", "Component", "File", "Naming",
            "shall", "be", "in", "Accordance", "with", "Geometry", "Standards",
            "Quality", "Integrity", "Information", "Design",
        };

        private sealed class Line
        {
            public double Y;
            public List<PdfWord> Words = new();
            public string Text = "";
        }

        private sealed class Anchors
        {
            public double? DescX;      // left edge of Description / Components column
            public double? RefX;       // left edge of Reference column (absent in section D)
            public double? ComplianceX;
            public double? RemarksX;
        }

        private sealed class Marker
        {
            public AuditFormRow Row = new();
            public double Y;
            public StringBuilder Desc = new();
            public StringBuilder Ref = new();
        }

        /// <summary>Extract checklist rows from every page. Throws when no rows
        /// are found (not an audit checklist, or a scan with no text layer).</summary>
        public static List<AuditFormRow> Parse(string pdfPath)
        {
            var pages = PdfReader.ExtractWords(pdfPath);
            var ordered = new List<Marker>();                       // global row order
            var anchorsBySection = new Dictionary<string, Anchors>();
            string section = "", sectionTitle = "";

            for (int p = 0; p < pages.Length; p++)
            {
                // Per-page: collect this page's markers + unassigned content
                // lines (with the section active when they were read), then
                // assign content to the vertically nearest marker of the same
                // section on the same page.
                var pageMarkers = new List<Marker>();
                var content = new List<(double Y, List<PdfWord> Words, string Section)>();

                foreach (var line in ToLines(pages[p]))
                {
                    var text = line.Text;
                    if (text.Length == 0 || IsNoise(text)) continue;

                    var sec = SectionHead.Match(text);
                    if (sec.Success && LooksLikeSectionTitle(sec.Groups[2].Value))
                    {
                        // Same section continuing on the next page keeps its identity.
                        if (sec.Groups[1].Value != section)
                        {
                            section = sec.Groups[1].Value;
                            sectionTitle = Collapse(sec.Groups[2].Value);
                        }
                        continue;
                    }
                    if (section.Length == 0) continue;   // project info block before section A

                    if (!anchorsBySection.TryGetValue(section, out var anchors))
                        anchorsBySection[section] = anchors = new Anchors();

                    if (IsHeaderLine(line))
                    {
                        HarvestAnchors(line, anchors);
                        continue;
                    }

                    var first = line.Words[0];
                    bool isMarker = RowRef.IsMatch(first.Text)
                                    && (anchors.DescX == null || first.X < anchors.DescX.Value - 2);
                    if (isMarker)
                    {
                        var m = new Marker
                        {
                            Y = line.Y,
                            Row = new AuditFormRow
                            {
                                Section = section,
                                SectionTitle = sectionTitle,
                                RowRef = first.Text.TrimEnd('.'),
                                Page = p + 1,
                            },
                        };
                        pageMarkers.Add(m);
                        ordered.Add(m);
                        if (line.Words.Count > 1)
                            content.Add((line.Y, line.Words.Skip(1).ToList(), section));
                    }
                    else
                    {
                        content.Add((line.Y, line.Words, section));
                    }
                }

                foreach (var sect in content.Select(c => c.Section).Distinct())
                {
                    var markers = pageMarkers.Where(m => m.Row.Section == sect)
                                             .OrderByDescending(m => m.Y).ToList();
                    if (markers.Count == 0) continue;   // stray text with no rows on page
                    var sectLines = content.Where(c => c.Section == sect)
                                           .OrderByDescending(c => c.Y).ToList();
                    anchorsBySection.TryGetValue(sect, out var anchorsMaybe);
                    var anchors = anchorsMaybe ?? new Anchors();

                    foreach (var (y, lineWords, _) in sectLines)
                    {
                        Marker nearest = markers[0];
                        foreach (var m in markers)
                            if (Math.Abs(m.Y - y) < Math.Abs(nearest.Y - y)) nearest = m;
                        AppendWords(lineWords, anchors, nearest.Desc, nearest.Ref);
                    }
                }
            }

            var rows = new List<AuditFormRow>();
            foreach (var m in ordered)
            {
                m.Row.Description = Collapse(m.Desc.ToString());
                m.Row.GuidelineRef = Collapse(m.Ref.ToString());
                if (m.Row.Description.Length > 0) rows.Add(m.Row);
            }

            if (rows.Count == 0)
                throw new InvalidOperationException(
                    "no checklist rows found in this PDF — it does not look like an audit "
                    + "form with a No./Description table (or it is a scan with no text layer)");
            return rows;
        }

        // ─── helpers ────────────────────────────────────────────────────

        private static IEnumerable<Line> ToLines(List<PdfWord> words)
        {
            const double yTol = 4.0;
            var lines = new List<Line>();
            foreach (var w in words.OrderByDescending(w => w.Y))
            {
                var line = lines.LastOrDefault(l => Math.Abs(l.Y - w.Y) <= yTol);
                if (line == null)
                {
                    line = new Line { Y = w.Y };
                    lines.Add(line);
                }
                line.Words.Add(w);
            }
            foreach (var l in lines)
            {
                l.Words.Sort((a, b) => a.X.CompareTo(b.X));
                l.Text = Collapse(string.Join(" ", l.Words.Select(w => w.Text)));
                yield return l;
            }
        }

        private static bool IsNoise(string text)
        {
            foreach (var t in NoiseTokens)
                if (text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool LooksLikeSectionTitle(string title)
        {
            // Real section titles are (near-)uppercase headings; a data row like
            // "B.1.A (a)" or a sentence never is.
            var letters = title.Where(char.IsLetter).ToList();
            if (letters.Count < 4) return false;
            return letters.Count(char.IsUpper) >= letters.Count * 0.8;
        }

        private static bool IsHeaderLine(Line line)
        {
            int known = line.Words.Count(w => HeaderWords.Contains(w.Text.Trim()));
            return known >= Math.Max(1, (int)Math.Ceiling(line.Words.Count * 0.7));
        }

        private static void HarvestAnchors(Line line, Anchors a)
        {
            foreach (var w in line.Words)
            {
                var t = w.Text.Trim();
                if ((t.Equals("Description", StringComparison.OrdinalIgnoreCase)
                     || t.Equals("Components", StringComparison.OrdinalIgnoreCase))
                    && a.DescX == null) a.DescX = w.X;
                else if (t.Equals("Reference", StringComparison.OrdinalIgnoreCase)
                    && a.RefX == null) a.RefX = w.X;
                else if (t.Equals("Compliance", StringComparison.OrdinalIgnoreCase)
                    && a.ComplianceX == null) a.ComplianceX = w.X;
                else if (t.Equals("Remarks", StringComparison.OrdinalIgnoreCase)
                    && a.RemarksX == null) a.RemarksX = w.X;
            }
        }

        /// <summary>Column bucketing: description | reference | (compliance/
        /// remarks — prefilled guidance text, dropped: it is instructions to the
        /// auditor, not part of the checklist item).</summary>
        private static void AppendWords(IEnumerable<PdfWord> words, Anchors a,
                                        StringBuilder desc, StringBuilder refBuf)
        {
            foreach (var w in words)
            {
                // Reference column exists between RefX and ComplianceX. Without a
                // detected RefX everything left of Compliance is description.
                double cutRight = a.ComplianceX ?? a.RemarksX ?? double.MaxValue;
                if (w.X >= cutRight - 2) continue;               // compliance/remarks zone
                if (a.RefX != null && w.X >= a.RefX.Value - 6)
                {
                    if (refBuf.Length > 0) refBuf.Append(' ');
                    refBuf.Append(w.Text);
                }
                else
                {
                    if (desc.Length > 0) desc.Append(' ');
                    desc.Append(w.Text);
                }
            }
        }

        private static string Collapse(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c)) { if (!space && sb.Length > 0) sb.Append(' '); space = true; }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString().Trim();
        }
    }
}

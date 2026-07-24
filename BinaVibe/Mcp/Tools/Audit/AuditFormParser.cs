// AuditFormParser — positioned-word table extraction for audit checklist PDFs
// (BIM 010 Model Audit Form family; layout varies, so nothing is hardcoded to
// one revision: sections, column x-anchors and row refs are all detected from
// the page itself).
//
// Deterministic by design: same PDF in, same rows out. No LLM anywhere.
//
// Row assignment. This form family prints the "No." word two different ways in
// the same document, and getting the difference wrong is what used to push the
// last line of a tall cell into the next row's description:
//
//   * SHARING its line with description text ("1. Architectural Model developed
//     in") — the number is top-aligned, so the cell STARTS on the marker's own
//     line.
//   * ALONE in the No. column, with the description wrapped above and below it
//     ("Curtain" / "9." / "Systems") — the number is vertically centred, so the
//     cell starts on the line ABOVE the marker.
//
// Telling those apart needs the description column, not just the marker: a
// marker line that also carries Reference or Remarks text ("1.2  Appendix") is
// still ALONE as far as the description column is concerned. So each line is
// classified by whether it has words inside the description column's x window,
// the cell's first line is derived per the two cases above, and every line then
// belongs to the last cell that started at or above it — reading order, with the
// cell starts corrected. Boundaries land between lines by construction, so a
// wrapped line is wholly in its own row or wholly in the neighbour's.
//
// Nearest-marker-by-distance was the previous rule; it assumed every marker was
// centred and therefore mis-cut every top-aligned row. The drawn table rulings
// were tried as exact boundaries and rejected: this form family draws borders
// cell-by-cell and per-column, so recovered rulings are both incomplete and
// polluted with sub-cell noise.

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
            public double Bottom => Words.Min(w => w.Y);
            public double Top => Words.Max(w => w.Top);
        }

        private sealed class Anchors
        {
            public double? DescX;      // left edge of Description / Components column
            public double? RefX;       // left edge of Reference column (absent in section D)
            /// <summary>Left edge of the section-D matrix's second column
            /// ("Standard Component File Naming …"). Section D has no Reference
            /// column, so this is what bounds its description text.</summary>
            public double? MatrixX;
            public double? ComplianceX;
            public double? RemarksX;

            /// <summary>Right edge of the description column: whatever column
            /// comes next on this section's header.</summary>
            public double DescRight =>
                RefX ?? MatrixX ?? ComplianceX ?? RemarksX ?? double.MaxValue;
        }

        private sealed class Marker
        {
            public AuditFormRow Row = new();
            public StringBuilder Desc = new();
            public StringBuilder Ref = new();
        }

        /// <summary>One line of a page awaiting row assignment. Words excludes
        /// the row-number word itself.</summary>
        private sealed class PageLine
        {
            public string Section = "";
            public Marker? Marker;
            public List<PdfWord> Words = new();
            public double Top;
            /// <summary>Does this line carry text in the DESCRIPTION column? A
            /// marker line with only Reference/Remarks text does not, and is
            /// therefore a centred marker.</summary>
            public bool HasDescriptionText;
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
                var pageLines = new List<PageLine>();

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
                    Marker? marker = null;
                    var words = line.Words;
                    if (isMarker)
                    {
                        marker = new Marker
                        {
                            Row = new AuditFormRow
                            {
                                Section = section,
                                SectionTitle = sectionTitle,
                                RowRef = first.Text.TrimEnd('.'),
                                Page = p + 1,
                            },
                        };
                        ordered.Add(marker);
                        words = line.Words.Skip(1).ToList();
                    }
                    pageLines.Add(new PageLine
                    {
                        Section = section,
                        Marker = marker,
                        Words = words,
                        Top = line.Top,
                        HasDescriptionText = words.Any(w => IsDescriptionWord(w, anchors)),
                    });
                }

                foreach (var sect in pageLines.Select(l => l.Section).Distinct())
                {
                    anchorsBySection.TryGetValue(sect, out var anchorsMaybe);
                    AssignLinesToRows(
                        pageLines.Where(l => l.Section == sect).OrderByDescending(l => l.Top).ToList(),
                        anchorsMaybe ?? new Anchors());
                }
            }

            var rows = new List<AuditFormRow>();
            foreach (var m in ordered)
            {
                m.Row.Description = Collapse(m.Desc.ToString());
                m.Row.GuidelineRef = Collapse(m.Ref.ToString());
                if (m.Row.Description.Length > 0) rows.Add(m.Row);
            }
            BackfillSectionRefs(rows);

            if (rows.Count == 0)
                throw new InvalidOperationException(
                    "no checklist rows found in this PDF — it does not look like an audit "
                    + "form with a No./Description table (or it is a scan with no text layer)");
            return rows;
        }

        // ─── row assignment ─────────────────────────────────────────────

        /// <summary>Give every line of one section on one page to a row: find
        /// each cell's first line (marker's own line when the marker shares it
        /// with description text, the line above when the marker is centred and
        /// alone), then walk top-down handing each line to the most recently
        /// started cell.</summary>
        private static void AssignLinesToRows(List<PageLine> lines, Anchors anchors)
        {
            var markerAt = Enumerable.Range(0, lines.Count).Where(i => lines[i].Marker != null).ToList();
            if (markerAt.Count == 0) return;   // stray text with no rows on this page

            var cellStart = new List<int>();
            foreach (var mi in markerAt)
            {
                int start = mi;
                if (!lines[mi].HasDescriptionText)
                {
                    // Centred marker: its cell opens on the nearest line above
                    // that carries description text.
                    int above = mi - 1;
                    while (above >= 0 && !lines[above].HasDescriptionText) above--;
                    if (above >= 0) start = above;
                }
                // A cell can never open at or above the previous cell's opening;
                // when that would happen (two centred markers sharing one line
                // above), keep this cell on the marker's own line.
                if (cellStart.Count > 0 && start <= cellStart[cellStart.Count - 1]) start = mi;
                cellStart.Add(start);
            }

            int current = -1, next = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                while (next < cellStart.Count && cellStart[next] <= i) current = next++;
                if (current < 0) continue;   // text above the first row's cell
                var marker = lines[markerAt[current]].Marker!;
                AppendWords(lines[i].Words, anchors, marker.Desc, marker.Ref);
            }
        }

        /// <summary>A blank Reference cell inherits the section's clause ONLY
        /// when the SAME clause is printed on at least two rows of that section
        /// and no other clause appears there — repetition is the only evidence
        /// available, without ruling lines, that the clause governs the whole
        /// block rather than one row.
        ///
        /// One printed ref in a section is NOT enough: in the reference BIM 010
        /// form, section A prints "Appendix B.1.A (a)" on row 4 only, and it
        /// belongs to row 4 (the Project Base Point clause) — copying it onto
        /// rows 1-3 and 5-7 would attribute a clause the form never made. A
        /// blank cell stays blank; nothing is ever synthesised. Each row records
        /// where its ref came from so the export can mark inherited ones.</summary>
        private static void BackfillSectionRefs(List<AuditFormRow> rows)
        {
            foreach (var group in rows.GroupBy(r => r.Section))
            {
                var printed = group.Where(r => r.GuidelineRef.Length > 0).ToList();
                foreach (var row in printed) row.ReferenceSource = "form";

                var distinct = printed.Select(r => r.GuidelineRef)
                                      .Distinct(StringComparer.Ordinal).ToList();
                if (distinct.Count != 1 || printed.Count < 2) continue;

                foreach (var row in group.Where(r => r.GuidelineRef.Length == 0))
                {
                    row.GuidelineRef = distinct[0];
                    row.ReferenceSource = "form_sibling";
                }
            }
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
                // Section D's matrix has no Reference column; its second column
                // ("Standard Component File Naming shall be in Accordance with
                // Geometry and Standards") is what bounds the category label.
                else if (t.Equals("Standard", StringComparison.OrdinalIgnoreCase)
                    && a.MatrixX == null) a.MatrixX = w.X;
                else if (t.Equals("Compliance", StringComparison.OrdinalIgnoreCase)
                    && a.ComplianceX == null) a.ComplianceX = w.X;
                else if (t.Equals("Remarks", StringComparison.OrdinalIgnoreCase)
                    && a.RemarksX == null) a.RemarksX = w.X;
            }
        }

        /// <summary>Only the RIGHT edge bounds the description column: wrapped
        /// body text is indented differently from the "Description" header word
        /// (and further left than it), so DescX cannot be used as a left cut —
        /// it would eat the first word of every wrapped line. Everything left of
        /// the next column, marker word already removed, is description.</summary>
        private static bool IsDescriptionWord(PdfWord w, Anchors a) => w.X < a.DescRight - 6;

        /// <summary>Column bucketing: description | reference | (compliance/
        /// remarks — prefilled guidance text, dropped: it is instructions to the
        /// auditor, not part of the checklist item).</summary>
        private static void AppendWords(IEnumerable<PdfWord> words, Anchors a,
                                        StringBuilder desc, StringBuilder refBuf)
        {
            foreach (var w in words)
            {
                // Reference column exists between RefX and ComplianceX. Without a
                // detected RefX everything left of the next column is description.
                double cutRight = a.ComplianceX ?? a.RemarksX ?? double.MaxValue;
                if (w.X >= cutRight - 2) continue;               // compliance/remarks zone
                if (a.RefX != null && w.X >= a.RefX.Value - 6)
                {
                    if (refBuf.Length > 0) refBuf.Append(' ');
                    refBuf.Append(w.Text);
                }
                else if (IsDescriptionWord(w, a))
                {
                    if (desc.Length > 0) desc.Append(' ');
                    desc.Append(w.Text);
                }
                // else: a middle matrix column (section D) — auditor guidance,
                // not part of the checklist item.
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

// AI-638: every row and line of copy the Check screens show, as deterministic templates over
// CheckState. No AI text anywhere here (AI wording is pending #9). Revit/WPF-free, unit-tested.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.CadToBim.Check
{
    /// <summary>One bindable row: lead mark, text (Bold + Text), badge, hover evidence, optional detail.</summary>
    public sealed class CheckRow
    {
        public string Mark { get; set; } = "";      // ok | warn | swatch-g | swatch-a | swatch-n | "" (none)
        public string Bold { get; set; } = "";
        public string Text { get; set; } = "";      // before Bold
        public string Post { get; set; } = "";      // after Bold
        public Src Source { get; set; }
        public string Badge { get; set; }          // null = the Source's default label
        public string Evidence { get; set; }
        public string Detail { get; set; }         // expandable rows (screen 7)
        public bool Expanded { get; set; }
        public PlanMap Map { get; set; }           // Gate 1 rows: click to zoom, edit mapping

        public string BadgeText => Badge ?? CheckRows.Label(Source);
        public string Tone => CheckRows.Tone(Source);
        public bool HasDetail => !string.IsNullOrEmpty(Detail);
        public bool IsPlan => Map != null;
        /// <summary>Clickable rows (zoom or expand) are keyboard-focusable too (Enter / Space).</summary>
        public bool IsActionable => IsPlan || HasDetail;
        public bool HasBadge => !string.IsNullOrEmpty(BadgeText);
        public string BadgeName => "Source: " + BadgeText + (string.IsNullOrEmpty(Evidence) ? "" : ". " + Evidence);
        public string MarkName => Mark switch
        {
            "swatch-g" => "From the drawing",
            "swatch-a" => "Assumed, to confirm",
            "swatch-n" => "Not used",
            "ok" => "OK",
            "warn" => "Warning",
            _ => "",
        };
        public string Caret => HasDetail ? (Expanded ? "▾" : "▸") : "";
    }

    public static class CheckRows
    {
        // shortcut: JKR defaults shown as amber assumptions until pending "remove hardcoded defaults" / legend sizes (#8).
        public const int JkrDoorHeadMm = 2100;
        public const int JkrSlabMm = 150;

        public const string ReadOnce = "Read once. Edits from now on are instant.";
        public const string Gate1Hint = "The labels are on the drawing in the view. Click a row to zoom to its box.";
        public const string SameLevelTitle = "Default is same level.";
        public const string SameLevelBody = "A plan only moves to another level when you confirm it. The pane never creates a level.";
        public const string Gate2Disagree = "You click the two ends of a known dimension in the view and type its value. Build stays blocked until the two sources match.";
        public const string ConsultantPending = "Consultant: not read yet (title-block reader)";

        public static string Label(Src s) => s switch
        {
            Src.Project => "Project",
            Src.DwgHeader => "DWG header",
            Src.Drawing => "Drawing",
            Src.TitleBlock => "Title block",
            Src.JkrDefault => "JKR default",
            Src.AiGuess => "AI guess",
            Src.DrawingSays => "Drawing",
            Src.Missing => "Missing",
            _ => "pending",
        };

        public static string Tone(Src s) => s switch
        {
            Src.Project or Src.DwgHeader or Src.Drawing or Src.TitleBlock => "g",
            Src.JkrDefault or Src.AiGuess or Src.DrawingSays => "a",
            Src.Missing => "r",
            _ => "n",
        };

        private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        // ── screen 2 ──
        public static List<CheckRow> Preflight(CheckState s)
        {
            var sh = s.Sheet;
            var rows = new List<CheckRow>
            {
                string.IsNullOrEmpty(s.Target.DocTitle)
                    ? new CheckRow { Mark = "warn", Text = "Target: no project open", Source = Src.Project, Evidence = "Open the .rvt the model goes into" }
                    : new CheckRow { Mark = "ok", Text = "Target: ", Bold = s.Target.DocTitle + " (active project)", Source = Src.Project, Evidence = "The open Revit document" },
                new CheckRow
                {
                    Mark = "ok",
                    Text = (s.Target.Workshared ? "Workshared" : "Not workshared") + " · " + Mb(sh.SizeBytes) + " · ~" + N(sh.ModelSpaceEntities) + " entities",
                    Source = Src.DwgHeader, Evidence = "File size, and entities in model space",
                },
            };
            int xrefs = sh.Warnings.Where(w => w.Kind.StartsWith("xref", StringComparison.Ordinal)).Sum(w => w.Count);
            rows.Add(xrefs == 0
                ? new CheckRow { Mark = "ok", Text = "No xrefs", Source = Src.Drawing, Evidence = "No external references in the file" }
                : new CheckRow { Mark = "warn", Text = xrefs + " xref" + (xrefs == 1 ? "" : "s") + " missing", Source = Src.Drawing,
                                 Evidence = string.Join("; ", sh.Warnings.Where(w => w.Kind.StartsWith("xref", StringComparison.Ordinal)).Select(w => w.Name + " " + w.Detail)) });
            foreach (var g in sh.Warnings.Where(w => !w.Kind.StartsWith("xref", StringComparison.Ordinal)).GroupBy(w => w.Kind))
            {
                int count = g.Sum(w => w.Count);
                string text = g.Key switch
                {
                    "image_missing" => count + " image" + (count == 1 ? "" : "s") + " missing",
                    "proxy_entity" => count + " proxy entit" + (count == 1 ? "y" : "ies") + " (read as plain lines)",
                    _ => g.First().Detail,
                };
                rows.Add(new CheckRow { Mark = "warn", Text = text, Source = Src.Drawing, Evidence = string.Join("; ", g.Select(w => w.Detail ?? w.Name)) });
            }
            if (s.PreviewError != null)
                rows.Add(new CheckRow { Mark = "warn", Text = "Preview couldn't be shown: " + s.PreviewError, Source = Src.DrawingSays, Badge = "Revit view", Evidence = "Details in cad-to-bim.log" });
            rows.Add(new CheckRow
            {
                Mark = "ok",
                Text = sh.Plans.Count + " plan" + (sh.Plans.Count == 1 ? "" : "s") + " · " + sh.Layers.Count + " layers"
                       + (sh.DetailsSkipped > 0 ? " · " + sh.DetailsSkipped + " details skipped" : ""),
                Source = Src.Drawing, Evidence = "Plan boxes found by wall clusters and their titles",
            });
            return rows;
        }

        public static string Mb(long bytes) => (bytes / 1048576.0).ToString(bytes < 10 * 1048576L ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";

        // ── screen 4 ──
        private static readonly Regex StoreyRx = new Regex(@"(?:ARAS|LEVEL|LANTAI|TINGKAT)\s*([0-9A-Z]+)", RegexOptions.IgnoreCase);

        private static string Storey(string text)
        {
            var m = StoreyRx.Match(text ?? "");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        public static List<CheckRow> Gate1(CheckState s)
        {
            string main = Storey(s.BuildLevel);
            var rows = s.Plans.Select(m =>
            {
                var p = m.Plan;
                var row = new CheckRow { Bold = p.Title ?? "Untitled plan " + p.N, Post = " → " + m.TargetLevel, Map = m };
                if (p.Level == null)
                {
                    row.Mark = "swatch-a"; row.Source = Src.AiGuess; row.Badge = "Same level";
                    row.Evidence = "No storey title: default is same level" + (p.Elevation != null ? " · " + p.Elevation : "");
                }
                else if (Storey(p.Level) == main && m.TargetLevel == s.BuildLevel)
                {
                    row.Mark = "swatch-g"; row.Source = Src.Drawing;
                    row.Evidence = "Title " + p.Title + (p.Elevation != null ? " + " + p.Elevation : "");
                }
                else
                {
                    row.Mark = "swatch-a"; row.Source = Src.DrawingSays; row.Badge = "Drawing says " + p.Level;
                    row.Evidence = "Drawing says " + p.Level + (p.Elevation != null ? " (" + p.Elevation + ")" : "") + " · same level until confirmed";
                }
                return row;
            }).OrderBy(r => r.Mark == "swatch-g" ? 0 : 1).ToList();   // the main-level plan first (stable sort keeps sheet order)
            if (s.Sheet.DetailsSkipped > 0)
                rows.Add(new CheckRow { Mark = "swatch-n", Text = s.Sheet.DetailsSkipped + " details · skipped", Source = Src.Drawing, Evidence = "Untitled and small: details, stairs, elevations" });
            rows.Add(s.Engine.LegendRow != null
                ? new CheckRow { Mark = "swatch-g", Text = s.Engine.LegendRow, Source = Src.Drawing, Badge = "Legend · read" }
                : new CheckRow { Mark = "swatch-n", Text = "Legend and schedule: not read yet", Source = Src.Pending, Evidence = "Legend reader (pending #8)" });
            // the plan reader does not look for elevations/sections yet, so this says so rather than "none found"
            rows.Add(new CheckRow { Mark = "swatch-n", Text = "Elevations, sections · not read (floor plans only)", Source = Src.Pending, Badge = "not read",
                                     Evidence = "The plan reader finds floor plans only" });
            return rows;
        }

        // ── screen 5 ──
        public static List<CheckRow> Gate2(CheckState s)
        {
            var sh = s.Sheet;
            return new List<CheckRow>
            {
                new CheckRow
                {
                    Text = "Units: ", Bold = UnitWord(sh.UsedMmPerUnit), Source = Src.DwgHeader,
                    Mark = s.Gate2 == GateState.Blocked ? "warn" : "ok",
                    Badge = s.Gate2 == GateState.Blocked ? "Header says " + sh.HeaderUnit : null,
                    Evidence = "Header INSUNITS = " + sh.HeaderUnit + (s.Gate2 == GateState.Blocked ? "; it makes the sheet an impossible size, read at " + UnitWord(sh.UsedMmPerUnit) + " instead" : ""),
                },
                sh.UnitsPlausible && s.Engine.DimensionRow != null
                    ? new CheckRow { Mark = "ok", Text = s.Engine.DimensionRow, Source = Src.Drawing, Evidence = "Dimension text vs measured length" }
                    : new CheckRow { Mark = "warn", Text = "Dimension check: not built yet", Source = Src.Pending, Evidence = "Dimension vs measured (AI-631)" },
            };
        }

        public static string UnitWord(double mmPerUnit) => mmPerUnit switch
        {
            1 => "millimetres",
            10 => "centimetres",
            1000 => "metres",
            25.4 => "inches",
            _ => mmPerUnit.ToString("0.###", CultureInfo.InvariantCulture) + " mm per unit",
        };

        public static string Gate2Verdict(CheckState s) => s.Gate2 == GateState.Blocked
            ? "Blocked: the units need confirming"
            : s.Engine.DimensionRow != null ? "Scale confirmed from 2 sources" : "DWG header only · dimension check not built yet";

        /// <summary>g = two sources agree, a = one source (header only), r = blocked.</summary>
        public static string Gate2Tone(CheckState s) => s.Gate2 == GateState.Blocked ? "r" : s.Engine.DimensionRow != null ? "g" : "a";

        // ── screen 7 ──
        public static List<CheckRow> Everything(CheckState s)
        {
            var sh = s.Sheet;
            var roles = sh.Layers.Where(l => l.Role != "other").GroupBy(l => l.Role).ToList();
            var other = sh.Layers.Where(l => l.Role == "other").ToList();
            var widths = s.Plans.Where(p => p.TargetLevel == s.BuildLevel)
                .SelectMany(p => p.Plan.WidthsMm).GroupBy(kv => kv.Key).Select(g => (Mm: g.Key, Count: g.Sum(kv => kv.Value)))
                .OrderByDescending(w => w.Count).ToList();
            var rows = new List<CheckRow>
            {
                new CheckRow
                {
                    Text = "Layers " + sh.Layers.Count + " → " + roles.Count + " · " + other.Count + " other left out, not guessed",
                    Source = Src.Drawing, Evidence = "Grouped by layer name; unknown layers left out",
                    Detail = string.Join(" · ", roles.Select(g => RoleWord(g.Key) + ": " + g.Count() + " layer" + (g.Count() == 1 ? "" : "s")))
                             + (other.Count > 0 ? "\nLeft out: " + string.Join(", ", other.Take(6).Select(l => l.Name.Length == 0 ? "(no layer)" : l.Name)) + (other.Count > 6 ? ", +" + (other.Count - 6) + " more" : "") : ""),
                },
                new CheckRow
                {
                    Text = "Wall widths: " + string.Join(" · ", widths.Take(4).Select(w => w.Mm + " mm ×" + N(w.Count))) + (widths.Count > 4 ? " · +" + (widths.Count - 4) : ""),
                    Source = Src.Drawing, Evidence = "Measured from double lines",
                    Detail = "Wall types: matched to the project's types at build (AI-649)",
                },
                s.Engine.LegendRow != null
                    ? new CheckRow { Text = s.Engine.LegendRow, Source = Src.Drawing, Badge = "Drawing (legend)", Evidence = "Legend rows" }
                    : new CheckRow { Text = "Doors and windows from the legend: not read yet", Source = Src.Pending, Evidence = "Legend reader (pending #8)" },
                new CheckRow
                {
                    Text = "Heights: walls " + N((int)s.WallHeightMm) + " mm, door head " + N(JkrDoorHeadMm), Source = Src.JkrDefault,
                    Evidence = "No elevation or section read from this drawing",
                },
                new CheckRow { Text = "Slab " + JkrSlabMm, Source = Src.JkrDefault, Evidence = "Never read from a plan" },
            };
            if (sh.OpeningBlocks.Count > 0)
                rows.Add(new CheckRow
                {
                    Text = sh.OpeningBlocks.Count + " door/window block" + (sh.OpeningBlocks.Count == 1 ? "" : "s") + " · matched at build",
                    Source = Src.Pending, Evidence = "Type matching (AI-649)",
                    Detail = string.Join(", ", sh.OpeningBlocks.Take(8)) + (sh.OpeningBlocks.Count > 8 ? ", +" + (sh.OpeningBlocks.Count - 8) + " more" : ""),
                });
            return rows;
        }

        private static string RoleWord(string role) => role switch
        {
            "wall" => "Walls",
            "door" => "Doors",
            "window" => "Windows",
            "door_or_window" => "Doors/windows",
            _ => role,
        };

        public static string AnsweredBanner(CheckState s) => s.Questions.Count == 0
            ? "0 questions · both gates passed"
            : s.Answered + " of " + s.Questions.Count + " answered";

        public static string AnswerSummary(CheckState s) =>
            string.Join(" · ", s.Questions.Where(q => q.Answer != null).Select(q => q.Answer));

        public static string BuildLabel(CheckState s) =>
            "Build " + s.BuildLevel + " · " + N(s.Walls) + " walls · " + N(s.Doors) + " doors";

        public static string ContinueLabel(CheckState s)
        {
            int left = s.Questions.Count - s.Answered;
            return left > 0 ? "Answer " + left + " more to continue" : "Continue";
        }

        public static string StatusLine(CheckState s) => "CAD to BIM · Check · " + s.Screen switch
        {
            CheckScreen.Loading => "reading",
            CheckScreen.Preflight => "reading done",
            CheckScreen.Consultant => "consultant",
            CheckScreen.Gate1 => "gate 1",
            CheckScreen.Gate2 => s.Gate2 == GateState.Blocked ? "gate 2 blocked" : "gate 2",
            CheckScreen.Questions => s.Answered + " of " + s.Questions.Count + " answered",
            CheckScreen.Rows => "ready to build",
            CheckScreen.Error => "read failed",
            _ => "open a drawing",
        };
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Bomba
{
    // View models for the "Modern Flow" pane (design 10A, Bomba Modern
    // Flow.dc.html). One screen at a time, plain language, big type. The
    // honesty rules are unchanged — a design decision gets no Fix button,
    // NOT CHECKED is its own state, and rule-derived values render [X] until
    // the tables are verified.

    public enum BombaScreen { Home, Setup, Checking, Summary, Detail, Done, Needs }

    /// The 10A LIGHT palette — the design file moved from dark to light and
    /// this fork follows it. Deliberately NOT the JKR tokens (user decision);
    /// kept in one place so the fork is at least consistent.
    public static class M
    {
        public static readonly Brush Bg = Freeze("#FFFFFF");
        public static readonly Brush Card = Freeze("#F4F3F0");
        public static readonly Brush CardHover = Freeze("#ECEAE4");
        public static readonly Brush CardDeep = Freeze("#EFEEE9");   // inline-select rows
        public static readonly Brush Line = Freeze("#E5E3DD");
        public static readonly Brush Ink = Freeze("#1A1C20");
        public static readonly Brush Body = Freeze("#44484F");
        public static readonly Brush Sub = Freeze("#6B7078");
        public static readonly Brush Dim = Freeze("#7A7F88");
        public static readonly Brush Faint = Freeze("#AEB2BA");
        public static readonly Brush Accent = Freeze("#A97F00");     // link/text accent
        public static readonly Brush AccentInk = Freeze("#2A2000");  // ink on the yellow button
        public static readonly Brush Red = Freeze("#D64545");
        public static readonly Brush Green = Freeze("#1F9D5B");
        public static readonly Brush Amber = Freeze("#B8721A");
        public static readonly Brush RedTint = Freeze("#24FF7A7A");
        public static readonly Brush GreenTint = Freeze("#2434C07A");
        public static readonly Brush AmberTint = Freeze("#24F0B45A");
        public static readonly Brush NoteBg = Freeze("#FDF3E2");     // amber note card
        public static readonly Brush NoteLine = Freeze("#F0E2C4");
        public static readonly Brush ChipNeutral = Freeze("#ECEDEF");

        private static Brush Freeze(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }

    /// One key/value row on an issue's facts card.
    public class FactVm
    {
        public string K { get; set; }
        public string V { get; set; }
        public Brush Ink { get; set; }

        public FactVm(string k, string v, Brush ink) { K = k; V = v; Ink = ink ?? M.Ink; }
    }

    /// One thing to deal with, in plain language. Built from a backend
    /// Finding; tri-state survives as the tag kind and the Cls bucket.
    public class IssueVm : NotifyBase
    {
        private bool _done;

        public string Subject { get; set; }      // backend Finding.subject — joins to requirements rows
        public string Cls { get; set; }          // "fix" | "cant" — feeds the summary filter chips
        public string Tag { get; set; }          // "NEEDS PLACING" · "CAN'T CHECK" · …
        public Brush TagInk { get; set; }
        public Brush TagBg { get; set; }
        public string Icon { get; set; }         // emoji
        public Brush IconBg { get; set; }
        public string Title { get; set; }        // "No manual call points"
        public string Where { get; set; }        // "Whole building"
        public string Sub { get; set; }          // summary-row second line
        public string Body { get; set; }         // plain-language explanation
        public string Cite { get; set; }         // small print: by-law · rules version
        public ObservableCollection<FactVm> Facts { get; private set; }
        public IList<long> ElementIds { get; private set; }

        // Phase 1 has no automatic fixes, so every issue resolves in the
        // model; the primary action is an honest re-check, never a simulate.
        public string DoLabel { get; set; }
        public string NoFixNote { get; set; }    // amber note; null hides it

        public IssueVm()
        {
            Facts = new ObservableCollection<FactVm>();
            ElementIds = new List<long>();
            DoLabel = "Re-check after fixing";
            Cls = "fix";
        }

        public bool Done
        {
            get { return _done; }
            set { Set(ref _done, value); }
        }

        public bool HasNote { get { return !string.IsNullOrEmpty(NoFixNote); } }
        public bool HasWhere { get { return !string.IsNullOrEmpty(Where); } }
        public bool HasElements { get { return ElementIds.Count > 0; } }
    }

    /// One progress dot in the detail wizard's top bar.
    public class DotVm
    {
        public double W { get; set; }
        public Brush Fill { get; set; }
    }

    /// One filter chip on the Summary verdict block. Chips are computed from
    /// the issue list — never hardcoded counts (the numbers must not be able
    /// to disagree with the list below them).
    public class ChipVm : NotifyBase
    {
        private bool _active;

        public string Cls { get; set; }          // "open" | "fix" | "cant" | "pass"
        public string Label { get; set; }
        public Brush Ink { get; set; }
        public Brush Bg { get; set; }

        public bool Active
        {
            get { return _active; }
            set { if (Set(ref _active, value)) Raise("Ring"); }
        }

        /// Active chip gets a 2px ring in its own ink.
        public Brush Ring { get { return Active ? Ink : Brushes.Transparent; } }
    }

    /// One building-type option in the Home inline select. The pick is the
    /// drafter's assertion; the "detected" badge only marks the suggestion.
    public class PgOptionVm
    {
        public string Path { get; set; }         // backend option path
        public string Label { get; set; }
        public bool Detected { get; set; }       // room-name read points here
        public bool Current { get; set; }
        public string Badge { get { return Detected ? "detected" : ""; } }
        public string Mark { get { return Current ? "✓" : "›"; } }
        public Brush MarkInk { get { return Current ? M.Green : M.Faint; } }
    }

    /// One row of the 'Required fire systems' screen — a schedule requirement
    /// with its presence chip. Linked rows tap through to their issue.
    public class ReqRowVm
    {
        public string Name { get; set; }
        public string ChipText { get; set; }
        public Brush ChipInk { get; set; }
        public Brush ChipBg { get; set; }
        public IssueVm Issue { get; set; }          // null = not tappable
        public bool Linked { get { return Issue != null; } }
    }

    /// One row of the checking screen's task list.
    public class CheckRowVm : NotifyBase
    {
        private string _glyph = "·";
        private Brush _glyphInk = M.Faint;
        private Brush _labelInk = M.Sub;

        public string Label { get; set; }
        public string Glyph { get { return _glyph; } set { Set(ref _glyph, value); } }
        public Brush GlyphInk { get { return _glyphInk; } set { Set(ref _glyphInk, value); } }
        public Brush LabelInk { get { return _labelInk; } set { Set(ref _labelInk, value); } }
    }
}

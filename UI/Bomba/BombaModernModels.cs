using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Bomba
{
    // View models for the "Modern Flow" pane (design 10A): one screen at a
    // time, plain language, big type. The honesty rules are unchanged — a
    // design decision gets no Fix button, NOT CHECKED is its own step, and
    // rule-derived values render [X] until the tables are verified.

    public enum BombaScreen { Home, Setup, Checking, Summary, Detail, Done, Needs }

    /// The 10A dark palette. Deliberately NOT the JKR tokens: the modern flow
    /// is its own visual system (user decision, design file Bomba Modern
    /// Flow.dc.html). Kept in one place so the fork is at least consistent.
    public static class M
    {
        public static readonly Brush Bg = Freeze("#0F1115");
        public static readonly Brush Card = Freeze("#1A1E26");
        public static readonly Brush CardHover = Freeze("#1F242E");
        public static readonly Brush Line = Freeze("#232834");
        public static readonly Brush Ink = Freeze("#E8EAF0");
        public static readonly Brush Body = Freeze("#C3C9D4");
        public static readonly Brush Sub = Freeze("#9AA3B2");
        public static readonly Brush Dim = Freeze("#6B7280");
        public static readonly Brush Faint = Freeze("#4A5160");
        public static readonly Brush Accent = Freeze("#7C86FF");
        public static readonly Brush Red = Freeze("#FF7A7A");
        public static readonly Brush Green = Freeze("#34C07A");
        public static readonly Brush Amber = Freeze("#F0B45A");
        public static readonly Brush RedTint = Freeze("#26FF7A7A");
        public static readonly Brush GreenTint = Freeze("#2434C07A");
        public static readonly Brush AmberTint = Freeze("#24F0B45A");
        public static readonly Brush AccentTint = Freeze("#247C86FF");

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
    /// Finding; tri-state survives as the tag kind.
    public class IssueVm : NotifyBase
    {
        private bool _done;

        public string Subject { get; set; }      // backend Finding.subject — joins to requirements rows
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
        }

        public bool Done
        {
            get { return _done; }
            set { Set(ref _done, value); }
        }

        public bool HasNote { get { return !string.IsNullOrEmpty(NoFixNote); } }
        public bool HasWhere { get { return !string.IsNullOrEmpty(Where); } }
    }

    /// One progress dot in the detail wizard's top bar.
    public class DotVm
    {
        public double W { get; set; }
        public Brush Fill { get; set; }
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

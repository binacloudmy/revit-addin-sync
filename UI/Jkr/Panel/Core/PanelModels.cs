using System;
using System.Collections.Generic;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>A BM / EN pair (prototype {ms, en}); a plain string is the same in both.</summary>
    public sealed class Text
    {
        public string Ms { get; }
        public string En { get; }
        public Text(string ms, string en) { Ms = ms ?? ""; En = en ?? ""; }
        public static Text Same(string s) => new Text(s, s);
        /// <summary>prototype L(v, lang)</summary>
        public string In(string lang) => string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase) ? En : Ms;
        public override string ToString() => En;
    }

    /// <summary>Who decides (prototype v): the AI's verdict, or a person.</summary>
    public enum Verify { Ai, Human }

    /// <summary>prototype status. Reviewed is only used on items that need a person.</summary>
    public enum ItemStatus { Open, Ignored, Resolved, Reviewed }

    /// <summary>prototype fix: null | 'fix'.</summary>
    public enum FixKind { None, Fix }

    public enum PanelStage { Start, Running, Results }

    public enum PanelTab { Open, Ignore, Resolved, Manual }

    /// <summary>One prototype issue (ISSUES[i] plus its live status / passed).</summary>
    public sealed class PanelItem
    {
        public string Id = "";
        /// <summary>Borang item ("A1", "C1.1", "D").</summary>
        public string Row = "";
        /// <summary>Section A–E.</summary>
        public string Sec = "";
        /// <summary>"High" | "Med" | "Low".</summary>
        public string Sev = "Med";
        public Verify V = Verify.Ai;
        public Text Title = Text.Same("");
        public Text Desc = Text.Same("");
        public string El = "";
        public string Req = "";
        public Text Act = Text.Same("");
        public string Prop;
        public string Cite = "";
        public FixKind Fix = FixKind.None;
        public string Hash = "";
        /// <summary>Why the AI did not judge it (items that need a person).</summary>
        public Text Reason;

        /// <summary>Why the last auto-fix failed, or empty (shown on the row).</summary>
        public string FixError = "";

        /// <summary>No ready-made fix, but the row can ask the backend AI fixer for one (never Fix all).</summary>
        public bool AiFix;

        /// <summary>AI-434: several valid codes; the row's input is a dropdown of <see cref="Options"/> and
        /// <see cref="PickCode"/> (the AI's choice, or "") is preselected. Never in Fix all.</summary>
        public bool Pick;
        public string PickCode = "";

        /// <summary>Why the AI fixer proposed <see cref="Prop"/> ("Closest match in the JKR material
        /// list…"). Only with a proposal.</summary>
        public Text AiReason;

        /// <summary>AI-435: the AI fixer was asked about this row and answered (with or without a value).</summary>
        public bool AiAsked;

        /// <summary>The allowed values for the row's manual input (reference set), or empty = free text.</summary>
        public System.Collections.Generic.IReadOnlyList<string> Options = System.Array.Empty<string>();

        public ItemStatus Status = ItemStatus.Open;
        /// <summary>Confirmed compliant by a re-scan: drops out of every list and
        /// counts as a passing cell.</summary>
        public bool Passed;

        /// <summary>The user marked it "Mark as Manual" (option b): it stays Open and still
        /// counts as a fail, is left out of every fix request, and prints "handled manually".</summary>
        public bool MarkedManual;

        /// <summary>"Mark as Manual" may be offered (an open AI item below the High tier).</summary>
        public bool CanMarkManual;

        public bool IsHuman => V == Verify.Human;

        public PanelItem Clone() => (PanelItem)MemberwiseClone();
    }

    public sealed class SectionStats
    {
        public int Pass;
        public int Fail;
        public int Pend;
        /// <summary>Math.round(pass / (pass + fail) * 100); null when the section
        /// has no AI cells (the prototype would show NaN).</summary>
        public int? Pct => Pass + Fail == 0 ? (int?)null : (int)Math.Round(Pass * 100.0 / (Pass + Fail), MidpointRounding.AwayFromZero);
    }

    public sealed class ConfirmRequest
    {
        /// <summary>"fix" (prototype confirm.kind).</summary>
        public FixKind Kind;
        public List<string> Ids = new List<string>();
    }

    /// <summary>Keys the panel reacts to (prototype handleKey).</summary>
    public enum PanelKey { Other, Escape, Enter, Up, Down, Left, Right, J, K, F, A, Slash }

    /// <summary>What the host must do after a key: nothing, mark it handled
    /// (preventDefault), focus the search box, or blur the focused input.</summary>
    public enum KeyOutcome { None, Handled, FocusSearch, Blur }

    /// <summary>Delayed callbacks (toasts, the run animation) behind an interface
    /// so tests advance time instead of waiting.</summary>
    public interface IPanelClock
    {
        /// <summary>Run <paramref name="action"/> once after <paramref name="delay"/>;
        /// dispose to cancel.</summary>
        IDisposable Schedule(TimeSpan delay, Action action);
    }
}

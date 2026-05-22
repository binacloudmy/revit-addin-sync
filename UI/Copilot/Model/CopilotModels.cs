using System;
using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    // ─── Enums (mirror the prototype state machine) ──────────────────────────
    public enum CpScreen { Home, ToolForm, ToolReview, Running, Result }
    public enum CpTab { Chat, Library, History, Saved }
    public enum CpMsgKind { User, Thinking, Clarify, Proposal, Running, Result, Note }
    public enum CpResultKind { Count, Issues, List, File, Plain }
    public enum CpFieldKind { Select, Text, Seg }

    /// <summary>One catalog command — vetted (Tier 1) or AI (Tier 2). Port of a data.jsx entry.</summary>
    public class ToolDef
    {
        public string Id;
        /// <summary>Backend/synthesizer tool name (e.g. rename_elements, set_parameter, open_view).</summary>
        public string BackendName;
        public string Title;
        public string Desc;
        public string Icon;       // CopilotIcons key
        public string TileBg;     // hex, e.g. "#fef3c7"
        public string TileFg;     // hex, e.g. "#a16207"
        public string Category;   // CATEGORIES id
        public int Tier;          // 1 = vetted, 2 = AI
        public bool Saved;        // catalog "saved" badge (e.g. count-doors)

        public List<FieldDef> Fields = new List<FieldDef>();   // Tier-1 form schema
        public List<string> Plan = new List<string>();          // Tier-2 plan steps
        public string Code;                                      // Tier-2 sample C# preview

        public Func<IDictionary<string, object>, string> RunLabel;   // green Run button label
        public Func<IDictionary<string, object>, string> PlanText;   // vetted preview line
        public Func<IDictionary<string, object>, ResultModel> Result; // mock/offline result shape
    }

    public class FieldDef
    {
        public string Id;
        public string Label;
        public string Hint;
        public CpFieldKind Kind;
        public string[] Options;
        public object Default;   // for Seg this is the option STRING (not the index)
    }

    public class CategoryDef
    {
        public string Id;
        public string Label;
        public int Count;
        public CategoryDef(string id, string label, int count) { Id = id; Label = label; Count = count; }
    }

    public class ResultModel
    {
        public CpResultKind Kind;
        public string Headline;   // count stores the number as a string ("47")
        public string Unit;
        public string Sub;
        public string Path;       // file variant
        public List<BarItem> Bars = new List<BarItem>();
        public List<IssueItem> Items = new List<IssueItem>();
        public List<DiffItem> Diffs = new List<DiffItem>();
        public int GroupedSkipped;   // set-parameter: elements skipped for being inside groups
    }

    public class BarItem
    {
        public string Label;
        public int Value;
        public string Color;   // hex
        public BarItem() { }
        public BarItem(string label, int value, string color) { Label = label; Value = value; Color = color; }
    }

    public class IssueItem
    {
        public string Id;
        public string Sub;
        public IssueItem() { }
        public IssueItem(string id, string sub) { Id = id; Sub = sub; }
    }

    public class DiffItem
    {
        public string From;
        public string To;
        public DiffItem() { }
        public DiffItem(string from, string to) { From = from; To = to; }
    }

    public class HistoryEntry
    {
        public string Time;
        public string ToolId;
        public string Status;    // "ok" | "warn" | "undone"
        public string Summary;
        public HistoryEntry() { }
        public HistoryEntry(string time, string toolId, string status, string summary)
        { Time = time; ToolId = toolId; Status = status; Summary = summary; }
    }

    public class Mention
    {
        public string Kind;    // level | category | view | selection
        public string Value;
        public Mention() { }
        public Mention(string kind, string value) { Kind = kind; Value = value; }
    }

    public class ClarifyOption
    {
        public string ToolId;
        public string Label;
        public string Prompt;
        public string Hint;
    }

    /// <summary>A chat thread message. The VM swaps Kind by replacing the item in the
    /// ObservableCollection (so the DataTemplate selector re-evaluates).</summary>
    public class ChatMessage
    {
        public string Role;          // "user" | "ai"
        public CpMsgKind Kind;
        public string Text;
        public string ToolId;        // proposal/running/result target tool (catalog id, may be null)
        public string Title;         // proposal display title (backend intent; falls back to tool title)
        public List<Mention> Mentions = new List<Mention>();
        public string Question;      // clarify
        public List<ClarifyOption> Options = new List<ClarifyOption>();  // clarify
        public ResultModel Result;   // result
        public string Code;          // proposal — generated C# (backend or catalog sample)
        public List<string> PlanSteps = new List<string>();  // proposal — plan, English
        public string SourcePrompt;  // proposal — the user's original text, for Regenerate
    }

    /// <summary>Floating viewport marker (Task 15). Coordinates are % of the active view rect.</summary>
    public class HighlightMarker
    {
        public double XPct;
        public double YPct;
        public string OldLabel;
        public string NewLabel;
        public string Color;   // hex
        public bool Dot;
        public bool Warn;
    }
}

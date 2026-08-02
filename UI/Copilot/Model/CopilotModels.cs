using System;
using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    // ─── Enums (mirror the prototype state machine) ──────────────────────────
    public enum CpScreen { Home, ToolForm, ToolReview, Running, Result }
    public enum CpTab { Chat, Library, History, Saved }
    public enum CpMsgKind { User, Thinking, Clarify, Proposal, Running, Result, AiReply, ConfirmActions }
    // AiReply = plain-text AI response (no card, no Save/Copy/Undo). Used
    // when the backend marks is_query=true: code is auto-run and the
    // structured result is reformulated as one conversational sentence.
    // When ToolCallTrace is set, the chat renders a compact "steps" panel
    // under the reply (one checked row per tool) so the drafter can see what
    // the agent actually ran.
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
        public string Details;    // ready markdown from SetResult.details — tables render in chat (with the CSV button); dropping this loses computed data (clearance round 2026-07-23)
        public string Path;       // file variant
        public List<BarItem> Bars = new List<BarItem>();
        public List<IssueItem> Items = new List<IssueItem>();
        public List<DiffItem> Diffs = new List<DiffItem>();
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

    public class History
    {
        public string Sender;   // "user" | "bot"
        public string Text;
        public string Time;
        public List<string> Tools;  // bot messages only — tool IDs used in the reply
        public List<HistoryFile> Files;  // user messages only — files attached to the prompt (name + line count, content not persisted)
        public History() { }
        public History(string sender, string text, string time, List<string> tools = null)
        { Sender = sender; Text = text; Time = time; Tools = tools; }
    }

    /// <summary>A file attachment as persisted in run history — enough to redraw
    /// the chip and no more. The contents are deliberately not stored, to keep
    /// copilot-state.json small.</summary>
    public class HistoryFile
    {
        public string Name;
        public int Lines;   // text attachments only
        public int Pages;   // pdf attachments only
        // "text" | "dwg" | "pdf". History written before this field has it null;
        // ResolvedKind maps those rows (including the old Lines == -1 drawing
        // sentinel) so existing copilot-state.json still redraws.
        public string Kind;

        public HistoryFile() { }
        public HistoryFile(string name, int lines) { Name = name; Lines = lines; Kind = "text"; }

        public static HistoryFile ForDrawing(string name) =>
            new HistoryFile { Name = name, Kind = "dwg" };

        public static HistoryFile ForDocument(string name, int pages) =>
            new HistoryFile { Name = name, Pages = pages, Kind = "pdf" };

        public string ResolvedKind =>
            !string.IsNullOrEmpty(Kind) ? Kind : (Lines < 0 ? "dwg" : "text");

        /// <summary>Chip projection of a live attachment — the one place that maps
        /// an AttachmentKind onto what the chip shows. A PDF's page count comes
        /// from the summary the addin produced, so it is 0 until the file has
        /// actually been read (and stays 0 when it could not be).</summary>
        public static HistoryFile From(FileAttachment f)
        {
            if (f == null) return null;
            switch (f.Kind)
            {
                case AttachmentKind.Dwg:
                    return ForDrawing(f.Name);
                case AttachmentKind.Pdf:
                    return ForDocument(f.Name, PagesFromSummary(f.SummaryJson));
                default:
                    return new HistoryFile(f.Name, LineCount(f.Content));
            }
        }

        private static int LineCount(string content)
        {
            if (string.IsNullOrEmpty(content)) return 0;
            int n = 1;
            foreach (var c in content) if (c == '\n') n++;
            return n;
        }

        private static int PagesFromSummary(string summaryJson)
        {
            if (string.IsNullOrEmpty(summaryJson)) return 0;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(summaryJson);
                return doc.RootElement.TryGetProperty("pages", out var p)
                       && p.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? p.GetInt32() : 0;
            }
            catch { return 0; }
        }
    }

    public class HistoryEntry
    {
        public string Time;
        public string Status;    // "ok" | "warn" | "undone"
        public string Summary;
        public string Label;     // user-set display name; null means show auto-generated Summary
        public string SessionId; // backend session this entry was recorded under; null on entries saved before Continue existed
        public List<History> History = new List<History>();
        public HistoryEntry() { }
        public HistoryEntry(string time, string status, string summary, List<History> history = null)
        { Time = time; Status = status; Summary = summary; History = history ?? new List<History>(); }
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
        public string ToolId;        // proposal/running/result target tool
        public SlashTool SlashCommand;  // slash-command chip shown atop a user bubble (UI-only)
        public List<Mention> Mentions = new List<Mention>();
        public string Question;      // clarify
        public List<ClarifyOption> Options = new List<ClarifyOption>();  // clarify
        public ResultModel Result;   // result
        public string Code;          // proposal — generated C# (backend or catalog sample)
        public List<string> PlanSteps = new List<string>();  // proposal — plan, English
        public List<string> ToolCallTrace; // tool-calling agent: ordered tool names called
        public IReadOnlyList<ProgressStep> Steps; // full phased trail; ChatView prefers this over ToolCallTrace
        // Transient live trail for the CURRENT turn's Thinking bubble (set by
        // CopilotViewModel's OnSteps/OnCodeStream handlers, cleared per-turn).
        // Unlike Steps (persisted with a resolved message), LiveSteps only ever
        // rides on the in-flight Thinking message — ChatView renders it via the
        // cached ProgressTrailView instead of the single-line ThinkingTrail.
        public IReadOnlyList<ProgressStep> LiveSteps;
        // A Thinking-kind message whose Text is the ACCUMULATING reply prose
        // (not a step trail). The VM reuses Kind=Thinking during reply streaming
        // so ReplaceLastThinking keeps targeting the same growing bubble; this
        // flag tells ChatView to render it as the reply (markdown) instead of the
        // thinking-steps trail, so the trail collapses the moment prose arrives.
        public bool StreamingReply;
        // Cancelled generation: rendered as the design's italic faint
        // "Interrupted." line (stop icon, no bubble, no feedback row).
        public bool Interrupted;
        // Proposal card status ("proposed" default / "dismissed") — the design's
        // "· Proposed / · Applied / · Dismissed" header suffix. Applied state is
        // carried by the Result message, so only dismissal is stored here.
        public bool Dismissed;
        // Send timestamp ("2:25 PM") shown under user bubbles / in AI feedback rows.
        public string Time;
        public List<string> ImagesBase64;  // screenshots pasted with this prompt (base64 PNG) — rendered as thumbnails
        public List<FileAttachment> Files;  // text files attached with this prompt — rendered as chips (content lives only in the backend route text)
        public RevitWebAppSync.Models.ReviewerVerdict Verdict; // attached to AiReply messages
        // One-tap "next step" offer parsed server-side from the reply's trailing
        // "Tindakan:" line. Empty/null = no offer (old backend or plain reply) —
        // ChatView must render no buttons in that case. Rendered ONLY on the
        // LAST AiReply in the thread and only while unresolved; older messages
        // with a stale offer fall back to plain text.
        public string Tindakan;
        public bool TindakanResolved;
        // ConfirmActions card: friendly one-line labels of the pending MUTATE
        // batch awaiting the user's Ya/Tidak. Buttons render only while
        // unresolved AND the card is last in the thread; a resolved card keeps
        // the action list as an audit trail.
        public List<string> ActionLabels;
        public bool ActionsResolved;
        // Which way it was resolved — null while pending. Lets the resolved-state
        // render (2026-08-02 spec) say "Allowed"/"Rejected" instead of a generic
        // "resolved" note.
        public bool? ActionsApproved;
        // Action Mode addendum (2026-08-02): true when this card was resolved
        // PROGRAMMATICALLY by Auto mode (never by a click) — ConfirmActionsCard
        // renders "Auto-approved · N writes" instead of "Allowed", and skips the
        // amber "Needs permission" header in favour of a green one, so the
        // transcript reads honestly (permission was never actually asked for).
        public bool AutoApproved;
        // Codegen approval gate (Action Mode addendum): non-empty means this
        // ConfirmActions card is gating a codegen C# run, NOT a MUTATE-tool
        // batch — ConfirmActionsCard/CopilotViewModel branch on this to route
        // Allow/Reject to AcceptCodeApproval/DeclineCodeApproval instead of
        // AcceptActions/DeclineActions. The rest of the fields below are just
        // enough context to call ExecuteAsChatReply on Allow — everything else
        // ExecuteAsChatReply needs (reasoning trail, followups, result summary,
        // tindakan, reply text) is already carried on the fields above/below,
        // reused rather than duplicated.
        public string PendingCode;
        public string PendingCodeRoutePrompt;
        public string PendingCodeDisplayPrompt;
        public List<HistoryFile> PendingCodeHistoryFiles;

        // ─── Streaming reasoning timeline (2026-08-02 copilot-reasoning-ui spec) ──
        // Persisted trail — set once the turn finishes, so a completed AiReply/
        // ConfirmActions message can still expand its "working narrative" from
        // history. Null/empty = no steps this turn (ChatView omits the block).
        public List<ReasoningStep> ReasoningSteps;
        // Transient live trail for the CURRENT turn's Thinking bubble — mirrors
        // LiveSteps above; only ever rides the in-flight message.
        public IReadOnlyList<ReasoningStep> LiveReasoningSteps;
        // Whole-turn reasoning duration, captured at completion (seconds).
        public double ReasoningElapsedSeconds;
        // Expand/collapse state, persisted with the message so a re-opened
        // history turn remembers whether the drafter had it open.
        public bool ReasoningOpen;
        // True once the drafter manually toggled the timeline THIS turn — the
        // client suppresses auto-collapse-on-answer when set (README behaviour).
        // Turn-scoped only; not meaningful once the message is persisted.
        public bool ReasoningUserToggled;

        // Done-frame follow-up chips (0-3), model-derived text — never a fixed
        // menu. Null/empty = none. "Undo" is a client-side chip ChatView adds
        // itself after any write, not carried here.
        public List<string> Followups;
        // Structured result breakdown (proportion-bar rows) — set only when the
        // turn's tool results carried a count_by/color legend/route_* summary.
        // Null = fall back to the plain answer text (no result card).
        public ResultSummaryModel ResultSummary;
    }

    /// <summary>Proportion-bar row: one system/category slice of a result_summary
    /// ("Supply Air", 486, color_hint "supply"). ColorHint maps to the
    /// Cp.System.* tokens client-side — the backend never sends a hex.</summary>
    public class ResultSummaryRow
    {
        public string Label;
        public int Count;
        public string ColorHint;   // "supply" | "return" | "exhaust" | "none" | ""
        public ResultSummaryRow() { }
        public ResultSummaryRow(string label, int count, string colorHint)
        { Label = label; Count = count; ColorHint = colorHint; }
    }

    /// <summary>Optional structured breakdown attached to a done frame — proportion
    /// bars in the result card instead of (or above) the plain answer text.</summary>
    public class ResultSummaryModel
    {
        public string Title;
        public int Total;
        public List<ResultSummaryRow> Rows = new List<ResultSummaryRow>();
    }

    /// <summary>What an attachment carries. Text files travel as CONTENT (read
    /// at attach time, embedded in the route text). Binary kinds travel as a
    /// PATH only — a DWG or a PDF is large and unreadable as text, so the addin
    /// reads it locally and the turn carries a compact summary, never bytes.</summary>
    public enum AttachmentKind { Text, Dwg, Pdf }

    /// <summary>A file attached to a prompt. Content is sent to the backend
    /// (embedded in the route text) but never shown as raw text in the chat bubble.</summary>
    public class FileAttachment
    {
        public string Name;
        public string Content;
        public AttachmentKind Kind = AttachmentKind.Text;
        // Local path — set for binary kinds only (never sent to the backend).
        public string Path;
        // Resolved by the pane before the turn is sent, and the agent's handle
        // for the matching detail tools: "att:<guid>" / "model:<id>" for a
        // drawing, "pdf:<guid>" for a document.
        public string Ref;
        // Compact <kind>.summary/1 JSON, or null when the file could not be read.
        public string SummaryJson;
        // Drafter-readable reason the file could not be read (null on success).
        public string ReadError;

        public FileAttachment() { }
        public FileAttachment(string name, string content) { Name = name; Content = content; }

        public static FileAttachment ForDrawing(string name, string path) =>
            new FileAttachment { Name = name, Path = path, Kind = AttachmentKind.Dwg };

        public static FileAttachment ForDocument(string name, string path) =>
            new FileAttachment { Name = name, Path = path, Kind = AttachmentKind.Pdf };

        /// <summary>Label used in the route-text block and nowhere else.</summary>
        public string BlockLabel => Kind switch
        {
            AttachmentKind.Dwg => "Attached DWG",
            AttachmentKind.Pdf => "Attached PDF",
            _ => "Attached",
        };
    }

    /// <summary>Composed prompt-bar submission: text plus any screenshots the user
    /// pasted (base64 PNG) and any text files attached. The PromptBar sends this
    /// object through ChatSendCommand when images or files are attached; plain
    /// string when not (chips, follow-ups).</summary>
    public class PromptPayload
    {
        public string Text;
        public List<string> ImagesBase64;
        public List<FileAttachment> Files;
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

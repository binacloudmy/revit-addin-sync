using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>What the Copilot launcher gets when a person clicks "Fix via Copilot" on a row.</summary>
    public sealed class CopilotFixRequest
    {
        public CopilotFixRequest(string issueId, string checkId, IEnumerable<long> elementIds, string title)
        {
            IssueId = issueId ?? "";
            CheckId = checkId ?? "";
            ElementIds = (elementIds ?? Enumerable.Empty<long>()).ToList();
            Title = title ?? "";
        }
        public string IssueId { get; }
        /// <summary>The spec check id ("Da.1", "C1.3.1").</summary>
        public string CheckId { get; }
        public IReadOnlyList<long> ElementIds { get; }
        public string Title { get; }

        /// <summary>AI-509: every row this request covers. One row for the row-level button (just <see cref="IssueId"/>); all the open
        /// rows of a check for "Fix all elements via Copilot" (<see cref="IsCheckWide"/>).</summary>
        public IReadOnlyList<string> IssueIds { get; private set; } = new string[0];

        /// <summary>AI-509: the request covers every open Copilot-fixable row of one spec check: one confirm, one chat, one re-check.
        /// <see cref="ElementIds"/> is then the union of the rows' element ids.</summary>
        public bool IsCheckWide { get; private set; }

        /// <summary>The request of the check header's "Fix all elements via Copilot": <paramref name="issueIds"/> are the rows (first one
        /// stands in as <see cref="IssueId"/>), <paramref name="elementIds"/> all their element ids (distinct, in order).</summary>
        public static CopilotFixRequest ForCheck(string checkId, IEnumerable<string> issueIds, IEnumerable<long> elementIds, string title)
        {
            var rows = (issueIds ?? Enumerable.Empty<string>()).Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal).ToList();
            var ids = (elementIds ?? Enumerable.Empty<long>()).Where(i => i > 0).Distinct().ToList();
            return new CopilotFixRequest(rows.FirstOrDefault() ?? "", checkId, ids, title) { IssueIds = rows, IsCheckWide = true };
        }

        /// <summary>The rows of this request: <see cref="IssueIds"/>, or just <see cref="IssueId"/> for a row-level request.</summary>
        public IReadOnlyList<string> Rows => IssueIds.Count > 0 ? IssueIds : (IReadOnlyList<string>)(IssueId.Length > 0 ? new[] { IssueId } : new string[0]);
    }

    /// <summary>
    /// AI-492 seam. The Copilot launcher (AI-494: open the pane, send the prompt) implements this and
    /// assigns <see cref="JkrCopilotFix.Launcher"/>. Until then the click does nothing.
    /// </summary>
    public interface ICopilotFixLauncher
    {
        void Launch(CopilotFixRequest request);
    }

    /// <summary>
    /// AI-492: which JKR rows may offer "Fix via Copilot". One code-side list keyed by spec check id
    /// (<see cref="Allowed"/>); add an id there (and its wording in JkrCopilotPrompt) and the button follows, no UI change.
    /// A row shows the button only when ALL hold (<see cref="Eligible"/>): the check is in the list and not
    /// in <see cref="Denied"/>, the catalog class is AI (never Human / List), no ready-made Fix, no picker, and at least
    /// one element id. Revit- and WPF-free.
    /// </summary>
    public static class JkrCopilotFix
    {
        /// <summary>The families the protocol-3 engine can do with <c>set_parameters</c> (one parameter, a value per call, on
        /// element ids): type renames through the "Type Name" parameter (Da.1, Dc.1, Dd.1), the Material parameter (Db.2), mandatory
        /// parameters with a derivable value (Db.1), grid names (A5.2), room parameters (C1.6.3) and sheet parameters (C4.2.2).
        /// Dd.1 (WP-6): the type is renamed to a name the add-in derived (first word swapped to its category) only when the NAME is the evidence; a failing JKR code or a contradicting family gets no button (a person decides). NOT here, and hidden: A2.2 and A3.1 (the
        /// model code lives in the FILE name: a Save As), the create-type families C1.3.1 C1.4.1 C1.6.1 C2.1.1 C3.1.1
        /// C5.1.1 (create views, schedules, legends, links: run_revit_code; C4.1.3 and E.1 are playbook runs, WP-4/5) and the template views C1.1.1 / C1.1.2,
        /// C1.6.2, C5.1.2. The gaps are listed in the AI-490 rework PR.
        /// C1.5.2 (WP-3): one new 3D view per level without one, by the stored playbook create-3d-view-per-level through run_revit_code. A C1.5.2 row is one level
        /// (SpecChecks.C1_5_2 passes the Level's id and name), so it already has an element id and the usual rule applies; the prompt passes the level NAMES
        /// (<see cref="CopilotPromptVariant.LevelBased"/>). No <c>confirmed_write</c> field is sent for it.</summary>
        public static IReadOnlyCollection<string> Allowed => AllowedSet;
        private static readonly HashSet<string> AllowedSet = new HashSet<string>(StringComparer.Ordinal)
        {
            "Da.1", "Db.2", "Db.1", "Dc.1", "Dd.1", "A5.2", "C1.6.3", "C4.2.2", "C1.5.2",
            // Playbook runs (run_revit_code with a stored playbook, bina-ai data/copilot/playbooks): WP-4 C4.1.3 place a view on an empty sheet, WP-5 E.1 apply a view template.
            "C4.1.3", "E.1",
        };

        /// <summary>Never offered, whatever <see cref="Allowed"/> says: a person decides. Project Information (B1.1),
        /// Project Base Point (A4.1), geometry (A1.x, matched by prefix). AI-504 moves some of these to the Human
        /// class; this list keeps them out before that merges. Human / List rows, rules 21-24 and 26 (the checklist,
        /// not issue rows) and stubs have no id in <see cref="Allowed"/>.</summary>
        public static IReadOnlyCollection<string> Denied => DeniedSet;
        private static readonly HashSet<string> DeniedSet = new HashSet<string>(StringComparer.Ordinal)
        {
            "B1.1", "A4.1", "A6.2",
        };

        private const string GeometryPrefix = "A1.";

        /// <summary>The id may ever show the button: in the list, not denied, not geometry.</summary>
        public static bool IsListed(string specCheckId)
        {
            var id = (specCheckId ?? "").Trim();
            if (id.Length == 0) return false;
            if (DeniedSet.Contains(id) || id.StartsWith(GeometryPrefix, StringComparison.Ordinal)) return false;
            return AllowedSet.Contains(id);
        }

        /// <summary>The catalog calls the check AI-verifiable (not Human, not List). An unknown id is not.</summary>
        public static bool IsAiClass(string specCheckId)
        {
            var c = SpecCatalog.Find((specCheckId ?? "").Trim());
            return c != null && c.Class == SpecCellClass.Ai;
        }

        /// <summary>AI-457: Copilot cannot change wall materials.</summary>
        public static bool IsWallMaterial(string specCheckId, string revitCategory) =>
            (specCheckId ?? "").Trim() == "Db.2" && string.Equals((revitCategory ?? "").Trim(), "Walls", StringComparison.OrdinalIgnoreCase);

        /// <summary>The whole rule, on plain values (the table test drives this).</summary>
        public static bool Eligible(string specCheckId, string revitCategory, bool needsPerson, bool hasFix, bool isPick, int elementIdCount, string renameTo = "", string viewSet = "") =>
            !needsPerson && !hasFix && !isPick && elementIdCount >= 1
            && IsListed(specCheckId) && IsAiClass(specCheckId) && !IsWallMaterial(specCheckId, revitCategory)
            && IsDerivableRename(specCheckId, renameTo) && IsDerivableSet(specCheckId, viewSet);

        /// <summary>WP-6: Dd.1 shows the button only when the add-in derived the new name (<c>SpecChecks.ProposeRename</c>): the NAME is the
        /// odd one out, not the JKR code and not the family. Without it the family may really be of another category, a rename would hide
        /// that model error, and a person decides. True for every other check.</summary>
        public static bool IsDerivableRename(string specCheckId, string renameTo) =>
            (specCheckId ?? "").Trim() != "Dd.1" || !string.IsNullOrWhiteSpace(renameTo);

        /// <summary>E.1: the button shows only when the evaluator named the set (BOMBA, PBT or Dokumen) of the failing row ("required: template X"):
        /// without it Copilot would have to read the set from a view name, so a person decides. True for every other check.</summary>
        public static bool IsDerivableSet(string specCheckId, string viewSet) =>
            (specCheckId ?? "").Trim() != "E.1" || !string.IsNullOrWhiteSpace(viewSet);

        public static string Label() =>
            "Fix via Copilot";

        /// <summary>AI-509: the check header's button: "Fix all 46 elements via Copilot".</summary>
        public static string FixAllLabel(int elements) => $"Fix all {elements} element{(elements == 1 ? "" : "s")} via Copilot";

        public static string FixAllTooltip() => "Asks you to confirm once, then opens one new Copilot chat that applies the change to every element of this check (in parts of 50). The check is re-checked afterwards; you can Undo it from the Copilot chat. Da.1 / Dc.1 / Dd.1: a type rename changes all instances of the type.";

        /// <summary>What a click does: a confirm here first (nothing is sent on No), then a new Copilot chat that applies the change.</summary>
        public static string Tooltip() => "Asks you to confirm, then opens a new Copilot chat that applies the change to this item's elements. You can Undo it from the Copilot chat.";

        // ── the seam ──

        /// <summary>Set by the Copilot launcher (AI-494). Null = the button's click is a no-op.</summary>
        public static ICopilotFixLauncher Launcher { get; set; }

        /// <summary>A launcher is set: the button may show. Without one it would be a dead button, so rows hide it
        /// (and it is no control for Mark manual).</summary>
        public static bool LauncherReady => Launcher != null;

        /// <summary>Hand a row to the launcher; the caller checks <c>IssueVm.CopilotFixable</c> first. False when
        /// there is no launcher yet (the stub).</summary>
        public static bool Request(CopilotFixRequest request)
        {
            var l = Launcher;
            if (l == null || request == null) return false;
            l.Launch(request);
            return true;
        }
    }
}

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
    /// (<see cref="Allowed"/>); add an id there and the button follows, no UI change. Revit- and WPF-free.
    /// A row shows the button only when ALL hold (<see cref="Eligible"/>): the check is in the list and not
    /// in <see cref="Denied"/>, the catalog class is AI (never Human / List), no ready-made Fix, no picker,
    /// and at least one element id.
    /// </summary>
    public static class JkrCopilotFix
    {
        /// <summary>The v1 list (umbrella AI-490): derivable values, then create-type rows.</summary>
        public static IReadOnlyCollection<string> Allowed => AllowedSet;
        private static readonly HashSet<string> AllowedSet = new HashSet<string>(StringComparer.Ordinal)
        {
            // naming / code-in-name, material code, missing params, name vs geometry, model code, grids, title block
            "Da.1", "Dd.1", "Db.2", "Db.1", "Dc.1", "A2.2", "A3.1", "A5.2", "C1.6.3", "C4.2.2",
            // create-type rows: views, schedules, legends, sheets, links, template views
            "C1.3.1", "C1.4.1", "C1.5.2", "C1.6.1", "C2.1.1", "C3.1.1", "C4.1.3", "C5.1.1", "C1.1.1", "C1.1.2", "C5.1.2",
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
        public static bool Eligible(string specCheckId, string revitCategory, bool needsPerson, bool hasFix, bool isPick, int elementIdCount) =>
            !needsPerson && !hasFix && !isPick && elementIdCount >= 1
            && IsListed(specCheckId) && IsAiClass(specCheckId) && !IsWallMaterial(specCheckId, revitCategory);

        public static string Label(string lang = null) =>
            IsEn(lang) ? "Fix via Copilot" : "Baiki melalui Copilot";

        /// <summary>What a click does: a new Copilot chat, and nothing is changed until the person confirms.</summary>
        public static string Tooltip(string lang = null) => IsEn(lang)
            ? "Opens a new Copilot chat for this item. Copilot proposes the change and asks you to confirm before it writes anything."
            : "Buka sembang Copilot baharu untuk item ini. Copilot mencadangkan perubahan dan meminta anda sahkan sebelum menulis apa-apa.";

        private static bool IsEn(string lang) =>
            string.Equals(lang ?? PaneLanguage.Current, PanelStrings.En, StringComparison.OrdinalIgnoreCase);

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

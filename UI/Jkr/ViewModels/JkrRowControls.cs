namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>Where "Locate" goes for an issue that has no element id (AI-480).</summary>
    public enum JkrLocateTarget
    {
        /// <summary>The issue's own element (RevitElementId > 0).</summary>
        Element,
        /// <summary>Revit's Project Information dialog (Project Information rows, B1.1).</summary>
        ProjectInformation,
        /// <summary>Select the Project Base Point (A4.1). Never moves it.</summary>
        ProjectBasePoint,
        /// <summary>Revit's Manage Links dialog (C5.1.1 and other link-level rows).</summary>
        ManageLinks,
    }

    /// <summary>
    /// AI-480: every failing row has at least one control (Fix, AI, Pick, Locate or Mark manual).
    /// Revit-free rules, so a test can walk the rows. A row nobody can fix (judgement rows, model-level
    /// rows with no element id) may be Marked manual whatever its tier: the tier map hides Mark manual
    /// on High so a fixable row gets fixed, not deferred, but a High row with no fix would be a dead end.
    /// </summary>
    public static class JkrRowControls
    {
        /// <summary>Locate target for a row with no element id, from its category / spec check id.
        /// Check ids match exactly (B1.1, A4.1, C5.1.1, or with the "SPEC-" prefix), never by suffix: B1.10 is not B1.1.</summary>
        public static JkrLocateTarget LocateTargetOf(string category, string checkId)
        {
            var c = (category ?? "").Trim();
            var id = (checkId ?? "").Trim();
            if (id.StartsWith("SPEC-", System.StringComparison.Ordinal)) id = id.Substring(5);
            if (c.Equals("Project Information", System.StringComparison.OrdinalIgnoreCase) || id == "B1.1") return JkrLocateTarget.ProjectInformation;
            if (c.Equals("Project Base Point", System.StringComparison.OrdinalIgnoreCase) || id == "A4.1") return JkrLocateTarget.ProjectBasePoint;
            if (id == "C5.1.1") return JkrLocateTarget.ManageLinks;
            return JkrLocateTarget.Element;
        }

        /// <summary>The AI button is still a way forward: the row is an AI candidate and either the AI has not
        /// been asked yet or it answered with a proposal. Asked and nothing proposed = no control left from it.</summary>
        public static bool AiStillOffers(bool aiFixCandidate, bool aiAsked, bool hasProposal) =>
            aiFixCandidate && (!aiAsked || hasProposal);

        /// <summary>A row can be fixed or answered by a button other than Locate / Mark manual. A CopilotFixable row counts
        /// (<paramref name="copilotButton"/>) only when it really shows the "Fix via Copilot" button (AI-492).</summary>
        public static bool HasFixControl(bool autoFixable, bool aiFixCandidate, bool isPick, bool copilotButton = false) =>
            autoFixable || aiFixCandidate || isPick || copilotButton;

        /// <summary>Mark manual: the tier allows it (<see cref="JkrTierMap.CanMarkManual"/>), or the row has no fix control at all.</summary>
        public static bool CanMarkManual(bool tierAllows, bool hasFixControl) =>
            tierAllows || !hasFixControl;

        /// <summary>A failing row's controls, as the pane offers them.</summary>
        public static bool HasAnyControl(bool autoFixable, bool aiFixCandidate, bool isPick, bool locatable, bool canMarkManual, bool copilotButton = false) =>
            autoFixable || aiFixCandidate || isPick || locatable || canMarkManual || copilotButton;
    }
}

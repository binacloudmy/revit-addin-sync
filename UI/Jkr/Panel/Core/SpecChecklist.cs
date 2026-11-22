using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>A Borang section on the Checklist (A–E, or "+" for other JKR rules).</summary>
    public sealed class ChecklistSection
    {
        public string Key { get; set; } = "";
        public string Letter { get; set; } = "";
        public string Name { get; set; } = "";
        public string Summary { get; set; } = "";
        /// <summary>fail | pass | partial | person | listed | na | none.</summary>
        public string Tone { get; set; } = "none";
        public bool IsOpen { get; set; }
        public string Chevron => IsOpen ? "▾" : "▸";
        public List<ChecklistItem> Items { get; set; } = new List<ChecklistItem>();
    }

    /// <summary>A row of the Result screen's list: a Borang section (A–E) or an aspect
    /// (N / P / Q / G) — letter, name, %, one tick per check, "n fail" / "n manual".</summary>
    public sealed class GroupRow
    {
        /// <summary>The section letter, or the aspect letter.</summary>
        public string Key { get; set; } = "";
        public string Letter { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Spec checks in the group.</summary>
        public int Checks { get; set; }
        public int? Pct { get; set; }
        public string PctLabel { get; set; } = "—";
        /// <summary>Bar fill 0..1.</summary>
        public double Fraction { get; set; }
        /// <summary>One code per check, catalog order (<see cref="SpecChecklist.TickOf"/>).</summary>
        public List<string> Ticks { get; set; } = new List<string>();
        public int Fails { get; set; }
        /// <summary>Checks a person decides (manual review).</summary>
        public int Manual { get; set; }
        /// <summary>"n fail"; when none fail, "n manual"; else "".</summary>
        public string CountLabel { get; set; } = "";
        /// <summary>fail | person | "" — the count's colour.</summary>
        public string CountTone { get; set; } = "";
        public string Aria { get; set; } = "";
    }

    public sealed class ChecklistItem
    {
        public string No { get; set; } = "";
        public string Title { get; set; } = "";
        public List<ChecklistCheck> Checks { get; set; } = new List<ChecklistCheck>();
    }

    public sealed class ChecklistCheck
    {
        /// <summary>Spec check id ("Da.1"); for other rules, a key unique in the list.</summary>
        public string Id { get; set; } = "";
        public string ShownId { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>Reference · how it is judged.</summary>
        public string Meta { get; set; } = "";
        public string StateLabel { get; set; } = "";
        /// <summary>fail | pass | partial | person | listed | na | none.</summary>
        public string Tone { get; set; } = "none";
        /// <summary>Why it has this state, in one line ("2 of 5 results fail · 2 elements.").</summary>
        public string Detail { get; set; } = "";
        /// <summary>The run's remarks (what was found), one per line, in the pane's language.</summary>
        public string Notes { get; set; } = "";
        public bool HasNotes => !string.IsNullOrEmpty(Notes);
        /// <summary>"Spec — does not comply when:" / "— comply when:" / "— a person checks:".</summary>
        public string WhyLabel { get; set; } = "";
        /// <summary>The spec's criterion for this state (Comply, NotComply or Manual cell).</summary>
        public string WhyText { get; set; } = "";
        public bool HasWhy => !string.IsNullOrEmpty(WhyText);
        /// <summary>"Tested: rule; rule" — the rules that answered it.</summary>
        public string Tested { get; set; } = "";
        public bool HasTested => !string.IsNullOrEmpty(Tested);
        /// <summary>The spec criterion and what was tested are shown (toggle "Why").</summary>
        public bool WhyOpen { get; set; }
        public bool CanToggleWhy => HasWhy || HasTested || HasPending;
        /// <summary>The check's card is open (inside a section: the first failing check, or as toggled).</summary>
        public bool IsOpen { get; set; }
        public bool ShowWhy => WhyOpen && CanToggleWhy;
        public string WhyToggleLabel { get; set; } = "";
        public List<ChecklistElement> Elements { get; set; } = new List<ChecklistElement>();
        public bool HasElements => Elements.Count > 0;
        public string MoreLabel { get; set; } = "";
        public bool HasMore => !string.IsNullOrEmpty(MoreLabel);
        /// <summary>"Showing 500 of 812 — …" when "Show all" lists fewer elements than the
        /// check has (<see cref="SpecChecklist.ElementsMax"/>); "" when it lists them all.</summary>
        public string ElementsCutLabel { get; set; } = "";
        public bool HasElementsCut => !string.IsNullOrEmpty(ElementsCutLabel);
        /// <summary>A check a person decides that carries findings (A7.2's non-JKR
        /// materials): "3 amaran" / "3 warnings". "" when it has none.</summary>
        public string WarningLabel { get; set; } = "";
        public bool HasWarning => !string.IsNullOrEmpty(WarningLabel);
        /// <summary>The spec's note that the rule awaits JKR's confirmation (shown in
        /// "Why?" whatever the state, so the user knows the rule may change).</summary>
        public string PendingLabel { get; set; } = "";
        public string PendingText { get; set; } = "";
        public bool HasPending => !string.IsNullOrEmpty(PendingText);
        /// <summary>The run's table rows ("Floor plan | Aras 01 | WIP - Pelan Aras 01"),
        /// first <see cref="SpecChecklist.ListShown"/> unless "show all".</summary>
        public List<ChecklistListRow> ListRows { get; set; } = new List<ChecklistListRow>();
        public bool HasListRows => ListRows.Count > 0;
        public string ListMoreLabel { get; set; } = "";
        public bool HasListMore => !string.IsNullOrEmpty(ListMoreLabel);
        /// <summary>"Showing 500 of 812 rows." when "Show all" lists fewer rows than the run
        /// found (<see cref="SpecChecklist.ListMax"/>); "" when it lists them all.</summary>
        public string ListCutLabel { get; set; } = "";
        public bool HasListCut => !string.IsNullOrEmpty(ListCutLabel);
        /// <summary>Open issues Fix can act on (ready-made or AI-fixer candidates).</summary>
        public List<string> FixIds { get; set; } = new List<string>();
        public bool CanFix { get; set; }
        public string FixLabel { get; set; } = "";
        /// <summary>AI-509: the open rows "Fix all elements via Copilot" acts on (this check is one of the Copilot-fixable ones).</summary>
        public List<string> CopilotFixIds { get; set; } = new List<string>();
        /// <summary>How many elements those rows hold (distinct ids): the count on the button.</summary>
        public int CopilotFixElements { get; set; }
        public bool CanFixViaCopilot { get; set; }
        /// <summary>"Fix all 46 elements via Copilot" / "Baiki semua 46 elemen melalui Copilot".</summary>
        public string CopilotFixLabel { get; set; } = "";
        public string CopilotFixTooltip { get; set; } = "";
    }

    /// <summary>An issue behind a check: click opens its detail (Locate, Fix, Ignore).</summary>
    public sealed class ChecklistElement
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Tag { get; set; } = "";
        public bool HasTag => !string.IsNullOrEmpty(Tag);
    }

    /// <summary>One row of a check's list: the first cell, then the rest ("Aras 01 · WIP - Pelan Aras 01").</summary>
    public sealed class ChecklistListRow
    {
        public string Lead { get; set; } = "";
        public string Rest { get; set; } = "";
        public bool HasRest => !string.IsNullOrEmpty(Rest);
    }

    /// <summary>A state on the Checklist legend: its colour, label and count; click filters by it.</summary>
    public sealed class StateChip
    {
        /// <summary><see cref="SpecChecklist.AllStates"/> or a tone (fail | person | partial | pass | listed | na | none).</summary>
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public int N { get; set; }
        /// <summary>Same as Key; "all" has no colour.</summary>
        public string Tone { get; set; } = "";
        /// <summary>The state's tick code (<see cref="SpecChecklist.TickOfTone"/>); "" for "all".</summary>
        public string Tick => SpecChecklist.TickOfTone(Tone);
        public bool IsOn { get; set; }
        public bool IsEmpty => N == 0;
        public string Aria { get; set; } = "";
    }

    /// <summary>
    /// Builds the Checklist from a <see cref="SpecReport"/> and the pane's items, in
    /// the pane's language, and the spec-based headline (two numbers, rule R2).
    /// WPF-free; ResultsScreenModel keeps which sections are open, which checks show
    /// every element or their "why", and the state filter.
    /// </summary>
    public static class SpecChecklist
    {
        public const string OtherKey = "+";
        /// <summary>The state filter that shows every check.</summary>
        public const string AllStates = "all";
        /// <summary>Elements shown under a check before "Show all".</summary>
        public const int ElementsShown = 4;
        /// <summary>Most elements "Show all" lists, a guard for the pane's speed only: past
        /// it the check says so ("Showing 500 of 812 — the rest are on the issue tabs").</summary>
        public const int ElementsMax = 500;
        /// <summary>List rows shown under a check before "Show all".</summary>
        public const int ListShown = 5;
        /// <summary>Most list rows "Show all" lists (every remark of the run,
        /// <see cref="SpecCheckResult.AllNotes"/>); past it the check says so.</summary>
        public const int ListMax = 500;

        /// <summary>Legend / filter order: what needs work first, then what is settled.</summary>
        public static readonly string[] StateKeys = { "fail", "person", "partial", "pass", "listed", "na", "none" };

        // A key ending in "1" is the singular form.
        private static readonly Dictionary<string, string> S = new Dictionary<string, string>
        {
            ["tab"] = "Checklist",
            ["intro"] = "Every check of the JKR Model Audit spec, by Borang section. Click an element to see, fix or ignore it.",
            ["stAll"] = "All",
            ["stPass"] = "Pass",
            ["stFail"] = "Fails",
            ["stPartial"] = "Partly",
            // Clear Sheet names the state a person decides "Manual review".
            ["stPerson"] = "Manual review",
            ["stListed"] = "Listed",
            ["stNa"] = "Not applicable",
            ["stNone"] = "Not checked",
            ["clsHuman"] = "a person decides",
            ["clsList"] = "list only",
            ["dPass"] = "{0} results checked — all comply.",
            ["dPass1"] = "1 result checked — it complies.",
            ["dFail"] = "Failed: {0} of {1} results",
            ["dFailOnly"] = "Failed: 1 of 1 result",
            ["dFailEl"] = " · {0} elements",
            ["dFailEl1"] = " · 1 element",
            ["dPartial"] = "Only part of the rule is tested by the scan, and that part complies — check the rest by hand.",
            ["dHuman"] = "The spec leaves this to a person — not scored.",
            ["dWarn"] = "Warnings, not failures — a person judges each one. Not scored.",
            ["warn"] = "{0} warnings",
            ["warn1"] = "1 warning",
            ["secWarn"] = "{0} warnings",
            ["secWarn1"] = "1 warning",
            ["pendingTag"] = "awaiting JKR's confirmation",
            ["wPending"] = "Spec — awaiting JKR's confirmation:",
            ["dUnjudged"] = "{0} results the scan could not judge — check by hand.",
            ["dUnjudged1"] = "1 result the scan could not judge — check by hand.",
            ["dListed"] = "List only — no verdict.",
            ["dNa"] = "Does not apply to this model, as the spec defines — not scored.",
            ["dNone"] = "The scan has no rule for this yet — not checked.",
            ["dNoneList"] = "List only — the scan does not produce this list yet.",
            ["dNoneBackend"] = "Depends on the backend, which is unavailable — not checked.",
            ["wComply"] = "Spec — complies when:",
            ["wNotComply"] = "Spec — does not comply when:",
            ["wManual"] = "Spec — checked by hand:",
            ["wNote"] = "Spec — note:",
            // The toggle in a check's card (Clear Sheet "Why this fails"); a chevron shows open / closed.
            ["whyFail"] = "Why this fails",
            ["whyPass"] = "Why this passes",
            ["whyOther"] = "What the spec says",
            ["tested"] = "Tested: ",
            ["secFail"] = "{0} fail",
            ["secPass"] = "{0} pass",
            ["secPartial"] = "{0} partly",
            ["secPerson"] = "{0} person",
            ["secNa"] = "{0} not applicable",
            ["secNone"] = "{0} not checked",
            ["secListed"] = "{0} listed",
            ["other"] = "Other JKR rules (not in the Borang spec)",
            ["otherItem"] = "Scan rules with no spec check — shown, not scored.",
            ["oFail"] = "{0} of {1} fail.",
            ["oFail1"] = "{0} of {1} fails.",
            ["oUnjudged"] = "{0} could not be judged.",
            ["fix"] = "Fix {0}",
            ["showAll"] = "Show all {0}",
            ["showLess"] = "Show less",
            ["elCut"] = "Showing {0} of {1} — the rest are on the issue tabs (Open, Manual).",
            ["listCut"] = "Showing {0} of {1} rows.",
            ["notesCut"] = "Showing {0} of {1} remarks.",
            ["notesCutEl"] = "Showing {0} of {1} remarks — every element is listed below.",
            ["vFails"] = "Does not comply — {0} spec checks fail",
            ["vFails1"] = "Does not comply — 1 spec check fails",
            ["vNoFail"] = "No failures in the checks covered",
            ["vNothing"] = "No spec check verified yet",
            ["vCounts"] = "{0} pass · {1} partly · {2} need a person · {3} not checked — of {4} spec checks.",
            ["vOthers"] = " {0} other JKR rules also fail.",
            ["vOthers1"] = " 1 other JKR rule also fails.",
            ["others"] = "{0} other JKR rules (not in the spec) also fail — see below.",
            ["others1"] = "1 other JKR rule (not in the spec) also fails — see below.",
            // The % counts spec checks (AI-474): AI pass + List + Human over all checks. Two
            // short lines: the summary line beside "n checks need a person" holds about 30
            // characters before it is cut with "…".
            ["hResults"] = "{0} / {1} checks pass\n(List and Human count as pass)",
            ["hResults1"] = "{0} / {1} check passes\n(List and Human count as pass)",
            // Under the %: the AI checks the run could not judge (not passed), with the reason,
            // and the partly checked ones as their own state.
            ["hUnjudged"] = "{0} could not be checked — {1}",
            ["hPartial"] = "{0} partly checked",
            ["hWhy_backend"] = "backend unavailable",
            ["hWhy_mixed"] = "some: backend unavailable, the rest no rule yet or could not verify",
            ["hWhy_nothing"] = "nothing was judged",
            ["hWhy_rule"] = "no rule yet, or could not verify",
            ["notCounted"] = "Also reported by {0} — not counted: the add-in's own check already judged this one.",
            ["hPerson"] = "checks need a person",
            ["hPerson1"] = "check needs a person",
            ["backendDown"] = "Backend unavailable — checks that depend on it are not checked",
            ["filterEmpty"] = "No check has this state.",
            // Result screen list: "Borang sections | Aspects" and its rows (Clear Sheet).
            ["bySections"] = "Borang sections",
            ["byAspects"] = "Aspects",
            ["groupAria"] = "Group checks by",
            ["gManual"] = "{0} manual",
            ["gCompliant"] = "compliant",
            ["gFailAria"] = "{0} checks fail",
            ["gFailAria1"] = "1 check fails",
            ["gManualAria"] = "{0} checks wait for manual review",
            ["gManualAria1"] = "1 check waits for manual review",
            ["filterAria"] = "Filter by state: {0}",
            ["filterGroup"] = "Filter by state",
            ["keysAria"] = "Keyboard shortcuts",
            // Result head (Clear Sheet): verdict, its line, the score sentence.
            ["vHeadFail"] = "Does not comply",
            ["vSubFail"] = "{0} spec checks fail.",
            ["vSubFail1"] = "1 spec check fails.",
            ["vSubPerson"] = "Not a full pass yet: some checks are for a person to decide.",
            ["hPct"] = "{0} compliant",
            ["hRest"] = ", {0} of {1} checks pass. List and Human checks count as pass; unjudged AI checks do not.",
            ["hRest1"] = ", {0} of {1} check passes. List and Human checks count as pass; unjudged AI checks do not.",
            ["hRestNone"] = ", no check to score.",
            // The issue view's one-line verdict: "18% compliant · 22 fail".
            ["vShort"] = "{0} compliant · {1} fail",
            ["tabIssues"] = "Issues",
            // The Issues tab while a section filters it, and the link to it from inside a section.
            ["tabIssuesSec"] = "Issues · Section {0}",
            ["secIssues"] = "{0} issues in this section",
            ["secIssues1"] = "1 issue in this section",
            // The Issues list (Clear Sheet "issues"): toolbar and rows.
            ["stateTabs"] = "Issue state",
            ["searchIssues"] = "Search issues",
            ["progResolved"] = "{0} of {1} resolved",
            // AI-442: the unit is the change, as in the progress line, toast and result window.
            ["fixAllN"] = "Fix all {0} changes",
            ["fixAllN1"] = "Fix all {0} change",
            // The AI fixer mark: "proposal" only when a proposed value exists on the row.
            ["aiProposal"] = "AI fixer proposal",
            ["aiCanMeta"] = "AI fixer can propose",
            ["noFix"] = "No ready-made fix. Needs manual action.",
            ["noFixReads"] = "Still reads “{0}”. Needs manual action.",
            // An AI fixer candidate before the fixer has proposed anything: no value is claimed.
            ["aiCan"] = "No ready-made fix. The AI fixer can propose one.",
            ["aiCanReads"] = "Still reads “{0}”. The AI fixer can propose a fix.",
            // A section / aspect opened from the list.
            ["posSection"] = "Section {0} of {1}",
            ["posAspect"] = "Aspect {0} of {1}",
            ["allChecks"] = "All checks",
            ["otherName"] = "Other JKR rules",
            ["otherNote"] = "not in the Borang spec",
            ["aspectsNote"] = "Aspects regroup the same {0} checks by what they test: naming, parameters and information, quality and integrity, and design geometry.",
            // The split banner on a result that is out of date or missing the backend's checks.
            ["lodStrong"] = "This result is for LOD {0}.",
            ["lodRest"] = "Re-scan to check the model against LOD {0}.",
            ["partialStrong"] = "Partial · incomplete: {0} source(s) missing ({1}). This board is not final.",
            ["beStrong"] = "BINA backend unavailable.",
            ["beOne"] = "{0} needs it, so it shows as not checked, never as passed.",
            ["beMany"] = "{0} need it, so they show as not checked, never as passed.",
            ["beNone"] = "Checks that need it show as not checked, never as passed.",
        };


        public static string Str(string key) =>
            S.TryGetValue(key, out var p) ? p : key;

        private static string F(string key, params object[] args) => string.Format(Str(key), args);

        /// <summary>Count-dependent wording: the English singular (key + "1") when n == 1.</summary>
        public static string Plural(string key, int n, params object[] args)
        {
            var k = n == 1 && S.ContainsKey(key + "1") ? key + "1" : key;
            return string.Format(Str(k), args.Length == 0 ? new object[] { n } : args);
        }

        // ─── states ───

        /// <summary>The colour / filter key of a state.</summary>
        public static string ToneOf(SpecState s)
        {
            switch (s)
            {
                case SpecState.Pass: return "pass";
                case SpecState.Fail: return "fail";
                case SpecState.Partial: return "partial";
                case SpecState.NeedsPerson: return "person";
                case SpecState.Listed: return "listed";
                case SpecState.NotApplicable: return "na";
                default: return "none";
            }
        }

        public static string StateLabel(string tone)
        {
            switch (tone)
            {
                case "pass": return Str("stPass");
                case "fail": return Str("stFail");
                case "partial": return Str("stPartial");
                case "person": return Str("stPerson");
                case "listed": return Str("stListed");
                case "na": return Str("stNa");
                case AllStates: return Str("stAll");
                default: return Str("stNone");
            }
        }

        public static string StateLabel(SpecState s) => StateLabel(ToneOf(s));

        /// <summary>The legend: All + every state with its count of spec checks; the one filtered on is on.</summary>
        public static List<StateChip> StateChips(SpecReport r, string filter) => StateChips(r, filter, null);

        /// <summary>The legend counted over the checks <paramref name="member"/> picks (a section,
        /// an aspect; null = every check).</summary>
        public static List<StateChip> StateChips(SpecReport r, string filter, Func<SpecCheck, bool> member)
        {
            var list = new List<StateChip>();
            if (r == null) return list;
            var checks = member == null ? r.Checks : r.Checks.Where(c => member(c.Check)).ToList();
            var on = string.IsNullOrEmpty(filter) ? AllStates : filter;
            list.Add(new StateChip
            {
                Key = AllStates, Tone = AllStates, Label = Str("stAll"), N = checks.Count, IsOn = on == AllStates,
                Aria = F("filterAria", Str("stAll")) + " · " + checks.Count,
            });
            foreach (var k in StateKeys)
            {
                var n = checks.Count(c => ToneOf(c.State) == k);
                list.Add(new StateChip
                {
                    Key = k, Tone = k, Label = StateLabel(k), N = n, IsOn = on == k,
                    Aria = F("filterAria", StateLabel(k)) + " · " + n,
                });
            }
            return list;
        }

        // ─── headline: two numbers (DESIGN-BRIEF R2, spec "Kiraan %"; AI-474) ───

        /// <summary>
        /// The headline tally over a set of spec checks, counted per CHECK (a Borang row):
        /// List and Human checks count as passed (Ashraf, overrides spec Q2); an AI check
        /// passes only in state Pass; Fail, Partial and unjudged AI checks do not pass but
        /// stay in the denominator. Not-applicable checks are in neither. So the % is
        /// <c>(AI pass + List + Human) / checks</c>, never "—" for a run that exists.
        /// </summary>
        public sealed class Tally
        {
            /// <summary>AI checks that passed (state Pass).</summary>
            public int AiPass;
            /// <summary>List-only and Human-decides checks, counted as passed.</summary>
            public int ListHuman;
            /// <summary>AI checks with a failing result.</summary>
            public int Fail;
            /// <summary>AI checks only partly covered (FailOnly rule passed): their own state, not passed.</summary>
            public int Partial;
            /// <summary>AI checks the scan did not judge (no rule yet, backend down, could not verify): not passed.</summary>
            public int Unjudged;
            /// <summary>Checks in the % (not-applicable ones left out).</summary>
            public int Total;
            /// <summary>Why the unjudged ones are unjudged: backend (all of them) | mixed (some) | nothing | rule | "" (none unjudged).</summary>
            public string UnjudgedReason = "";
            public int Passed => AiPass + ListHuman;
            /// <summary>AI checks that got a verdict (pass or fail) or a partial one.</summary>
            public int Judged => AiPass + Fail + Partial;
        }

        public const string ReasonBackend = "backend", ReasonMixed = "mixed", ReasonNothing = "nothing", ReasonRule = "rule";

        public static Tally TallyOf(SpecReport r, string section = null) => TallyOf(r, InSection(section));

        public static Tally TallyOf(SpecReport r, Func<SpecCheck, bool> member)
        {
            var t = new Tally();
            if (r == null) return t;
            int backendBlocked = 0;
            foreach (var c in r.Checks)
            {
                if (!member(c.Check) || c.State == SpecState.NotApplicable) continue;
                t.Total++;
                if (c.Check.Class != SpecCellClass.Ai) { t.ListHuman++; continue; }
                switch (c.State)
                {
                    case SpecState.Pass: t.AiPass++; break;
                    case SpecState.Fail: t.Fail++; break;
                    case SpecState.Partial: t.Partial++; break;
                    // AI-504: nothing was judged, and all that was found is for a person to decide
                    // (never a verdict): it counts as passed, as a Human check does (AI-474).
                    case SpecState.NeedsPerson when c.PersonRows > 0 && c.Unjudged == 0: t.ListHuman++; break;
                    default:
                        t.Unjudged++;
                        if (r.BackendUnavailable && SpecReport.BackendMappedChecks.Contains(c.Check.Id)) backendBlocked++;
                        break;
                }
            }
            if (t.Unjudged > 0)
                t.UnjudgedReason = backendBlocked == t.Unjudged ? ReasonBackend : backendBlocked > 0 ? ReasonMixed : t.Judged == 0 ? ReasonNothing : ReasonRule;
            return t;
        }

        /// <summary>
        /// Checks that passed and checks in the %, in one section or all (null): AI checks in
        /// state Pass, plus every List and Human check; the total is all checks that apply.
        /// </summary>
        public static (int Passed, int Total) Score(SpecReport r, string section = null) =>
            Score(r, InSection(section));

        /// <summary>Score over the checks <paramref name="member"/> picks (a section, an aspect).</summary>
        public static (int Passed, int Total) Score(SpecReport r, Func<SpecCheck, bool> member)
        {
            var t = TallyOf(r, member);
            return (t.Passed, t.Total);
        }

        /// <summary>Score as a whole %; null only without a report (or a set with no applicable check).</summary>
        public static int? Pct(SpecReport r, string section = null) => Pct(r, InSection(section));

        public static int? Pct(SpecReport r, Func<SpecCheck, bool> member)
        {
            var (p, t) = Score(r, member);
            return t == 0 ? (int?)null : (int)Math.Round(100.0 * p / t, MidpointRounding.AwayFromZero);
        }

        /// <summary>"4 could not be checked — no rule yet" under the %, or "" when every AI check was judged.</summary>
        public static string UnjudgedLine(SpecReport r, string section = null)
        {
            var t = TallyOf(r, section);
            if (t.Unjudged == 0) return "";
            return F("hUnjudged", t.Unjudged, Str("hWhy_" + t.UnjudgedReason));
        }

        /// <summary>"12 separa" — AI checks partly covered, their own state (not passed); "" when none.</summary>
        public static string PartialLine(SpecReport r, string section = null)
        {
            var t = TallyOf(r, section);
            return t.Partial == 0 ? "" : F("hPartial", t.Partial);
        }

        /// <summary>One section, or every check (null / PanelCore.AllSections).</summary>
        private static Func<SpecCheck, bool> InSection(string section) =>
            string.IsNullOrEmpty(section) || section == PanelCore.AllSections
                ? (Func<SpecCheck, bool>)(c => true)
                : c => c.Section == section;

        // ─── group rows: a Borang section or an aspect (the Result screen's list) ───

        /// <summary>The tick code of a state (Clear Sheet's tick strip): f fail, p pass,
        /// h partly, m manual review, l listed, n not applicable, x not checked.</summary>
        public static string TickOf(SpecState s)
        {
            switch (s)
            {
                case SpecState.Fail: return "f";
                case SpecState.Pass: return "p";
                case SpecState.Partial: return "h";
                case SpecState.NeedsPerson: return "m";
                case SpecState.Listed: return "l";
                case SpecState.NotApplicable: return "n";
                default: return "x";
            }
        }

        /// <summary>The tick code of a tone (fail → f …); "" for "all" or an unknown tone.</summary>
        public static string TickOfTone(string tone)
        {
            switch (tone)
            {
                case "fail": return "f";
                case "pass": return "p";
                case "partial": return "h";
                case "person": return "m";
                case "listed": return "l";
                case "na": return "n";
                case "none": return "x";
                default: return "";
            }
        }

        /// <summary>
        /// One row of the Result screen's list, for the checks <paramref name="member"/>
        /// picks: % as the headline counts it (<see cref="Pct(SpecReport, Func{SpecCheck, bool})"/>),
        /// one tick per check in catalog order, and "n fail" — or, when none fail,
        /// "n manual". Section rows and aspect rows are both built here, the same way.
        /// </summary>
        public static GroupRow Group(SpecReport r, string key, string letter, string name, Func<SpecCheck, bool> member)
        {
            var checks = r == null ? new List<SpecCheckResult>()
                : SpecCatalog.Checks.Where(member).Select(c => r.For(c.Id)).Where(c => c != null).ToList();
            var pct = Pct(r, member);
            var fails = checks.Count(c => c.State == SpecState.Fail);
            var manual = checks.Count(c => c.State == SpecState.NeedsPerson);
            var pctLabel = pct.HasValue ? pct.Value + "%" : "—";
            string count = "", tone = "", aria = name + ", " + pctLabel + " " + Str("gCompliant");
            if (fails > 0) { count = F("secFail", fails); tone = "fail"; aria += ", " + Plural("gFailAria", fails); }
            else if (manual > 0) { count = F("gManual", manual); tone = "person"; aria += ", " + Plural("gManualAria", manual); }
            return new GroupRow
            {
                Key = key, Letter = letter, Name = name, Checks = checks.Count,
                Pct = pct, PctLabel = pctLabel, Fraction = (pct ?? 0) / 100.0,
                Ticks = checks.Select(c => TickOf(c.State)).ToList(),
                Fails = fails, Manual = manual, CountLabel = count, CountTone = tone, Aria = aria,
            };
        }

        /// <summary>The Borang section rows A–E (the "Borang sections" view).</summary>
        public static List<GroupRow> SectionRows(SpecReport r) =>
            r == null ? new List<GroupRow>()
                : PanelCore.Secs.Select(s => Group(r, s, s, SectionName(s), c => c.Section == s)).ToList();

        /// <summary>Checks a person decides, in one section or all (null).</summary>
        public static int NeedsPerson(SpecReport r, string section = null) =>
            r?.Checks.Count(c => c.State == SpecState.NeedsPerson
                                 && (string.IsNullOrEmpty(section) || section == PanelCore.AllSections || c.Check.Section == section)) ?? 0;

        public static string ResultsLabel(SpecReport r)
        {
            var (p, t) = Score(r);
            return Plural("hResults", t, p, t);
        }

        /// <summary>"Also reported by backend: Naming convention — not counted: …" for the
        /// backend results the add-in's own evaluator outranked
        /// (<see cref="SpecCheckResult.NotCounted"/>), or "".</summary>
        public static string NotCountedLine(SpecCheckResult r)
        {
            var list = r?.NotCounted ?? new List<string>();
            if (list.Count == 0) return "";
            return F("notCounted", string.Join("; ", list.Take(3)) + (list.Count > 3 ? "; …" : ""));
        }

        public static string NeedPersonLabel(SpecReport r) => Plural("hPerson", NeedsPerson(r), NeedsPerson(r));

        public static string BackendBanner() => Str("backendDown");

        /// <summary>"B1.2 needs it, so it shows as not checked, never as passed." for the checks
        /// only the backend answers that this run could not check (none: the general line).</summary>
        public static string BackendNeedsLine(IEnumerable<string> ids)
        {
            var list = (ids ?? Enumerable.Empty<string>()).Where(i => !string.IsNullOrEmpty(i)).ToList();
            if (list.Count == 0) return Str("beNone");
            return F(list.Count == 1 ? "beOne" : "beMany", string.Join(", ", list));
        }

        /// <summary>Checks only the backend answers that stay "not checked" because it was unavailable.</summary>
        public static List<string> BackendNotChecked(SpecReport r) =>
            r == null || !r.BackendUnavailable ? new List<string>()
                : r.Checks.Where(c => c.State == SpecState.NotChecked && SpecReport.BackendMappedChecks.Contains(c.Check.Id))
                          .Select(c => c.Check.Id).ToList();

        // ─── verdict ───

        public static string VerdictTitle(SpecReport r)
        {
            if (r == null) return "";
            switch (r.Verdict)
            {
                case SpecVerdict.Fails: return Plural("vFails", r.FailChecks);
                case SpecVerdict.NoFailures: return Str("vNoFail");
                default: return Str("vNothing");
            }
        }

        /// <summary>fail | pass | none — the verdict's colour.</summary>
        public static string VerdictTone(SpecReport r) =>
            r == null ? "none" : r.Verdict == SpecVerdict.Fails ? "fail" : r.Verdict == SpecVerdict.NoFailures ? "pass" : "none";

        public static string VerdictText(SpecReport r)
        {
            if (r == null) return "";
            var s = F("vCounts", r.PassChecks, r.PartialChecks, r.NeedsPersonChecks, r.NotCheckedChecks + r.ListedChecks, r.Checks.Count);
            if (r.OtherFailing > 0) s += Plural("vOthers", r.OtherFailing);
            return s;
        }

        /// <summary>"4 other JKR rules (not in the spec) also fail — see below." or "".</summary>
        public static string OthersNote(SpecReport r) =>
            r == null || r.OtherFailing == 0 ? "" : Plural("others", r.OtherFailing);

        // ─── checklist ───

        /// <param name="items">The pane's items (status, element label, fix) by id.</param>
        /// <param name="section">Only this section (PanelCore.AllSections = all, with other rules).</param>
        /// <param name="isOpen">Whether a section is expanded (null = the default: open when it fails,
        /// when it is the only section shown, or when a state filter is on).</param>
        /// <param name="showAll">Whether a check lists every element.</param>
        /// <param name="stateFilter">Only checks in this state (a tone; null / "all" = every check).
        /// Other JKR rules are not spec checks and are left out while a filter is on.</param>
        /// <param name="whyOpen">Whether a check shows its spec criterion (null = the default: open when it fails).</param>
        public static List<ChecklistSection> Build(SpecReport report, IEnumerable<PanelItem> items, string section, Func<string, bool?> isOpen, Func<string, bool> showAll, string stateFilter = null, Func<string, bool?> whyOpen = null, Func<SpecCheck, bool> member = null)
        {
            var list = new List<ChecklistSection>();
            if (report == null) return list;
            // Only the other JKR rules (their row on the Result list was opened).
            var onlyOthers = section == OtherKey;
            var byId = new Dictionary<string, PanelItem>();
            foreach (var i in items ?? Enumerable.Empty<PanelItem>())
                if (!string.IsNullOrEmpty(i?.Id) && !byId.ContainsKey(i.Id)) byId[i.Id] = i;
            var all = section == null || section == PanelCore.AllSections;
            var filtered = !string.IsNullOrEmpty(stateFilter) && stateFilter != AllStates;
            bool Keep(SpecCheckResult r) => !filtered || ToneOf(r.State) == stateFilter;

            foreach (var sec in PanelCore.Secs)
            {
                if (onlyOthers || (!all && sec != section)) continue;
                var inSec = report.Checks.Where(x => x.Check.Section == sec && (member == null || member(x.Check))).ToList();
                if (member != null && inSec.Count == 0) continue;
                if (filtered && !inSec.Any(Keep)) continue;
                int N(SpecState s) => inSec.Count(x => x.State == s);
                int fail = N(SpecState.Fail), pass = N(SpecState.Pass), partial = N(SpecState.Partial),
                    person = N(SpecState.NeedsPerson), na = N(SpecState.NotApplicable),
                    none = N(SpecState.NotChecked), listed = N(SpecState.Listed);
                var warnings = inSec.Sum(WarningCount);
                var vm = new ChecklistSection
                {
                    Key = sec,
                    Letter = sec,
                    Name = SectionName(sec),
                    Summary = Join(
                        fail > 0 ? F("secFail", fail) : null,
                        pass > 0 ? F("secPass", pass) : null,
                        partial > 0 ? F("secPartial", partial) : null,
                        person > 0 ? F("secPerson", person) : null,
                        warnings > 0 ? Plural("secWarn", warnings) : null,
                        na > 0 ? F("secNa", na) : null,
                        listed > 0 ? F("secListed", listed) : null,
                        none > 0 ? F("secNone", none) : null),
                    Tone = fail > 0 ? "fail" : pass > 0 ? "pass" : partial > 0 ? "partial" : person > 0 ? "person" : "none",
                };
                vm.IsOpen = isOpen?.Invoke(sec) ?? (fail > 0 || !all || filtered);
                if (vm.IsOpen)
                {
                    foreach (var it in SpecCatalog.ItemsOf(sec))
                    {
                        var ivm = new ChecklistItem { No = it.No, Title = it.Title.En };
                        foreach (var check in SpecCatalog.ChecksOf(it.No))
                        {
                            var r = report.For(check.Id);
                            if (r == null || !Keep(r) || (member != null && !member(check))) continue;
                            ivm.Checks.Add(CheckRow(r, report, byId, showAll, whyOpen));
                        }
                        if (ivm.Checks.Count > 0) vm.Items.Add(ivm);
                    }
                }
                list.Add(vm);
            }

            if ((onlyOthers || (all && !filtered && member == null)) && report.Others.Count > 0)
            {
                var failing = report.Others.Count(o => o.State == SpecState.Fail);
                var passing = report.Others.Count(o => o.State == SpecState.Pass);
                var other = new ChecklistSection
                {
                    Key = OtherKey,
                    Letter = "+",
                    Name = Str("other"),
                    Summary = Join(
                        failing > 0 ? F("secFail", failing) : null,
                        passing > 0 ? F("secPass", passing) : null,
                        report.Others.Count - failing - passing > 0 ? F("secPerson", report.Others.Count - failing - passing) : null),
                    Tone = failing > 0 ? "fail" : "pass",
                };
                other.IsOpen = isOpen?.Invoke(OtherKey) ?? onlyOthers;
                if (other.IsOpen)
                {
                    var ivm = new ChecklistItem { No = "", Title = Str("otherItem") };
                    var n = 0;
                    foreach (var o in report.Others) ivm.Checks.Add(OtherRow(o, "other-" + n++, byId, showAll));
                    other.Items.Add(ivm);
                }
                list.Add(other);
            }
            return list;
        }

        /// <summary>The checklist row of one spec check (state, why, notes, elements).</summary>
        public static ChecklistCheck CheckRow(SpecCheckResult r, SpecReport report, Dictionary<string, PanelItem> byId, Func<string, bool> showAll, Func<string, bool?> whyOpen = null)
        {
            var c = r.Check;
            var pending = IsPendingJkr(c);
            var row = new ChecklistCheck
            {
                Id = c.Id,
                ShownId = c.Id,
                Title = c.Title.En,
                Meta = Meta(c),
                Tone = ToneOf(r.State),
                StateLabel = StateLabel(r.State),
            };
            row.Detail = DetailOf(r, report);
            var (label, text) = Criterion(r);
            row.WhyLabel = label;
            row.WhyText = text;
            if (pending)
            {
                // Shown whatever the state, unless the criterion already is that note.
                var note = c.Manual.En.Replace("`", "");
                if (note != row.WhyText) { row.PendingLabel = Str("wPending"); row.PendingText = note; }
            }

            var warnings = WarningCount(r);
            if (warnings > 0) row.WarningLabel = Plural("warn", warnings);

            if (r.State != SpecState.NotChecked && r.Rules.Count > 0)
                row.Tested = Str("tested") + string.Join("; ", r.Rules.Take(3));
            var notCounted = NotCountedLine(r);
            if (notCounted.Length > 0) row.Tested = string.IsNullOrEmpty(row.Tested) ? notCounted : row.Tested + "\n" + notCounted;
            // Closed until asked for: the check's line already says why in one sentence.
            row.WhyOpen = whyOpen?.Invoke(c.Id) ?? false;
            row.WhyToggleLabel = Str(r.State == SpecState.Fail ? "whyFail" : r.State == SpecState.Pass ? "whyPass" : "whyOther");

            // Every remark of the run (not only the Borang's first few), both languages side
            // by side; an element row that shows one takes it (in the pane's language), so it
            // is printed once, and each row of a check a person decides finds its own.
            var notes = NotePairs(r);
            var taken = new HashSet<int>();
            var elements = AddElements(row, r.IssueIds, byId ?? new Dictionary<string, PanelItem>(), showAll, notes, taken);
            var left = notes.Where((n, i) => !taken.Contains(i)).Select(n => n.En)
                            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            // A list-only check's remarks are its list: all of them. Any other check shows its
            // first few remarks (the element rows carry the rest), and says when it holds more.
            var cut = c.Class == SpecCellClass.List ? left.Count : Math.Min(left.Count, SpecReport.MaxNotes);
            SplitList(row, left.Take(cut).ToList(), showAll?.Invoke(c.Id) == true);
            if (cut < left.Count)
            {
                // Under the remarks: after their rows when they read as a table, else as a last line.
                var line = F(elements >= left.Count ? "notesCutEl" : "notesCut", cut, left.Count);
                if (row.HasListRows) row.ListCutLabel = line;
                else row.Notes = string.IsNullOrEmpty(row.Notes) ? line : row.Notes + "\n" + line;
            }
            return row;
        }

        /// <summary>Findings of a human-decides check whose spec note calls them a WARNING
        /// (A7.2's non-JKR materials): warnings for a person. 0 for any other check — a
        /// human-decides check whose findings are information only (A6.3: an empty
        /// Department) still lists them, without the warning label.</summary>
        public static int WarningCount(SpecCheckResult r) =>
            r != null && r.Check.Class == SpecCellClass.Human
            && (r.Check.Manual?.Ms ?? "").IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) >= 0
                ? r.IssueIds.Count : 0;

        /// <summary>The spec's Manual / note cell says the rule awaits JKR's confirmation.</summary>
        public static bool IsPendingJkr(SpecCheck c) =>
            c != null && !SpecCatalog.IsEmpty(c.Manual)
            && c.Manual.Ms.IndexOf("menunggu pengesahan JKR", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// "Reference · class · awaiting JKR". The reference is left out when the title
        /// already names it ("Ketinggian siling (NS §4.1.9)" + "NS §4.1.9").
        /// </summary>
        public static string Meta(SpecCheck c) => Join(
            TitleHasReference(c.Title.Ms, c.Reference) || TitleHasReference(c.Title.En, c.Reference) ? null : SpecCatalog.ShownReference(c.Reference),
            c.Class == SpecCellClass.Human ? Str("clsHuman") : c.Class == SpecCellClass.List ? Str("clsList") : null,
            IsPendingJkr(c) ? Str("pendingTag") : null);

        private static readonly System.Text.RegularExpressions.Regex RefNumber =
            new System.Text.RegularExpressions.Regex(@"\d+(?:[.:]\d+)+", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The title holds the reference: as written, or its document word and every
        /// clause number ("(NS §5.8.1/5.8.3)" holds "NS §5.8.1, §5.8.3").</summary>
        public static bool TitleHasReference(string title, string reference)
        {
            title = title ?? "";
            var rf = (reference ?? "").Trim();
            if (rf.Length == 0 || title.Length == 0) return false;
            if (title.IndexOf(rf, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var nums = RefNumber.Matches(rf).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).ToList();
            if (nums.Count == 0) return false;
            var doc = rf.Split(' ')[0];
            return doc.Length > 0 && !char.IsDigit(doc[0]) && title.IndexOf(doc, StringComparison.Ordinal) >= 0
                   && nums.All(n => title.Contains(n));
        }

        /// <summary>Every remark of the result as BM / EN pairs (English = BM when it has
        /// none): <see cref="SpecCheckResult.AllNotes"/>, else the capped Notes (a result
        /// built by hand).</summary>
        private static List<Text> NotePairs(SpecCheckResult r)
        {
            var all = r.AllNotes != null && r.AllNotes.Count > 0 && r.AllNotes.Count >= (r.Notes?.Count ?? 0);
            var ms = (all ? r.AllNotes : r.Notes) ?? new List<string>();
            var enList = all ? r.AllNotesEn : r.NotesEn;
            var en = enList != null && enList.Count == ms.Count ? enList : ms;
            return ms.Select((m, i) => new Text(m, string.IsNullOrWhiteSpace(en[i]) ? m : en[i])).ToList();
        }

        /// <summary>
        /// The remarks under a check: table rows ("a | b | c") become list rows, the
        /// rest stays as notes; a long list of plain lines keeps its first line as the
        /// note and lists the others. Lists are collapsed to <see cref="ListShown"/> rows.
        /// </summary>
        private static void SplitList(ChecklistCheck row, List<string> notes, bool all)
        {
            var plain = new List<string>();
            var rows = new List<ChecklistListRow>();
            foreach (var n in notes)
            {
                if (n.Contains(" | "))
                {
                    var cells = n.Split(new[] { " | " }, StringSplitOptions.None).Select(x => x.Trim()).ToList();
                    rows.Add(new ChecklistListRow { Lead = cells[0], Rest = string.Join(" · ", cells.Skip(1).Where(x => x.Length > 0)) });
                }
                else plain.Add(n);
            }
            if (plain.Count > ListShown + 1)
            {
                rows.InsertRange(0, plain.Skip(1).Select(p => new ChecklistListRow { Lead = p }));
                plain = plain.Take(1).ToList();
            }
            row.Notes = string.Join("\n", plain);
            row.ListRows = all ? rows.Take(ListMax).ToList() : rows.Take(ListShown).ToList();
            if (rows.Count > ListShown)
                row.ListMoreLabel = all ? Str("showLess") : F("showAll", rows.Count);
            // "Show all" never lists fewer rows than the run found without saying so.
            if (all && rows.Count > ListMax) row.ListCutLabel = F("listCut", ListMax, rows.Count);
        }

        /// <summary>Why the check has its state, in one line.</summary>
        public static string DetailOf(SpecCheckResult r, SpecReport report)
        {
            var c = r.Check;
            switch (r.State)
            {
                case SpecState.Pass:
                    return Plural("dPass", r.Passed);
                case SpecState.Fail:
                {
                    var total = r.Passed + r.Failed + r.Unjudged;
                    return (total == 1 ? Str("dFailOnly") : Plural("dFail", r.Failed, r.Failed, total))
                           + (r.Elements > 0 ? Plural("dFailEl", r.Elements) : "") + ".";
                }
                case SpecState.Partial:
                    return Str("dPartial");
                case SpecState.NeedsPerson:
                    if (WarningCount(r) > 0) return Str("dWarn");
                    return c.Class == SpecCellClass.Human ? Str("dHuman") : Plural("dUnjudged", r.Unjudged);
                case SpecState.Listed:
                    return Str("dListed");
                case SpecState.NotApplicable:
                    return Str("dNa");
                default:
                    if (c.Class == SpecCellClass.List) return Str("dNoneList");
                    return report != null && report.BackendUnavailable ? Str("dNoneBackend") : Str("dNone");
            }
        }

        /// <summary>
        /// The spec's own words for the state: the Comply cell when it passed, the
        /// NotComply cell when it failed, the Manual note when a person decides or the
        /// check is only listed; partly / not checked show what complying means. A
        /// "—" cell falls back to the next one that says something.
        /// </summary>
        public static (string Label, string Text) Criterion(SpecCheckResult r)
        {
            var c = r.Check;
            // The spec marks columns and fields with backticks (`Type | lebar | tinggi`): shown without them.
            (string, string) Of(string key, Text t) => SpecCatalog.IsEmpty(t) ? (null, null) : (Str(key), t.En.Replace("`", ""));
            var comply = Of("wComply", c.Comply);
            var notComply = Of("wNotComply", c.NotComply);
            var manual = Of(c.Class == SpecCellClass.Ai ? "wNote" : "wManual", c.Manual);
            IEnumerable<(string, string)> order;
            switch (r.State)
            {
                case SpecState.Fail: order = new[] { notComply, comply }; break;
                case SpecState.Pass: order = new[] { comply }; break;
                case SpecState.NeedsPerson:
                    order = c.Class == SpecCellClass.Human ? new[] { manual, comply } : new[] { manual, comply, notComply };
                    break;
                case SpecState.Listed:
                case SpecState.NotApplicable: order = new[] { manual, comply }; break;
                default: order = c.Class == SpecCellClass.List ? new[] { manual } : new[] { comply, manual }; break;
            }
            foreach (var o in order) if (o.Item2 != null) return o;
            return ("", "");
        }

        private static ChecklistCheck OtherRow(OtherResult o, string key, Dictionary<string, PanelItem> byId, Func<string, bool> showAll)
        {
            var total = o.Passed + o.Failed + o.Unjudged;
            var row = new ChecklistCheck { Id = key, ShownId = "", Title = o.Rule, Meta = o.Where };
            if (o.State == SpecState.Fail) { row.Tone = "fail"; row.StateLabel = Str("stFail"); row.Detail = Plural("oFail", o.Failed, o.Failed, total); }
            else if (o.State == SpecState.NeedsPerson) { row.Tone = "person"; row.StateLabel = Str("stPerson"); row.Detail = F("oUnjudged", o.Unjudged); }
            else { row.Tone = "pass"; row.StateLabel = Str("stPass"); row.Detail = Plural("dPass", o.Passed); }
            AddElements(row, o.IssueIds, byId, showAll);
            return row;
        }

        /// <returns>How many elements the check has (shown or not).</returns>
        private static int AddElements(ChecklistCheck row, List<string> ids, Dictionary<string, PanelItem> byId, Func<string, bool> showAll, List<Text> notes = null, HashSet<int> taken = null)
        {
            var items = (ids ?? new List<string>()).Where(byId.ContainsKey).Select(id => byId[id]).Where(x => !x.Passed).Distinct().ToList();
            var all = showAll?.Invoke(row.Id) == true;
            var shown = all ? Math.Min(items.Count, ElementsMax) : Math.Min(items.Count, ElementsShown);
            row.Elements = items.Take(shown).Select(x => ElementRow(x, notes, taken)).ToList();
            // A remark a hidden element row takes is not printed above the list either.
            if (notes != null && taken != null)
                foreach (var x in items.Skip(shown)) TakeNote(x, notes, taken);
            if (items.Count > ElementsShown)
                row.MoreLabel = all ? Str("showLess") : F("showAll", items.Count);
            // "Show all" never lists fewer elements than the check has without saying so.
            if (all && items.Count > ElementsMax) row.ElementsCutLabel = F("elCut", ElementsMax, items.Count);
            row.FixIds = items.Where(x => !x.IsHuman && x.Status == ItemStatus.Open && !x.MarkedManual && x.Fix == FixKind.Fix)
                              .Select(x => x.Id).ToList();
            row.CanFix = row.FixIds.Count > 0;
            row.FixLabel = F("fix", row.FixIds.Count);
            AddCopilotFix(row, items);
            return items.Count;
        }

        /// <summary>AI-509: the check header's "Fix all n elements via Copilot". Only for the Copilot-fixable checks
        /// (<see cref="RevitWebAppSync.UI.Jkr.ViewModels.JkrCopilotFix.IsListed"/>, AI class) and only over rows that are themselves eligible
        /// (<c>PanelItem.CopilotFixable</c>: launcher ready, not Human, no Fix, no Pick, has element ids, not a wall material), still Open and
        /// not marked manual. Never part of <c>FixIds</c> / Fix all.</summary>
        private static void AddCopilotFix(ChecklistCheck row, List<PanelItem> items)
        {
            row.CopilotFixIds = new List<string>();
            row.CopilotFixElements = 0;
            row.CanFixViaCopilot = false;
            row.CopilotFixLabel = "";
            row.CopilotFixTooltip = "";
            var id = row.Id ?? "";
            if (!RevitWebAppSync.UI.Jkr.ViewModels.JkrCopilotFix.IsListed(id) || !RevitWebAppSync.UI.Jkr.ViewModels.JkrCopilotFix.IsAiClass(id)) return;
            var rows = items.Where(x => !x.IsHuman && !x.Passed && x.Status == ItemStatus.Open && !x.MarkedManual && x.CopilotFixable && x.Fix == FixKind.None && !x.Pick).ToList();
            if (rows.Count == 0) return;
            row.CopilotFixIds = rows.Select(x => x.Id).ToList();
            row.CopilotFixElements = rows.SelectMany(x => x.ElementIds ?? System.Array.Empty<long>()).Where(e => e > 0).Distinct().Count();
            if (row.CopilotFixElements == 0) { row.CopilotFixIds = new List<string>(); return; }
            row.CanFixViaCopilot = true;
            row.CopilotFixLabel = RevitWebAppSync.UI.Jkr.ViewModels.JkrCopilotFix.FixAllLabel(row.CopilotFixElements);
            row.CopilotFixTooltip = RevitWebAppSync.UI.Jkr.ViewModels.JkrCopilotFix.FixAllTooltip();
        }

        private static string FirstLine(string s)
        {
            s = s ?? "";
            var i = s.IndexOf('\n');
            return SpecReport.Clean(i < 0 ? s : s.Substring(0, i));
        }

        /// <summary>
        /// One issue under a check. An issue a person decides shows its reason; when that
        /// reason is one of the check's remarks, the row takes the remark in the pane's
        /// language and the remark is not printed again above (<paramref name="taken"/>).
        /// </summary>
        private static ChecklistElement ElementRow(PanelItem x, List<Text> notes = null, HashSet<int> taken = null)
        {
            var act = x.Act.En;
            var hasAct = !string.IsNullOrEmpty(act) && act != "—";
            // A finding with no element (a material, a set) is named by what the model has.
            var generic = string.IsNullOrEmpty(x.El) || x.El == "—" || x.El == "Model";
            var label = !generic ? x.El : x.IsHuman && hasAct ? act : x.Title.En;
            string detail;
            if (x.IsHuman)
            {
                var reason = x.Reason ?? x.Title;
                detail = FirstLine(reason.En);
                var i = TakeNote(x, notes, taken);
                if (i >= 0) detail = notes[i].En;
                if (detail.Length == 0) detail = x.Title.En;
            }
            else if (x.Prop != null) detail = (string.IsNullOrEmpty(act) || act == "—" ? "" : act + " → ") + x.Prop;
            else if (!string.IsNullOrEmpty(act) && act != "—") detail = x.Title.En + ": " + act;
            else detail = x.Title.En;
            string tag;
            if (x.IsHuman) tag = x.Status == ItemStatus.Reviewed ? PanelStrings.Get("reviewed") : "";
            else if (x.Status == ItemStatus.Resolved) tag = PanelStrings.Get("resolvedRow");
            else if (x.Status == ItemStatus.Ignored) tag = PanelStrings.Get("ignoredRow");
            else tag = "";
            return new ChecklistElement { Id = x.Id, Label = label, Detail = detail, Tag = tag };
        }

        /// <summary>The first remark not yet taken that is this issue's reason (BM or EN), now
        /// taken; -1 when none is, or the issue is not one a person decides.</summary>
        private static int TakeNote(PanelItem x, List<Text> notes, HashSet<int> taken)
        {
            if (x == null || !x.IsHuman || notes == null || taken == null) return -1;
            var reason = x.Reason ?? x.Title;
            var ms = FirstLine(reason.Ms);
            var en = FirstLine(reason.En);
            for (var i = 0; i < notes.Count; i++)
            {
                if (taken.Contains(i)) continue;
                var nm = SpecReport.Clean(notes[i].Ms);
                var ne = SpecReport.Clean(notes[i].En);
                if (nm.Length == 0 || !(nm == ms || nm == en || ne == ms || ne == en)) continue;
                taken.Add(i);
                return i;
            }
            return -1;
        }

        public static string SectionName(string s) =>
            BinaVibe.Mcp.Tools.Audit.Bim010Form.SectionNames.TryGetValue(s, out var n)
                ? n.En : s;

        private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }
}

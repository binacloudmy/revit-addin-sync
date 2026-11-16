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
        public bool CanToggleWhy => HasWhy || HasTested;
        public bool ShowWhy => WhyOpen && CanToggleWhy;
        public string WhyToggleLabel { get; set; } = "";
        public List<ChecklistElement> Elements { get; set; } = new List<ChecklistElement>();
        public bool HasElements => Elements.Count > 0;
        public string MoreLabel { get; set; } = "";
        public bool HasMore => !string.IsNullOrEmpty(MoreLabel);
        /// <summary>Open issues Fix can act on (ready-made or AI-fixer candidates).</summary>
        public List<string> FixIds { get; set; } = new List<string>();
        public bool CanFix { get; set; }
        public string FixLabel { get; set; } = "";
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

    /// <summary>A state on the Checklist legend: its colour, label and count; click filters by it.</summary>
    public sealed class StateChip
    {
        /// <summary><see cref="SpecChecklist.AllStates"/> or a tone (fail | person | partial | pass | listed | na | none).</summary>
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public int N { get; set; }
        /// <summary>Same as Key; "all" has no colour.</summary>
        public string Tone { get; set; } = "";
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
        /// <summary>Most elements "Show all" lists (the Open tab has every issue).</summary>
        public const int ElementsMax = 40;

        /// <summary>Legend / filter order: what needs work first, then what is settled.</summary>
        public static readonly string[] StateKeys = { "fail", "person", "partial", "pass", "listed", "na", "none" };

        // A key ending in "1" is the English singular (BM does not inflect).
        private static readonly Dictionary<string, (string Ms, string En)> S = new Dictionary<string, (string, string)>
        {
            ["tab"] = ("Senarai semak", "Checklist"),
            ["intro"] = ("Setiap semakan spec JKR Model Audit, ikut seksyen Borang. Klik elemen untuk lihat, baiki atau abaikan.",
                         "Every check of the JKR Model Audit spec, by Borang section. Click an element to see, fix or ignore it."),
            ["stAll"] = ("Semua", "All"),
            ["stPass"] = ("Lulus", "Pass"),
            ["stFail"] = ("Gagal", "Fails"),
            ["stPartial"] = ("Sebahagian", "Partly"),
            ["stPerson"] = ("Manusia", "Person"),
            ["stListed"] = ("Senarai", "Listed"),
            ["stNa"] = ("Tidak berkenaan", "Not applicable"),
            ["stNone"] = ("Belum disemak", "Not checked"),
            ["clsHuman"] = ("keputusan manusia", "a person decides"),
            ["clsList"] = ("senarai sahaja", "list only"),
            ["dPass"] = ("{0} keputusan disemak — semua patuh.", "{0} results checked — all comply."),
            ["dPass1"] = ("", "1 result checked — it complies."),
            ["dFail"] = ("Gagal: {0} daripada {1} keputusan", "Failed: {0} of {1} results"),
            ["dFailOnly"] = ("Gagal: 1 daripada 1 keputusan", "Failed: 1 of 1 result"),
            ["dFailEl"] = (" · {0} elemen", " · {0} elements"),
            ["dFailEl1"] = ("", " · 1 element"),
            ["dPartial"] = ("Hanya sebahagian peraturan ini disemak oleh imbasan, dan bahagian itu patuh — semak selebihnya secara manual.",
                            "Only part of the rule is tested by the scan, and that part complies — check the rest by hand."),
            ["dHuman"] = ("Spec menyerahkan ini kepada manusia — tidak dikira dalam %.", "The spec leaves this to a person — not scored."),
            ["dUnjudged"] = ("{0} keputusan tidak dapat dinilai oleh imbasan — semak secara manual.", "{0} results the scan could not judge — check by hand."),
            ["dUnjudged1"] = ("", "1 result the scan could not judge — check by hand."),
            ["dListed"] = ("Senarai sahaja, tiada keputusan.", "List only — no verdict."),
            ["dNa"] = ("Tidak berkenaan untuk model ini, seperti yang ditakrif spec — tidak dikira dalam %.",
                       "Does not apply to this model, as the spec defines — not scored."),
            ["dNone"] = ("Imbasan belum ada peraturan untuk ini — belum disemak.", "The scan has no rule for this yet — not checked."),
            ["dNoneList"] = ("Senarai sahaja — imbasan belum menghasilkan senarai ini.", "List only — the scan does not produce this list yet."),
            ["dNoneBackend"] = ("Bergantung pada backend, yang tidak tersedia — belum disemak.", "Depends on the backend, which is unavailable — not checked."),
            ["wComply"] = ("Spec — patuh bila:", "Spec — complies when:"),
            ["wNotComply"] = ("Spec — tidak patuh bila:", "Spec — does not comply when:"),
            ["wManual"] = ("Spec — semak manual:", "Spec — checked by hand:"),
            ["wNote"] = ("Spec — nota:", "Spec — note:"),
            ["whyShow"] = ("Kenapa? ▸", "Why? ▸"),
            ["whyHide"] = ("Sembunyi ▾", "Hide ▾"),
            ["tested"] = ("Disemak: ", "Tested: "),
            ["secFail"] = ("{0} gagal", "{0} fail"),
            ["secPass"] = ("{0} lulus", "{0} pass"),
            ["secPartial"] = ("{0} sebahagian", "{0} partly"),
            ["secPerson"] = ("{0} manusia", "{0} person"),
            ["secNa"] = ("{0} tidak berkenaan", "{0} not applicable"),
            ["secNone"] = ("{0} belum disemak", "{0} not checked"),
            ["secListed"] = ("{0} senarai", "{0} listed"),
            ["other"] = ("Peraturan JKR lain (bukan dalam spec Borang)", "Other JKR rules (not in the Borang spec)"),
            ["otherItem"] = ("Peraturan imbasan yang tiada semakan spec — ditunjuk, tidak dikira.",
                             "Scan rules with no spec check — shown, not scored."),
            ["oFail"] = ("{0} daripada {1} gagal.", "{0} of {1} fail."),
            ["oFail1"] = ("", "{0} of {1} fails."),
            ["oUnjudged"] = ("{0} tidak dapat dinilai.", "{0} could not be judged."),
            ["fix"] = ("Baiki {0}", "Fix {0}"),
            ["showAll"] = ("Tunjuk semua {0}", "Show all {0}"),
            ["showLess"] = ("Tunjuk kurang", "Show less"),
            ["showCapped"] = ("Tunjuk kurang · {0} lagi dalam tab Terbuka", "Show less · {0} more in the Open tab"),
            ["vFails"] = ("Tidak patuh — {0} semakan spec gagal", "Does not comply — {0} spec checks fail"),
            ["vFails1"] = ("", "Does not comply — 1 spec check fails"),
            ["vNoFail"] = ("Tiada kegagalan dalam semakan yang diliputi", "No failures in the checks covered"),
            ["vNothing"] = ("Belum ada semakan spec yang disahkan", "No spec check verified yet"),
            ["vCounts"] = ("{0} lulus · {1} sebahagian · {2} perlu manusia · {3} belum disemak — daripada {4} semakan spec.",
                           "{0} pass · {1} partly · {2} need a person · {3} not checked — of {4} spec checks."),
            ["vOthers"] = (" {0} peraturan JKR lain juga gagal.", " {0} other JKR rules also fail."),
            ["vOthers1"] = ("", " 1 other JKR rule also fails."),
            ["others"] = ("{0} peraturan JKR lain (bukan dalam spec) juga gagal — lihat di bawah.", "{0} other JKR rules (not in the spec) also fail — see below."),
            ["others1"] = ("", "1 other JKR rule (not in the spec) also fails — see below."),
            ["hResults"] = ("{0} / {1} keputusan disahkan AI lulus", "{0} / {1} AI-verified results pass"),
            ["hResults1"] = ("", "{0} / {1} AI-verified result passes"),
            ["hPerson"] = ("semakan perlu manusia", "checks need a person"),
            ["hPerson1"] = ("", "check needs a person"),
            ["backendDown"] = ("Backend tidak tersedia — semakan yang bergantung padanya belum disemak",
                               "Backend unavailable — checks that depend on it are not checked"),
            ["filterEmpty"] = ("Tiada semakan dengan status ini.", "No check has this state."),
            ["filterAria"] = ("Tapis ikut status: {0}", "Filter by state: {0}"),
        };

        private static bool IsEn(string lang) => string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase);

        public static string Str(string key, string lang) =>
            S.TryGetValue(key, out var p) ? (IsEn(lang) ? p.En : p.Ms) : key;

        private static string F(string key, string lang, params object[] args) => string.Format(Str(key, lang), args);

        /// <summary>Count-dependent wording: the English singular (key + "1") when n == 1.</summary>
        public static string Plural(string key, string lang, int n, params object[] args)
        {
            var k = IsEn(lang) && n == 1 && S.ContainsKey(key + "1") ? key + "1" : key;
            return string.Format(Str(k, lang), args.Length == 0 ? new object[] { n } : args);
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

        public static string StateLabel(string tone, string lang)
        {
            switch (tone)
            {
                case "pass": return Str("stPass", lang);
                case "fail": return Str("stFail", lang);
                case "partial": return Str("stPartial", lang);
                case "person": return Str("stPerson", lang);
                case "listed": return Str("stListed", lang);
                case "na": return Str("stNa", lang);
                case AllStates: return Str("stAll", lang);
                default: return Str("stNone", lang);
            }
        }

        public static string StateLabel(SpecState s, string lang) => StateLabel(ToneOf(s), lang);

        /// <summary>The legend: All + every state with its count of spec checks; the one filtered on is on.</summary>
        public static List<StateChip> StateChips(SpecReport r, string lang, string filter)
        {
            var list = new List<StateChip>();
            if (r == null) return list;
            var on = string.IsNullOrEmpty(filter) ? AllStates : filter;
            list.Add(new StateChip
            {
                Key = AllStates, Tone = AllStates, Label = Str("stAll", lang), N = r.Checks.Count, IsOn = on == AllStates,
                Aria = F("filterAria", lang, Str("stAll", lang)) + " · " + r.Checks.Count,
            });
            foreach (var k in StateKeys)
            {
                var n = r.Checks.Count(c => ToneOf(c.State) == k);
                list.Add(new StateChip
                {
                    Key = k, Tone = k, Label = StateLabel(k, lang), N = n, IsOn = on == k,
                    Aria = F("filterAria", lang, StateLabel(k, lang)) + " · " + n,
                });
            }
            return list;
        }

        // ─── headline: two numbers (DESIGN-BRIEF R2, spec "Kiraan %") ───

        /// <summary>
        /// AI-verifiable spec results that passed, and those that passed or failed,
        /// counted per result (per cell, not per check or Borang row), in one section
        /// or all (null). Human-decides and list-only checks are never in it; nor are
        /// results the scan could not judge, partial-rule passes (they prove no pass)
        /// or not-applicable results.
        /// </summary>
        public static (int Passed, int Total) Score(SpecReport r, string section = null)
        {
            if (r == null) return (0, 0);
            var ai = r.Checks.Where(c => c.Check.Class == SpecCellClass.Ai
                                         && (string.IsNullOrEmpty(section) || section == PanelCore.AllSections || c.Check.Section == section));
            int pass = 0, total = 0;
            foreach (var c in ai) { pass += c.Passed; total += c.Passed + c.Failed; }
            return (pass, total);
        }

        /// <summary>Score as a whole %, or null when nothing AI-verifiable was judged.</summary>
        public static int? Pct(SpecReport r, string section = null)
        {
            var (p, t) = Score(r, section);
            return t == 0 ? (int?)null : (int)Math.Round(100.0 * p / t, MidpointRounding.AwayFromZero);
        }

        /// <summary>Checks a person decides, in one section or all (null).</summary>
        public static int NeedsPerson(SpecReport r, string section = null) =>
            r?.Checks.Count(c => c.State == SpecState.NeedsPerson
                                 && (string.IsNullOrEmpty(section) || section == PanelCore.AllSections || c.Check.Section == section)) ?? 0;

        public static string ResultsLabel(SpecReport r, string lang)
        {
            var (p, t) = Score(r);
            return Plural("hResults", lang, t, p, t);
        }

        public static string NeedPersonLabel(SpecReport r, string lang) => Plural("hPerson", lang, NeedsPerson(r), NeedsPerson(r));

        public static string BackendBanner(string lang) => Str("backendDown", lang);

        // ─── verdict ───

        public static string VerdictTitle(SpecReport r, string lang)
        {
            if (r == null) return "";
            switch (r.Verdict)
            {
                case SpecVerdict.Fails: return Plural("vFails", lang, r.FailChecks);
                case SpecVerdict.NoFailures: return Str("vNoFail", lang);
                default: return Str("vNothing", lang);
            }
        }

        /// <summary>fail | pass | none — the verdict's colour.</summary>
        public static string VerdictTone(SpecReport r) =>
            r == null ? "none" : r.Verdict == SpecVerdict.Fails ? "fail" : r.Verdict == SpecVerdict.NoFailures ? "pass" : "none";

        public static string VerdictText(SpecReport r, string lang)
        {
            if (r == null) return "";
            var s = F("vCounts", lang, r.PassChecks, r.PartialChecks, r.NeedsPersonChecks, r.NotCheckedChecks + r.ListedChecks, r.Checks.Count);
            if (r.OtherFailing > 0) s += Plural("vOthers", lang, r.OtherFailing);
            return s;
        }

        /// <summary>"4 other JKR rules (not in the spec) also fail — see below." or "".</summary>
        public static string OthersNote(SpecReport r, string lang) =>
            r == null || r.OtherFailing == 0 ? "" : Plural("others", lang, r.OtherFailing);

        // ─── checklist ───

        /// <param name="items">The pane's items (status, element label, fix) by id.</param>
        /// <param name="section">Only this section (PanelCore.AllSections = all, with other rules).</param>
        /// <param name="isOpen">Whether a section is expanded (null = the default: open when it fails,
        /// when it is the only section shown, or when a state filter is on).</param>
        /// <param name="showAll">Whether a check lists every element.</param>
        /// <param name="stateFilter">Only checks in this state (a tone; null / "all" = every check).
        /// Other JKR rules are not spec checks and are left out while a filter is on.</param>
        /// <param name="whyOpen">Whether a check shows its spec criterion (null = the default: open when it fails).</param>
        public static List<ChecklistSection> Build(SpecReport report, IEnumerable<PanelItem> items, string lang,
                                                   string section, Func<string, bool?> isOpen, Func<string, bool> showAll,
                                                   string stateFilter = null, Func<string, bool?> whyOpen = null)
        {
            var list = new List<ChecklistSection>();
            if (report == null) return list;
            var byId = new Dictionary<string, PanelItem>();
            foreach (var i in items ?? Enumerable.Empty<PanelItem>())
                if (!string.IsNullOrEmpty(i?.Id) && !byId.ContainsKey(i.Id)) byId[i.Id] = i;
            var all = section == null || section == PanelCore.AllSections;
            var filtered = !string.IsNullOrEmpty(stateFilter) && stateFilter != AllStates;
            bool Keep(SpecCheckResult r) => !filtered || ToneOf(r.State) == stateFilter;

            foreach (var sec in PanelCore.Secs)
            {
                if (!all && sec != section) continue;
                var inSec = report.Checks.Where(x => x.Check.Section == sec).ToList();
                if (filtered && !inSec.Any(Keep)) continue;
                int N(SpecState s) => inSec.Count(x => x.State == s);
                int fail = N(SpecState.Fail), pass = N(SpecState.Pass), partial = N(SpecState.Partial),
                    person = N(SpecState.NeedsPerson), na = N(SpecState.NotApplicable),
                    none = N(SpecState.NotChecked), listed = N(SpecState.Listed);
                var vm = new ChecklistSection
                {
                    Key = sec,
                    Letter = sec,
                    Name = SectionName(sec, lang),
                    Summary = Join(
                        fail > 0 ? F("secFail", lang, fail) : null,
                        pass > 0 ? F("secPass", lang, pass) : null,
                        partial > 0 ? F("secPartial", lang, partial) : null,
                        person > 0 ? F("secPerson", lang, person) : null,
                        na > 0 ? F("secNa", lang, na) : null,
                        listed > 0 ? F("secListed", lang, listed) : null,
                        none > 0 ? F("secNone", lang, none) : null),
                    Tone = fail > 0 ? "fail" : pass > 0 ? "pass" : partial > 0 ? "partial" : person > 0 ? "person" : "none",
                };
                vm.IsOpen = isOpen?.Invoke(sec) ?? (fail > 0 || !all || filtered);
                if (vm.IsOpen)
                {
                    foreach (var it in SpecCatalog.ItemsOf(sec))
                    {
                        var ivm = new ChecklistItem { No = it.No, Title = it.Title.In(lang) };
                        foreach (var check in SpecCatalog.ChecksOf(it.No))
                        {
                            var r = report.For(check.Id);
                            if (r == null || !Keep(r)) continue;
                            ivm.Checks.Add(CheckRow(r, report, byId, lang, showAll, whyOpen));
                        }
                        if (ivm.Checks.Count > 0) vm.Items.Add(ivm);
                    }
                }
                list.Add(vm);
            }

            if (all && !filtered && report.Others.Count > 0)
            {
                var failing = report.Others.Count(o => o.State == SpecState.Fail);
                var passing = report.Others.Count(o => o.State == SpecState.Pass);
                var other = new ChecklistSection
                {
                    Key = OtherKey,
                    Letter = "+",
                    Name = Str("other", lang),
                    Summary = Join(
                        failing > 0 ? F("secFail", lang, failing) : null,
                        passing > 0 ? F("secPass", lang, passing) : null,
                        report.Others.Count - failing - passing > 0 ? F("secPerson", lang, report.Others.Count - failing - passing) : null),
                    Tone = failing > 0 ? "fail" : "pass",
                };
                other.IsOpen = isOpen?.Invoke(OtherKey) ?? false;
                if (other.IsOpen)
                {
                    var ivm = new ChecklistItem { No = "", Title = Str("otherItem", lang) };
                    var n = 0;
                    foreach (var o in report.Others) ivm.Checks.Add(OtherRow(o, "other-" + n++, byId, lang, showAll));
                    other.Items.Add(ivm);
                }
                list.Add(other);
            }
            return list;
        }

        /// <summary>The checklist row of one spec check (state, why, notes, elements).</summary>
        public static ChecklistCheck CheckRow(SpecCheckResult r, SpecReport report, Dictionary<string, PanelItem> byId, string lang,
                                              Func<string, bool> showAll, Func<string, bool?> whyOpen = null)
        {
            var c = r.Check;
            var row = new ChecklistCheck
            {
                Id = c.Id,
                ShownId = c.Id,
                Title = c.Title.In(lang),
                Meta = c.Reference + (c.Class == SpecCellClass.Human ? " · " + Str("clsHuman", lang)
                                    : c.Class == SpecCellClass.List ? " · " + Str("clsList", lang) : ""),
                Tone = ToneOf(r.State),
                StateLabel = StateLabel(r.State, lang),
            };
            row.Detail = DetailOf(r, report, lang);
            var (label, text) = Criterion(r, lang);
            row.WhyLabel = label;
            row.WhyText = text;

            var notes = (IsEn(lang) && r.NotesEn != null && r.NotesEn.Count == r.Notes.Count ? r.NotesEn : r.Notes) ?? new List<string>();
            row.Notes = string.Join("\n", notes.Where(n => !string.IsNullOrWhiteSpace(n)));
            if (r.State != SpecState.NotChecked && r.Rules.Count > 0)
                row.Tested = Str("tested", lang) + string.Join("; ", r.Rules.Take(3));
            row.WhyOpen = whyOpen?.Invoke(c.Id) ?? r.State == SpecState.Fail;
            row.WhyToggleLabel = Str(row.WhyOpen ? "whyHide" : "whyShow", lang);
            AddElements(row, r.IssueIds, byId ?? new Dictionary<string, PanelItem>(), lang, showAll);
            return row;
        }

        /// <summary>Why the check has its state, in one line.</summary>
        public static string DetailOf(SpecCheckResult r, SpecReport report, string lang)
        {
            var c = r.Check;
            switch (r.State)
            {
                case SpecState.Pass:
                    return Plural("dPass", lang, r.Passed);
                case SpecState.Fail:
                {
                    var total = r.Passed + r.Failed + r.Unjudged;
                    return (total == 1 ? Str("dFailOnly", lang) : Plural("dFail", lang, r.Failed, r.Failed, total))
                           + (r.Elements > 0 ? Plural("dFailEl", lang, r.Elements) : "") + ".";
                }
                case SpecState.Partial:
                    return Str("dPartial", lang);
                case SpecState.NeedsPerson:
                    return c.Class == SpecCellClass.Human ? Str("dHuman", lang) : Plural("dUnjudged", lang, r.Unjudged);
                case SpecState.Listed:
                    return Str("dListed", lang);
                case SpecState.NotApplicable:
                    return Str("dNa", lang);
                default:
                    if (c.Class == SpecCellClass.List) return Str("dNoneList", lang);
                    return report != null && report.BackendUnavailable ? Str("dNoneBackend", lang) : Str("dNone", lang);
            }
        }

        /// <summary>
        /// The spec's own words for the state: the Comply cell when it passed, the
        /// NotComply cell when it failed, the Manual note when a person decides or the
        /// check is only listed; partly / not checked show what complying means. A
        /// "—" cell falls back to the next one that says something.
        /// </summary>
        public static (string Label, string Text) Criterion(SpecCheckResult r, string lang)
        {
            var c = r.Check;
            // The spec marks columns and fields with backticks (`Type | lebar | tinggi`): shown without them.
            (string, string) Of(string key, Text t) => SpecCatalog.IsEmpty(t) ? (null, null) : (Str(key, lang), t.In(lang).Replace("`", ""));
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

        private static ChecklistCheck OtherRow(OtherResult o, string key, Dictionary<string, PanelItem> byId, string lang, Func<string, bool> showAll)
        {
            var total = o.Passed + o.Failed + o.Unjudged;
            var row = new ChecklistCheck { Id = key, ShownId = "", Title = o.Rule, Meta = o.Where };
            if (o.State == SpecState.Fail) { row.Tone = "fail"; row.StateLabel = Str("stFail", lang); row.Detail = Plural("oFail", lang, o.Failed, o.Failed, total); }
            else if (o.State == SpecState.NeedsPerson) { row.Tone = "person"; row.StateLabel = Str("stPerson", lang); row.Detail = F("oUnjudged", lang, o.Unjudged); }
            else { row.Tone = "pass"; row.StateLabel = Str("stPass", lang); row.Detail = Plural("dPass", lang, o.Passed); }
            AddElements(row, o.IssueIds, byId, lang, showAll);
            return row;
        }

        private static void AddElements(ChecklistCheck row, List<string> ids, Dictionary<string, PanelItem> byId, string lang, Func<string, bool> showAll)
        {
            var items = ids.Where(byId.ContainsKey).Select(id => byId[id]).Where(x => !x.Passed).Distinct().ToList();
            var all = showAll?.Invoke(row.Id) == true;
            var shown = all ? Math.Min(items.Count, ElementsMax) : Math.Min(items.Count, ElementsShown);
            row.Elements = items.Take(shown).Select(x => ElementRow(x, lang)).ToList();
            if (items.Count > ElementsShown)
                row.MoreLabel = !all ? F("showAll", lang, items.Count)
                              : items.Count > ElementsMax ? F("showCapped", lang, items.Count - ElementsMax)
                              : Str("showLess", lang);
            row.FixIds = items.Where(x => !x.IsHuman && x.Status == ItemStatus.Open && (x.Fix == FixKind.Fix || x.AiFix))
                              .Select(x => x.Id).ToList();
            row.CanFix = row.FixIds.Count > 0;
            row.FixLabel = F("fix", lang, row.FixIds.Count);
        }

        private static ChecklistElement ElementRow(PanelItem x, string lang)
        {
            var label = !string.IsNullOrEmpty(x.El) && x.El != "—" ? x.El : x.Title.In(lang);
            var act = x.Act.In(lang);
            string detail;
            if (x.IsHuman) detail = x.Reason?.In(lang) ?? x.Title.In(lang);
            else if (x.Prop != null) detail = (string.IsNullOrEmpty(act) || act == "—" ? "" : act + " → ") + x.Prop;
            else if (!string.IsNullOrEmpty(act) && act != "—") detail = x.Title.In(lang) + ": " + act;
            else detail = x.Title.In(lang);
            string tag;
            if (x.IsHuman) tag = x.Status == ItemStatus.Reviewed ? PanelStrings.Get(lang, "reviewed") : "";
            else if (x.Status == ItemStatus.Resolved) tag = PanelStrings.Get(lang, "fixedRow");
            else if (x.Status == ItemStatus.Ignored) tag = PanelStrings.Get(lang, "ignoredRow");
            else tag = "";
            return new ChecklistElement { Id = x.Id, Label = label, Detail = detail, Tag = tag };
        }

        public static string SectionName(string s, string lang) =>
            BinaVibe.Mcp.Tools.Audit.Bim010Form.SectionNames.TryGetValue(s, out var n)
                ? (IsEn(lang) ? n.En : n.Ms) : s;

        private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }
}

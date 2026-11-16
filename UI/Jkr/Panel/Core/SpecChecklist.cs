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
        /// <summary>fail | pass | partial | person | listed | none.</summary>
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
        /// <summary>fail | pass | partial | person | listed | none.</summary>
        public string Tone { get; set; } = "none";
        public string Detail { get; set; } = "";
        /// <summary>The run's remarks (what was found), one per line.</summary>
        public string Notes { get; set; } = "";
        public bool HasNotes => !string.IsNullOrEmpty(Notes);
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

    /// <summary>
    /// Builds the Checklist from a <see cref="SpecReport"/> and the pane's items, in
    /// the pane's language. WPF-free; ResultsScreenModel keeps which sections are
    /// open and which checks show every element.
    /// </summary>
    public static class SpecChecklist
    {
        public const string OtherKey = "+";
        /// <summary>Elements shown under a check before "Show all".</summary>
        public const int ElementsShown = 4;
        /// <summary>Most elements "Show all" lists (the Open tab has every issue).</summary>
        public const int ElementsMax = 40;

        private static readonly Dictionary<string, (string Ms, string En)> S = new Dictionary<string, (string, string)>
        {
            ["tab"] = ("Senarai semak", "Checklist"),
            ["intro"] = ("Setiap semakan spec JKR Model Audit, ikut seksyen Borang. Klik elemen untuk lihat, baiki atau abaikan.",
                         "Every check of the JKR Model Audit spec, by Borang section. Click an element to see, fix or ignore it."),
            ["stPass"] = ("Lulus", "Pass"),
            ["stFail"] = ("Gagal", "Fails"),
            ["stPartial"] = ("Sebahagian", "Partly"),
            ["stPerson"] = ("Manusia", "Person"),
            ["stListed"] = ("Senarai", "Listed"),
            ["stNone"] = ("Belum", "Not checked"),
            ["clsHuman"] = ("keputusan manusia", "a person decides"),
            ["clsList"] = ("senarai sahaja", "list only"),
            ["dPass"] = ("{0} keputusan disemak — semua patuh.", "{0} result(s) checked — all comply."),
            ["dFail"] = ("{0} daripada {1} keputusan gagal", "{0} of {1} result(s) fail"),
            ["dFailEl"] = (" · {0} elemen", " · {0} element(s)"),
            ["dPartial"] = ("Disemak sebahagian sahaja dan bahagian itu patuh — semak selebihnya secara manual.",
                            "Only partly checked, and that part complies — check the rest by hand."),
            ["dHuman"] = ("Spec menyerahkan ini kepada manusia — tidak dikira dalam %.", "The spec leaves this to a person — not scored."),
            ["dUnjudged"] = ("{0} keputusan tidak dapat dinilai oleh imbasan — semak secara manual.", "{0} result(s) the scan could not judge — check by hand."),
            ["dListed"] = ("Senarai sahaja, tiada keputusan.", "List only — no verdict."),
            ["dNone"] = ("Imbasan belum ada peraturan untuk ini — belum disemak.", "The scan has no rule for this yet — not checked."),
            ["dNoneList"] = ("Senarai sahaja — imbasan belum menghasilkan senarai ini.", "List only — the scan does not produce this list yet."),
            ["tested"] = ("Disemak: ", "Tested: "),
            ["secFail"] = ("{0} gagal", "{0} fail"),
            ["secPass"] = ("{0} lulus", "{0} pass"),
            ["secPartial"] = ("{0} sebahagian", "{0} partly"),
            ["secPerson"] = ("{0} manusia", "{0} person"),
            ["secNone"] = ("{0} belum disemak", "{0} not checked"),
            ["other"] = ("Peraturan JKR lain (bukan dalam spec Borang)", "Other JKR rules (not in the Borang spec)"),
            ["otherItem"] = ("Peraturan imbasan yang tiada semakan spec — ditunjuk, tidak dikira.",
                             "Scan rules with no spec check — shown, not scored."),
            ["oFail"] = ("{0} daripada {1} gagal.", "{0} of {1} fail."),
            ["oUnjudged"] = ("{0} tidak dapat dinilai.", "{0} could not be judged."),
            ["fix"] = ("Baiki {0}", "Fix {0}"),
            ["showAll"] = ("Tunjuk semua {0}", "Show all {0}"),
            ["showLess"] = ("Tunjuk kurang", "Show less"),
            ["showCapped"] = ("Tunjuk kurang · {0} lagi dalam tab Terbuka", "Show less · {0} more in the Open tab"),
            ["vFails"] = ("Tidak patuh — {0} semakan spec gagal", "Does not comply — {0} spec check(s) fail"),
            ["vNoFail"] = ("Tiada kegagalan dalam semakan yang diliputi", "No failures in the checks covered"),
            ["vNothing"] = ("Belum ada semakan spec yang disahkan", "No spec check verified yet"),
            ["vCounts"] = ("{0} lulus · {1} sebahagian · {2} perlu manusia · {3} belum disemak — daripada {4} semakan spec.",
                           "{0} pass · {1} partly · {2} need a person · {3} not checked — of {4} spec checks."),
            ["vOthers"] = (" {0} peraturan JKR lain juga gagal.", " {0} other JKR rule(s) also fail."),
        };

        public static string Str(string key, string lang) =>
            S.TryGetValue(key, out var p) ? (string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase) ? p.En : p.Ms) : key;

        private static string F(string key, string lang, params object[] args) => string.Format(Str(key, lang), args);

        // ─── verdict ───

        public static string VerdictTitle(SpecReport r, string lang)
        {
            if (r == null) return "";
            switch (r.Verdict)
            {
                case SpecVerdict.Fails: return F("vFails", lang, r.FailChecks);
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
            if (r.OtherFailing > 0) s += F("vOthers", lang, r.OtherFailing);
            return s;
        }

        // ─── checklist ───

        /// <param name="items">The pane's items (status, element label, fix) by id.</param>
        /// <param name="section">Only this section (PanelCore.AllSections = all, with other rules).</param>
        /// <param name="isOpen">Whether a section is expanded (null = the default: open when it fails).</param>
        /// <param name="showAll">Whether a check lists every element.</param>
        public static List<ChecklistSection> Build(SpecReport report, IEnumerable<PanelItem> items, string lang,
                                                   string section, Func<string, bool?> isOpen, Func<string, bool> showAll)
        {
            var list = new List<ChecklistSection>();
            if (report == null) return list;
            var byId = new Dictionary<string, PanelItem>();
            foreach (var i in items ?? Enumerable.Empty<PanelItem>())
                if (!string.IsNullOrEmpty(i?.Id) && !byId.ContainsKey(i.Id)) byId[i.Id] = i;
            var all = section == null || section == PanelCore.AllSections;

            foreach (var sec in PanelCore.Secs)
            {
                if (!all && sec != section) continue;
                var c = report.Section(sec) ?? new SpecSectionCount { Section = sec };
                var vm = new ChecklistSection
                {
                    Key = sec,
                    Letter = sec,
                    Name = SectionName(sec, lang),
                    Summary = Join(
                        c.Fail > 0 ? F("secFail", lang, c.Fail) : null,
                        c.Pass > 0 ? F("secPass", lang, c.Pass) : null,
                        c.Partial > 0 ? F("secPartial", lang, c.Partial) : null,
                        c.NeedsPerson > 0 ? F("secPerson", lang, c.NeedsPerson) : null,
                        c.NotChecked + c.Listed > 0 ? F("secNone", lang, c.NotChecked + c.Listed) : null),
                    Tone = c.Fail > 0 ? "fail" : c.Pass > 0 ? "pass" : c.Partial > 0 ? "partial" : c.NeedsPerson > 0 ? "person" : "none",
                };
                vm.IsOpen = isOpen?.Invoke(sec) ?? (c.Fail > 0 || !all);
                if (vm.IsOpen)
                {
                    foreach (var it in SpecCatalog.ItemsOf(sec))
                    {
                        var ivm = new ChecklistItem { No = it.No, Title = it.Title };
                        foreach (var check in SpecCatalog.ChecksOf(it.No))
                            ivm.Checks.Add(CheckRow(report.For(check.Id), byId, lang, showAll));
                        vm.Items.Add(ivm);
                    }
                }
                list.Add(vm);
            }

            if (all && report.Others.Count > 0)
            {
                var failing = report.Others.Count(o => o.State == SpecState.Fail);
                var other = new ChecklistSection
                {
                    Key = OtherKey,
                    Letter = "+",
                    Name = Str("other", lang),
                    Summary = Join(
                        failing > 0 ? F("secFail", lang, failing) : null,
                        report.Others.Count - failing > 0 ? F("secPass", lang, report.Others.Count(o => o.State == SpecState.Pass)) : null),
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

        private static ChecklistCheck CheckRow(SpecCheckResult r, Dictionary<string, PanelItem> byId, string lang, Func<string, bool> showAll)
        {
            var c = r.Check;
            var row = new ChecklistCheck
            {
                Id = c.Id,
                ShownId = c.Id,
                Title = c.Title.In(lang),
                Meta = c.Reference + (c.Class == SpecCellClass.Human ? " · " + Str("clsHuman", lang)
                                    : c.Class == SpecCellClass.List ? " · " + Str("clsList", lang) : ""),
            };
            switch (r.State)
            {
                case SpecState.Pass:
                    row.Tone = "pass"; row.StateLabel = Str("stPass", lang);
                    row.Detail = F("dPass", lang, r.Passed);
                    break;
                case SpecState.Fail:
                    row.Tone = "fail"; row.StateLabel = Str("stFail", lang);
                    row.Detail = F("dFail", lang, r.Failed, r.Passed + r.Failed + r.Unjudged)
                               + (r.Elements > 0 ? F("dFailEl", lang, r.Elements) : "") + ".";
                    break;
                case SpecState.Partial:
                    row.Tone = "partial"; row.StateLabel = Str("stPartial", lang);
                    row.Detail = Str("dPartial", lang);
                    break;
                case SpecState.NeedsPerson:
                    row.Tone = "person"; row.StateLabel = Str("stPerson", lang);
                    row.Detail = c.Class == SpecCellClass.Human ? Str("dHuman", lang) : F("dUnjudged", lang, r.Unjudged);
                    break;
                case SpecState.Listed:
                    row.Tone = "listed"; row.StateLabel = Str("stListed", lang);
                    row.Detail = Str("dListed", lang);
                    break;
                default:
                    row.Tone = "none"; row.StateLabel = Str("stNone", lang);
                    row.Detail = c.Class == SpecCellClass.List ? Str("dNoneList", lang) : Str("dNone", lang);
                    break;
            }
            var notes = new List<string>(r.Notes);
            if (r.State != SpecState.NotChecked && r.Rules.Count > 0 && r.State != SpecState.Fail)
                notes.Add(Str("tested", lang) + string.Join("; ", r.Rules.Take(3)));
            row.Notes = string.Join("\n", notes);
            AddElements(row, r.IssueIds, byId, lang, showAll);
            return row;
        }

        private static ChecklistCheck OtherRow(OtherResult o, string key, Dictionary<string, PanelItem> byId, string lang, Func<string, bool> showAll)
        {
            var total = o.Passed + o.Failed + o.Unjudged;
            var row = new ChecklistCheck { Id = key, ShownId = "", Title = o.Rule, Meta = o.Where };
            if (o.State == SpecState.Fail) { row.Tone = "fail"; row.StateLabel = Str("stFail", lang); row.Detail = F("oFail", lang, o.Failed, total); }
            else if (o.State == SpecState.NeedsPerson) { row.Tone = "person"; row.StateLabel = Str("stPerson", lang); row.Detail = F("oUnjudged", lang, o.Unjudged); }
            else { row.Tone = "pass"; row.StateLabel = Str("stPass", lang); row.Detail = F("dPass", lang, o.Passed); }
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
                ? (string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase) ? n.En : n.Ms) : s;

        private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }
}

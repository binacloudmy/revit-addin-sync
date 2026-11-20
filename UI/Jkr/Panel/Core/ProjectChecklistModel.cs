using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    // AI-436: the Project Checklist — the JKR rules about the project, not about an
    // element (1 file name, 21 LOD, 22 BPEP, 23 template, 24 shared parameters,
    // 26 naming adoption). They have no RevitElementId, so they never join the issue
    // list. Revit-free; the Revit side is UI\Jkr\Panel\ProjectChecklistController.cs.

    public enum ProjectCheckState { NotChecked, Checking, Pass, Fail, NotApplicable, Unavailable }

    public enum ProjectCheckAction { None, SaveAs, FocusLod, ConfirmBpep, LoadSharedParams }

    /// <summary>POST /v1/compliance/project-recheck/{key}: one rule, re-run on the project's state.</summary>
    public sealed class JkrProjectRecheckResponse
    {
        [JsonProperty("rule_number")] public int RuleNumber { get; set; }
        [JsonProperty("rule_key")] public string RuleKey { get; set; } = "";
        [JsonProperty("passed")] public bool Passed { get; set; }
        /// <summary>False when the rule has nothing to evaluate (rule 21 with no phase declared).</summary>
        [JsonProperty("applicable")] public bool Applicable { get; set; } = true;
        [JsonProperty("checks")] public List<JkrProjectRecheckCheck> Checks { get; set; } = new List<JkrProjectRecheckCheck>();
        [JsonProperty("error")] public string Error { get; set; } = "";
    }

    public sealed class JkrProjectRecheckCheck
    {
        [JsonProperty("status")] public string Status { get; set; } = "";
        [JsonProperty("reason")] public string Reason { get; set; } = "";
        [JsonProperty("actual_value")] public string ActualValue { get; set; } = "";
        [JsonProperty("expected_value")] public string ExpectedValue { get; set; } = "";
    }

    /// <summary>The backend's "Element naming adoption" check: Actual = "66.7% (10/15)".</summary>
    public struct NamingAdoption
    {
        public double Percent;
        public int Named;
        public int Total;

        private static readonly Regex Shape = new Regex(@"^\s*(\d+(?:\.\d+)?)\s*%\s*\(\s*(\d+)\s*/\s*(\d+)\s*\)\s*$", RegexOptions.Compiled);

        public static bool TryParse(string actual, out NamingAdoption a)
        {
            a = default(NamingAdoption);
            if (string.IsNullOrEmpty(actual)) return false;
            var m = Shape.Match(actual);
            if (!m.Success) return false;
            a.Percent = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            a.Named = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            a.Total = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            return true;
        }
    }

    public sealed class ProjectCheckItem : INotifyPropertyChanged
    {
        private readonly ProjectChecklistModel _owner;
        public ProjectCheckItem(ProjectChecklistModel owner, int rule) { _owner = owner; Rule = rule; }

        public int Rule { get; }
        public ProjectCheckState State { get; internal set; } = ProjectCheckState.NotChecked;
        /// <summary>Why it fails (the backend's reason), or for rule 26 "7 of 10 elements use JKR names".</summary>
        public string Detail { get; internal set; } = "";
        /// <summary>Rule 26: 0..100, null until a scan has answered.</summary>
        public double? PercentValue { get; internal set; }

        public double Percent => PercentValue ?? 0;
        public bool ShowProgress => Rule == ProjectChecklistModel.NamingRule;
        public bool HasProgress => ShowProgress && PercentValue.HasValue;
        public ProjectCheckAction Action => ProjectChecklistModel.ActionOf(Rule);
        public bool HasAction => Action != ProjectCheckAction.None;
        public bool CanRecheck => ProjectChecklistModel.EndpointKey(Rule) != null;
        public bool Busy => State == ProjectCheckState.Checking;
        public bool Idle => !Busy;

        public string Title => ProjectChecklistText.Title(Rule, _owner.Lang);
        public string Guidance => ProjectChecklistText.Guidance(Rule, _owner.Lang);
        public string ActionLabel => ProjectChecklistText.ActionLabel(Action, _owner.Lang);
        public string StateText => ProjectChecklistText.StateText(State, _owner.Lang);
        /// <summary>"pass" / "fail" / "muted": the view colours the mark by it.</summary>
        public string Tone => State == ProjectCheckState.Pass ? "pass"
            : State == ProjectCheckState.Fail ? "fail" : "muted";
        public bool ShowDetail => !string.IsNullOrEmpty(Detail) && State != ProjectCheckState.Pass;
        public bool ShowGuidance => State != ProjectCheckState.Pass;
        /// <summary>The action button: for a rule that is not passing now.</summary>
        public bool ShowAction => HasAction && State != ProjectCheckState.Pass;
        public string RecheckLabel => ProjectChecklistText.RecheckLabel(_owner.Lang);

        public event PropertyChangedEventHandler PropertyChanged;
        internal void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public sealed class ProjectChecklistModel : INotifyPropertyChanged
    {
        public const int NamingRule = 26;

        /// <summary>Rule 26's pass line: the share of physical elements with a JKR name (Ashraf, 20 Nov: 100%).
        /// The backend's own warn line is 80%; this is the line for "done" on the checklist.</summary>
        public const double NamingPassPercent = 100.0;

        private string _lang;

        public ProjectChecklistModel()
        {
            Items = new[] { 1, 21, 22, 23, 24, NamingRule }.Select(r => new ProjectCheckItem(this, r)).ToList();
        }

        public IReadOnlyList<ProjectCheckItem> Items { get; }
        public ProjectCheckItem Item(int rule) => Items.First(i => i.Rule == rule);

        public string Lang => _lang ?? PaneLanguage.Current;
        public void SetLang(string lang) { _lang = lang; RaiseAll(); }

        /// <summary>The user pressed an item's action button (rule). The host does the work, then re-checks.</summary>
        public event Action<int> ActionRequested;
        /// <summary>The user pressed an item's "Check again" (rule).</summary>
        public event Action<int> RecheckRequested;
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Rule 21's action: bring the pane's LOD box into view and focus it (it is a pane setting, not a Revit one).</summary>
        public event Action FocusLodRequested;

        public void Act(int rule)
        {
            if (ActionOf(rule) == ProjectCheckAction.FocusLod) FocusLodRequested?.Invoke();
            else ActionRequested?.Invoke(rule);
        }
        public void Recheck(int rule) => RecheckRequested?.Invoke(rule);

        public int FailCount => Items.Count(i => i.State == ProjectCheckState.Fail);
        public int PassCount => Items.Count(i => i.State == ProjectCheckState.Pass);
        public string Summary => ProjectChecklistText.Summary(PassCount, Items.Count, Lang);
        public string Heading => ProjectChecklistText.Heading(Lang);
        public string Intro => ProjectChecklistText.Intro(Lang);

        /// <summary>A re-check of this rule started. The item shows "Checking…" until its answer.</summary>
        public void Begin(int rule)
        {
            var it = Item(rule);
            it.State = ProjectCheckState.Checking;
            it.Raise();
            RaiseAll();
        }

        /// <summary>
        /// The ONLY way a rule 1 / 21–24 item changes state: its re-check answered. A missing
        /// or errored answer is never a pass.
        /// </summary>
        public void ApplyRecheck(int rule, JkrProjectRecheckResponse r)
        {
            var it = Item(rule);
            if (r == null || !string.IsNullOrEmpty(r.Error))
            {
                it.State = ProjectCheckState.Unavailable;
                it.Detail = r?.Error ?? "";
            }
            else if (!r.Applicable)
            {
                it.State = ProjectCheckState.NotApplicable;
                it.Detail = "";
            }
            else if (r.Passed)
            {
                it.State = ProjectCheckState.Pass;
                it.Detail = "";
            }
            else
            {
                it.State = ProjectCheckState.Fail;
                var why = (r.Checks ?? new List<JkrProjectRecheckCheck>())
                    .FirstOrDefault(c => c != null && !string.Equals(c.Status, "pass", StringComparison.OrdinalIgnoreCase));
                it.Detail = why?.Reason ?? "";
            }
            it.Raise();
            RaiseAll();
        }

        /// <summary>
        /// Rule 26: recomputed from the "Element naming adoption" check of every element-rule
        /// run. Null or unreadable text (the scan did not return the check) is "not checked".
        /// </summary>
        public void ApplyNamingAdoption(string actual)
        {
            var it = Item(NamingRule);
            if (!NamingAdoption.TryParse(actual, out var a))
            {
                it.State = ProjectCheckState.NotChecked;
                it.PercentValue = null;
                it.Detail = "";
            }
            else
            {
                it.PercentValue = a.Percent;
                it.State = a.Percent >= NamingPassPercent ? ProjectCheckState.Pass : ProjectCheckState.Fail;
                it.Detail = ProjectChecklistText.NamingDetail(a.Named, a.Total, Lang);
            }
            it.Raise();
            RaiseAll();
        }

        private void RaiseAll()
        {
            foreach (var i in Items) i.Raise();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        // ── wire ──

        /// <summary>The endpoint's last segment; null for rule 26 (no endpoint: it follows the element scan).</summary>
        public static string EndpointKey(int rule)
        {
            switch (rule)
            {
                case 1: return "file-name";
                case 21: return "lod";
                case 22: return "bpep";
                case 23: return "template";
                case 24: return "shared-params";
                default: return null;
            }
        }

        public static string EndpointPath(int rule)
        {
            var key = EndpointKey(rule);
            return key == null ? null : "/v1/compliance/project-recheck/" + key;
        }

        public static ProjectCheckAction ActionOf(int rule)
        {
            switch (rule)
            {
                case 1: return ProjectCheckAction.SaveAs;
                case 21: return ProjectCheckAction.FocusLod;
                case 22: return ProjectCheckAction.ConfirmBpep;
                case 23: return ProjectCheckAction.SaveAs;
                case 24: return ProjectCheckAction.LoadSharedParams;
                default: return ProjectCheckAction.None;
            }
        }
    }

    /// <summary>BM / EN text of the Project Checklist.</summary>
    public static class ProjectChecklistText
    {
        private static string L(string lang, string ms, string en) =>
            string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase) ? en : ms;

        public static string Heading(string lang) => L(lang, "Senarai semak projek", "Project checklist");
        public static string Intro(string lang) => L(lang,
            "Semakan tahap projek — bukan elemen. Setiap satu berubah hanya selepas disemak semula.",
            "Checks on the project, not on elements. Each one changes only after it is checked again.");
        public static string Summary(int pass, int total, string lang) => L(lang, $"{pass} daripada {total} lulus", $"{pass} of {total} pass");
        public static string RecheckLabel(string lang) => L(lang, "Semak semula", "Check again");

        public static string Title(int rule, string lang)
        {
            switch (rule)
            {
                case 1: return L(lang, "Nama fail mengikut format JKR", "File name follows the JKR format");
                case 21: return L(lang, "LOD memenuhi minimum fasa", "LOD meets the phase minimum");
                case 22: return L(lang, "BPEP disahkan", "BPEP confirmed");
                case 23: return L(lang, "Templat JKR digunakan", "JKR template used");
                case 24: return L(lang, "Parameter dikongsi JKR dimuatkan", "JKR shared parameters loaded");
                case ProjectChecklistModel.NamingRule: return L(lang, "Penggunaan penamaan JKR", "JKR naming adoption");
                default: return "";
            }
        }

        public static string Guidance(int rule, string lang)
        {
            switch (rule)
            {
                case 1: return L(lang,
                    "Revit tidak boleh menamakan semula fail yang dibuka. Guna Simpan Sebagai dengan nama JKR, kemudian semak semula.",
                    "Revit cannot rename the open file. Use Save As with a JKR name, then check again.");
                case 21: return L(lang,
                    "Ditetapkan secara manual: pilih LOD di kotak LOD pada bar alat di atas, kemudian semak semula.",
                    "Set by hand: pick the LOD in the LOD box on the tools bar above, then check again.");
                case 22: return L(lang,
                    "Sahkan bahawa BPEP wujud. Ini menulis BPEP_Confirmed = Yes pada Project Information.",
                    "Confirm that a BPEP exists. This writes BPEP_Confirmed = Yes on Project Information.");
                case 23: return L(lang,
                    "Mulakan dari templat JKR: Simpan Sebagai daripada fail templat JKR, kemudian semak semula.",
                    "Start from the JKR template: Save As from the JKR template file, then check again.");
                case 24: return L(lang,
                    "Muatkan fail parameter dikongsi JKR (Manage > Shared Parameters), kemudian semak semula.",
                    "Load the JKR shared parameter file (Manage > Shared Parameters), then check again.");
                case ProjectChecklistModel.NamingRule: return L(lang,
                    "Berubah sendiri apabila semakan elemen dijalankan semula. Tiada tindakan di sini.",
                    "Updates by itself whenever the element checks run again. Nothing to press here.");
                default: return "";
            }
        }

        public static string ActionLabel(ProjectCheckAction a, string lang)
        {
            switch (a)
            {
                case ProjectCheckAction.SaveAs: return L(lang, "Simpan Sebagai…", "Save As…");
                case ProjectCheckAction.FocusLod: return L(lang, "Ke kotak LOD", "Go to LOD box");
                case ProjectCheckAction.ConfirmBpep: return L(lang, "Sahkan BPEP", "Confirm BPEP");
                case ProjectCheckAction.LoadSharedParams: return L(lang, "Muat parameter dikongsi", "Load Shared Parameters");
                default: return "";
            }
        }

        public static string StateText(ProjectCheckState s, string lang)
        {
            switch (s)
            {
                case ProjectCheckState.Pass: return L(lang, "Lulus", "Pass");
                case ProjectCheckState.Fail: return L(lang, "Gagal", "Fails");
                case ProjectCheckState.Checking: return L(lang, "Menyemak…", "Checking…");
                case ProjectCheckState.NotApplicable: return L(lang, "Tiada fasa ditetapkan", "No phase set");
                case ProjectCheckState.Unavailable: return L(lang, "Tidak dapat disemak", "Could not check");
                default: return L(lang, "Belum disemak", "Not checked");
            }
        }

        public static string NamingDetail(int named, int total, string lang) =>
            L(lang, $"{named} daripada {total} elemen guna nama JKR", $"{named} of {total} elements use JKR names");
    }
}

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
        /// <summary>AI-440: why the last re-check or write failed (backend, network, auth, Revit). Cleared by the next check.</summary>
        public string Error { get; internal set; } = "";
        public bool ShowError => !string.IsNullOrEmpty(Error);

        public double Percent => PercentValue ?? 0;
        public bool ShowProgress => Rule == ProjectChecklistModel.NamingRule;
        public bool HasProgress => ShowProgress && PercentValue.HasValue;
        public ProjectCheckAction Action => ProjectChecklistModel.ActionOf(Rule);
        public bool HasAction => Action != ProjectCheckAction.None;
        public bool CanRecheck => ProjectChecklistModel.EndpointKey(Rule) != null;
        public bool Busy => State == ProjectCheckState.Checking;
        public bool Idle => !Busy;

        public string Title => ProjectChecklistText.Title(Rule);
        public string Guidance => ProjectChecklistText.Guidance(Rule);
        public string ActionLabel => ProjectChecklistText.ActionLabel(Action);
        public string StateText => ProjectChecklistText.StateText(State);
        /// <summary>"pass" / "fail" / "muted": the view colours the mark by it.</summary>
        public string Tone => State == ProjectCheckState.Pass ? "pass"
            : State == ProjectCheckState.Fail ? "fail" : "muted";
        public bool ShowDetail => !string.IsNullOrEmpty(Detail) && State != ProjectCheckState.Pass;
        public bool ShowGuidance => State != ProjectCheckState.Pass;
        /// <summary>The action button: for a rule that is not passing now.</summary>
        public bool ShowAction => HasAction && State != ProjectCheckState.Pass;
        public string RecheckLabel => ProjectChecklistText.RecheckLabel();

        public event PropertyChangedEventHandler PropertyChanged;
        internal void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public sealed class ProjectChecklistModel : INotifyPropertyChanged
    {
        public const int NamingRule = 26;

        /// <summary>Rule 26's pass line: the share of physical elements with a JKR name (Ashraf, 20 Nov: 100%).
        /// The backend's own warn line is 80%; this is the line for "done" on the checklist.</summary>
        public const double NamingPassPercent = 100.0;

        public ProjectChecklistModel()
        {
            Items = new[] { 1, 21, 22, 23, 24, NamingRule }.Select(r => new ProjectCheckItem(this, r)).ToList();
        }

        public IReadOnlyList<ProjectCheckItem> Items { get; }
        public ProjectCheckItem Item(int rule) => Items.First(i => i.Rule == rule);

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
        public string Summary => ProjectChecklistText.Summary(PassCount, Items.Count);
        public string Heading => ProjectChecklistText.Heading();
        public string Intro => ProjectChecklistText.Intro();

        /// <summary>A re-check of this rule started. The item shows "Checking…" until its answer.</summary>
        public void Begin(int rule)
        {
            var it = Item(rule);
            it.State = ProjectCheckState.Checking;
            it.Error = "";
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
            it.Error = "";
            if (r == null || !string.IsNullOrEmpty(r.Error))
            {
                it.State = ProjectCheckState.Unavailable;
                it.Detail = "";
                it.Error = string.IsNullOrEmpty(r?.Error) ? ProjectChecklistText.NoAnswer() : r.Error;
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

        /// <summary>AI-440: a button's own work failed (the BPEP write). Shown on the row; the state is the last answer's.</summary>
        public void ApplyActionError(int rule, string message)
        {
            var it = Item(rule);
            it.Error = string.IsNullOrEmpty(message) ? ProjectChecklistText.NoAnswer() : message;
            it.Raise();
            RaiseAll();
        }

        /// <summary>Error text for a non-200 re-check: status, plus the backend's "detail" (or the body, cut short).</summary>
        public static string HttpErrorText(int code, string status, string body)
        {
            var text = $"Recheck error: {code} {status}";
            string why = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var d = Newtonsoft.Json.Linq.JObject.Parse(body)["detail"];
                    why = d == null ? null : (d.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)d : d.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch { why = body.Trim(); }
            }
            if (string.IsNullOrWhiteSpace(why)) return text;
            if (why.Length > 200) why = why.Substring(0, 200) + "…";
            return text + " — " + why;
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
                it.Detail = ProjectChecklistText.NamingDetail(a.Named, a.Total);
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

    /// <summary>Text of the Project Checklist.</summary>
    public static class ProjectChecklistText
    {
        public static string Heading() => "Project checklist";
        public static string Intro() => "Checks on the project, not on elements. Each one changes only after it is checked again.";
        public static string Summary(int pass, int total) => $"{pass} of {total} pass";
        public static string RecheckLabel() => "Check again";

        public static string Title(int rule)
        {
            switch (rule)
            {
                case 1: return "File name follows the JKR format";
                case 21: return "LOD meets the phase minimum";
                case 22: return "BPEP confirmed";
                case 23: return "JKR template used";
                case 24: return "JKR shared parameters loaded";
                case ProjectChecklistModel.NamingRule: return "JKR naming adoption";
                default: return "";
            }
        }

        public static string Guidance(int rule)
        {
            switch (rule)
            {
                case 1: return "Revit cannot rename the open file. Use Save As with a JKR name, then check again.";
                case 21: return "Set by hand: pick the LOD in the LOD box on the tools bar above, then check again.";
                case 22: return "Confirm that a BPEP exists. This writes BPEP_Confirmed = Yes on Project Information.";
                case 23: return "Start from the JKR template: Save As from the JKR template file, then check again.";
                case 24: return "Load the JKR shared parameter file (Manage > Shared Parameters), then check again.";
                case ProjectChecklistModel.NamingRule: return "Updates by itself whenever the element checks run again. Nothing to press here.";
                default: return "";
            }
        }

        public static string ActionLabel(ProjectCheckAction a)
        {
            switch (a)
            {
                case ProjectCheckAction.SaveAs: return "Save As…";
                case ProjectCheckAction.FocusLod: return "Go to LOD box";
                case ProjectCheckAction.ConfirmBpep: return "Confirm BPEP";
                case ProjectCheckAction.LoadSharedParams: return "Load Shared Parameters";
                default: return "";
            }
        }

        public static string StateText(ProjectCheckState s)
        {
            switch (s)
            {
                case ProjectCheckState.Pass: return "Pass";
                case ProjectCheckState.Fail: return "Fails";
                case ProjectCheckState.Checking: return "Checking…";
                case ProjectCheckState.NotApplicable: return "No phase set";
                case ProjectCheckState.Unavailable: return "Could not check";
                default: return "Not checked";
            }
        }

        public static string NoAnswer() =>
            "The check returned no answer. Try again.";

        public static string RecheckFailedToast(int rule, string error) =>
            $"Could not check \"{Title(rule)}\": {error}";

        public static string NamingDetail(int named, int total) =>
            $"{named} of {total} elements use JKR names";
    }
}

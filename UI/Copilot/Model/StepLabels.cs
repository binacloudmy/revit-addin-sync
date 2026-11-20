#nullable enable
// r17, tester 20 Nov: "the AI WORK PROGRESS shows 'Running Revit code' several times while the copilot works". Every run_revit_code
// row said the same words whatever it did (read the doors, preview a change, write it, check it), and a retried run looked like a
// new step. The row now says what the step does, from the call's own arguments: the run's intent ("Hide all tags in the active
// view"), a stored job's name ("Running the stored job: circuits"), the category and level a find searched ("Finding Doors on
// Aras 01"), the view a look pictures; a run that follows a failed run with the same words says "Retrying: ..."; and the same
// label twice in a row is one row with a counter (StepCollapse). Malay in a Malay chat. Pure (no WPF).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class StepLabels
    {
        private const int MaxChars = 70;

        /// <summary>The generic label of a tool (the old row text), used when the call says nothing more.</summary>
        public static string Generic(string tool, bool ms) => tool switch
        {
            ToolNames.ModelOverview => ms ? "Membaca gambaran model" : "Reading the model overview",
            ToolNames.FindElements => ms ? "Mencari elemen" : "Finding elements",
            ToolNames.Inspect => ms ? "Memeriksa parameter" : "Inspecting parameters",
            ToolNames.Check => ms ? "Menyemak peraturan" : "Checking the rules",
            ToolNames.SetParameters => ms ? "Menetapkan parameter" : "Setting parameters",
            ToolNames.Look => ms ? "Mengambil imej model" : "Taking a picture of the model",
            ToolNames.RunRevitCode => ms ? "Menjalankan kod Revit" : "Running Revit code",
            _ => tool,
        };

        /// <summary>What this call does, in a few words.</summary>
        public static string For(ToolCallDto? call, bool ms)
        {
            if (call == null) return "";
            var args = call.Args;
            var obj = args.ValueKind == JsonValueKind.Object;
            switch (call.Tool)
            {
                case ToolNames.RunRevitCode:
                {
                    var intent = obj ? Str(args, "intent") : "";
                    if (intent.Length > 0) return Clip(Capital(intent));
                    var playbook = obj ? Str(args, "playbook") : "";
                    if (playbook.Length > 0) return Clip((ms ? "Menjalankan kerja tersimpan: " : "Running the stored job: ") + playbook.Replace('-', ' '));
                    break;
                }
                case ToolNames.FindElements:
                {
                    var (cats, level) = obj ? FindScope(args) : (new List<string>(), "");
                    if (cats.Count == 0 && level.Length == 0) break;
                    var what = cats.Count == 0 ? (ms ? "elemen" : "elements") : cats.Count <= 2 ? string.Join(ms ? " dan " : " and ", cats) : cats[0] + (ms ? " dan " : " and ") + (cats.Count - 1) + (ms ? " kategori lagi" : " more categories");
                    return Clip((ms ? "Mencari " : "Finding ") + what + (level.Length > 0 ? (ms ? " di " : " on ") + level : ""));
                }
                case ToolNames.Inspect:
                {
                    var n = obj && args.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array ? ids.GetArrayLength() : 0;
                    if (obj && Str(args, "schedule").Length > 0) return Clip((ms ? "Membaca jadual " : "Reading the schedule ") + Str(args, "schedule"));
                    if (n > 0) return ms ? "Membaca parameter " + n + " elemen" : "Reading the parameters of " + n + (n == 1 ? " element" : " elements");
                    break;
                }
                case ToolNames.Look:
                {
                    var view = obj ? Str(args, "view") : "";
                    if (view.Length > 0) return Clip((ms ? "Mengambil imej " : "Taking a picture of ") + view);
                    break;
                }
                case ToolNames.SetParameters:
                {
                    var param = obj ? Str(args, "param") : "";
                    if (param.Length > 0) return Clip((ms ? "Menulis " : "Writing ") + param);
                    break;
                }
                case ToolNames.ModelOverview:
                {
                    if (obj && args.TryGetProperty("sections", out var s) && s.ValueKind == JsonValueKind.Array)
                    {
                        var parts = s.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => Section(e.GetString() ?? "", ms)).Where(x => x.Length > 0).ToList();
                        if (parts.Count > 0) return Clip((ms ? "Membaca senarai " : "Reading the ") + string.Join(ms ? " dan " : " and ", parts.Take(2)));
                    }
                    break;
                }
            }
            return Generic(call.Tool, ms);
        }

        /// <summary>The label for a step that follows a failed step with the same label: "Retrying: ..." (the failed row stays as it was).</summary>
        public static string Retry(string label, bool ms) => (ms ? "Mencuba semula: " : "Retrying: ") + Uncapital(label);

        /// <summary>Malay for the server's own progress texts in a Malay chat ("Thinking", "Applying the change in Revit"); other text as sent.</summary>
        public static string Server(string text, bool ms)
        {
            if (!ms || string.IsNullOrEmpty(text)) return text;
            switch (text.Trim())
            {
                case "Thinking": return "Berfikir";
                case "Applying the change in Revit": return "Menggunakan perubahan dalam Revit";
                case "Running the code in Revit": return "Menjalankan kod dalam Revit";
                default: return text;
            }
        }

        private static string Section(string s, bool ms) => s switch
        {
            "views" => ms ? "paparan" : "views",
            "levels" => ms ? "aras" : "levels",
            "sheets" => ms ? "helaian" : "sheets",
            "categories" => ms ? "kategori" : "categories",
            "families" => ms ? "famili" : "families",
            "schedules" => ms ? "jadual" : "schedules",
            _ => "",
        };

        private static (List<string> cats, string level) FindScope(JsonElement args)
        {
            var cats = new List<string>();
            var level = "";
            if (args.TryGetProperty("queries", out var qs) && qs.ValueKind == JsonValueKind.Array)
                foreach (var q in qs.EnumerateArray())
                {
                    if (q.ValueKind != JsonValueKind.Object) continue;
                    var c = Str(q, "category");
                    if (c.Length > 0 && !cats.Contains(c, StringComparer.OrdinalIgnoreCase)) cats.Add(c);
                    var l = Str(q, "level");
                    if (level.Length == 0 && l.Length > 0) level = l;
                }
            return (cats, level);
        }

        private static string Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

        private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string Uncapital(string s) =>
            s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s.Substring(1) : s;

        private static string Clip(string s)
        {
            s = string.Join(" ", (s ?? "").Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            if (s.Length <= MaxChars) return s;
            var cut = s.Substring(0, MaxChars - 1);
            var sp = cut.LastIndexOf(' ');
            return (sp > 30 ? cut.Substring(0, sp) : cut).TrimEnd(',', ';', ':', '-', ' ') + "…";
        }
    }

    /// <summary>Consecutive rows with the same label are one row (r17): the second and later add a counter ("Running Revit code ×3")
    /// to the first instead of a new row; a row after a FAILED row with the same label is a new row that says "Retrying: ...".
    /// Pure: the turn asks it where a new step goes.</summary>
    public static class StepCollapse
    {
        /// <summary>The step id a new row should reuse (the last visible row with the same label and phase, finished well), or null for a new row.</summary>
        public static ProgressStep? Reuse(IReadOnlyList<ProgressStep> steps, string phase, string label)
        {
            var last = LastVisible(steps);
            if (last == null || string.IsNullOrEmpty(label) || last.State == StepState.Error) return null;
            return string.Equals(last.Phase, phase, StringComparison.Ordinal) && string.Equals(Base(last.Label), label, StringComparison.Ordinal) ? last : null;
        }

        /// <summary>True when the last visible row with this phase failed under the same label: the new row is a retry.</summary>
        public static bool IsRetry(IReadOnlyList<ProgressStep> steps, string phase, string label)
        {
            var last = LastVisible(steps);
            return last != null && last.State == StepState.Error && string.Equals(last.Phase, phase, StringComparison.Ordinal)
                   && (string.Equals(Base(last.Label), label, StringComparison.Ordinal) || Base(last.Label).EndsWith(Uncap(label), StringComparison.Ordinal));
        }

        /// <summary>"Running Revit code ×3" → "Running Revit code".</summary>
        public static string Base(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            var i = label.LastIndexOf(" ×", StringComparison.Ordinal);
            return i > 0 && int.TryParse(label.Substring(i + 2), out _) ? label.Substring(0, i) : label;
        }

        /// <summary>The label with its counter one higher.</summary>
        public static string Bump(string label)
        {
            var b = Base(label);
            var i = label.LastIndexOf(" ×", StringComparison.Ordinal);
            var n = i > 0 && int.TryParse(label.Substring(i + 2), out var k) ? k : 1;
            return b + " ×" + (n + 1);
        }

        /// <summary>Adds a tool row for call step <paramref name="id"/> labelled <paramref name="label"/>: a retry of a failed row with the same words
        /// gets "Retrying: ..."; the same words as the last visible row (finished well) bump that row's counter and send <paramref name="id"/> to it
        /// (<paramref name="alias"/>), so the call's later done/error and outcome land on the one row. Returns the id the row has.</summary>
        public static string AddTool(System.Collections.ObjectModel.ObservableCollection<ProgressStep> steps, IDictionary<string, string> alias,
                                     string id, string label, bool ms)
        {
            const string phase = "tool";
            if (IsRetry(steps, phase, label))
            {
                ProgressReducer.Apply(steps, id, phase, StepLabels.Retry(label, ms), "", StepState.Running);
                return id;
            }
            var reuse = Reuse(steps, phase, label);
            if (reuse == null)
            {
                ProgressReducer.Apply(steps, id, phase, label, "", StepState.Running);
                return id;
            }
            alias[id] = reuse.StepId;
            reuse.Label = Bump(reuse.Label);
            reuse.Outcome = "";
            ProgressReducer.Apply(steps, reuse.StepId, phase, "", "", StepState.Running);   // restarts its clock
            return reuse.StepId;
        }

        /// <summary>The step id a call's id now points at (itself unless AddTool folded it into an earlier row).</summary>
        public static string Resolve(IDictionary<string, string> alias, string id) =>
            id != null && alias != null && alias.TryGetValue(id, out var to) ? to : id ?? "";

        /// <summary>The server's "Running the code in Revit" / "Applying the change in Revit" row (progress kind awaiting_confirm) says the same as the
        /// tool rows that follow it, less precisely: removed once the tool rows come.</summary>
        public static void DropServerRun(System.Collections.ObjectModel.ObservableCollection<ProgressStep> steps)
        {
            if (steps == null || steps.Count == 0) return;
            var last = steps[steps.Count - 1];
            if (last != null && last.StepId.StartsWith("p:", StringComparison.Ordinal) && string.Equals(last.Phase, "awaiting_confirm", StringComparison.Ordinal))
                steps.RemoveAt(steps.Count - 1);
        }

        private static string Uncap(string s) =>
            s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s.Substring(1) : s;

        private static ProgressStep? LastVisible(IReadOnlyList<ProgressStep> steps)
        {
            if (steps == null) return null;
            for (var i = steps.Count - 1; i >= 0; i--)
            {
                var s = steps[i];
                if (s == null || ProgressTrail.IsDevStep(s) || ProgressTrail.IsWaitStep(s)) continue;
                return s;
            }
            return null;
        }
    }
}

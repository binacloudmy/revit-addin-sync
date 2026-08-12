using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Bomba
{
    /// <summary>
    /// Maps the wire DTOs (Services.Bomba*Dto) onto the pane's view models.
    /// Same role as UI/Jkr/ViewModels/IssueMapper.cs. Builds fresh VMs every
    /// scan — FindingVm's settable properties do not notify, so mutation in
    /// place would leave the pane stale.
    /// </summary>
    public static class BombaMapper
    {
        /// Subject titles, never schedule numbers — numbering differs between
        /// state adoptions (design constraint from the pane plan).
        private static readonly Dictionary<string, string> CheckTitles = new Dictionary<string, string>
        {
            { "fire_systems", "Fire systems" },
            { "exit_width", "Exit width" },
            { "travel_distance", "Travel distance" },
            { "dead_ends", "Dead ends" },
            { "unprotected_areas", "Unprotected areas" },
            { "stair_discharge", "Staircase discharge" },
        };

        public static List<CheckVm> Map(BombaCheckResponseDto response)
        {
            var checks = new List<CheckVm>();
            if (response == null || response.Findings == null) return checks;

            // Provenance suffix: rules not yet consultant-verified must say so
            // on every finding — the [X]-until-verified rule applied to provenance.
            string versionLabel = response.RulesVersion ?? "";
            if (!string.IsNullOrEmpty(response.RulesStatus) && response.RulesStatus != "VERIFIED")
                versionLabel = versionLabel + " · " + response.RulesStatus;

            foreach (var group in response.Findings.GroupBy(f => f.Check ?? ""))
            {
                var check = new CheckVm();
                check.Title = TitleFor(group.Key);
                foreach (var dto in group)
                    check.Findings.Add(MapFinding(dto, versionLabel));
                checks.Add(check);
            }
            return checks;
        }

        private static string TitleFor(string checkKey)
        {
            string title;
            if (CheckTitles.TryGetValue(checkKey, out title)) return title;
            // Unknown check key: readable fallback, still subject-flavoured.
            var words = checkKey.Replace('_', ' ').Trim();
            if (words.Length == 0) return "Compliance";
            return char.ToUpper(words[0], CultureInfo.InvariantCulture) + words.Substring(1);
        }

        private static FindingVm MapFinding(BombaFindingDto dto, string versionLabel)
        {
            var vm = new FindingVm();
            vm.Subject = dto.Subject;
            vm.Passed = dto.Passed;                       // tri-state, straight through
            vm.Severity = dto.Passed == true ? Severity.Pass
                        : dto.Passed == false ? Severity.High
                        : Severity.NotChecked;
            vm.Action = MapAction(dto.Action);
            vm.Headline = Headline(dto);
            vm.Metrics = MetricsText(dto);
            vm.Guidance = dto.Guidance;
            vm.ClauseRef = dto.ClauseRef;
            vm.RulesVersion = versionLabel;
            vm.Jurisdiction = dto.Jurisdiction;
            vm.SchedulePath = dto.SchedulePath;
            if (dto.ElementIds != null)
                foreach (var id in dto.ElementIds) vm.ElementIds.Add(id);
            if (dto.SearchedModels != null)
                foreach (var m in dto.SearchedModels) vm.SearchedModels.Add(m);
            if (dto.Steps != null)
            {
                foreach (var s in dto.Steps)
                {
                    var step = new CalcStepVm();
                    step.Label = s.Label;
                    step.Expression = s.Expression;
                    step.ByLaw = s.ByLaw;
                    vm.Steps.Add(step);
                }
            }
            return vm;
        }

        private static FindingAction MapAction(string action)
        {
            switch (action)
            {
                case "fixable": return FindingAction.Fixable;
                case "guidance_only": return FindingAction.GuidanceOnly;
                case "needs_modelling": return FindingAction.NeedsModelling;
                default: return FindingAction.None;
            }
        }

        private static string Headline(BombaFindingDto dto)
        {
            if (dto.Passed == true)
            {
                double present;
                if (dto.Metrics != null && dto.Metrics.TryGetValue("present", out present))
                    return "Present — " + ((int)present) + " found";
                return "Requirement met";
            }
            if (dto.Passed == false)
                return "Required but not found in the models searched";
            // null: NOT CHECKED — neither pass nor fail, and the wording must
            // never read as an accusation of absence.
            return "Cannot verify — M&E model not searched";
        }

        private static string MetricsText(BombaFindingDto dto)
        {
            var lines = new List<string>();
            if (dto.Metrics != null)
                foreach (var kv in dto.Metrics)
                    lines.Add(kv.Key + "  " + kv.Value.ToString("0.##", CultureInfo.InvariantCulture));
            return string.Join("\n", lines.ToArray());
        }
    }
}

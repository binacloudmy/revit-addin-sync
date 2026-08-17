using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Bomba
{
    /// <summary>
    /// Maps the wire DTOs onto Modern Flow issues — plain language up front,
    /// the citation in small print. Tri-state survives as the tag:
    ///   passed=false + needs_modelling → NEEDS PLACING (checked, absent)
    ///   passed=null                    → CAN'T CHECK   (never a failure)
    ///   passed=true                    → not an issue; counts toward Done.
    /// Phase 1 has no automatic fixes, so no issue ever gets a Fix button —
    /// the primary action is an honest re-check after fixing in the model.
    /// </summary>
    public static class BombaMapper
    {
        public class MapResult
        {
            public List<IssueVm> Issues = new List<IssueVm>();
            public int PassCount;
            public string RulesLine = "";
        }

        public static MapResult Map(BombaCheckResponseDto response)
        {
            var result = new MapResult();
            if (response == null || response.Findings == null) return result;

            var cite = "UBBL 1984 · rules " + (response.RulesVersion ?? "?");
            if (!string.IsNullOrEmpty(response.RulesStatus) && response.RulesStatus != "VERIFIED")
                cite += " · " + response.RulesStatus + " — values show [X] until verified";
            result.RulesLine = cite;

            // Failures first, then can't-check — the wizard walks this order.
            foreach (var f in response.Findings.Where(x => x.Passed == false))
                result.Issues.Add(Missing(f, cite));
            foreach (var f in response.Findings.Where(x => !x.Passed.HasValue))
                result.Issues.Add(CantCheck(f, cite));
            result.PassCount = response.Findings.Count(x => x.Passed == true);

            return result;
        }

        private static IssueVm Missing(BombaFindingDto f, string cite)
        {
            var vm = new IssueVm
            {
                Subject = f.Subject,
                Cls = "fix",
                Tag = "NEEDS PLACING",
                TagInk = M.Amber,
                TagBg = M.AmberTint,
                Icon = "🔔",
                IconBg = M.AmberTint,
                Title = "No " + Lower(f.Subject),
                Where = "Searched: " + JoinModels(f),
                Sub = "required · none found in the models searched",
                Body = f.Guidance ?? ("The rules require a " + Lower(f.Subject)
                    + " and the models searched carry none. Place it in the model, then re-check."),
                Cite = cite,
                NoFixNote = "Placing this is modelling work — the pane never writes. Place it in the model (or ask the copilot), then re-check.",
            };
            vm.Facts.Add(new FactVm("Required", "[X]", M.Red));
            vm.Facts.Add(new FactVm("In the model", Count(f), M.Red));
            vm.Facts.Add(new FactVm("Schedule row", f.SchedulePath ?? "—", M.Ink));
            CopyIds(f, vm);

            // Fire-rating failures are a parameter write, not modelling work
            // — the one bomba finding class with a real autofix. One
            // transaction, one undo; already-compliant types are never touched.
            if (f.Check == "fire_resistance")
            {
                double requiredMin;
                if (f.Metrics != null && f.Metrics.TryGetValue("required_min", out requiredMin))
                {
                    vm.CanFix = true;
                    vm.FixRequiredMinutes = (int)requiredMin;
                    var label = vm.FixRequiredMinutes % 60 == 0
                        ? (vm.FixRequiredMinutes / 60) + " hr"
                        : vm.FixRequiredMinutes + " min";
                    vm.FixLabel = "Fix automatically — set ratings to " + label;
                    vm.Tag = "AUTO-FIXABLE";
                    vm.NoFixNote = "One click writes \"" + label + "\" into the Fire Rating "
                        + "parameter of every unrated/under-rated type in use — one "
                        + "transaction, one Ctrl+Z. Types already rated ≥ required are never touched.";
                }
            }
            return vm;
        }

        private static IssueVm CantCheck(BombaFindingDto f, string cite)
        {
            var vm = new IssueVm
            {
                Subject = f.Subject,
                Cls = "cant",
                Tag = "CAN'T CHECK",
                TagInk = M.Amber,
                TagBg = M.AmberTint,
                Icon = "⚠️",
                IconBg = M.AmberTint,
                Title = "Can't check " + Lower(f.Subject),
                Where = "The M&E model isn't linked",
                Sub = "not checked — this is not a finding of absence",
                Body = f.Guidance ?? ("Fire systems live in the M&E model. Until it's linked, absence "
                    + "proves nothing — link it and re-check. “All passed” would be a lie meanwhile."),
                Cite = cite,
                NoFixNote = "Link the M&E model in Revit, then re-check. Nothing is wrong yet — it just isn't verified.",
                DoLabel = "Re-check after linking",
            };
            vm.Facts.Add(new FactVm("Required", "[X]", M.Ink));
            vm.Facts.Add(new FactVm("Searched", JoinModels(f), M.Sub));
            vm.Facts.Add(new FactVm("Verdict", "not checked", M.Amber));
            CopyIds(f, vm);
            return vm;
        }

        private static void CopyIds(BombaFindingDto f, IssueVm vm)
        {
            if (f.ElementIds == null) return;
            foreach (var id in f.ElementIds) vm.ElementIds.Add(id);
        }

        private static string JoinModels(BombaFindingDto f)
        {
            return f.SearchedModels != null && f.SearchedModels.Count > 0
                ? string.Join(", ", f.SearchedModels.ToArray())
                : "—";
        }

        private static string Count(BombaFindingDto f)
        {
            double present;
            if (f.Metrics != null && f.Metrics.TryGetValue("present", out present))
                return ((int)present).ToString();
            return "0";
        }

        private static string Lower(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return "system";
            return char.ToLower(subject[0]) + subject.Substring(1);
        }
    }
}

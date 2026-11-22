using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.Services.JkrSpec
{
    /// <summary>One spec check's evaluator: results for that check from the snapshot.</summary>
    public delegate IEnumerable<SpecResult> SpecRule(SpecModel model);

    /// <summary>
    /// The add-in's own evaluators for the JKR Model Audit spec (JKR-Model-Audit-Spec.xlsx,
    /// "Peraturan comply"): one <see cref="SpecRule"/> per spec check id, registered by
    /// the section files (SpecChecks.A.cs, SpecChecks.BC.cs, SpecChecks.DE.cs). A check
    /// that needs data only the backend has (BPEP, the JKR AR template, the Buku
    /// Parameter rule set) is listed in <see cref="BackendOnly"/> with the reason —
    /// never given a made-up pass. Pure: no Revit, no WPF.
    /// </summary>
    public static partial class SpecChecks
    {
        private static Dictionary<string, SpecRule> _rules;
        private static Dictionary<string, string> _backendOnly;

        /// <summary>Spec check id → evaluator.</summary>
        public static IReadOnlyDictionary<string, SpecRule> Rules
        {
            get
            {
                if (_rules != null) return _rules;
                var d = new Dictionary<string, SpecRule>(StringComparer.Ordinal);
                RegisterA(d);
                RegisterBC(d);
                RegisterDE(d);
                return _rules = d;
            }
        }

        /// <summary>Spec check id → why the add-in cannot decide it alone (backend data it needs).
        /// A check may have both an evaluator (the part the model shows) and an entry here.</summary>
        public static IReadOnlyDictionary<string, string> BackendOnly
        {
            get
            {
                if (_backendOnly != null) return _backendOnly;
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                BackendA(d);
                BackendBC(d);
                BackendDE(d);
                return _backendOnly = d;
            }
        }

        // Implemented in the section files.
        static partial void RegisterA(Dictionary<string, SpecRule> rules);
        static partial void RegisterBC(Dictionary<string, SpecRule> rules);
        static partial void RegisterDE(Dictionary<string, SpecRule> rules);
        static partial void BackendA(Dictionary<string, string> reasons);
        static partial void BackendBC(Dictionary<string, string> reasons);
        static partial void BackendDE(Dictionary<string, string> reasons);

        /// <summary>
        /// Run every evaluator over the snapshot. A rule that throws gives one
        /// "could not be judged" result for its check (never a pass); results are
        /// stamped Source = Model and the check id.
        /// </summary>
        public static List<SpecResult> Run(SpecModel model)
        {
            model = (model ?? new SpecModel()).Normalize();
            var list = new List<SpecResult>();
            foreach (var kv in Rules)
            {
                List<SpecResult> got;
                try
                {
                    got = (kv.Value(model) ?? Enumerable.Empty<SpecResult>()).Where(r => r != null).ToList();
                }
                catch (Exception ex)
                {
                    got = new List<SpecResult> { Unjudged(kv.Key, "Check failed to run: " + ex.Message) };
                }
                foreach (var r in got)
                {
                    r.Source = SpecReport.Model;
                    if (string.IsNullOrEmpty(r.CheckId)) r.CheckId = kv.Key;
                    list.Add(r);
                }
            }
            return list;
        }

        // ─── result helpers (evaluators use these; ids stay stable across runs) ───

        /// <summary>The check passed. <paramref name="count"/> = results it covers (elements, levels …).</summary>
        public static SpecResult Pass(string id, string note, int count = 1, SpecCoverage coverage = SpecCoverage.Full) =>
            new SpecResult { CheckId = id, Status = "pass", Note = note ?? "", ElementCount = Math.Max(0, count), Coverage = coverage, Rule = id };

        /// <summary>One failure (an element, a level, a missing view …). <paramref name="key"/>
        /// makes the issue id stable when there is no element (e.g. a level name).</summary>
        public static SpecResult Fail(string id, string note, long elementId = 0, string elementName = "",
                                      string actual = "", string required = "", string key = null,
                                      SpecCoverage coverage = SpecCoverage.Full, IEnumerable<long> elementIds = null)
        {
            var ids = (elementIds ?? (elementId > 0 ? new[] { elementId } : Array.Empty<long>())).Where(x => x > 0).ToList();
            return new SpecResult
            {
                CheckId = id, Status = "fail", Note = note ?? "", Coverage = coverage, Rule = id,
                ElementId = elementId > 0 ? elementId : ids.FirstOrDefault(),
                ElementIds = ids, ElementCount = Math.Max(ids.Count, elementId > 0 ? 1 : 0),
                ElementName = elementName ?? "", Actual = actual ?? "", Required = required ?? "",
                IssueId = StableId(id, key ?? (elementId > 0 ? elementId.ToString() : note ?? "")),
            };
        }

        /// <summary>The model shows something the evaluator cannot judge: a person checks it.</summary>
        public static SpecResult Unjudged(string id, string note, long elementId = 0, string elementName = "", string key = null)
        {
            return new SpecResult
            {
                CheckId = id, Status = "cannot_verify", Note = note ?? "", Coverage = SpecCoverage.Full, Rule = id,
                ElementId = elementId, ElementIds = elementId > 0 ? new List<long> { elementId } : new List<long>(),
                ElementCount = elementId > 0 ? 1 : 0, ElementName = elementName ?? "",
                IssueId = StableId(id, key ?? (elementId > 0 ? elementId.ToString() : note ?? "")),
            };
        }

        /// <summary>Data for a list-only check (no verdict).</summary>
        public static SpecResult Listing(string id, string note, int count = 0) =>
            new SpecResult { CheckId = id, Status = "pass", Note = note ?? "", ElementCount = count, Coverage = SpecCoverage.Listing, Rule = id };

        /// <summary>A finding beside a human-decides check (no verdict). <paramref name="elementId"/>
        /// / <paramref name="key"/> give it an issue the person can open.</summary>
        public static SpecResult Context(string id, string note, long elementId = 0, string elementName = "", string key = null,
                                         IEnumerable<long> elementIds = null)
        {
            var withIssue = elementId > 0 || key != null;
            var ids = (elementIds ?? (elementId > 0 ? new[] { elementId } : Array.Empty<long>())).Where(x => x > 0).Distinct().ToList();
            return new SpecResult
            {
                CheckId = id, Status = withIssue ? "cannot_verify" : "pass", Note = note ?? "", Coverage = SpecCoverage.Context, Rule = id,
                ElementId = elementId > 0 ? elementId : ids.FirstOrDefault(), ElementIds = ids,
                ElementCount = ids.Count, ElementName = elementName ?? "",
                IssueId = withIssue ? StableId(id, key ?? elementId.ToString()) : "",
            };
        }

        /// <summary>The check does not apply to this model, as the spec defines (e.g. A3.2 on a W00 model).</summary>
        public static SpecResult NotApplicable(string id, string note) =>
            new SpecResult { CheckId = id, Status = "pass", Note = note ?? "", NotApplicable = true, Coverage = SpecCoverage.Full, Rule = id };

        /// <summary>"SPEC-A1.2-3f9c0a1b2c3d": the same check + key gives the same id every run,
        /// so Ignore / Reviewed decisions survive a re-scan.</summary>
        public static string StableId(string checkId, string key)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes((checkId ?? "") + "|" + (key ?? "")));
                var hex = string.Concat(h.Take(6).Select(b => b.ToString("x2")));
                return "SPEC-" + checkId + "-" + hex;
            }
        }
    }
}

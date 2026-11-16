using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>One result from a run: a backend check (every status, passes
    /// included) or a BIM010 checker record (fill_audit). The host builds these;
    /// tests build them from a fixture.</summary>
    public sealed class SpecResult
    {
        /// <summary><see cref="SpecReport.Backend"/> or <see cref="SpecReport.Audit"/>.</summary>
        public string Source = SpecReport.Backend;
        /// <summary>Id of the pane issue built from it ("" when it has none, e.g. a pass).</summary>
        public string IssueId = "";
        /// <summary>Backend: JKR or Revit category. Audit: "".</summary>
        public string Category = "";
        /// <summary>Backend: rule text. Audit: the checker's rule.</summary>
        public string Rule = "";
        /// <summary>pass | fail | warning | cannot_verify (audit: yes → pass, no → fail, else cannot_verify).</summary>
        public string Status = "";
        public long ElementId;
        /// <summary>Elements behind it (audit records name several).</summary>
        public int ElementCount;
        /// <summary>Audit: the fill_audit checker id ("file_naming", "family_category" …).</summary>
        public string CheckerId = "";
        /// <summary>Audit: the Borang row key ("A2", "C4.2", "D10").</summary>
        public string RowKey = "";
        /// <summary>Audit: the Borang row label ("Doors" for D10).</summary>
        public string RowLabel = "";
        /// <summary>The checker's remark or the backend's reason.</summary>
        public string Note = "";
        /// <summary>Audit: nothing of this kind in the model (category not modelled).</summary>
        public bool NotApplicable;
    }

    /// <summary>What a mapped result can prove about its spec check.</summary>
    public enum SpecCoverage
    {
        /// <summary>A pass proves the check passes; a fail proves it fails.</summary>
        Full,
        /// <summary>The rule tests only part of the check (e.g. materials on four
        /// categories): a fail proves a failure, a pass only "partly checked".</summary>
        FailOnly,
        /// <summary>Data for a list-only check: shown, never a verdict.</summary>
        Listing,
        /// <summary>A finding beside a check a person decides: shown, never a verdict.</summary>
        Context,
    }

    public enum SpecState
    {
        /// <summary>Verified compliant by a rule that tests the whole check.</summary>
        Pass,
        /// <summary>At least one result failed.</summary>
        Fail,
        /// <summary>Only partly covered by the scan, and that part passed.</summary>
        Partial,
        /// <summary>A person decides: a human-decides check, or a result the scan could not judge.</summary>
        NeedsPerson,
        /// <summary>A list-only check the run produced data for (no verdict).</summary>
        Listed,
        /// <summary>The run has no rule for it yet.</summary>
        NotChecked,
    }

    public enum SpecVerdict
    {
        /// <summary>At least one spec check fails.</summary>
        Fails,
        /// <summary>No spec check fails and at least one passes or is partly checked.
        /// Never "compliant": checks a person decides and checks not covered remain.</summary>
        NoFailures,
        /// <summary>Nothing in the run answers a spec check.</summary>
        NothingVerified,
    }

    public sealed class SpecCheckResult
    {
        public SpecCheck Check;
        public SpecState State;
        public int Passed, Failed, Unjudged, Listed;
        /// <summary>Some result passed under fail-only coverage (part of the check).</summary>
        public bool PartlyPassed;
        /// <summary>Issue ids behind the failed / unjudged / context results, in run order.</summary>
        public List<string> IssueIds = new List<string>();
        /// <summary>Distinct elements among the failed and unjudged results.</summary>
        public int Elements;
        /// <summary>Remarks to show (failures first, at most <see cref="SpecReport.MaxNotes"/>).</summary>
        public List<string> Notes = new List<string>();
        /// <summary>The rules that answered it (what was actually tested).</summary>
        public List<string> Rules = new List<string>();
    }

    /// <summary>Results no spec check names, grouped by source + rule, so nothing the run found is dropped.</summary>
    public sealed class OtherResult
    {
        /// <summary>"Borang C1.1" for checker records, the backend category otherwise.</summary>
        public string Where = "";
        public string Rule = "";
        public int Passed, Failed, Unjudged;
        public List<string> IssueIds = new List<string>();
        public SpecState State => Failed > 0 ? SpecState.Fail : Unjudged > 0 ? SpecState.NeedsPerson : SpecState.Pass;
    }

    public sealed class SpecSectionCount
    {
        public string Section = "";
        public int Pass, Fail, Partial, NeedsPerson, NotChecked, Listed;
    }

    /// <summary>
    /// A run measured against the JKR Model Audit spec: every one of the 61 checks
    /// gets a state; results the spec does not name go to <see cref="Others"/>.
    /// Only rules that test what a check says are mapped to it, so the checklist
    /// never shows a pass the run did not prove. WPF- and Revit-free.
    /// </summary>
    public sealed class SpecReport
    {
        public const string Backend = "backend";
        public const string Audit = "audit";
        public const int MaxNotes = 3;

        public List<SpecCheckResult> Checks = new List<SpecCheckResult>();
        public List<OtherResult> Others = new List<OtherResult>();
        public List<SpecSectionCount> Sections = new List<SpecSectionCount>();

        public int PassChecks => Count(SpecState.Pass);
        public int FailChecks => Count(SpecState.Fail);
        public int PartialChecks => Count(SpecState.Partial);
        public int NeedsPersonChecks => Count(SpecState.NeedsPerson);
        public int ListedChecks => Count(SpecState.Listed);
        /// <summary>Checks the run has no rule for (list-only ones included).</summary>
        public int NotCheckedChecks => Count(SpecState.NotChecked);
        public int OtherFailing => Others.Count(o => o.State == SpecState.Fail);
        private int Count(SpecState s) => Checks.Count(c => c.State == s);

        public SpecVerdict Verdict =>
            FailChecks > 0 ? SpecVerdict.Fails
            : PassChecks + PartialChecks > 0 ? SpecVerdict.NoFailures
            : SpecVerdict.NothingVerified;

        public SpecCheckResult For(string id) => Checks.FirstOrDefault(c => c.Check.Id == id);
        public SpecSectionCount Section(string key) => Sections.FirstOrDefault(s => s.Section == key);

        // ─── status ───
        public static bool IsPass(string s) => Norm(s) == "pass";
        public static bool IsFail(string s) { var n = Norm(s); return n == "fail" || n == "warning"; }
        private static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

        /// <summary>fill_audit compliance → status: yes → pass, no → fail, anything else → cannot_verify.</summary>
        public static string StatusOfCompliance(string compliance) =>
            compliance == "yes" ? "pass" : compliance == "no" ? "fail" : "cannot_verify";

        // ─── backend rule → spec check ───

        private static readonly Regex LodComplete = new Regex(@"^lod \d+ required parameters complete$", RegexOptions.Compiled);

        /// <summary>The spec check a backend result answers and what it proves, or null.</summary>
        public static (string Id, SpecCoverage Coverage)? MapBackend(string category, string rule, long elementId)
        {
            var cat = (category ?? "").Trim();
            var r = (rule ?? "").Trim().ToLowerInvariant();
            switch (cat)
            {
                case "Project Naming":
                    // Compares the user's discipline pick with the file, not the file name with the guidelines.
                    if (r == "selected discipline matches file") return null;
                    return ("A2.1", SpecCoverage.Full);
                case "Project Base Point": return ("A4.1", SpecCoverage.Full);
                // A fixed set of fields, not the Buku Parameter list filtered by LOD.
                case "Project Information": return ("B1.1", SpecCoverage.FailOnly);
            }
            // Share of JKR-named elements: a low share proves generic names; a high one does not prove all.
            if (r == "element naming adoption") return ("Da.1", SpecCoverage.FailOnly);
            if (elementId <= 0) return null;
            switch (r)
            {
                case "element has a name":
                case "naming convention":
                case "valid discipline code in element name":
                case "valid subtype code":
                    return ("Da.1", SpecCoverage.Full);
                case "category code matches revit category":
                case "discipline matches category":
                    return ("Dd.1", SpecCoverage.Full);
                case "parameter rules defined":
                    return ("Db.1", SpecCoverage.Full);
                case "material code in name is valid":
                    return ("Db.2", SpecCoverage.Full);
            }
            if ((r.StartsWith("missing ") || r.StartsWith("empty ")) && r.Contains(" parameter:")) return ("Db.1", SpecCoverage.Full);
            if (LodComplete.IsMatch(r)) return ("Db.1", SpecCoverage.Full);
            if (r.EndsWith(" material code valid") || r.StartsWith("material code in name matches")) return ("Db.2", SpecCoverage.Full);
            return null;
        }

        // ─── BIM010 checker → spec check ───

        /// <summary>
        /// The spec check a fill_audit record answers, or null. Checkers that test
        /// the spec rule itself are Full; checkers that test part of it are
        /// FailOnly; checkers that feed a list-only or human-decides check are
        /// Listing / Context and never give a verdict.
        /// </summary>
        public static (string Id, SpecCoverage Coverage)? MapAudit(string checkerId, string rowKey, string rowLabel, bool notApplicable)
        {
            var key = (rowKey ?? "").Trim();
            if (key.StartsWith("D", StringComparison.OrdinalIgnoreCase))
            {
                var label = (rowLabel ?? "").Trim();
                if (string.Equals(label, "Room", StringComparison.OrdinalIgnoreCase)) return ("De.1", SpecCoverage.Listing);
                if (notApplicable)
                {
                    return label.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0
                        ? ("De.2", SpecCoverage.Context)
                        : ("De.3", SpecCoverage.Listing);
                }
                // JKR code / jkrAR prefix / bare material name: looser than JKR coding.
                return ("Da.1", SpecCoverage.FailOnly);
            }
            if (notApplicable) return null;

            switch (checkerId ?? "")
            {
                case "file_naming": return ("A2.1", SpecCoverage.Full);
                case "base_point": return ("A4.1", SpecCoverage.Full);
                case "grids_levels": return ("A5.1", SpecCoverage.Listing);
                case "rooms_department": return ("A6.3", SpecCoverage.Context);
                case "materials_assigned": return ("A7.1", SpecCoverage.FailOnly);   // walls/floors/ceilings/roofs only
                case "project_info": return ("B1.1", SpecCoverage.FailOnly);         // six fields, not the PB list by LOD
                case "views_wip": return ("C1.2.1", SpecCoverage.Listing);
                case "views_pbt": return ("C1.3.1", SpecCoverage.FailOnly);          // a PBT view exists, not the three types
                case "views_bomba": return ("C1.4.1", SpecCoverage.FailOnly);
                case "views_dokumen": return ("C1.5.1", SpecCoverage.Listing);
                case "legends": return ("C2.1.1", SpecCoverage.Full);
                case "sheets_contents": return ("C4.1.3", SpecCoverage.Full);
                case "titleblock_jkr": return ("C4.2.1", SpecCoverage.Full);
                case "links_current": return ("C5.1.1", SpecCoverage.FailOnly);      // loaded, not ST/ME/EL present
                case "view_template_applied": return ("E.1", SpecCoverage.FailOnly);  // a template, not the right set
                default: return null;   // views_template, area_plans, schedules_required: other rules
            }
        }

        // ─── build ───

        public static SpecReport Build(IEnumerable<SpecResult> results)
        {
            var report = new SpecReport();
            var byId = SpecCatalog.Checks.ToDictionary(c => c.Id, c => new SpecCheckResult { Check = c });
            var elements = byId.Keys.ToDictionary(k => k, k => new HashSet<string>());
            var failNotes = byId.Keys.ToDictionary(k => k, k => new List<string>());
            var otherNotes = byId.Keys.ToDictionary(k => k, k => new List<string>());
            var others = new Dictionary<string, OtherResult>();

            foreach (var r in results ?? Enumerable.Empty<SpecResult>())
            {
                if (r == null) continue;
                var audit = r.Source == Audit;
                var map = audit ? MapAudit(r.CheckerId, r.RowKey, r.RowLabel, r.NotApplicable)
                                : MapBackend(r.Category, r.Rule, r.ElementId);
                if (map == null)
                {
                    if (r.NotApplicable) continue;
                    var where = audit ? "Borang " + r.RowKey : (string.IsNullOrEmpty(r.Category) ? "JKR" : r.Category);
                    var key = where + "\u0001" + r.Rule;
                    if (!others.TryGetValue(key, out var o)) others[key] = o = new OtherResult { Where = where, Rule = r.Rule ?? "" };
                    if (IsPass(r.Status)) o.Passed++;
                    else
                    {
                        if (IsFail(r.Status)) o.Failed++; else o.Unjudged++;
                        if (!string.IsNullOrEmpty(r.IssueId)) o.IssueIds.Add(r.IssueId);
                    }
                    continue;
                }

                var (id, cov) = map.Value;
                var res = byId[id];
                var rule = audit ? (string.IsNullOrEmpty(r.RowKey) ? r.Rule : r.RowKey + ": " + r.Rule) : r.Rule;
                if (!string.IsNullOrWhiteSpace(rule) && !res.Rules.Contains(rule) && res.Rules.Count < 6) res.Rules.Add(rule);
                var note = Clean(r.Note);

                if (cov == SpecCoverage.Listing || cov == SpecCoverage.Context)
                {
                    res.Listed++;
                    if (note.Length > 0) otherNotes[id].Add(r.NotApplicable && !string.IsNullOrEmpty(r.RowLabel) ? r.RowLabel + ": " + note : note);
                    if (cov == SpecCoverage.Context && !IsPass(r.Status) && !string.IsNullOrEmpty(r.IssueId)) res.IssueIds.Add(r.IssueId);
                    continue;
                }

                if (IsPass(r.Status))
                {
                    if (cov == SpecCoverage.Full) res.Passed++; else res.PartlyPassed = true;
                    continue;
                }
                if (IsFail(r.Status)) res.Failed++; else res.Unjudged++;
                if (!string.IsNullOrEmpty(r.IssueId)) res.IssueIds.Add(r.IssueId);
                if (r.ElementId > 0) elements[id].Add(r.Source + ":" + r.ElementId);
                else if (r.ElementCount > 0) elements[id].Add(r.Source + ":" + r.RowKey + ":" + r.ElementCount);
                if (note.Length > 0) (IsFail(r.Status) ? failNotes : otherNotes)[id].Add(note);
            }

            foreach (var check in SpecCatalog.Checks)
            {
                var res = byId[check.Id];
                res.IssueIds = res.IssueIds.Distinct().ToList();
                res.Elements = elements[check.Id].Count;
                res.Notes = failNotes[check.Id].Concat(otherNotes[check.Id]).Distinct().Take(MaxNotes).ToList();
                res.State = StateOf(check.Class, res);
                report.Checks.Add(res);
            }

            report.Others = others.Values
                .OrderByDescending(o => o.Failed).ThenByDescending(o => o.Unjudged)
                .ThenBy(o => o.Where, StringComparer.Ordinal).ThenBy(o => o.Rule, StringComparer.Ordinal)
                .ToList();

            foreach (var sec in PanelCore.Secs)
            {
                var inSec = report.Checks.Where(c => c.Check.Section == sec).ToList();
                report.Sections.Add(new SpecSectionCount
                {
                    Section = sec,
                    Pass = inSec.Count(c => c.State == SpecState.Pass),
                    Fail = inSec.Count(c => c.State == SpecState.Fail),
                    Partial = inSec.Count(c => c.State == SpecState.Partial),
                    NeedsPerson = inSec.Count(c => c.State == SpecState.NeedsPerson),
                    Listed = inSec.Count(c => c.State == SpecState.Listed),
                    NotChecked = inSec.Count(c => c.State == SpecState.NotChecked),
                });
            }
            return report;
        }

        /// <summary>
        /// Human-decides → needs a person, always. List-only → listed when the run
        /// has data for it, else not checked. AI-verifiable → fail if any result
        /// failed; else needs a person if any could not be judged; else pass if a
        /// rule that tests the whole check passed; else partly checked if a
        /// partial rule passed; else not checked.
        /// </summary>
        private static SpecState StateOf(SpecCellClass cls, SpecCheckResult r)
        {
            if (cls == SpecCellClass.Human) return SpecState.NeedsPerson;
            if (cls == SpecCellClass.List) return r.Listed > 0 ? SpecState.Listed : SpecState.NotChecked;
            if (r.Failed > 0) return SpecState.Fail;
            if (r.Unjudged > 0) return SpecState.NeedsPerson;
            if (r.Passed > 0) return SpecState.Pass;
            if (r.PartlyPassed) return SpecState.Partial;
            return SpecState.NotChecked;
        }

        private static string Clean(string s) =>
            string.Join(" ", (s ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}

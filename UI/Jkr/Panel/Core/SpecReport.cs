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
        /// <summary>The checker's remark or the backend's reason (BM for model results).</summary>
        public string Note = "";
        /// <summary>English note (model results); "" = show <see cref="Note"/> in both languages.</summary>
        public string NoteEn = "";
        /// <summary>Audit: nothing of this kind in the model (category not modelled).</summary>
        public bool NotApplicable;

        // ── Model results (Source = Model): the add-in's own spec evaluators
        //    (Services/JkrSpec) answer a spec check directly, no mapping. ──
        /// <summary>Model: the spec check id this result answers ("A1.2").</summary>
        public string CheckId = "";
        /// <summary>Model: what the result proves (null = Full).</summary>
        public SpecCoverage? Coverage;
        /// <summary>Model: what the model has ("850 mm") — shown on the issue.</summary>
        public string Actual = "";
        /// <summary>Model: <see cref="Actual"/> in English ("" = same as BM).</summary>
        public string ActualEn = "";
        /// <summary>Model: what the spec requires ("≥ 900 mm").</summary>
        public string Required = "";
        /// <summary>Model: element label ("Doors · PT1p_850x2100").</summary>
        public string ElementName = "";
        /// <summary>Model: Revit element ids behind the result (first one is located).</summary>
        public List<long> ElementIds = new List<long>();
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
        /// <summary>Does not apply to this model, as the spec defines (e.g. A3.2 on a single W00 model).
        /// Not in the %, not pending.</summary>
        NotApplicable,
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
        /// <summary>AI-504: findings with an issue that a person decides, beside an AI-verifiable check
        /// (Context results: a value that only reads as the minimum when rounded, a slab without a room,
        /// a prefixed grid …). Never a verdict. A check with nothing else judged counts as passed in the %
        /// (AI-474: what a person decides counts as pass), see <see cref="SpecChecklist.TallyOf(SpecReport, Func{SpecCheck, bool})"/>.</summary>
        public int PersonRows;
        /// <summary>Results saying the check does not apply to this model.</summary>
        public int NotApplicableHits;
        /// <summary>Some result passed under fail-only coverage (part of the check).</summary>
        public bool PartlyPassed;
        /// <summary>Issue ids behind the failed / unjudged / context results, in run order.</summary>
        public List<string> IssueIds = new List<string>();
        /// <summary>Distinct elements among the failed and unjudged results.</summary>
        public int Elements;
        /// <summary>Remarks for the Borang (failures first, at most <see cref="SpecReport.MaxNotes"/>,
        /// or <see cref="SpecReport.MaxListNotes"/> for a list-only check), BM.</summary>
        public List<string> Notes = new List<string>();
        /// <summary>The same remarks in English (a remark with no English text repeats the BM one).</summary>
        public List<string> NotesEn = new List<string>();
        /// <summary>Every remark, uncapped, in the order of <see cref="Notes"/> (BM): the
        /// Checklist lists them all and the pane puts every issue's text in both languages.</summary>
        public List<string> AllNotes = new List<string>();
        /// <summary><see cref="AllNotes"/> in English (a remark with no English text repeats the BM one).</summary>
        public List<string> AllNotesEn = new List<string>();
        /// <summary>The rules that answered it (what was actually tested).</summary>
        public List<string> Rules = new List<string>();
        /// <summary>Backend results mapped here that do not count because the add-in's own
        /// evaluator already judged the check ("backend: Naming convention"): shown in
        /// "Why?" only, never in the counts, notes or element list. (A BIM010 checker
        /// record on a check the evaluators answered is dropped, not named.)</summary>
        public List<string> NotCounted = new List<string>();
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
        /// <summary>Checks that do not apply to this model (not in the other counts).</summary>
        public int NotApplicable;
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
        /// <summary>The add-in's spec evaluators over the model snapshot (Services/JkrSpec).</summary>
        public const string Model = "model";
        /// <summary>Remarks a check keeps in <see cref="SpecCheckResult.Notes"/> (the Borang's
        /// remark cell; the Checklist shows this many above the element rows and says how
        /// many there are). <see cref="SpecCheckResult.AllNotes"/> keeps every one.</summary>
        public const int MaxNotes = 3;
        /// <summary>A list-only check's notes are its list (WIP views, sheets, rooms per level):
        /// Notes keep up to this many rows instead of <see cref="MaxNotes"/>; the Checklist
        /// lists them all (<see cref="SpecCheckResult.AllNotes"/>).</summary>
        public const int MaxListNotes = 60;

        public List<SpecCheckResult> Checks = new List<SpecCheckResult>();
        public List<OtherResult> Others = new List<OtherResult>();
        public List<SpecSectionCount> Sections = new List<SpecSectionCount>();
        /// <summary>The backend check failed or timed out (not 401): its message, else "".
        /// Checks that only the backend answers are then "not checked", never passed.</summary>
        public string BackendError = "";
        public bool BackendUnavailable => !string.IsNullOrEmpty(BackendError);
        /// <summary>AI-506: the sources of the run that gave nothing ("backend", "BIM010", "spec"). A run with any is partial:
        /// its board is not final, and Fix all is not offered from it.</summary>
        public List<string> MissingSources = new List<string>();
        public bool BackendMissing => BackendUnavailable || MissingSources.Contains("backend");
        /// <summary>The missing sources other than the backend (BIM010 checker, spec read).</summary>
        public List<string> OtherMissing => MissingSources.Where(x => x != "backend").ToList();
        public bool IsPartial => BackendUnavailable || MissingSources.Count > 0;
        /// <summary>How many sources are missing (the backend counts once, however it is reported).</summary>
        public int MissingCount => MissingSources.Union(BackendUnavailable ? new[] { "backend" } : new string[0]).Count();

        public int PassChecks => Count(SpecState.Pass);
        public int FailChecks => Count(SpecState.Fail);
        public int PartialChecks => Count(SpecState.Partial);
        public int NeedsPersonChecks => Count(SpecState.NeedsPerson);
        public int ListedChecks => Count(SpecState.Listed);
        public int NotApplicableChecks => Count(SpecState.NotApplicable);
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
        // Known statuses: pass, fail, warning, cannot_verify (+ not_applicable).
        // Anything else is "could not be judged": never a pass, never a fail.
        // A backend "warning" counts as a pass (Ashraf, 20 Nov, AI-466): in the %, in the
        // gagal count and in OtherFailing. The Issues tab still lists it: see IsWarning.
        public static bool IsPass(string s) { var n = Norm(s); return n == "pass" || n == "warning"; }
        public static bool IsFail(string s) => Norm(s) == "fail";
        /// <summary>"warning": scored as a pass, but still a finding that gives an issue.</summary>
        public static bool IsWarning(string s) => Norm(s) == "warning";
        /// <summary>"not_applicable" / "n/a": the check does not apply (never a pass).</summary>
        public static bool IsNotApplicableStatus(string s)
        {
            var n = Norm(s);
            return n == "not_applicable" || n == "n/a" || n == "na" || n == "not applicable";
        }
        private static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

        /// <summary>The AI-verifiable spec checks <see cref="MapBackend"/> maps to. When the
        /// backend failed, these are "not checked" unless a model or checker result answers
        /// them, so a missing backend never reads as a pass. C5.1.3 (link versions) is a
        /// MapBackend target too, but a person decides it whether or not the backend
        /// answers, so it is not listed.</summary>
        public static readonly IReadOnlyList<string> BackendMappedChecks =
            new[] { "A2.1", "A4.1", "B1.1", "Da.1", "Db.1", "Db.2", "Dd.1" };

        /// <summary>Checks whose backend results are several rules about one thing (A2.1: "File
        /// name provided", "… 'jkr' prefix", "Valid discipline code in file name", "File naming
        /// convention" all judge the one file name): they count as ONE result toward the % —
        /// fail if any fails, else unjudged if any could not be judged, else pass. Every note
        /// and issue is kept.</summary>
        public static readonly IReadOnlyList<string> BackendOneResultChecks = new[] { "A2.1" };

        /// <summary>The source a result came from: <see cref="Backend"/> (also for null / ""),
        /// <see cref="Audit"/>, <see cref="Model"/>, or "" for an unknown source.</summary>
        public static string SourceOf(string source)
        {
            var s = Norm(source);
            if (s.Length == 0 || s == Backend) return Backend;
            if (s == Audit) return Audit;
            if (s == Model) return Model;
            return "";
        }

        /// <summary>The catalog id for a model result's check id ("a1.2 " → "A1.2"), or null.</summary>
        public static string ResolveCheckId(string id)
        {
            var t = (id ?? "").Trim();
            if (t.Length == 0) return null;
            return SpecCatalog.Find(t)?.Id
                   ?? SpecCatalog.Checks.FirstOrDefault(c => string.Equals(c.Id, t, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        /// <summary>fill_audit compliance → status: yes → pass, no → fail, anything else → cannot_verify.</summary>
        public static string StatusOfCompliance(string compliance) =>
            compliance == "yes" ? "pass" : compliance == "no" ? "fail" : "cannot_verify";

        // ─── backend rule → spec check ───

        private static readonly Regex LodComplete = new Regex(@"^lod \d+ required parameters complete$", RegexOptions.Compiled);
        private static readonly Regex LinkWord = new Regex(@"\b(?:rvt\s+)?link(?:s|ed)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex VersionWord = new Regex(@"\b(?:version|versions|latest|up[- ]to[- ]date|current|versi|terkini)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>A result about whether linked files are the latest version ("Link files
        /// are latest version"): a rule naming links and their version, or a Project
        /// Information result whose rule or text is about links. Only a person can judge
        /// a link's version (C5.1.3), so it is never a Project Information field (B1.1).</summary>
        public static bool IsLinkVersion(string category, string rule, string note)
        {
            var r = rule ?? "";
            if (LinkWord.IsMatch(r) && VersionWord.IsMatch(r)) return true;
            return string.Equals((category ?? "").Trim(), "Project Information", StringComparison.OrdinalIgnoreCase)
                   && (LinkWord.IsMatch(r) || LinkWord.IsMatch(note ?? ""));
        }

        /// <summary>The spec check a backend result answers and what it proves, or null.</summary>
        public static (string Id, SpecCoverage Coverage)? MapBackend(string category, string rule, long elementId) =>
            MapBackend(category, rule, elementId, null);

        /// <summary>As <see cref="MapBackend(string, string, long)"/>; <paramref name="note"/> (the
        /// backend's reason) tells a link-version result filed under Project Information.</summary>
        public static (string Id, SpecCoverage Coverage)? MapBackend(string category, string rule, long elementId, string note)
        {
            var cat = (category ?? "").Trim();
            var r = (rule ?? "").Trim().ToLowerInvariant();
            // Link versions: a person checks them against the CDE (spec C5.1.3).
            if (IsLinkVersion(cat, rule, note)) return ("C5.1.3", SpecCoverage.Context);
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

        /// <summary>Longest backend error kept in a check's note.</summary>
        private const int MaxErrorInNote = 160;

        public static SpecReport Build(IEnumerable<SpecResult> results) => Build(results, null);

        /// <summary>
        /// Every catalog check gets a state from the results. Never throws on bad
        /// input: null results / fields are skipped or read as empty, unknown
        /// sources, checker ids and model check ids go to <see cref="Others"/>,
        /// unknown statuses count as "could not be judged" (never a pass), and an
        /// exact duplicate result counts once. <paramref name="backendError"/> = the
        /// backend failed or timed out (not 401): the checks in
        /// <see cref="BackendMappedChecks"/> that no model / checker result answers
        /// stay "not checked", with the error as their note.
        /// </summary>
        public static SpecReport Build(IEnumerable<SpecResult> results, string backendError)
        {
            var report = new SpecReport { BackendError = Clean(backendError) };
            var byId = SpecCatalog.Checks.ToDictionary(c => c.Id, c => new SpecCheckResult { Check = c });
            var elements = byId.Keys.ToDictionary(k => k, k => new HashSet<string>());
            var failNotes = byId.Keys.ToDictionary(k => k, k => new List<string>());
            var otherNotes = byId.Keys.ToDictionary(k => k, k => new List<string>());
            // Checks a model or checker result answered (not the backend).
            var local = new HashSet<string>(StringComparer.Ordinal);
            // BM note → EN note, for the remarks that have both.
            var en = new Dictionary<string, string>(StringComparer.Ordinal);
            var others = new Dictionary<string, OtherResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // BackendOneResultChecks: the backend results gathered into one (see below).
            var grouped = new Dictionary<string, OneResult>(StringComparer.Ordinal);
            // Checks a model or checker result passed under a rule that tests the whole check,
            // and checks one passed only partly. A backend pass is per element (Da.1, Db.1,
            // Db.2, Dd.1): it never lifts a check the local results prove only partly to Pass.
            var localFull = new HashSet<string>(StringComparer.Ordinal);
            var localPartial = new HashSet<string>(StringComparer.Ordinal);

            var input = (results ?? Enumerable.Empty<SpecResult>()).Where(r => r != null).ToList();

            // AI-verifiable checks the add-in's own evaluators judged: a pass or a fail
            // under a rule that gives verdicts (Full / FailOnly), not "does not apply", not
            // "could not be judged". Of those, the ones with a Full judged result.
            var modelJudged = new HashSet<string>(StringComparer.Ordinal);
            var modelJudgedFull = new HashSet<string>(StringComparer.Ordinal);
            // List-only and human-decides checks the evaluators answered: a listing, a
            // context finding, a pass / fail or "does not apply" — anything but "could not
            // be judged" under a verdict rule (the shape of a snapshot that failed to read).
            var modelAnswered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in input)
            {
                if (SourceOf(r.Source) != Model) continue;
                var cid = ResolveCheckId(r.CheckId);
                if (cid == null || !byId.TryGetValue(cid, out var c)) continue;
                var na = r.NotApplicable || IsNotApplicableStatus(r.Status);
                var cov = r.Coverage ?? SpecCoverage.Full;
                var verdictRule = cov == SpecCoverage.Full || cov == SpecCoverage.FailOnly;
                var judged = IsPass(r.Status) || IsFail(r.Status);
                if (c.Check.Class != SpecCellClass.Ai)
                {
                    if (na || judged || !verdictRule) modelAnswered.Add(cid);
                    continue;
                }
                // A finding a person decides (AI-504) never outranks a checker or backend result: only a
                // judged result (pass / fail) does. A Context row must not hide a real fail beside it.
                if (na || !judged || !verdictRule) continue;
                modelJudged.Add(cid);
                if (cov == SpecCoverage.Full) modelJudgedFull.Add(cid);
            }

            // One rule, one count. The evaluators read the same model the BIM010 checkers
            // and the backend read. Once an evaluator answered a check, its BIM010 checker
            // record is dropped altogether — counts, notes, issue, elements: it says the same
            // thing again (C4.1.3: the evaluator's 4 empty sheets and the checker's "4/25
            // sheets empty" are not 5 failures), or contradicts it (the view checkers find a
            // set by view name only; the evaluators read the Project Browser folders). It
            // stays only where no local result answers the check (e.g. the snapshot failed).
            // A backend result on a check an evaluator judged does not count either, and is
            // named in "Why?" (its issue is still on the Open tab). Exception: a backend
            // result on one element, under a rule that tests the whole check for that
            // element, beside an evaluator that proves failures only (FailOnly): it proves
            // what the evaluator cannot (that element against the full rule), so it counts —
            // passes too, so the % is not one-sided.
            bool Outranked(string source, string id, SpecCoverage mapped, SpecResult r)
            {
                if (source == Model) return false;
                if (source == Audit) return modelJudged.Contains(id) || modelAnswered.Contains(id);
                if (!modelJudged.Contains(id)) return false;
                if (mapped != SpecCoverage.Full && mapped != SpecCoverage.FailOnly) return false;
                if (source == Backend && r.ElementId > 0 && mapped == SpecCoverage.Full && !modelJudgedFull.Contains(id)) return false;
                return true;
            }
            void NotCounted(SpecCheckResult res, string source, SpecResult r)
            {
                if (source == Audit) return;   // dropped, not named (see above)
                var rl = Clean(r.Rule);
                var label = ("backend: " + (rl.Length > 0 ? rl : Clean(r.Category))).Trim();
                if (!res.NotCounted.Contains(label)) res.NotCounted.Add(label);
            }

            foreach (var r in input)
            {
                var source = SourceOf(r.Source);
                if (!seen.Add(DedupKey(r, source))) continue;   // the same result twice counts once
                var audit = source == Audit;
                var na = r.NotApplicable || IsNotApplicableStatus(r.Status);

                (string Id, SpecCoverage Coverage)? map;
                if (source == Model)
                {
                    var cid = ResolveCheckId(r.CheckId);
                    map = cid != null ? (cid, r.Coverage ?? SpecCoverage.Full) : ((string, SpecCoverage)?)null;
                }
                else if (audit)
                {
                    map = MapAudit(r.CheckerId, r.RowKey, r.RowLabel, na);
                    if (map == null && na)
                    {
                        // A checker that tests an AI check found nothing of its kind in the
                        // model: the check does not apply, unless another result answers it.
                        var m = MapAudit(r.CheckerId, r.RowKey, r.RowLabel, false);
                        if (m != null && m.Value.Coverage != SpecCoverage.Listing && m.Value.Coverage != SpecCoverage.Context
                            && byId.TryGetValue(m.Value.Id, out var target) && target.Check.Class == SpecCellClass.Ai)
                        {
                            if (Outranked(source, m.Value.Id, m.Value.Coverage, r)) { NotCounted(target, source, r); continue; }
                            target.NotApplicableHits++;
                            local.Add(m.Value.Id);
                            var n = Clean(r.Note);
                            if (n.Length > 0) otherNotes[m.Value.Id].Add(n);
                        }
                        continue;
                    }
                }
                else if (source == Backend) map = MapBackend(r.Category, r.Rule, r.ElementId, r.Note);
                else map = null;

                if (map == null || !byId.ContainsKey(map.Value.Id))
                {
                    if (na) continue;
                    var where = WhereOf(r, source);
                    var rl = r.Rule ?? "";
                    if (rl.Length == 0 && source == Model) rl = (r.CheckId ?? "").Trim();
                    var key = where + "\u0001" + rl;
                    if (!others.TryGetValue(key, out var o)) others[key] = o = new OtherResult { Where = where, Rule = rl };
                    if (IsPass(r.Status))
                    {
                        o.Passed++;
                        if (IsWarning(r.Status) && !string.IsNullOrEmpty(r.IssueId) && !o.IssueIds.Contains(r.IssueId)) o.IssueIds.Add(r.IssueId);
                    }
                    else
                    {
                        if (IsFail(r.Status)) o.Failed++; else o.Unjudged++;
                        if (!string.IsNullOrEmpty(r.IssueId) && !o.IssueIds.Contains(r.IssueId)) o.IssueIds.Add(r.IssueId);
                    }
                    continue;
                }

                var (id, cov) = map.Value;
                var res = byId[id];
                // A list-only check never gets a verdict, a human-decides one never a ✓/✗:
                // whatever answers them is data / context.
                if (res.Check.Class == SpecCellClass.List && cov != SpecCoverage.Context) cov = SpecCoverage.Listing;
                else if (res.Check.Class == SpecCellClass.Human && cov != SpecCoverage.Listing) cov = SpecCoverage.Context;
                if (Outranked(source, id, cov, r)) { NotCounted(res, source, r); continue; }
                if (source != Backend) local.Add(id);

                var note = Clean(r.Note);
                var noteEn = Clean(r.NoteEn);
                // Data, never a verdict: a list-only row, a check a person decides, or a
                // listing / context result keeps what was found and drops "— patuh".
                if (cov == SpecCoverage.Listing || cov == SpecCoverage.Context || res.Check.Class != SpecCellClass.Ai)
                {
                    note = StripVerdict(note);
                    noteEn = StripVerdict(noteEn);
                }
                if (note.Length > 0 && noteEn.Length > 0 && !en.ContainsKey(note)) en[note] = noteEn;
                if (na && !audit)
                {
                    res.NotApplicableHits++;
                    if (note.Length > 0) otherNotes[id].Add(note);
                    continue;
                }
                var rule = audit ? (string.IsNullOrEmpty(r.RowKey) ? r.Rule : r.RowKey + ": " + r.Rule) : r.Rule;
                if (!string.IsNullOrWhiteSpace(rule) && !res.Rules.Contains(rule) && res.Rules.Count < 6) res.Rules.Add(rule);

                if (cov == SpecCoverage.Listing || cov == SpecCoverage.Context)
                {
                    res.Listed++;
                    if (note.Length > 0) otherNotes[id].Add(r.NotApplicable && !string.IsNullOrEmpty(r.RowLabel) ? r.RowLabel + ": " + note : note);
                    // A finding beside a check a person decides: its issue opens from the check.
                    if (cov == SpecCoverage.Context && !string.IsNullOrEmpty(r.IssueId)) res.IssueIds.Add(r.IssueId);
                    if (cov == SpecCoverage.Context && res.Check.Class == SpecCellClass.Ai && !string.IsNullOrEmpty(r.IssueId)) res.PersonRows++;
                    continue;
                }

                var one = source == Backend && BackendOneResultChecks.Contains(id);
                if (one && !grouped.ContainsKey(id)) grouped[id] = new OneResult();
                if (IsPass(r.Status))
                {
                    if (IsWarning(r.Status) && !string.IsNullOrEmpty(r.IssueId)) res.IssueIds.Add(r.IssueId);
                    if (source != Backend) (cov == SpecCoverage.Full ? localFull : localPartial).Add(id);
                    if (one) { if (cov == SpecCoverage.Full) grouped[id].Passed = true; else grouped[id].Partly = true; }
                    else if (cov == SpecCoverage.Full) res.Passed++;
                    else res.PartlyPassed = true;
                    continue;
                }
                if (one) { if (IsFail(r.Status)) grouped[id].Failed = true; else grouped[id].Unjudged = true; }
                else if (IsFail(r.Status)) res.Failed++;
                else res.Unjudged++;
                if (!string.IsNullOrEmpty(r.IssueId)) res.IssueIds.Add(r.IssueId);
                // Every element behind the result (a type's placed instances), not only the
                // first one Locate goes to: "· n elements" is the real total.
                var ids = SpecModelIssues.ElementIdsOf(r);
                if (ids.Count > 0)
                    foreach (var x in ids) elements[id].Add(source + ":" + x);
                else if (r.ElementCount > 0) elements[id].Add(source + ":" + r.RowKey + ":" + r.CheckId + ":" + r.ElementCount);
                if (note.Length > 0) (IsFail(r.Status) ? failNotes : otherNotes)[id].Add(note);
            }

            foreach (var kv in grouped)
            {
                var res = byId[kv.Key];
                var g = kv.Value;
                if (g.Failed) res.Failed++;
                else if (g.Unjudged) res.Unjudged++;
                else if (g.Passed) res.Passed++;
                else if (g.Partly) res.PartlyPassed = true;
            }

            var errBm = "";
            if (report.BackendUnavailable)
            {
                var err = report.BackendError.Length > MaxErrorInNote
                    ? report.BackendError.Substring(0, MaxErrorInNote).TrimEnd() + "…"
                    : report.BackendError;
                errBm = "Backend tidak tersedia: " + err;
                en[errBm] = "Backend unavailable: " + err;
            }

            foreach (var check in SpecCatalog.Checks)
            {
                var res = byId[check.Id];
                res.IssueIds = res.IssueIds.Distinct().ToList();
                res.Elements = elements[check.Id].Count;
                var notes = failNotes[check.Id].Concat(otherNotes[check.Id]).Distinct();
                res.State = StateOf(check.Class, res);
                // Backend passes alone over a local result that proves only part of the check:
                // partly checked, never a pass (fail > unjudged > pass is StateOf's own order).
                if (res.State == SpecState.Pass && localPartial.Contains(check.Id) && !localFull.Contains(check.Id))
                    res.State = SpecState.Partial;
                if (report.BackendUnavailable && check.Class == SpecCellClass.Ai
                    && BackendMappedChecks.Contains(check.Id) && !local.Contains(check.Id))
                {
                    // Only the backend answers it, and the backend did not: not checked, never passed.
                    res.State = SpecState.NotChecked;
                    notes = new[] { errBm }.Concat(notes.Where(n => n != errBm)).ToList();
                }
                // Notes (the Borang's remarks) are capped; AllNotes keeps every one, so the
                // pane never shows fewer than the run found, nor loses a remark's English.
                res.AllNotes = notes.ToList();
                res.AllNotesEn = res.AllNotes.Select(n => en.TryGetValue(n, out var e) ? e : n).ToList();
                var cap = check.Class == SpecCellClass.List ? MaxListNotes : MaxNotes;
                res.Notes = res.AllNotes.Take(cap).ToList();
                res.NotesEn = res.AllNotesEn.Take(cap).ToList();
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
                    NotApplicable = inSec.Count(c => c.State == SpecState.NotApplicable),
                });
            }
            return report;
        }

        /// <summary>Several backend results of one <see cref="BackendOneResultChecks"/> check, as one.</summary>
        private sealed class OneResult { public bool Passed, Partly, Failed, Unjudged; }

        /// <summary>Where an unmapped result is listed under Others.</summary>
        private static string WhereOf(SpecResult r, string source)
        {
            if (source == Audit) return "Borang " + (r.RowKey ?? "").Trim();
            if (source == Model)
            {
                var id = (r.CheckId ?? "").Trim();
                return id.Length == 0 ? "Model" : "Model " + id;
            }
            if (source == Backend) return string.IsNullOrEmpty(r.Category) ? "JKR" : r.Category;
            return string.IsNullOrWhiteSpace(r.Source) ? "?" : r.Source.Trim();
        }

        /// <summary>Two results with this key are the same result (sent twice).</summary>
        private static string DedupKey(SpecResult r, string source)
        {
            var ids = r.ElementIds == null ? "" : string.Join(",", r.ElementIds);
            return string.Join("\u0001", source, r.Source ?? "", r.IssueId ?? "", r.CheckId ?? "", r.Category ?? "", r.Rule ?? "",
                Norm(r.Status), r.ElementId.ToString(), ids, r.ElementCount.ToString(), r.CheckerId ?? "", r.RowKey ?? "",
                r.RowLabel ?? "", r.Note ?? "", r.NoteEn ?? "", r.NotApplicable ? "1" : "0", r.Coverage?.ToString() ?? "");
        }

        // ─── fill_audit records → results ───

        /// <summary>
        /// The BIM010 checker records of a run as results (IssueMapper.ToSpecResults):
        /// headings skipped, not-applicable rows kept so the checklist can say "not
        /// modelled". Null records, rows, ref / section strings, element lists and
        /// evidence are read as empty; rows not on the form are skipped.
        /// <paramref name="issueIdOfKey"/> gives the pane issue id of a row key (the
        /// same id IssueMapper.FromAuditRecords gives the row's issue).
        /// </summary>
        public static List<SpecResult> FromAuditRecords(IEnumerable<BinaVibe.Mcp.Tools.Audit.AuditRecord> records, Func<string, string> issueIdOfKey)
        {
            var list = new List<SpecResult>();
            foreach (var r in records ?? Enumerable.Empty<BinaVibe.Mcp.Tools.Audit.AuditRecord>())
            {
                if (r?.Row == null) continue;
                var rowRef = r.Row.RowRef ?? "";
                if (rowRef.EndsWith(".0", StringComparison.Ordinal) || (r.Evidence != null && r.Evidence.ContainsKey("section_header"))) continue;
                var key = BinaVibe.Mcp.Tools.Audit.Bim010Form.KeyOf(r.Row.Section, rowRef);
                var row = BinaVibe.Mcp.Tools.Audit.Bim010Form.Find(key);
                if (row == null || row.IsHeading) continue;
                bool na;
                try { na = BinaVibe.Mcp.Tools.Audit.AuditSummary.IsNotApplicable(r); }
                catch { na = false; }
                string issueId = "";
                // FromAuditRecords makes an issue for every scored record that is not "yes".
                if (!na && r.Compliance != "yes")
                {
                    try { issueId = issueIdOfKey?.Invoke(row.Key) ?? ""; }
                    catch { issueId = ""; }
                }
                list.Add(new SpecResult
                {
                    Source = Audit,
                    IssueId = issueId,
                    Rule = string.IsNullOrEmpty(r.RulePattern) ? (r.Row.Description ?? row.Description ?? "") : r.RulePattern,
                    Status = StatusOfCompliance(r.Compliance),
                    ElementId = 0,
                    ElementCount = r.ElementIds?.Count ?? 0,
                    CheckerId = r.CheckerId ?? "",
                    RowKey = row.Key,
                    RowLabel = row.Description ?? "",
                    Note = r.Remark ?? "",
                    NoteEn = PaneText.AuditRemarkEn(r.Remark) ?? "",
                    NotApplicable = na,
                });
            }
            return list;
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
            if (cls == SpecCellClass.List)
                return r.Listed > 0 ? SpecState.Listed : r.NotApplicableHits > 0 ? SpecState.NotApplicable : SpecState.NotChecked;
            if (r.Failed > 0) return SpecState.Fail;
            if (r.Unjudged > 0) return SpecState.NeedsPerson;
            if (r.Passed > 0) return SpecState.Pass;
            if (r.PartlyPassed) return SpecState.Partial;
            if (r.PersonRows > 0) return SpecState.NeedsPerson;   // AI-504: only findings a person decides
            if (r.NotApplicableHits > 0) return SpecState.NotApplicable;
            return SpecState.NotChecked;
        }

        internal static string Clean(string s) =>
            string.Join(" ", (s ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).Trim();

        private const string VerdictWords =
            @"(?:tidak\s+patuh|patuh|does\s+not\s+comply|do\s+not\s+comply|not\s+compliant|non-compliant|compliant|complies|comply)";
        /// <summary>A verdict closing a clause: "… — patuh." / "… – compliant" / "…, does not comply."
        /// (followed by the sentence's end, the note's end or a bracket).</summary>
        private static readonly Regex VerdictTail = new Regex(
            @"\s*(?:[—–]|\s-\s|[,;])\s*(?:semua\s+|all\s+|it\s+)?" + VerdictWords + @"\s*(?=[.!]|$|\()",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        /// <summary>A sentence that is only a verdict: "Patuh." / "Compliant".</summary>
        private static readonly Regex VerdictSentence = new Regex(
            @"(?<=(?:^|[.!;])\s*)" + VerdictWords + @"\s*(?:[.!]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// The remark without its verdict ("Dijumpai 1 view (cth: …) — patuh." → "Dijumpai 1
        /// view (cth: …)."), for rows that never carry one: list-only checks, checks a person
        /// decides, listing / context results. The BIM010 checkers end their remarks with a
        /// verdict whatever the spec says of the row. Words that describe the data
        /// ("1/6 jenis tidak patuh") are kept: only a verdict closing a clause after a dash,
        /// comma or semicolon, or a sentence of its own, is dropped.
        /// </summary>
        public static string StripVerdict(string note)
        {
            var before = Clean(note);
            if (before.Length == 0) return before;
            var s = VerdictSentence.Replace(VerdictTail.Replace(before, ""), "");
            if (s == before) return before;
            s = Regex.Replace(s, @"\s+([.!;,])", "$1");
            s = Regex.Replace(s, @"([.!])[.!]+", "$1");
            s = Regex.Replace(s, @"^[\s.!;,—–-]+", "");
            return Clean(s);
        }
    }

    /// <summary>
    /// How a model result (Source = Model, Services/JkrSpec) becomes a pane issue —
    /// the WPF-free half of IssueMapper.FromModelResults, so it is unit-tested on
    /// any machine. Null-safe throughout.
    /// </summary>
    public static class SpecModelIssues
    {
        /// <summary>Prefix of every model issue's CheckId ("SPEC-A1.2"). The backend
        /// AI fixer does not know these checks.</summary>
        public const string CheckIdPrefix = "SPEC-";

        /// <summary>Most element ids listed in an issue's description.</summary>
        public const int MaxIdsListed = 10;

        /// <summary>The result gets an issue: it has an issue id, is not "does not
        /// apply", and failed, could not be judged, or is a finding beside a check
        /// a person decides (Context). Passes and listings give none.</summary>
        public static bool GivesIssue(SpecResult r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.IssueId)) return false;
            if (r.NotApplicable || SpecReport.IsNotApplicableStatus(r.Status)) return false;
            if (r.Coverage == SpecCoverage.Listing) return false;
            if (r.Coverage == SpecCoverage.Context) return true;
            return !SpecReport.IsPass(r.Status) || SpecReport.IsWarning(r.Status);
        }

        /// <summary>An AI verdict ("not compliant"): a failure on an AI-verifiable check
        /// under a rule that gives verdicts. Everything else that gives an issue needs a person.</summary>
        public static bool IsAiVerdict(SpecResult r)
        {
            if (r == null || !(SpecReport.IsFail(r.Status) || SpecReport.IsWarning(r.Status))) return false;
            if (r.Coverage == SpecCoverage.Context || r.Coverage == SpecCoverage.Listing) return false;
            var check = SpecCatalog.Find(SpecReport.ResolveCheckId(r.CheckId) ?? "");
            return check != null && check.Class == SpecCellClass.Ai;
        }

        /// <summary>"SPEC-A1.2" (the trimmed check id when it is not in the catalog).</summary>
        public static string CheckIdOf(SpecResult r) =>
            CheckIdPrefix + (SpecReport.ResolveCheckId(r?.CheckId) ?? (r?.CheckId ?? "").Trim());

        /// <summary>"A1.2 · Saiz pintu: …" in the language; the id alone when it is not in the catalog.</summary>
        public static string Title(SpecResult r, string lang)
        {
            var id = SpecReport.ResolveCheckId(r?.CheckId);
            var check = id == null ? null : SpecCatalog.Find(id);
            if (check == null)
            {
                var raw = (r?.CheckId ?? "").Trim();
                var rule = SpecReport.Clean(r?.Rule);
                if (raw.Length == 0) return rule;
                return rule.Length == 0 || rule == raw ? raw : raw + " · " + rule;
            }
            return check.Id + " · " + (check.Title?.In(lang) ?? "");
        }

        /// <summary>The result's note in the language (English when it has one, else the BM note).</summary>
        public static string Note(SpecResult r, string lang)
        {
            if (r == null) return "";
            var en = string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase);
            return SpecReport.Clean(en && !string.IsNullOrWhiteSpace(r.NoteEn) ? r.NoteEn : r.Note);
        }

        /// <summary>Element ids behind the result: ElementIds, else ElementId (positive, distinct, in order).</summary>
        public static List<long> ElementIdsOf(SpecResult r)
        {
            var ids = new List<long>();
            if (r == null) return ids;
            if (r.ElementId > 0) ids.Add(r.ElementId);
            foreach (var x in r.ElementIds ?? new List<long>())
                if (x > 0 && !ids.Contains(x)) ids.Add(x);
            return ids;
        }

        /// <summary>What the model has (<see cref="SpecResult.Actual"/>) in the pane's language:
        /// the evaluator's English when it gave one, else <see cref="ActualToEnglish"/>.</summary>
        public static string ActualIn(SpecResult r, string lang)
        {
            if (r == null) return "";
            if (!string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase)) return r.Actual ?? "";
            return !string.IsNullOrEmpty(r.ActualEn) ? r.ActualEn : ActualToEnglish(r.Actual);
        }

        private static readonly (System.Text.RegularExpressions.Regex Rx, string En)[] ActualPhrases =
        {
            (Rx(@"^tiada$"), "none"),
            (Rx(@"^(\d+) kolum tiada$"), "$1 columns missing"),
            (Rx(@"^(\d+) kolum kosong$"), "$1 columns empty"),
            (Rx(@"^(\d+) field kosong$"), "$1 fields empty"),
            (Rx(@"^(\d+) elemen, tiada schedule$"), "$1 elements, no schedule"),
            (Rx(@"^(\d+) segmen$"), "$1 segments"),
            (Rx(@"^(\d+)% di bawah room$"), "$1% under a room"),
            (Rx(@"^(\d+) view (\S+)$"), "$1 $2 views"),
            (Rx(@"^tiada dalam jadual$"), "not in the schedule"),
            (Rx(@"^3D view di luar set Dokumen: "), "3D view outside the Dokumen set: "),
            (Rx(@"^kosong: "), "empty: "),
            (Rx(@"^tiada jadual ruang$"), "no room schedule"),
            (Rx(@"^tiada 3D view Dokumen$"), "no Dokumen 3D view"),
            (Rx(@"^tiada schedule kategori (.+)$"), "no $1 schedule"),
            (Rx(@"^tiada detail pintu$"), "no door detail"),
            (Rx(@"^tiada detail tingkap$"), "no window detail"),
            (Rx(@"^tiada (.+)$"), "no $1"),
        };

        private static System.Text.RegularExpressions.Regex Rx(string p) =>
            new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The BM "what the model has" values the evaluators write ("tiada room",
        /// "6 kolum tiada", "kosong: …") in English; anything else (numbers, names) as is.
        /// Real Dewan model, 16 Nov: these stayed BM in an EN pane.</summary>
        public static string ActualToEnglish(string bm)
        {
            var s = (bm ?? "").Trim();
            foreach (var (rx, en) in ActualPhrases)
                if (rx.IsMatch(s)) return rx.Replace(s, en, 1);
            return bm ?? "";
        }

        /// <summary>
        /// The issue text: the note; then what the model has against what the spec
        /// requires when the issue needs a person (an AI verdict shows that pair on
        /// its own row line); then the element ids when there are several.
        /// </summary>
        public static string Description(SpecResult r, string lang)
        {
            if (r == null) return "";
            var en = string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase);
            var parts = new List<string>();
            var note = Note(r, lang);
            if (note.Length > 0) parts.Add(note);
            var actual = SpecReport.Clean(en ? ActualIn(r, PanelStrings.En) : r.Actual);
            var required = SpecReport.Clean(r.Required);
            if (!IsAiVerdict(r) && (actual.Length > 0 || required.Length > 0))
            {
                var pair = new List<string>();
                if (actual.Length > 0) pair.Add("Model: " + actual);
                if (required.Length > 0) pair.Add((en ? "Required: " : "Keperluan: ") + required);
                parts.Add(string.Join(" · ", pair));
            }
            var ids = ElementIdsOf(r);
            if (ids.Count > 1)
            {
                var shown = string.Join(", ", ids.Take(MaxIdsListed));
                if (ids.Count > MaxIdsListed) shown += en ? $" … ({ids.Count} in all)" : $" … ({ids.Count} kesemuanya)";
                parts.Add((en ? "Elements: " : "Elemen: ") + shown);
            }
            return string.Join("\n", parts);
        }

        /// <summary>The element's Revit category: the result's Category, else the label
        /// before " · " in ElementName ("Doors · PT1p_850x2100" → "Doors"), else "".</summary>
        public static string RevitCategoryOf(SpecResult r)
        {
            if (r == null) return "";
            if (!string.IsNullOrWhiteSpace(r.Category)) return r.Category.Trim();
            var name = r.ElementName ?? "";
            var i = name.IndexOf(" · ", StringComparison.Ordinal);
            return i > 0 ? name.Substring(0, i).Trim() : "";
        }

        /// <summary>
        /// The Borang BIM010 row of the result's check: A1–A7 and B1 as the item;
        /// C items their own row (C1.1 … C5.1; the C1.0 heading has none); Section D
        /// the family-category row of the element ("Doors" → "D10") when known, else
        /// "D"; Section E the set's row (BOMBA → E1.1, PBT → E1.2) when known, else
        /// "E". "" for an unknown check.
        /// </summary>
        public static string BorangKeyOf(SpecResult r)
        {
            var id = SpecReport.ResolveCheckId(r?.CheckId);
            var check = id == null ? null : SpecCatalog.Find(id);
            if (check == null) return "";
            var item = check.Item ?? "";
            switch (check.Section)
            {
                case "D":
                {
                    var cat = RevitCategoryOf(r);
                    return BinaVibe.Mcp.Tools.Audit.Bim010Mapping.DKeyForRevitCategory(cat) ?? "D";
                }
                case "E":
                {
                    var set = SetOf(r);
                    return set == "BOMBA" ? "E1.1" : set == "PBT" ? "E1.2" : "E";
                }
            }
            var row = BinaVibe.Mcp.Tools.Audit.Bim010Form.Find(item);
            return row != null && !row.IsHeading ? row.Key : "";
        }

        /// <summary>"BOMBA" / "PBT" when the result names exactly one of the two sets
        /// (category, element name, then note), else "".</summary>
        private static string SetOf(SpecResult r)
        {
            foreach (var text in new[] { r?.Category, r?.ElementName, r?.Note, r?.NoteEn })
            {
                var t = (text ?? "").ToUpperInvariant();
                var bomba = t.Contains("BOMBA");
                var pbt = Regex.IsMatch(t, @"\bPBT\b");
                if (bomba && !pbt) return "BOMBA";
                if (pbt && !bomba) return "PBT";
            }
            return "";
        }
    }
}

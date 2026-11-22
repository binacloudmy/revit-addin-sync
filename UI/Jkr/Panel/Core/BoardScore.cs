using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// AI-479 (de-dupe): one problem, one row. A2.1 (file name), A4.1 (base point) and
    /// B1.1 (Project Information) are answered by up to three sources at once: the
    /// backend's legacy rows, the BIM010 checker rows and the add-in's spec evaluator.
    /// The board de-dupes by issue id only, and each source has its own id, so the same
    /// problem showed up to three times, with different tiers. This keeps the rows of
    /// ONE source per such check and gives them ONE tier. WPF-free: the host maps its
    /// issues to <see cref="Row"/> and applies the answer.
    /// </summary>
    public static class SpecDedupe
    {
        /// <summary>Checks that are one problem however many sources answer them.</summary>
        public static readonly IReadOnlyList<string> OneProblemChecks = new[] { "A2.1", "A4.1", "B1.1" };

        /// <summary>The source of a row. Lower wins: the spec evaluator (the one the score and the
        /// Borang follow), then the backend's rows, then the BIM010 checker's.</summary>
        public const int FromModel = 0, FromBackend = 1, FromAudit = 2;

        /// <summary>One board row, as far as the de-dupe needs it.</summary>
        public sealed class Row
        {
            /// <summary>The spec check the row answers ("A2.1"), or "" (a family with no spec id).</summary>
            public string CheckId = "";
            /// <summary><see cref="FromModel"/>, <see cref="FromBackend"/> or <see cref="FromAudit"/>.</summary>
            public int Source;
            /// <summary>Board tier: 0 High, 1 Medium, 2 Low (lower is more severe).</summary>
            public int Tier = 1;
            /// <summary>The row has a fix or a picker: it is never dropped, whatever the source.</summary>
            public bool Fixable;
            /// <summary>AI-506: issue id (a merge keeps the first row of an id) and the caller's object.</summary>
            public string Id = "";
            public object Tag;
            /// <summary>AI-506 tally: <see cref="ScanBoard.Open"/> ... <see cref="ScanBoard.Resolved"/>; <see cref="Extra"/> = no spec id; <see cref="FixKey"/> = one write Fix all makes (null = not in Fix all).</summary>
            public int Bucket;
            public bool Extra;
            public string FixKey;
            /// <summary>AI-506 (Db.1 only). On a backend / checker row: the one parameter it names
            /// ("Kod_Komponen_jkr_stt") and the one element it sits on. Empty / 0 = not a parameter row.</summary>
            public string Param = "";
            public long ElementId;
            /// <summary>AI-506b. The row's rule text names the parameter ("Missing Type Parameter: X"), whatever its spec id
            /// says: a backend row with no element id has no spec id, and still says the same thing as a Db.1 spec row.</summary>
            public bool ParamRule;
            /// <summary>AI-516. "type" / "instance" / "" (unknown), from the rule text ("Missing Type parameter: X"). A fixable row
            /// of a TYPE parameter stands for the whole type, so it can hold the spec row's parameter; an instance row only holds it for its own element.</summary>
            public string ParamKind = "";
            /// <summary>AI-516 (output of <see cref="SpecDedupe.Resolve"/>, spec rows only): parameters of this row another kept, fixable row already reports.
            /// The host removes them from the shown text (<see cref="SpecDedupe.ActualWithout"/>).</summary>
            public IReadOnlyCollection<string> HeldParams = new string[0];
            /// <summary>AI-506b. Revit category of the row's element ("Doors"), "" when unknown. A backend parameter row whose
            /// id is of another kind than the spec row's (a TYPE id against INSTANCE ids) matches by (category, parameter).</summary>
            public string Category = "";
            /// <summary>AI-506b. Revit type name of the row's element ("PT1p_850x2100"), "" when unknown (a spec row: the text after "Category · ").</summary>
            public string TypeName = "";
            /// <summary>AI-516. Revit family of the row's type, "" when unknown. A spec row's label is "Category · Family : Type", and JKR types are
            /// often all named "Normal": a type-name match counts only when the backend row has a family and it equals the spec row's.</summary>
            public string Family = "";
            /// <summary>AI-506 (Db.1 only). On a spec row: every parameter it reports and every element it
            /// covers. A spec row covers a type, so one row stands for many per-element backend rows.</summary>
            public IReadOnlyCollection<string> Params = new string[0];
            public IReadOnlyCollection<long> ElementIds = new long[0];
            /// <summary>AI-516 (dump only): the row's title, raw rule, element label and fix action as the host knows them.</summary>
            public string Required = "";
            public string Title = "", RuleText = "", ElementLabel = "", FixAction = "";
        }

        /// <summary>The spec check whose spec rows list parameters per type (AI-506): a backend
        /// "Missing Type Parameter: X" row for an element of that type says the same thing.</summary>
        public const string ParameterCheck = "Db.1";

        /// <summary>"Kod_Komponen_jkr_stt" and "Kod Komponen Jkr Stt" are the same parameter.</summary>
        public static string ParamKey(string name) =>
            string.Join("_", (name ?? "").Trim().ToLowerInvariant().Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>AI-516. <see cref="ParamKey"/> without the trailing "_jkr_stt" / "_jkr_sit" / "_jkr_sty" ... suffix (any "jkr_s" + 2 letters):
        /// "Bidang Kejuruteraan" and "Bidang_Kejuruteraan_jkr_stt" give "bidang_kejuruteraan". Two spec names that differ only by the suffix
        /// (Coping_jkr_stm / Coping_jkr_stt) are DIFFERENT parameters: see <see cref="SameParam"/>.</summary>
        public static string ParamBaseKey(string name)
        {
            var k = ParamKey(name);
            var m = JkrSuffix.Match(k);
            return m.Success ? m.Groups[1].Value : k;
        }

        private static readonly System.Text.RegularExpressions.Regex JkrSuffix = new System.Text.RegularExpressions.Regex(@"^(.*)_jkr_s[a-z]{2}$");

        /// <summary>AI-516. Which of the spec row's parameters (<paramref name="specKeys"/>: distinct <see cref="ParamKey"/>s, built once per spec row) the row
        /// parameter <paramref name="rowKey"/> names, or null. Equal key first. The suffix-tolerant step runs only when the row's own name has NO suffix
        /// ("Bahan") AND its Required text (<paramref name="requiredKey"/>) carries the SUFFIXED name ("Bahan_jkr_stt") with the same base: then the spec
        /// parameter equal to that Required name. A bare "Bahan" with no suffixed Required is another, non-JKR parameter and matches nothing.</summary>
        public static string MatchSpecParam(IReadOnlyCollection<string> specKeys, string rowKey, string requiredKey = "")
        {
            if (string.IsNullOrEmpty(rowKey) || specKeys == null) return null;
            if (specKeys.Contains(rowKey)) return rowKey;
            if (JkrSuffix.IsMatch(rowKey)) return null;
            var req = requiredKey ?? "";
            if (!JkrSuffix.IsMatch(req) || ParamBaseKey(req) != rowKey) return null;
            return specKeys.Contains(req) ? req : null;
        }

        /// <summary>Distinct non-empty <see cref="ParamKey"/>s of a spec row's parameters.</summary>
        public static List<string> SpecKeys(IEnumerable<string> specParams) =>
            (specParams ?? new string[0]).Select(ParamKey).Where(k => k.Length > 0).Distinct().ToList();

        /// <summary>Where the parameter name starts in a backend rule: "Missing Type parameter: X", "Empty Instance parameter: X"
        /// and (AI-516) "Optional Type parameter missing: X". -1 = the rule names no parameter.</summary>
        private static int ParamNameStart(string rule)
        {
            var r = (rule ?? "").Trim();
            var low = r.ToLowerInvariant();
            if (low.StartsWith("missing ") || low.StartsWith("empty "))
            {
                var k = low.IndexOf(" parameter:", StringComparison.Ordinal);
                return k > 0 ? k + " parameter:".Length : -1;
            }
            if (low.StartsWith("optional "))
            {
                var k = low.IndexOf(" parameter missing:", StringComparison.Ordinal);
                return k > 0 ? k + " parameter missing:".Length : -1;
            }
            return -1;
        }

        /// <summary>The parameter a backend rule names (its raw rule is "missing type parameter: X"), else the row's Required text. "" when none.</summary>
        public static string ParamOfRule(string rawRule, string required)
        {
            var r = (rawRule ?? "").Trim();
            var at = ParamNameStart(r);
            return at > 0 ? r.Substring(at).Trim() : (required ?? "").Trim();
        }

        /// <summary>The rule text says "Missing/Empty ... Parameter: X" (or "Optional ... parameter missing: X").</summary>
        public static bool RuleNamesParameter(string rawRule) => ParamNameStart(rawRule) > 0;

        /// <summary>AI-516. "type" or "instance" from a parameter rule's text; "" when it says neither.</summary>
        public static string ParamKindOfRule(string rawRule)
        {
            if (!RuleNamesParameter(rawRule)) return "";
            var words = (rawRule ?? "").ToLowerInvariant().Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var w in words)
            {
                if (w == "type") return "type";
                if (w == "instance") return "instance";
                if (w == "parameter") break;
            }
            return "";
        }

        /// <summary>AI-516. The shown "empty: A, B; missing: C" text without the parameters in <paramref name="held"/> (compared as
        /// <see cref="ParamKey"/>). A part left with no parameter is dropped. Text with no ":" parts is returned unchanged.</summary>
        public static string ActualWithout(string actual, IEnumerable<string> held)
        {
            var drop = new HashSet<string>((held ?? new string[0]).Select(ParamKey));
            if (drop.Count == 0) return actual ?? "";
            var parts = new List<string>();
            foreach (var part in (actual ?? "").Split(';'))
            {
                var c = part.IndexOf(':');
                if (c < 0) { if (part.Trim().Length > 0) parts.Add(part.Trim()); continue; }
                var names = part.Substring(c + 1).Split(',').Select(n => n.Trim()).Where(n => n.Length > 0 && !drop.Contains(ParamKey(n))).ToList();
                if (names.Count > 0) parts.Add(part.Substring(0, c).Trim() + ": " + string.Join(", ", names));
            }
            return string.Join("; ", parts);
        }

        /// <summary>Mirrors SpecModelReader.InstanceIdCap: a spec row lists at most this many instance ids per type.</summary>
        public const int InstanceIdCap = 2000;

        private static string CategoryKey(string c) => (c ?? "").Trim().ToLowerInvariant();

        /// <summary>The parameters a Db.1 spec row lists in its Actual text: "kosong: A, B; tiada: C" /
        /// "empty: A; not on the type: C".</summary>
        public static IReadOnlyList<string> ParamsOfActual(string actual)
        {
            var list = new List<string>();
            foreach (var part in (actual ?? "").Split(';'))
            {
                var c = part.IndexOf(':');
                if (c < 0) continue;
                foreach (var n in part.Substring(c + 1).Split(','))
                {
                    var k = ParamKey(n);
                    if (k.Length > 0 && !list.Contains(k)) list.Add(k);
                }
            }
            return list;
        }

        public sealed class Outcome
        {
            /// <summary>Per input row: keep it on the board.</summary>
            public bool[] Keep = new bool[0];
            /// <summary>Per input row: the tier to show (the most severe of its check's rows).</summary>
            public int[] Tier = new int[0];
            /// <summary>AI-516: per input row, the spec-row parameters a kept fixable row reports instead (null = none).</summary>
            public HashSet<string>[] Held = new HashSet<string>[0];
            /// <summary>AI-516 (dump): per input row, why it was kept / dropped / trimmed ("" = no dedupe rule looked at it).</summary>
            public string[] Reason = new string[0];
            public int Dropped => Keep.Count(k => !k);
        }

        /// <summary>
        /// AI-506: a Db.1 spec row (one per type) lists the parameters that type lacks or leaves empty,
        /// and the backend reports each of them again per element ("Missing Type Parameter: X"). One
        /// parameter on one element or type is ONE row. A backend / checker row names the same parameter as
        /// a spec row when (<see cref="Covers"/>): its element is in the spec row's id list, or it is not
        /// (a TYPE id, or none) and the category matches and one of: it has no element, its type name is the
        /// spec row's type, the spec row's id list hit the reader's cap. Names compare as <see cref="ParamKey"/>.
        /// A row whose id a spec row lists, but not for this parameter, is another problem and stays.
        ///
        /// Which row stays (AI-516): a row that is not fixable is dropped, the spec row wins (it counts in the %).
        /// A fixable row (AI-479: never dropped; it holds the set_parameter write that Fix all makes and counts)
        /// stays, and the spec row gives the parameter up instead: the parameter is removed from the spec row's
        /// shown text (<see cref="Outcome.Held"/>), and the spec row is dropped when nothing is left. The % is
        /// the spec report's and does not read board rows. A fixable row holds a parameter only when it stands
        /// for every element the spec row lists: a TYPE-parameter row (one fix writes the type) or instance rows
        /// that together cover all of the spec row's ids. Otherwise the spec row keeps the parameter.
        /// A row no spec row covers stays (never hide a problem). Order of the input does not change the result.
        /// </summary>
        private static void DropCoveredParameterRows(IReadOnlyList<Row> rows, Outcome o)
        {
            var specs = new List<int>();
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r == null || r.Source != FromModel || !IsCheck(r, ParameterCheck)) continue;
                if ((r.Params ?? new string[0]).Any(p => ParamKey(p).Length > 0)) specs.Add(i);
            }
            if (specs.Count == 0) return;
            var specIds = new HashSet<long>();                       // every element id any Db.1 spec row covers
            foreach (var si in specs) foreach (var e in rows[si].ElementIds ?? new long[0]) specIds.Add(e);

            var specKeys = new Dictionary<int, List<string>>();     // each spec row's keys, built once
            foreach (var si in specs) specKeys[si] = SpecKeys(rows[si].Params);

            // (spec row, spec parameter) -> the instance ids / "the whole type" a kept fixable row holds
            var heldIds = new Dictionary<(int, string), HashSet<long>>();
            var heldType = new HashSet<(int, string)>();
            int dropped = 0, keptFixable = 0, unmatched = 0;

            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r == null || r.Source == FromModel || string.IsNullOrEmpty(r.Param)) continue;
                if (!IsCheck(r, ParameterCheck) && !r.ParamRule) continue;
                var key = ParamKey(r.Param);
                if (key.Length == 0) continue;
                var reqKey = ParamKey(r.Required);
                var matched = new List<(int spec, string key, string why)>();
                var misses = new List<string>();
                foreach (var si in specs)
                {
                    var mk = MatchSpecParam(specKeys[si], key, reqKey);
                    if (mk == null) { misses.Add("spec " + rows[si].Id + ": lists no '" + key + "'"); continue; }
                    var why = CoverWhy(rows[si], r, mk, specIds);
                    if (why.StartsWith("covered:")) matched.Add((si, mk, why)); else misses.Add("spec " + rows[si].Id + ": " + why);
                }
                if (matched.Count == 0)
                {
                    unmatched++;
                    o.Reason[i] = "kept:not-covered | " + (misses.Count > 6 ? string.Join("; ", misses.Take(6)) + "; +" + (misses.Count - 6) + " more" : string.Join("; ", misses));
                    continue;
                }
                var via = string.Join(",", matched.Select(m => rows[m.spec].Id + "=" + m.why).Take(3));
                if (!r.Fixable) { o.Keep[i] = false; dropped++; o.Reason[i] = "dropped:covered-not-fixable (spec row wins) | " + via; continue; }
                keptFixable++;
                var typeRow = string.Equals(r.ParamKind, "type", StringComparison.OrdinalIgnoreCase);
                o.Reason[i] = "kept:fixable-holds-param (" + (typeRow ? "type row" : "instance row") + ") | " + via;
                foreach (var m in matched)
                {
                    if (typeRow) heldType.Add((m.spec, m.key));
                    if (r.ElementId > 0)
                    {
                        if (!heldIds.TryGetValue((m.spec, m.key), out var set)) heldIds[(m.spec, m.key)] = set = new HashSet<long>();
                        set.Add(r.ElementId);
                    }
                }
            }

            foreach (var si in specs)
            {
                var all = rows[si].Params.Select(ParamKey).Where(k => k.Length > 0).Distinct().ToList();
                var ids = (rows[si].ElementIds ?? new long[0]).Where(e => e > 0).ToList();
                var held = new HashSet<string>();
                foreach (var k in all)
                {
                    if (heldType.Contains((si, k))) { held.Add(k); continue; }
                    if (ids.Count > 0 && heldIds.TryGetValue((si, k), out var got) && ids.All(got.Contains)) held.Add(k);
                }
                if (held.Count == 0) { o.Reason[si] = "kept:no-parameter-held (" + all.Count + " params, " + ids.Count + " ids)"; continue; }
                if (held.Count == all.Count) { o.Keep[si] = false; o.Reason[si] = "dropped:all-" + all.Count + "-params-held-by-fixable-rows"; }
                else { o.Held[si] = held; o.Reason[si] = "trimmed:held " + held.Count + " of " + all.Count + " (" + string.Join(",", held) + ")"; }
            }
            if (dropped + keptFixable > 0)
                System.Diagnostics.Debug.WriteLine("[SpecDedupe] AI-516: parameter rows covered by a Db.1 row: " + dropped + " dropped, " + keptFixable + " fixable kept (spec row gives the parameter up); " + unmatched + " not covered");
        }

        /// <summary>"covered:&lt;rule&gt;" or "no:&lt;why&gt;": the one matching function, with the reason for the dump.</summary>
        private static string CoverWhy(Row spec, Row r, string key, HashSet<long> specIds)
        {
            if (!(spec.Params ?? new string[0]).Any(p => ParamKey(p) == key)) return "no:spec-lists-no-such-param";
            var ids = spec.ElementIds ?? new long[0];
            if (r.ElementId > 0 && ids.Contains(r.ElementId)) return "covered:id-in-spec-list";
            if (r.ElementId > 0 && specIds.Contains(r.ElementId)) return "no:id-" + r.ElementId + "-listed-by-another-spec-row";
            // The id says nothing. Never on the parameter alone: the category must match, and one of
            // (a) the row has no element, (b) it is a type id: its family and type name are the spec row's (a row with no family is not enough: types are often all "Normal"), (c) the spec row's id list was cut at the cap.
            var cat = CategoryKey(r.Category);
            if (cat.Length == 0) return "no:row-has-no-revit-category";
            if (cat != CategoryKey(spec.Category)) return "no:category-differs (row '" + r.Category + "' spec '" + spec.Category + "')";
            if (r.ElementId <= 0) return "covered:no-element-same-category";
            var type = CategoryKey(r.TypeName);
            if (type.Length > 0 && type == CategoryKey(spec.TypeName) && CategoryKey(r.Family).Length > 0 && CategoryKey(r.Family) == CategoryKey(spec.Family)) return "covered:same-family-and-type";
            if (ids.Count >= InstanceIdCap) return "covered:spec-id-list-at-cap";
            return "no:id-not-listed, type/family differ or unknown (row type '" + r.TypeName + "' family '" + r.Family + "', spec type '" + spec.TypeName + "' family '" + spec.Family + "')";
        }

        private static bool IsCheck(Row r, string id) => string.Equals((r.CheckId ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase);

        /// <summary>AI-517: the one-problem checks the spec evaluator judged PASS (a Full pass result and no fail result of that
        /// check). Today only A4.1 is read this way: it outranks the legacy backend "Project Base Point surveyed" row.</summary>
        public static IReadOnlyCollection<string> PassedByEvaluator(IEnumerable<SpecResult> modelResults)
        {
            var list = (modelResults ?? new SpecResult[0]).Where(r => r != null && SpecReport.SourceOf(r.Source) == SpecReport.Model).ToList();
            var passed = new List<string>();
            foreach (var id in new[] { "A4.1" })
            {
                var mine = list.Where(r => string.Equals(SpecReport.ResolveCheckId(r.CheckId), id, StringComparison.OrdinalIgnoreCase)).ToList();
                if (mine.Count > 0 && mine.All(r => SpecReport.IsPass(r.Status) && (r.Coverage ?? SpecCoverage.Full) == SpecCoverage.Full && !r.NotApplicable))
                    passed.Add(id);
            }
            return passed;
        }

        public static bool IsOneProblem(string checkId) =>
            !string.IsNullOrEmpty(checkId) && OneProblemChecks.Contains(checkId.Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Per check in <see cref="OneProblemChecks"/> with rows from several sources: keep
        /// the rows of the best source (<see cref="FromModel"/> first) and every fixable
        /// row; drop the rest. All kept rows of the check take the most severe tier of
        /// the check's rows, dropped ones included, so a Must Fix never turns ignorable
        /// because a duplicate is gone. Rows of any other check, or with none, pass through.
        /// </summary>
        public static Outcome Resolve(IReadOnlyList<Row> rows)
        {
            rows = rows ?? new Row[0];
            var o = new Outcome { Keep = Enumerable.Repeat(true, rows.Count).ToArray(), Tier = new int[rows.Count], Held = new HashSet<string>[rows.Count], Reason = Enumerable.Repeat("", rows.Count).ToArray() };
            for (var i = 0; i < rows.Count; i++) o.Tier[i] = rows[i]?.Tier ?? 1;

            foreach (var id in OneProblemChecks)
            {
                var group = Enumerable.Range(0, rows.Count)
                    .Where(i => rows[i] != null && string.Equals((rows[i].CheckId ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (group.Count == 0) continue;
                var best = group.Min(i => rows[i].Source);
                var tier = group.Min(i => rows[i].Tier);
                foreach (var i in group)
                {
                    o.Tier[i] = tier;
                    if (rows[i].Source != best && !rows[i].Fixable) { o.Keep[i] = false; o.Reason[i] = "dropped:one-problem-check " + id + " (source " + rows[i].Source + " lost to source " + best + ")"; }
                    else o.Reason[i] = "kept:one-problem-check " + id + " (tier " + tier + ")";
                }
            }
            DropCoveredParameterRows(rows, o);
            return o;
        }
    }

    /// <summary>
    /// AI-479 (one number): what a Fix all moved, on the board and on the score. The board
    /// counts rows, the score counts checks (cells), and the families with no spec id
    /// (Grid, Level, JKR code, element-less rows; Kod_Jenis, Sistem, Sub_Sistem and Bidang map to Db.1 and are in the %) are "extra": fixable
    /// rows on the board outside the %. A fix there moves the board and never the score;
    /// this says so, instead of three numbers that disagree. WPF-free.
    /// </summary>
    public static class FixImpact
    {
        /// <summary>The numbers of one moment: the board's open rows and the score.</summary>
        public sealed class Snapshot
        {
            /// <summary>Open rows on the board (AI rows: the number the Open tab shows).</summary>
            public int Open;
            /// <summary>Of <see cref="Open"/>, the "extra" rows (no spec id).</summary>
            public int OpenExtra;
            /// <summary>Checks passed / checks in the % (<see cref="SpecChecklist.Score(SpecReport, string)"/>); Total 0 = no score.</summary>
            public int Passed;
            public int Total;
            public int? Pct;
        }

        public static Snapshot Take(int open, int openExtra, SpecReport spec)
        {
            var s = new Snapshot { Open = Math.Max(0, open), OpenExtra = Math.Max(0, Math.Min(openExtra, open)) };
            if (spec == null) return s;
            var sc = SpecChecklist.Score(spec);
            s.Passed = sc.Passed;
            s.Total = sc.Total;
            s.Pct = SpecChecklist.Pct(spec);
            return s;
        }

        /// <summary>Rows that left the board (never negative: a re-scan can find new rows).</summary>
        public static int BoardMoved(Snapshot before, Snapshot after) => Math.Max(0, before.Open - after.Open);

        /// <summary>Of the rows that left the board, the extra ones (outside the %).</summary>
        public static int ExtraMoved(Snapshot before, Snapshot after) => Math.Max(0, Math.Min(BoardMoved(before, after), before.OpenExtra - after.OpenExtra));

        /// <summary>Checks that moved onto pass (negative when the re-scan failed some).</summary>
        public static int ScoreMoved(Snapshot before, Snapshot after) => after.Passed - before.Passed;

        /// <summary>
        /// "Board 12 → 5 open (7 fixed, 4 of them extra, outside the %) · Score 61% → 64% (+2 checks)".
        /// With no score (no spec report) only the board part; with an unchanged score and extra
        /// rows moved, says why the % did not move.
        /// </summary>
        public static string Text(Snapshot before, Snapshot after)
        {
            if (before == null || after == null) return "";
            var moved = BoardMoved(before, after);
            var extra = ExtraMoved(before, after);
            var board = "Board " + before.Open + " → " + after.Open + " open";
            if (moved > 0)
            {
                board += " (" + moved + " fixed";
                if (extra > 0)
                    board += (", " + extra + " of them extra, outside the %");
                board += ")";
            }
            if (before.Total <= 0 || after.Total <= 0 || !before.Pct.HasValue || !after.Pct.HasValue) return board;

            var d = ScoreMoved(before, after);
            var score = "Score " + before.Pct + "%" + (before.Pct == after.Pct ? "" : " → " + after.Pct + "%");
            if (d != 0)
                score += " (" + (d > 0 ? "+" : "") + d + (Math.Abs(d) == 1 ? " check" : " checks") + ")";
            else if (extra > 0)
                score += " (unchanged: extra fixes are not in the %)";
            else if (moved > 0)
                score += " (unchanged: those checks do not pass yet)";
            return board + " · " + score;
        }
    }
}

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// AI-506: the scan's merge and the numbers shown from it, as pure code over <see cref="SpecDedupe.Row"/>.
    /// The same input gives the same rows and the same numbers on every scan, whatever the order inside a
    /// source, and the board, Fix all and the export all read ONE <see cref="Tally"/>.
    /// </summary>
    public static class ScanBoard
    {
        public const int Open = 0, Manual = 1, Ignored = 2, Resolved = 3;

        /// <summary>Backend rows, then checker rows, then spec rows; an id already taken is skipped; then one row
        /// per problem (<see cref="SpecDedupe"/>). Each kept row has the tier to show.</summary>
        public static List<SpecDedupe.Row> Merge(IEnumerable<SpecDedupe.Row> backend, IEnumerable<SpecDedupe.Row> audit, IEnumerable<SpecDedupe.Row> model)
            => Merge(backend, audit, model, null);

        /// <summary>AI-517: <paramref name="specPassed"/> = one-problem checks (<see cref="SpecDedupe.OneProblemChecks"/>) the add-in's
        /// spec evaluator PASSED (A4.1 at the Grid A/1 crossing: the full pass only, never a person row). The evaluator outranks a legacy backend / checker
        /// row of that check that still says fail ("Project Base Point surveyed" at E/N/Elev 0): the row is dropped, so no
        /// failing row stands against a pass. A fixable row is never dropped (AI-479). Mapping: <see cref="SpecReport.MapBackend(string, string, long)"/>
        /// sends category "Project Base Point" to A4.1.</summary>
        public static List<SpecDedupe.Row> Merge(IEnumerable<SpecDedupe.Row> backend, IEnumerable<SpecDedupe.Row> audit, IEnumerable<SpecDedupe.Row> model, IReadOnlyCollection<string> specPassed)
        {
            bool Suppressed(SpecDedupe.Row r) =>
                specPassed != null && specPassed.Count > 0 && !r.Fixable && SpecDedupe.IsOneProblem(r.CheckId)
                && specPassed.Contains((r.CheckId ?? "").Trim(), StringComparer.OrdinalIgnoreCase);
            backend = (backend ?? new SpecDedupe.Row[0]).Where(x => x == null || !Suppressed(x)).ToList();
            audit = (audit ?? new SpecDedupe.Row[0]).Where(x => x == null || !Suppressed(x)).ToList();
            var merged = new List<SpecDedupe.Row>();
            var skipped = new List<(SpecDedupe.Row row, string why)>();
            var known = new HashSet<string>(StringComparer.Ordinal);
            // The backend's own rows are all kept (as before AI-506); the other two sources skip a known id.
            foreach (var r in (backend ?? new SpecDedupe.Row[0]).Where(x => x != null)) { merged.Add(r); known.Add(r.Id ?? ""); }
            foreach (var r in (audit ?? new SpecDedupe.Row[0]).Where(x => x != null)) { if (known.Add(r.Id ?? "")) merged.Add(r); else skipped.Add((r, "skipped:id-already-taken")); }
            foreach (var r in (model ?? new SpecDedupe.Row[0]).Where(x => x != null))
            {
                if (string.IsNullOrEmpty(r.Id)) skipped.Add((r, "skipped:no-id"));
                else if (known.Add(r.Id)) merged.Add(r);
                else skipped.Add((r, "skipped:id-already-taken"));
            }
            var o = SpecDedupe.Resolve(merged);
            var kept = new List<SpecDedupe.Row>();
            for (var i = 0; i < merged.Count; i++)
            {
                if (!o.Keep[i]) continue;
                merged[i].Tier = o.Tier[i];
                merged[i].HeldParams = o.Held[i] != null ? (IReadOnlyCollection<string>)o.Held[i] : new string[0];
                kept.Add(merged[i]);
            }
            DedupeDump.Remember(merged, o, skipped, kept);
            return kept;
        }

        /// <summary>
        /// Every number from one list, so they reconcile: <c>Total = Open + Manual + Ignored + Resolved</c>,
        /// and Total is what an export writes with both options on. <see cref="Extra"/> and
        /// <see cref="Fixable"/> are parts of Open, never added to it.
        /// </summary>
        public sealed class Tally
        {
            /// <summary>The "Isu" number: AI-verified rows still open.</summary>
            public int Open;
            /// <summary>Rows that need a person or that someone marked manual (Manual tab). In the export, not in Open.</summary>
            public int Manual;
            public int Ignored;
            public int Resolved;
            /// <summary>Of Open: rows with no spec id, outside the %.</summary>
            public int Extra;
            /// <summary>Of Open: the changes Fix all makes (rows that ask for the same write are one change).</summary>
            public int Fixable;
            public int Total => Open + Manual + Ignored + Resolved;
        }

        public static Tally Count(IEnumerable<SpecDedupe.Row> rows)
        {
            var t = new Tally();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rows ?? new SpecDedupe.Row[0])
            {
                if (r == null) continue;
                switch (r.Bucket)
                {
                    case Open:
                        t.Open++;
                        if (r.Extra) t.Extra++;
                        if (r.FixKey != null && keys.Add(r.FixKey)) t.Fixable++;
                        break;
                    case Manual: t.Manual++; break;
                    case Ignored: t.Ignored++; break;
                    default: t.Resolved++; break;
                }
            }
            return t;
        }

        /// <summary>"943 open + 54 manual review = 997 in the file": what an export writes, by group.</summary>
        public static string Line(Tally t)
        {
            if (t == null) return "";
            var parts = new List<string> { t.Open + " open" };
            if (t.Manual > 0) parts.Add(t.Manual + " manual review");
            if (t.Ignored > 0) parts.Add(t.Ignored + " ignored");
            if (t.Resolved > 0) parts.Add(t.Resolved + " resolved");
            return string.Join(" + ", parts) + " = " + t.Total + " in the file"
                   + (t.Extra > 0 ? " (" + t.Extra + " open are extra, outside the %)" : "");
        }
    }
}

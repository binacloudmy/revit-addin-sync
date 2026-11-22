using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// AI-516 diagnostic: for the LAST scan, every board row before and after the Db.1 / legacy de-dupe with the fields the
    /// matching reads, and the reason code of the rule that dropped, held, trimmed or kept it. Three rounds of fixes were guessed
    /// from a CSV that has no element ids, categories or fix columns; one run on a real model writes the data instead.
    /// No UI. Written (a) after a scan when the environment variable <c>BINA_JKR_DEDUPE_DUMP=1</c> is set, to
    /// %TEMP%\BINA\jkr-dedupe-dump-*.json, and (b) next to a CSV / Excel export, as "&lt;export&gt;.dedupe.json".
    /// </summary>
    public static class DedupeDump
    {
        public const string EnvVar = "BINA_JKR_DEDUPE_DUMP";

        private static readonly object Gate = new object();
        private static List<Entry> _last;
        private static string _lastWhen = "";

        public sealed class Entry
        {
            public string Stage;          // "before" rows carry the verdict; skipped rows never reached the dedupe
            public string Id, CheckId, SpecCheckId, Source, Title, Rule, Category, RevitCategory, TypeName, Family, ElementLabel;
            public long ElementId;
            public int ElementIdCount;
            public List<long> FirstElementIds;
            public string ParamOfRule, ParamKey, ParamBaseKey, ParamKind;
            public bool ParamRule, Fixable, Extra, Kept;
            public string FixAction, FixKey, FixValue;
            public int TierBefore, TierAfter;
            public List<string> SpecParams;      // spec rows: every parameter listed
            public List<string> HeldParams;      // spec rows: those a fixable row reports instead
            public string Verdict;               // reason code
        }

        public static void Remember(IReadOnlyList<SpecDedupe.Row> merged, SpecDedupe.Outcome o, IReadOnlyList<(SpecDedupe.Row row, string why)> skipped, IReadOnlyList<SpecDedupe.Row> kept)
        {
            try
            {
                var keptSet = new HashSet<SpecDedupe.Row>(kept ?? new SpecDedupe.Row[0]);
                var list = new List<Entry>();
                for (var i = 0; i < merged.Count; i++)
                {
                    var e = Describe(merged[i], o.Reason[i]);
                    e.Kept = o.Keep[i];
                    e.TierAfter = o.Tier[i];
                    if (o.Held[i] != null) e.HeldParams = o.Held[i].OrderBy(x => x).ToList();
                    list.Add(e);
                }
                foreach (var s in skipped ?? new (SpecDedupe.Row, string)[0]) { var e = Describe(s.row, s.why); e.Kept = false; e.TierAfter = e.TierBefore; list.Add(e); }
                lock (Gate) { _last = list; _lastWhen = DateTime.Now.ToString("s"); }
                if (Environment.GetEnvironmentVariable(EnvVar) == "1") WriteTemp();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[DedupeDump] " + ex.Message); }
        }

        private static Entry Describe(SpecDedupe.Row r, string verdict)
        {
            var ids = (r.ElementIds ?? new long[0]).Where(x => x > 0).ToList();
            var pk = SpecDedupe.ParamKey(r.Param);
            return new Entry
            {
                Stage = "before",
                Id = r.Id, CheckId = r.CheckId, SpecCheckId = r.CheckId,
                Source = r.Source == SpecDedupe.FromModel ? "FromModel(spec)" : r.Source == SpecDedupe.FromBackend ? "FromBackend" : "FromAudit",
                Title = r.Title, Rule = r.RuleText, Category = r.Category, RevitCategory = r.Category, TypeName = r.TypeName, Family = r.Family, ElementLabel = r.ElementLabel,
                ElementId = r.ElementId, ElementIdCount = ids.Count, FirstElementIds = ids.Take(5).ToList(),
                ParamOfRule = r.Param, ParamKey = pk, ParamBaseKey = SpecDedupe.ParamBaseKey(r.Param), ParamKind = r.ParamKind,
                ParamRule = r.ParamRule, Fixable = r.Fixable, Extra = r.Extra, FixAction = r.FixAction, FixKey = r.FixKey, FixValue = r.FixValue,
                TierBefore = r.Tier,
                SpecParams = r.Source == SpecDedupe.FromModel ? (r.Params ?? new string[0]).ToList() : null,
                Verdict = string.IsNullOrEmpty(verdict) ? "kept:no-dedupe-rule-applied" : verdict,
            };
        }

        /// <summary>The last scan's dump as JSON (null when no scan has run).</summary>
        public static string Json()
        {
            List<Entry> l; string when;
            lock (Gate) { l = _last; when = _lastWhen; }
            if (l == null) return null;
            var summary = l.GroupBy(e => e.Verdict.Split('|')[0].Trim().Split(' ')[0]).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
            return JsonConvert.SerializeObject(new { issue = "AI-516", scannedAt = when, rowsBefore = l.Count(e => !e.Verdict.StartsWith("skipped")), rowsKept = l.Count(e => e.Kept), summary, rows = l }, Formatting.Indented);
        }

        private static string TempDir() => Path.Combine(Path.GetTempPath(), "BINA");

        private static void WriteTemp()
        {
            Directory.CreateDirectory(TempDir());
            WriteTo(Path.Combine(TempDir(), "jkr-dedupe-dump-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json"));
        }

        /// <summary>Writes the last scan's dump to <paramref name="path"/>. Never throws; returns the path or null.</summary>
        public static string WriteTo(string path)
        {
            try { var j = Json(); if (j == null) return null; File.WriteAllText(path, j); return path; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[DedupeDump] " + ex.Message); return null; }
        }

        /// <summary>Next to an export, only when BINA_JKR_DEDUPE_DUMP=1: "report.csv" gives "report.dedupe.json". Never throws.</summary>
        public static string WriteBesideExport(string exportPath)
        {
            if (Environment.GetEnvironmentVariable(EnvVar) != "1") return null;   // the export folder gets shared: only on request
            try { return WriteTo(Path.Combine(Path.GetDirectoryName(exportPath) ?? "", Path.GetFileNameWithoutExtension(exportPath) + ".dedupe.json")); }
            catch { return null; }
        }
    }
}

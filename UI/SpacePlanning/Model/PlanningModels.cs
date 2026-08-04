using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace RevitWebAppSync.UI.SpacePlanning.Model
{
    // ─── Wire shapes for POST {AIBaseUrl}/planning/suggest ────────────────────
    //
    // Deliberately Revit-free AND WPF-free so Tests/ and UiHarness/ can link this
    // file and exercise the contract against the frozen sample payload before the
    // bina-ai side is live. snake_case on both directions (same as the /cost pane
    // endpoint), so EVERY field carries an explicit [JsonProperty] — Newtonsoft's
    // default resolver would otherwise send/expect PascalCase and silently
    // deserialize every number to 0.
    //
    // UNITS: every *_m2 is square METRES and every room x/y/w/h is METRES,
    // origin bottom-left, +x east / +y north. The mm conversion happens in exactly
    // one place (MassingArgs.Build) — see the note there.

    /// <summary>Request body for /planning/suggest. Only <see cref="Brief"/> is
    /// required in v1; the rest are optional overrides.</summary>
    public sealed class SuggestRequest
    {
        [JsonProperty("brief")] public string Brief { get; set; }
        [JsonProperty("needs")] public object Needs { get; set; }
        [JsonProperty("target_gfa_m2")] public double? TargetGfaM2 { get; set; }
        [JsonProperty("site_width_m")] public double? SiteWidthM { get; set; }
        [JsonProperty("site_depth_m")] public double? SiteDepthM { get; set; }

        // The site figures the PROGRAM READ chips show. The backend echoes these
        // back (and also parses them out of the brief text, e.g. "tapak 5800 m2,
        // setback 6 m"), but the AUTHORITATIVE source is the property-line sketch
        // in the Revit model — so the add-in should send what it reads from the
        // document. Verified 2026-07-30: site_width_m/site_depth_m do NOT feed
        // these; only these two fields (or the brief text) do.
        [JsonProperty("site_area_m2")] public double? SiteAreaM2 { get; set; }
        [JsonProperty("setback_m")] public double? SetbackM { get; set; }

        [JsonProperty("user_id")] public int? UserId { get; set; }
    }

    /// <summary>Response body of /planning/suggest. On failure the backend returns
    /// success=false + error and no schemes — the pane shows the error inline and
    /// stays on Home.</summary>
    public sealed class SuggestResult
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
        [JsonProperty("soa")] public Soa Soa { get; set; }
        [JsonProperty("schemes")] public List<MassingScheme> Schemes { get; set; } = new List<MassingScheme>();
        [JsonProperty("rejected")] public List<RejectedScheme> Rejected { get; set; } = new List<RejectedScheme>();
        [JsonProperty("stats")] public MassingStats Stats { get; set; }

        // ── PROGRAM READ chips (added by the backend 2026-07-30) ─────────────
        // All nullable: site/setback are null unless supplied in the request or
        // stated in the brief, so each chip renders only when it has real data.
        // Never substitute a placeholder — a made-up site area on a screen full
        // of cited standards would undermine every cited number on it.
        [JsonProperty("site_area_m2")] public double? SiteAreaM2 { get; set; }
        [JsonProperty("setback_m")] public double? SetbackM { get; set; }
        [JsonProperty("target_gfa_m2")] public double? TargetGfaM2 { get; set; }
        [JsonProperty("building_type")] public string BuildingType { get; set; }

        /// <summary>Floor-to-floor height in METRES — the same figure the SOA's
        /// volume is derived from. Build extrudes and stacks to this so the model
        /// matches the reported isipadu. Null from a backend that predates the
        /// field, in which case <see cref="MassingArgs.DefaultStoreyHeightMm"/>
        /// applies. Added 2026-08-04: the two sides had disagreed silently (SOA at
        /// 3.6 m, masses built at 4.0 m).</summary>
        [JsonProperty("floor_height_m")] public double? FloorHeightM { get; set; }

        /// <summary>"sekolah_rendah" → "Sekolah rendah" for display.</summary>
        public string BuildingTypeLabel
        {
            get
            {
                if (string.IsNullOrWhiteSpace(BuildingType)) return null;
                var s = BuildingType.Replace('_', ' ').Trim();
                return s.Length == 0 ? null : char.ToUpperInvariant(s[0]) + s.Substring(1);
            }
        }

        /// <summary>Typed soft-failure — a backend outage must never throw into the UI.</summary>
        public static SuggestResult Fail(string error) => new SuggestResult { Success = false, Error = error };
    }

    /// <summary>Schedule of Accommodation — the derived room program. Every item
    /// carries the Malaysian-standard citation that produced its number.</summary>
    public sealed class Soa
    {
        [JsonProperty("items")] public List<SoaItem> Items { get; set; } = new List<SoaItem>();
        [JsonProperty("total_gfa_m2")] public double TotalGfaM2 { get; set; }
        [JsonProperty("sanitary")] public List<FixtureReq> Sanitary { get; set; } = new List<FixtureReq>();
        [JsonProperty("notes")] public string Notes { get; set; }
    }

    public sealed class SoaItem
    {
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("label_ms")] public string LabelMs { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("unit_area_m2")] public double UnitAreaM2 { get; set; }
        [JsonProperty("total_area_m2")] public double TotalAreaM2 { get; set; }
        /// <summary>Single storey, or null for a space that SPANS storeys. Kept for
        /// back-compat; prefer <see cref="LevelLabel"/>, which handles both.</summary>
        [JsonProperty("level")] public int? Level { get; set; }

        /// <summary>Every storey this space occupies — [1,2] for classrooms/support/
        /// toilets, [1] for the hall/canteen/field. Added 2026-07-30; before it,
        /// `level` was null on 4 of 6 rows and most of the program couldn't say
        /// where it sat.</summary>
        [JsonProperty("levels")] public List<int> Levels { get; set; }

        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("clause")] public string Clause { get; set; }
        [JsonProperty("advisory")] public bool Advisory { get; set; }

        /// <summary>"Tingkat 1", "Tingkat 1–2", "Tingkat 1, 3" or null. Prefers the
        /// levels array, falls back to the single level, and collapses a contiguous
        /// run to a dash so a 4-storey school doesn't print "Tingkat 1, 2, 3, 4".</summary>
        public string LevelLabel
        {
            get
            {
                var ls = (Levels ?? new List<int>()).Where(n => n > 0).Distinct().OrderBy(n => n).ToList();
                if (ls.Count == 0)
                    return Level.HasValue ? "Tingkat " + Level.Value : null;
                if (ls.Count == 1) return "Tingkat " + ls[0];
                bool contiguous = ls[ls.Count - 1] - ls[0] == ls.Count - 1;
                return contiguous
                    ? $"Tingkat {ls[0]}–{ls[ls.Count - 1]}"
                    : "Tingkat " + string.Join(", ", ls);
            }
        }

        /// <summary>"UBBL 1984 · By-law 42 &amp; 10th Schedule" — the citation chip's text.
        /// Either half may be missing, so this collapses rather than showing a bare "·".</summary>
        public string Citation =>
            string.IsNullOrWhiteSpace(Clause) ? (Source ?? "")
            : string.IsNullOrWhiteSpace(Source) ? Clause
            : Source + " · " + Clause;
    }

    /// <summary>One sanitary-fixture requirement (the auto-derived tandas counts).</summary>
    public sealed class FixtureReq
    {
        [JsonProperty("fixture")] public string Fixture { get; set; }   // wc | urinal | wash_basin
        [JsonProperty("gender")] public string Gender { get; set; }     // male | female | all
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("clause")] public string Clause { get; set; }
    }

    /// <summary>One candidate block scheme — a list of rooms as metric rectangles.</summary>
    public sealed class MassingScheme
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("rooms")] public List<MassingRoom> Rooms { get; set; } = new List<MassingRoom>();
        // Keys arrive as STRINGS ("1", "2") — JSON object keys always are.
        [JsonProperty("level_areas_m2")] public Dictionary<string, double> LevelAreasM2 { get; set; } = new Dictionary<string, double>();
        [JsonProperty("total_gfa_m2")] public double TotalGfaM2 { get; set; }
        [JsonProperty("footprint_m2")] public double FootprintM2 { get; set; }
        [JsonProperty("target_gfa_m2")] public double TargetGfaM2 { get; set; }
        [JsonProperty("margin_m2")] public double MarginM2 { get; set; }
        [JsonProperty("meets_gfa")] public bool MeetsGfa { get; set; }
        [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>Floor area for one level, 0 when the scheme has no such level.</summary>
        public double LevelArea(int level) =>
            LevelAreasM2 != null && LevelAreasM2.TryGetValue(level.ToString(), out var a) ? a : 0.0;

        /// <summary>Levels actually present in the room list, ascending. Drives the
        /// L1/L2 toggle — a single-storey scheme must not offer an empty L2.</summary>
        public List<int> Levels()
        {
            var seen = new SortedSet<int>();
            if (Rooms != null)
                foreach (var r in Rooms) seen.Add(r.Level);
            return new List<int>(seen);
        }
    }

    /// <summary>One room rectangle. x/y is the bottom-left corner in METRES.</summary>
    public sealed class MassingRoom
    {
        [JsonProperty("label")] public string Label { get; set; }
        // kelas | sokongan | tandas | perhimpunan | kantin | padang | selasar
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("w")] public double W { get; set; }
        [JsonProperty("h")] public double H { get; set; }
        [JsonProperty("level")] public int Level { get; set; }
        // false → site-only (the padang): drawn in the preview, never built as a slab.
        [JsonProperty("counts_as_gfa")] public bool CountsAsGfa { get; set; } = true;
    }

    public sealed class RejectedScheme
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("total_gfa_m2")] public double TotalGfaM2 { get; set; }
        [JsonProperty("gap_m2")] public double GapM2 { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }

        /// <summary>"below_target_gfa" → "below target GFA" for the collapsed list.</summary>
        public string ReasonLabel =>
            string.IsNullOrEmpty(Reason) ? "" : Reason.Replace('_', ' ');
    }

    public sealed class MassingStats
    {
        [JsonProperty("target_gfa_m2")] public double TargetGfaM2 { get; set; }
        [JsonProperty("scheme_count")] public int SchemeCount { get; set; }
        [JsonProperty("passing_count")] public int PassingCount { get; set; }
        [JsonProperty("rejected_count")] public int RejectedCount { get; set; }
        [JsonProperty("best_margin_m2")] public double? BestMarginM2 { get; set; }
    }
}

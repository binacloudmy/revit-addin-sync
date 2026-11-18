using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.Services.JkrSpec
{
    // Spec checks for Borang sections D (Architectural Families) and E (View
    // Templates) — see SpecEval.cs for the contract. Rules: Tests/data/jkr_spec_rules.tsv
    // rows Da.1–De.3 and E.1; detail: model-audit-spec.md §D, §E.
    public static partial class SpecChecks
    {
        static partial void RegisterDE(Dictionary<string, SpecRule> rules)
        {
            rules["Da.1"] = DaTypeNames;
            rules["Db.1"] = DbMandatoryParameters;
            rules["Db.2"] = DbMaterials;
            rules["Dc.1"] = DcNameNumbers;
            rules["Dd.1"] = DdNameCategory;
            rules["Dd.2"] = DdTechnique;
            rules["De.1"] = DeRoom;
            rules["De.2"] = DeColumns;
            rules["De.3"] = DeNotModelled;
            rules["E.1"] = EViewTemplates;
        }

        static partial void BackendDE(Dictionary<string, string> reasons)
        {
            reasons["Da.1"] = "Whether the code in a type name is a real JKR component code needs the JKR code list, which the backend holds; the add-in tests the coding pattern only.";
            reasons["Db.1"] = "The mandatory parameters per category and LOi (Buku Parameter p.38–102) are the backend's rule set; the add-in checks only the parameters the book makes mandatory for every AR category from LOi 400.";
            reasons["Db.2"] = "Whether a material code is a real JKR material code needs the JKR code list, which the backend holds; the add-in tests the 'jkrXX' prefix only.";
        }

        // ─── shared ───

        /// <summary>The Borang D family rows (Model Audit Form BIM010, F D.1–D.23, Room
        /// excluded: it has no type) plus Planting (Buku Parameter p.70).</summary>
        internal static readonly string[] SectionDCategories =
        {
            "Floors", "Walls", "Ceilings", "Roofs", "Stairs", "Railings", "Ramps", "Curtain Panels",
            "Doors", "Windows", "Casework", "Furniture Systems", "Furniture", "Plumbing Fixtures",
            "Specialty Equipment", "Generic Models", "Structural Columns", "Columns", "Parking",
            "Pipes", "Topography", "Mass", "Planting",
        };

        /// <summary>The AR element categories that have a Buku Parameter page (PB p.38–102:
        /// Casework … Window), as Revit categories. Column (p.45) covers both column categories;
        /// Curtain System (p.48) is the curtain panels.</summary>
        internal static readonly string[] PbElementCategories =
        {
            "Casework", "Ceilings", "Columns", "Structural Columns", "Curtain Panels", "Doors", "Floors",
            "Furniture", "Furniture Systems", "Generic Models", "Planting", "Plumbing Fixtures", "Ramps",
            "Railings", "Roofs", "Specialty Equipment", "Stairs", "Walls", "Windows",
        };

        private static bool In(string[] list, string category) =>
            list.Any(c => string.Equals(c, (category ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<SpecType> TypesIn(SpecModel m, string[] categories) =>
            m.Types.Where(t => In(categories, t.Category));

        private static string Label(SpecType t) => t.Category + " · " + t.TypeName;

        /// <summary>Borang row the pane files the result under: the fixed English Revit
        /// category for Section D, the view set for E.1 (W4 panel wiring).</summary>
        private static SpecResult InCat(this SpecResult r, string category)
        {
            if (r != null) r.Category = category ?? "";
            return r;
        }

        private static void Bump(Dictionary<string, int> counts, string category) =>
            counts[category] = (counts.TryGetValue(category, out var n) ? n : 0) + 1;

        private static string Mm(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + " mm";

        /// <summary>One failure for a placed type: located at its instances, keyed by the
        /// type id so Ignore / Reviewed survive a re-scan.</summary>
        private static SpecResult FailType(string id, SpecType t, string bm, string en, SpecCoverage coverage,
                                           string actual = "", string required = "", string keySuffix = "")
        {
            var first = t.InstanceIds.FirstOrDefault(x => x > 0);
            return Fail(id, bm, first > 0 ? first : t.TypeId, Label(t), actual, required,
                        key: "type:" + t.TypeId + keySuffix, coverage: coverage,
                        elementIds: t.InstanceIds.Count > 0 ? t.InstanceIds : null).En(en).InCat(t.Category);
        }

        private static SpecResult UnjudgedType(string id, SpecType t, string bm, string en, string keySuffix = "")
        {
            var first = t.InstanceIds.FirstOrDefault(x => x > 0);
            return Unjudged(id, bm, first > 0 ? first : t.TypeId, Label(t), key: "type:" + t.TypeId + keySuffix).En(en).InCat(t.Category);
        }

        // ─── Da.1 — type name follows JKR coding ───

        // Revit / library default names the spec calls generic ("Door 1", "Basic Wall").
        private static readonly Regex GenericName = new Regex(
            @"^\s*(generic|default|basic|standard|new|type|door|window|wall|floor|roof|ceiling|column|stair|railing|ramp|panel|pintu|tingkap|dinding|lantai)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Da.1 (T 22:27–22:28): every placed type in the Borang D categories must carry the
        /// JKR coding — a component code "(PT2p600a)" or the "jkrAR…" prefix, as
        /// <see cref="AuditNaming.IsJkrName"/> defines it (the repo's JKR naming rule, shared
        /// with fill_audit). A bare material name ("UPVC") is NOT JKR coding: fill_audit's
        /// looser <see cref="AuditNaming.IsSectionDCompliant"/> accepts it, the spec does not.
        /// Generic names ("Door 1", "Basic Wall") fail at once. Coverage is FailOnly: the
        /// pattern is tested, not whether the code exists in the JKR code list (backend).
        /// </summary>
        private static IEnumerable<SpecResult> DaTypeNames(SpecModel m)
        {
            const string id = "Da.1";
            var ok = new Dictionary<string, int>(StringComparer.Ordinal);
            var byFamily = 0;
            foreach (var t in TypesIn(m, SectionDCategories))
            {
                if (AuditNaming.IsJkrName(t.TypeName)) { Bump(ok, t.Category); continue; }
                // A loadable family carries the JKR code in its family name and the size or
                // variant in the type name ("jkrAR18_wdw_TKt_…_(TKt001)-3" : "1800 X 500 mm
                // @TK11a"); Revit shows both as the type. Real model, 16 Nov: 12 of 23 such
                // types were failed on the type name alone. System families ("Basic Wall",
                // "System Panel", "Ramp") never match, so they are still judged on the type.
                if (AuditNaming.IsJkrName(t.FamilyName)) { Bump(ok, t.Category); byFamily++; continue; }
                var name = t.TypeName.Trim();
                var generic = name.Length == 0 || GenericName.IsMatch(name);
                var bm = name.Length == 0
                    ? $"{t.Category}: type tanpa nama ({t.InstanceCount} elemen) — tidak ikut coding JKR"
                    : $"{t.Category}: type '{name}' ({t.InstanceCount} elemen) tidak ikut coding JKR{(generic ? " (nama generik)" : "")} — tiada kod '(XXnnn)' atau awalan 'jkrAR'";
                var en = name.Length == 0
                    ? $"{t.Category}: unnamed type ({t.InstanceCount} elements) does not follow JKR coding"
                    : $"{t.Category}: type '{name}' ({t.InstanceCount} elements) does not follow JKR coding{(generic ? " (generic name)" : "")} — no '(XXnnn)' code or 'jkrAR' prefix";
                yield return FailType(id, t, bm, en, SpecCoverage.FailOnly, actual: name,
                                      required: "(XXnnn) … / jkrAR_…");
            }
            foreach (var kv in ok)
                yield return Pass(id, $"{kv.Key}: {kv.Value} type ikut pola coding JKR (kod belum disahkan dengan senarai kod JKR)", kv.Value, SpecCoverage.FailOnly)
                    .En($"{kv.Key}: {kv.Value} types follow the JKR coding pattern (codes not yet checked against the JKR code list)").InCat(kv.Key);
            if (byFamily > 0)
                yield return SpecChecks.Context(id, $"{byFamily} type diterima kerana kod JKR pada nama family (nama type hanya saiz/varian)")
                    .En($"{byFamily} types accepted because the JKR code is in the family name (the type name holds only the size/variant)");
        }

        // ─── Db.1 — mandatory parameters per Buku Parameter × LOD ───

        /// <summary>LOi from which every AR element category in the Buku Parameter
        /// (p.38–102) ticks the parameters in <see cref="PbMandatoryAllArFromLoi400"/>.</summary>
        internal const int PbCommonLoi = 400;

        /// <summary>The parameters the Buku Parameter marks mandatory (no "*" = "isi jika
        /// berkaitan") for EVERY AR element category, ticked at LOi 400 and 500 in each
        /// (PB p.38–102: type Kod_Jenis_jkr_stt; instance purchase / install / supplier /
        /// contractor data). Text-valued ones only — the snapshot's Texts.</summary>
        internal static readonly string[] PbMandatoryAllArFromLoi400 =
        {
            "Kod_Jenis_jkr_stt",
            "Tarikh_Perolehan_jkr_sit", "Kod_PTJ_jkr_sit", "Tarikh_Dipasang_jkr_sit",
            "Tarikh_Waranti_Tamat_jkr_sit", "Tarikh_Tamat_DLP_jkr_sit",
            "Pembekal_jkr_sit", "Alamat_Pembekal_jkr_sit", "No_Tel_Pembekal_jkr_sit",
            "Kontraktor_jkr_sit", "Alamat_Kontraktor_jkr_sit", "No_Tel_Kontraktor_jkr_sit",
        };

        /// <summary>
        /// Db.1 (PB per category, T 22:28): every parameter mandatory at the project's LOi
        /// must be filled. The full per-category × LOi set is the backend's (BackendDE). The
        /// add-in tests the part it can prove from the book alone: at project LOD ≥ 400, the
        /// parameters every AR category makes mandatory (<see cref="PbMandatoryAllArFromLoi400"/>)
        /// that the type (or its first placed instance) carries but leaves empty → fail.
        /// A parameter the model does not carry is not judged (it may not be text-typed or
        /// may be read under another name). FailOnly. Below LOD 400 this part has nothing
        /// the book makes mandatory for every category, so it returns nothing.
        /// </summary>
        private static IEnumerable<SpecResult> DbMandatoryParameters(SpecModel m)
        {
            const string id = "Db.1";
            if (m.Lod < PbCommonLoi) yield break;
            var ok = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in TypesIn(m, PbElementCategories))
            {
                var present = PbMandatoryAllArFromLoi400.Where(p => t.Texts.ContainsKey(p)).ToList();
                if (present.Count == 0) continue;
                var empty = present.Where(p => string.IsNullOrWhiteSpace(t.Texts[p])).ToList();
                if (empty.Count == 0) { Bump(ok, t.Category); continue; }
                var list = string.Join(", ", empty);
                yield return FailType(id, t,
                    $"{t.Category}: type '{t.TypeName}' — {empty.Count}/{present.Count} parameter wajib LOi {m.Lod} kosong: {list}",
                    $"{t.Category}: type '{t.TypeName}' — {empty.Count}/{present.Count} mandatory LOi {m.Lod} parameters empty: {list}",
                    SpecCoverage.FailOnly, actual: "kosong: " + list, required: "terisi (PB, LOi " + m.Lod + ")");
            }
            foreach (var kv in ok)
                yield return Pass(id, $"{kv.Key}: {kv.Value} type: parameter wajib semua kategori AR (LOi {PbCommonLoi}+) terisi — senarai penuh per kategori belum disemak", kv.Value, SpecCoverage.FailOnly)
                    .En($"{kv.Key}: {kv.Value} types: parameters mandatory for every AR category (LOi {PbCommonLoi}+) filled — the full per-category list is not checked here").InCat(kv.Key);
        }

        // ─── Db.2 — element material filled and JKR-coded ───

        // "jkrAR_dor-b_(ab)-2 Aluminium Bronzed Anodised" (PB p.35, Material Identity):
        // lower-case "jkr", the two-letter discipline, then anything.
        private static readonly Regex JkrMaterial = new Regex(@"^jkr[A-Z]{2}", RegexOptions.CultureInvariant);

        /// <summary>True when a material name carries the JKR coding prefix ("jkrAR_…", PB p.35).</summary>
        internal static bool IsJkrMaterial(string name) => JkrMaterial.IsMatch((name ?? "").Trim());

        /// <summary>
        /// Db.2 (T 22:28–22:29; PB p.35): each placed type in a Buku Parameter AR category has
        /// at least one material, and every material on it is JKR-coded ("jkrAR_…"). No
        /// material → fail; any material without the prefix → fail. FailOnly: the prefix is
        /// tested, not the code against the JKR code list (backend).
        /// </summary>
        private static IEnumerable<SpecResult> DbMaterials(SpecModel m)
        {
            const string id = "Db.2";
            var ok = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in TypesIn(m, PbElementCategories))
            {
                var mats = t.Materials.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                    .Distinct(StringComparer.Ordinal).ToList();
                if (mats.Count == 0)
                {
                    yield return FailType(id, t,
                        $"{t.Category}: type '{t.TypeName}' tiada material",
                        $"{t.Category}: type '{t.TypeName}' has no material",
                        SpecCoverage.FailOnly, actual: "tiada material", required: "material jkrAR_…", keySuffix: ":none");
                    continue;
                }
                var bad = mats.Where(x => !IsJkrMaterial(x)).ToList();
                if (bad.Count == 0) { Bump(ok, t.Category); continue; }
                var list = string.Join(", ", bad.Take(5).Select(x => "'" + x + "'")) + (bad.Count > 5 ? " …" : "");
                yield return FailType(id, t,
                    $"{t.Category}: type '{t.TypeName}' guna material tidak ikut coding JKR: {list}",
                    $"{t.Category}: type '{t.TypeName}' uses materials not JKR-coded: {list}",
                    SpecCoverage.FailOnly, actual: string.Join(", ", bad), required: "jkrAR_…", keySuffix: ":code");
            }
            foreach (var kv in ok)
                yield return Pass(id, $"{kv.Key}: {kv.Value} type: material terisi dan berawalan JKR (kod belum disahkan dengan senarai kod JKR)", kv.Value, SpecCoverage.FailOnly)
                    .En($"{kv.Key}: {kv.Value} types: materials filled and JKR-prefixed (codes not yet checked against the JKR code list)").InCat(kv.Key);
        }

        // ─── Dc.1 — numbers in the type name tally with the geometry ───

        private static readonly Regex Bracketed = new Regex(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.CultureInvariant);

        // "900 x 2325", "0915 x 2134mm", "600mm × 1800mm"
        private static readonly Regex Pair = new Regex(
            @"(?<![A-Za-z0-9.])(\d{2,5}(?:\.\d+)?)\s*(?:mm)?\s*[xX×*]\s*(\d{2,5}(?:\.\d+)?)\s*(?:mm)?(?![A-Za-z0-9.'""])",
            RegexOptions.CultureInvariant);

        // A stand-alone number read as mm: "Wall 146", "Generic 150mm"; not "s900",
        // "2.4m", "12\"", "60 min", "35%".
        private static readonly Regex Single = new Regex(
            @"(?<![A-Za-z0-9.])(\d{2,5}(?:\.\d+)?)(?:\s*mm)?(?![A-Za-z0-9.'""%°])(?!\s*(?:min|minit|hr|jam|deg|kg|kn|mpa)\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The category's single-number parameter and the plausible range (mm) for
        /// reading a name number as it; null = the name's single number is not read.</summary>
        private static (string Param, double Min, double Max)? SingleParamOf(string category)
        {
            switch ((category ?? "").Trim())
            {
                case "Walls": return ("Width", 10, 1500);
                case "Floors": case "Roofs": case "Ceilings": case "Ramps": return ("Thickness", 5, 2000);
                case "Railings": return ("Height", 500, 2500);
                default: return null;
            }
        }

        private static bool IsDoorOrWindow(string category) =>
            string.Equals(category, "Doors", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "Windows", StringComparison.OrdinalIgnoreCase);

        private static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>The (parameter, value-in-name) pairs a type name states, as Dc.1 reads it:
        /// doors / windows "W x H"; walls one number = Width; floors, roofs, ceilings, ramps one
        /// number = Thickness; railings one number = Height. JKR codes and other bracketed text
        /// are ignored; a name with several candidate numbers is not read (ambiguous).</summary>
        internal static List<(string Param, double Value)> NameNumbers(string category, string typeName)
        {
            var got = new List<(string, double)>();
            var s = Bracketed.Replace(typeName ?? "", " ");
            var pairs = Pair.Matches(s);
            if (IsDoorOrWindow(category))
            {
                if (pairs.Count != 1) return got;
                double w = Num(pairs[0].Groups[1].Value), h = Num(pairs[0].Groups[2].Value);
                if (w < 100 || w > 10000 || h < 100 || h > 10000) return got;
                got.Add(("Width", w));
                got.Add(("Height", h));
                return got;
            }
            var single = SingleParamOf(category);
            if (single == null || pairs.Count > 0) return got;      // "600 x 600 grid" is not a thickness
            var nums = Single.Matches(s).Cast<Match>().Select(x => Num(x.Groups[1].Value)).ToList();
            if (nums.Count != 1) return got;
            var v = nums[0];
            if (v < single.Value.Min || v > single.Value.Max) return got;
            got.Add((single.Value.Param, v));
            return got;
        }

        /// <summary>
        /// Dc.1 (T 22:33–22:34): a number in the type name (read as mm) must equal the
        /// geometry parameter it names — "Wall 146" → Width 146; door "900 x 2325" → Width 900,
        /// Height 2325 (see <see cref="NameNumbers"/>). Mismatches only (±1 mm): one fail per
        /// type, "Type name | value in name | parameter | actual". A name number whose parameter
        /// the snapshot lacks is unjudged. FailOnly: names whose numbers cannot be read
        /// unambiguously are not checked.
        /// </summary>
        private static IEnumerable<SpecResult> DcNameNumbers(SpecModel m)
        {
            const string id = "Dc.1";
            var ok = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in TypesIn(m, SectionDCategories))
            {
                var stated = NameNumbers(t.Category, t.TypeName);
                if (stated.Count == 0) continue;
                var bad = new List<string>();
                var badEn = new List<string>();
                var missing = new List<string>();
                foreach (var (param, value) in stated)
                {
                    if (!t.Numbers.TryGetValue(param, out var actual) || double.IsNaN(actual)) { missing.Add(param); continue; }
                    if (Math.Abs(actual - value) > 1.0)
                    {
                        bad.Add($"{param} {Mm(actual)} (nama: {Mm(value)})");
                        badEn.Add($"{param} {Mm(actual)} (name: {Mm(value)})");
                    }
                }
                if (bad.Count > 0)
                {
                    yield return FailType(id, t,
                        $"{t.Category}: type '{t.TypeName}' — nilai dalam nama tidak padan: {string.Join("; ", bad)}",
                        $"{t.Category}: type '{t.TypeName}' — number in the name does not match: {string.Join("; ", badEn)}",
                        SpecCoverage.FailOnly,
                        actual: string.Join("; ", stated.Where(x => t.Numbers.ContainsKey(x.Param)).Select(x => x.Param + " " + Mm(t.Numbers[x.Param]))),
                        required: string.Join("; ", stated.Select(x => x.Param + " " + Mm(x.Value))));
                }
                else if (missing.Count > 0)
                {
                    yield return UnjudgedType(id, t,
                        $"{t.Category}: type '{t.TypeName}' — nilai {string.Join(", ", missing)} tidak dibaca dari model; tidak dapat dibanding dengan nama",
                        $"{t.Category}: type '{t.TypeName}' — {string.Join(", ", missing)} was not read from the model; cannot compare with the name");
                }
                else Bump(ok, t.Category);
            }
            foreach (var kv in ok)
                yield return Pass(id, $"{kv.Key}: {kv.Value} type: nilai dalam nama padan dengan parameter geometri", kv.Value, SpecCoverage.FailOnly)
                    .En($"{kv.Key}: {kv.Value} types: numbers in the name match the geometry").InCat(kv.Key);
        }

        // ─── Dd.1 — name ↔ Revit category ───

        // JKR component code → the Revit categories it belongs in. Prefixes from the Buku
        // Parameter Kod_Komponen examples (PB p.38–102): PTc080a door, TKk360a window,
        // SLt750 ceiling, BGc003 roof, LBk201a floor, LAk001 ramp, TGc001 stair / railing,
        // DGb493a wall / DGg188a curtain system, Co100 column, PKa100a planting, LSb048
        // plumbing, PBm/PAm/PMt casework / furniture / furniture system (one "perabot" group).
        // Prefixes the book gives no clear category for (GM, AL …) are not judged.
        private static readonly Dictionary<string, string[]> CodeCategories = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["PT"] = new[] { "Doors" },
            ["TK"] = new[] { "Windows", "Curtain Panels" },
            ["SL"] = new[] { "Ceilings" },
            ["BG"] = new[] { "Roofs" },
            ["LB"] = new[] { "Floors" },
            ["LA"] = new[] { "Ramps", "Floors" },
            ["TG"] = new[] { "Stairs", "Railings" },
            ["DG"] = new[] { "Walls", "Curtain Panels", "Curtain Wall Mullions" },
            ["CO"] = new[] { "Columns", "Structural Columns" },
            ["PK"] = new[] { "Planting" },
            ["LS"] = new[] { "Plumbing Fixtures" },
            ["PB"] = new[] { "Casework", "Furniture", "Furniture Systems" },
            ["PA"] = new[] { "Casework", "Furniture", "Furniture Systems" },
            ["PM"] = new[] { "Casework", "Furniture", "Furniture Systems" },
        };

        private static readonly Regex CodeRx = new Regex(@"\(([A-Z][A-Za-z])[A-Za-z]*\d[A-Za-z0-9]*\)", RegexOptions.CultureInvariant);

        // First word of a name → the categories that word names (EN and BM).
        private static readonly Dictionary<string, string[]> WordCategories = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["door"] = new[] { "Doors" }, ["doors"] = new[] { "Doors" }, ["pintu"] = new[] { "Doors" },
            ["window"] = new[] { "Windows", "Curtain Panels" }, ["windows"] = new[] { "Windows", "Curtain Panels" },
            ["tingkap"] = new[] { "Windows", "Curtain Panels" },
            ["wall"] = new[] { "Walls" }, ["dinding"] = new[] { "Walls" },
            ["floor"] = new[] { "Floors" }, ["lantai"] = new[] { "Floors" },
            ["ceiling"] = new[] { "Ceilings" }, ["siling"] = new[] { "Ceilings" },
            ["roof"] = new[] { "Roofs" }, ["bumbung"] = new[] { "Roofs" },
            ["stair"] = new[] { "Stairs", "Railings" }, ["stairs"] = new[] { "Stairs", "Railings" }, ["tangga"] = new[] { "Stairs", "Railings" },
            ["railing"] = new[] { "Railings" }, ["handrail"] = new[] { "Railings" }, ["guardrail"] = new[] { "Railings" },
            ["balustrade"] = new[] { "Railings" },
            ["ramp"] = new[] { "Ramps", "Floors", "Railings" },
            ["column"] = new[] { "Columns", "Structural Columns" }, ["tiang"] = new[] { "Columns", "Structural Columns" },
            ["curtain"] = new[] { "Walls", "Curtain Panels", "Curtain Wall Mullions", "Doors", "Windows" },
            ["mullion"] = new[] { "Curtain Wall Mullions" },
        };

        // Categories whose names routinely start with the host element ("Door Closer",
        // "Wall Cabinet", "Floor Trap"): a first-word keyword is read only when the rest
        // of the name has no other word ("Floor 150", "Wall").
        private static readonly string[] AccessoryCategories =
        {
            "Casework", "Furniture", "Furniture Systems", "Plumbing Fixtures", "Specialty Equipment",
            "Generic Models", "Planting", "Parking", "Mass", "Pipes", "Topography",
        };

        private static readonly Regex Words = new Regex(@"[A-Za-z]+", RegexOptions.CultureInvariant);

        /// <summary>The categories a name's first word names, or null. In an accessory
        /// category the word counts only when it is the name's only word.</summary>
        private static string[] WordExpectation(string name, bool accessory)
        {
            var s = Bracketed.Replace(name ?? "", " ");
            var words = Words.Matches(s).Cast<Match>().Select(x => x.Value).ToList();
            if (words.Count == 0) return null;
            if (!WordCategories.TryGetValue(words[0], out var cats)) return null;
            if (accessory && words.Skip(1).Any(w => !string.Equals(w, "mm", StringComparison.OrdinalIgnoreCase))) return null;
            return cats;
        }

        /// <summary>The categories the type's name says it belongs in, and what said so
        /// ("kod (TK…)" / "nama" / "famili"); null when the name says nothing judgeable.</summary>
        internal static (string[] Categories, string Source)? ExpectedCategories(SpecType t)
        {
            var code = CodeRx.Match(t.TypeName ?? "");
            if (code.Success && CodeCategories.TryGetValue(code.Groups[1].Value.ToUpperInvariant(), out var byCode))
                return (byCode, "kod (" + code.Groups[1].Value.ToUpperInvariant() + "…)");
            var accessory = In(AccessoryCategories, t.Category);
            var byType = WordExpectation(t.TypeName, accessory);
            var byFamily = WordExpectation(t.FamilyName, accessory);
            if (byType == null && byFamily == null) return null;
            // Either name matching the category is enough ("Door-Double" family, "Panel 900" type).
            var all = (byType ?? new string[0]).Concat(byFamily ?? new string[0]).Distinct().ToArray();
            return (all, byType != null ? "nama" : "famili");
        }

        /// <summary>
        /// Dd.1 (T 22:35): the Revit category must be the one the name implies — a window
        /// type is not in Doors, a floor is in Floors. Expected category from the JKR
        /// component code in the type name (strongest; <see cref="CodeCategories"/>), else
        /// from the first word of the type or family name (EN/BM keywords). Mismatches only;
        /// FailOnly, since names without a code or keyword are not judged.
        /// </summary>
        private static IEnumerable<SpecResult> DdNameCategory(SpecModel m)
        {
            const string id = "Dd.1";
            var ok = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in TypesIn(m, SectionDCategories))
            {
                var exp = ExpectedCategories(t);
                if (exp == null) continue;
                if (In(exp.Value.Categories, t.Category)) { Bump(ok, t.Category); continue; }
                var expected = string.Join(" / ", exp.Value.Categories);
                var srcEn = exp.Value.Source.StartsWith("kod", StringComparison.Ordinal) ? exp.Value.Source.Replace("kod", "code")
                          : exp.Value.Source == "nama" ? "name" : "family";
                yield return FailType(id, t,
                    $"Type '{t.TypeName}' dalam kategori {t.Category} — {exp.Value.Source} menunjukkan {expected}",
                    $"Type '{t.TypeName}' is in {t.Category} — its {srcEn} says {expected}",
                    SpecCoverage.FailOnly, actual: t.Category, required: expected);
            }
            foreach (var kv in ok)
                yield return Pass(id, $"{kv.Key}: {kv.Value} type: kategori Revit padan dengan nama", kv.Value, SpecCoverage.FailOnly)
                    .En($"{kv.Key}: {kv.Value} types: Revit category matches the name").InCat(kv.Key);
        }

        // ─── Dd.2, De.1–De.3 — human / list rows ───

        /// <summary>Dd.2 (T 22:35–22:36): modelling technique is out of the automatic
        /// check's scope — a person decides. Context only.</summary>
        private static IEnumerable<SpecResult> DdTechnique(SpecModel m)
        {
            yield return Context("Dd.2", "SEMAK MANUAL — teknik pemodelan (cara letak permukaan dll) di luar skop semakan automatik")
                .En("MANUAL CHECK — modelling technique (how surfaces are placed, etc.) is outside the automatic check");
        }

        /// <summary>De.1 (F D.8): Room has no ElementType, so Section D does not judge it;
        /// rooms are checked under A6 and C1.6. List only.</summary>
        private static IEnumerable<SpecResult> DeRoom(SpecModel m)
        {
            var placed = m.Rooms.Count(r => r.Area > 0);
            yield return Listing("De.1", $"Room tiada ElementType — semakan room di A6 / C1.6 ({placed} room diletak)", placed)
                .En($"Rooms have no element type — checked under A6 / C1.6 ({placed} placed rooms)").InCat("Rooms");
        }

        private static int CountOf(SpecModel m, string category) =>
            m.CategoryCounts.Where(kv => string.Equals(kv.Key, category, StringComparison.OrdinalIgnoreCase)).Sum(kv => Math.Max(0, kv.Value));

        /// <summary>
        /// De.2 (F D.18, D.21): Columns / Structural Columns with 0 elements → "sahkan skop"
        /// for a person, never an automatic MAJOR. One Context note with both counts
        /// ("Columns: 0 · Structural Columns: 4"), so a 0 in one is read beside the other; it
        /// asks to confirm scope only for a category that is 0, and then carries an issue the
        /// person can open (filed under the first zero category). Nothing when the category
        /// counts were not read.
        /// </summary>
        private static IEnumerable<SpecResult> DeColumns(SpecModel m)
        {
            if (m.CategoryCounts.Count == 0) yield break;
            var cats = new[] { "Columns", "Structural Columns" };
            var counts = cats.Select(c => (Cat: c, N: CountOf(m, c))).ToList();
            var both = string.Join(" · ", counts.Select(x => x.Cat + ": " + x.N));
            var zero = counts.Where(x => x.N == 0).Select(x => x.Cat).ToList();
            if (zero.Count == 0)
            {
                yield return Context("De.2", both + " — kedua-dua kategori dimodelkan")
                    .En(both + " — both categories are modelled").InCat(cats[0]);
                yield break;
            }
            var z = string.Join(", ", zero);
            yield return Context("De.2", $"{both} — {z} 0 elemen: sahkan skop", key: string.Join("|", zero))
                .En($"{both} — {z} 0 elements: confirm scope").InCat(zero[0]);
        }

        /// <summary>The Buku Parameter AR categories (PB p.36–102; C 3.1 point 5), as Revit
        /// categories, for De.3. Column = Columns + Structural Columns.</summary>
        private static readonly (string Pb, string[] Revit)[] PbArCategories =
        {
            ("Area", new[] { "Areas" }), ("Room", new[] { "Rooms" }), ("Casework", new[] { "Casework" }),
            ("Ceiling", new[] { "Ceilings" }), ("Column", new[] { "Columns", "Structural Columns" }),
            ("Curtain System", new[] { "Curtain Panels" }), ("Door", new[] { "Doors" }), ("Floor", new[] { "Floors" }),
            ("Furniture", new[] { "Furniture" }), ("Furniture System", new[] { "Furniture Systems" }),
            ("Generic Model", new[] { "Generic Models" }), ("Planting", new[] { "Planting" }),
            ("Plumbing Fixture", new[] { "Plumbing Fixtures" }), ("Ramp", new[] { "Ramps" }),
            ("Railing", new[] { "Railings" }), ("Roof", new[] { "Roofs" }),
            ("Specialty Equipment", new[] { "Specialty Equipment" }), ("Stair", new[] { "Stairs" }),
            ("Wall", new[] { "Walls" }), ("Window", new[] { "Windows" }),
        };

        /// <summary>
        /// De.3 (C 3.1): categories not modelled are "tidak berkenaan", consistent with C 3.1 —
        /// listed, never a shortfall. Lists each Buku Parameter AR category with 0 placed
        /// elements (one listing per category). Nothing when the category counts were not read.
        /// </summary>
        private static IEnumerable<SpecResult> DeNotModelled(SpecModel m)
        {
            if (m.CategoryCounts.Count == 0) yield break;
            var none = PbArCategories.Where(c => c.Revit.Sum(r => CountOf(m, r)) == 0).ToList();
            if (none.Count == 0)
            {
                yield return Listing("De.3", "Semua kategori AR Buku Parameter dimodelkan").En("Every Buku Parameter AR category is modelled");
                yield break;
            }
            // One row per category, filed under its Revit category (Column under Columns).
            foreach (var c in none)
                yield return Listing("De.3", $"{c.Pb}: tidak dimodelkan — tidak berkenaan")
                    .En($"{c.Pb}: not modelled — not applicable").InCat(c.Revit[0]);
        }

        // ─── E.1 — each set's views use that set's template ───

        /// <summary>
        /// E.1 (T 22:37–22:39; spec revision 2): every BOMBA / PBT / Dokumen view uses its set's
        /// template: BOMBA → a template named Bomba, PBT → PBT, Dokumen → Dokumen. The set comes
        /// from <see cref="SpecViewSets"/> as in C 1.3–1.5 (browser folder / View_jkr_pit first),
        /// except that the view's name is read before its template's: a set taken from the
        /// template would always match it. No template, or a template that is not the set's
        /// (another set's, or none's) → fail (one per view: set, view, template used, template
        /// expected). Legends are skipped — Revit puts no view template on a legend — and that
        /// is said once. The spec has no template rule for WIP views (question H7): each WIP
        /// view is listed with its template, never a verdict. A view in two sets is unjudged.
        /// Passes are one per set, Full when every BOMBA / PBT / Dokumen view was judged (none
        /// unjudged); no such view at all → unjudged, never a pass.
        /// Real model, 16 Nov 2026: by name only, the 30 Dokumen detail views without a template
        /// went unjudged (no "Dokumen" in their names) and two Bomba legends failed.
        /// </summary>
        private static IEnumerable<SpecResult> EViewTemplates(SpecModel m)
        {
            const string id = "E.1";
            if (m.Views.Count == 0) yield break;
            var passed = new Dictionary<string, int>(StringComparer.Ordinal);
            int judgedSets = 0, unjudged = 0;
            var legends = new List<(SpecView View, string Set)>();
            foreach (var v in m.Views)
            {
                var hit = SpecViewSets.Of(v, templateLast: true);
                var sets = hit.Sets;
                if (sets.Count == 0) continue;
                var name = v.Name.Trim();
                if (string.Equals((v.Kind ?? "").Trim(), "Legend", StringComparison.OrdinalIgnoreCase))
                {
                    if (!sets.Contains("WIP")) legends.Add((v, sets[0]));
                    continue;
                }
                var tpl = v.TemplateName.Trim();
                if (sets.Count > 1)
                {
                    judgedSets++;
                    unjudged++;
                    yield return Unjudged(id, $"View '{name}' — lebih dari satu set ({string.Join(", ", sets)}, {SpecViewSets.SourceText(hit.Source, hit.By, false)}); sahkan template", v.Id, name)
                        .En($"View '{name}' — more than one set ({string.Join(", ", sets)}, {SpecViewSets.SourceText(hit.Source, hit.By, true)}); confirm the template").InCat(sets[0]);
                    continue;
                }
                var set = sets[0];
                if (set == "WIP")
                {
                    yield return ViewListing(id,
                        $"WIP | view '{name}' | template: {(tpl.Length == 0 ? "tiada" : "'" + tpl + "'")} — tiada peraturan template WIP (menunggu pengesahan JKR)",
                        $"WIP | view '{name}' | template: {(tpl.Length == 0 ? "none" : "'" + tpl + "'")} — no WIP template rule (awaiting JKR's confirmation)",
                        v, set);
                    continue;
                }
                judgedSets++;
                var expected = "template " + set;
                if (tpl.Length == 0)
                {
                    yield return Fail(id, $"{set} | view '{name}' tiada view template (dijangka: {expected})", v.Id, name,
                                      actual: "tiada", required: expected).En($"{set} | view '{name}' has no view template (expected: {expected})").InCat(set);
                    continue;
                }
                var tplSets = SpecViewSets.SetsIn(tpl);
                if (tplSets.Contains(set)) { Bump(passed, set); continue; }
                var whose = tplSets.Count > 0 ? $" (template set {string.Join("/", tplSets)})" : "";
                var whoseEn = tplSets.Count > 0 ? $" (a {string.Join("/", tplSets)} set template)" : "";
                yield return Fail(id, $"{set} | view '{name}' guna template '{tpl}' — bukan template {set}{whose}", v.Id, name,
                                  actual: tpl, required: expected)
                    .En($"{set} | view '{name}' uses template '{tpl}' — not a {set} template{whoseEn}").InCat(set);
            }
            if (legends.Count > 0)
            {
                var names = string.Join(", ", legends.Take(5).Select(l => l.View.Name.Trim())) + (legends.Count > 5 ? " …" : "");
                yield return Listing(id, $"{legends.Count} legend dilangkau — legend tidak boleh guna view template ({names})", legends.Count)
                    .En($"{legends.Count} legends skipped — a legend cannot take a view template ({names})").InCat(legends[0].Set);
            }
            if (judgedSets == 0)
            {
                yield return Unjudged(id, "Tiada view set BOMBA / PBT / Dokumen dijumpai (folder Project Browser, View_jkr_pit, nama view atau template) — sahkan template secara manual", key: "no-set-views")
                    .En("No BOMBA / PBT / Dokumen view found (browser folder, View_jkr_pit, view name or template) — check the templates by hand");
                yield break;
            }
            // One pass per set; Full only when no BOMBA / PBT / Dokumen view was left unjudged.
            var cov = unjudged > 0 ? SpecCoverage.FailOnly : SpecCoverage.Full;
            foreach (var kv in passed)
                yield return Pass(id, $"{kv.Key}: {kv.Value} view guna template set yang betul", kv.Value, cov)
                    .En($"{kv.Key}: {kv.Value} views use their set's template").InCat(kv.Key);
        }

        /// <summary>A list row for one view (no verdict): filed under its set, named after the view.</summary>
        private static SpecResult ViewListing(string id, string bm, string en, SpecView v, string set)
        {
            var r = Listing(id, bm, 1).En(en).InCat(set);
            r.ElementName = v.Name.Trim();
            if (v.Id > 0) { r.ElementId = v.Id; r.ElementIds = new List<long> { v.Id }; }
            return r;
        }
    }
}

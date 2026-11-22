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
            reasons["Da.1"] = "The add-in checks that the type name carries a real JKR code: a component code whose type-code prefix is in the JKR list (Piawaian Lampiran A3 and the Buku Parameter examples), never the \"jkrAR\" prefix alone. The three-digit number is chosen by the project and is in no list the add-in holds; the backend judges what it can.";
            reasons["Db.1"] = "The add-in checks the Buku Parameter AR tables (p.36–102, all 20 categories, per LOi) for what it can read: text parameters empty, any parameter the type lacks. Yes/No, number, currency and material values, the plain Revit built-in parameters, and the starred (*) rows are not judged; the backend judges those.";
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
        /// <summary>A length in mm with every digit that differs (Dc.1 compares exactly, so 900.04 must not print as 900).</summary>
        private static string MmExact(double v) => SpecUnits.CleanMm(v).ToString("0.######", CultureInfo.InvariantCulture) + " mm";

        /// <summary>One failure for a placed type: located at its instances, keyed by the
        /// type id so Ignore / Reviewed survive a re-scan.</summary>
        private static SpecResult FailType(string id, SpecType t, string en, SpecCoverage coverage,
                                           string actual = "", string required = "", string keySuffix = "")
        {
            var first = t.InstanceIds.FirstOrDefault(x => x > 0);
            return Fail(id, en, first > 0 ? first : t.TypeId, Label(t), actual, required,
                        key: "type:" + t.TypeId + keySuffix, coverage: coverage,
                        elementIds: t.InstanceIds.Count > 0 ? t.InstanceIds : null).InCat(t.Category);
        }

        private static SpecResult UnjudgedType(string id, SpecType t, string en, string keySuffix = "")
        {
            var first = t.InstanceIds.FirstOrDefault(x => x > 0);
            return Unjudged(id, en, first > 0 ? first : t.TypeId, Label(t), key: "type:" + t.TypeId + keySuffix).InCat(t.Category);
        }

        // ─── Da.1 — type name follows JKR coding ───

        // Revit / library default names the spec calls generic ("Door 1", "Basic Wall").
        private static readonly Regex GenericName = new Regex(
            @"^\s*(generic|default|basic|standard|new|type|door|window|wall|floor|roof|ceiling|column|stair|railing|ramp|panel|pintu|tingkap|dinding|lantai)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Da.1 (T 22:27–22:28): every placed type in the Borang D categories must carry a REAL JKR code: a component code
        /// "(TGc002)" whose type-code prefix is in the JKR code list (<see cref="JkrCodeList"/>: Piawaian Lampiran A3 + the Buku Parameter
        /// examples), or, in a "jkrAR…" system-family name, a material / finish code of that list ("(bb02)"). The "jkrAR" prefix alone is NOT
        /// coding (AI-535: Copilot wrote "jkrAR_System-Panel_100" and the old prefix test passed it), and neither is a code-shaped token whose
        /// type-code prefix is not in the list. A loadable family carries the code in its family name and the size in the type name: the family
        /// name is judged the same way. Generic names fail at once. A bare material name ("UPVC") is not coding.
        /// Coverage is Full for the pattern and the type-code prefix; the three-digit number is the project's own and is not in any list, so a
        /// pass never says the number exists in the project's component list.
        /// A failing type gets a derived new name only when the type's own Kod_Komponen_jkr_stt parameter holds a real code (WP-6 style:
        /// the add-in derives, the model never does); otherwise the row carries a <see cref="SpecResult.CodeHint"/> (type-code prefix of the
        /// category and the codes already in use in this model) so that Copilot asks the person instead of inventing.
        /// </summary>
        private static IEnumerable<SpecResult> DaTypeNames(SpecModel m)
        {
            const string id = "Da.1";
            var ok = new Dictionary<string, int>(StringComparer.Ordinal);
            var byFamily = 0;
            var fails = new List<(SpecType T, SpecResult R, JkrCodeList.NameJudgement J)>();
            var persons = new List<SpecResult>();
            foreach (var t in TypesIn(m, SectionDCategories))
            {
                var jt = JkrCodeList.Judge(t.TypeName);
                if (jt.Verdict == JkrCodeList.Verdict.Coded) { Bump(ok, t.Category); continue; }
                // A loadable family carries the JKR code in its family name and the size or
                // variant in the type name ("jkrAR18_wdw_TKt_…_(TKt001)-3" : "1800 X 500 mm
                // @TK11a"); Revit shows both as the type. Real model, 16 Nov: 12 of 23 such
                // types were failed on the type name alone. System families ("Basic Wall",
                // "System Panel", "Ramp") never match, so they are still judged on the type.
                var jf = JkrCodeList.Judge(t.FamilyName);
                if (jf.Verdict == JkrCodeList.Verdict.Coded) { Bump(ok, t.Category); byFamily++; continue; }
                // A code-shaped token that is not in the add-in's list is NOT a failure: the list may be incomplete. A person confirms it.
                var unk = jt.Verdict == JkrCodeList.Verdict.UnknownCode ? jt : jf.Verdict == JkrCodeList.Verdict.UnknownCode ? jf : null;
                if (unk != null)
                {
                    persons.Add(UnjudgedType(id, t, $"{t.Category}: type '{(t.TypeName ?? "").Trim()}' ({t.InstanceCount} elements): code ({unk.Code}) is not in the add-in's JKR code list: confirm it is a real JKR code (the list is case-sensitive)"));
                    continue;
                }
                var name = (t.TypeName ?? "").Trim();
                var generic = name.Length == 0 || GenericName.IsMatch(name);
                var j = jt.Verdict == JkrCodeList.Verdict.UnknownCode ? jt : jf.Verdict == JkrCodeList.Verdict.UnknownCode ? jf : jt;
                string why;
                if (!string.IsNullOrEmpty(jt.CaseMismatch) || !string.IsNullOrEmpty(jf.CaseMismatch))
                    why = $" — the code '({(string.IsNullOrEmpty(jt.CaseMismatch) ? jf.CaseMismatch : jt.CaseMismatch)})' is not written like the JKR list: codes are case-sensitive (for example TGc002, not TGC002)";
                else if (jt.HasPrefix || jf.HasPrefix)
                    why = " — the 'jkrAR' prefix alone is not a code: no real JKR code '(XXxnnn)' in the name";
                else
                    why = " — no real JKR code '(XXxnnn)' in the name";
                var en = name.Length == 0
                    ? $"{t.Category}: unnamed type ({t.InstanceCount} elements) does not follow JKR coding"
                    : $"{t.Category}: type '{name}' ({t.InstanceCount} elements) does not follow JKR coding{(generic ? " (generic name)" : "")}{why}";
                fails.Add((t, FailType(id, t, en, SpecCoverage.Full, actual: name, required: "(XXxnnn) … with a code of the JKR list"), j));
            }
            var proposals = ProposeCodeRenames(m, fails.Select(f => f.T).ToList());
            foreach (var f in fails)
            {
                f.R.RenameFrom = (f.T.TypeName ?? "").Trim();
                if (proposals.TryGetValue(f.T, out var pr)) { f.R.RenameTo = pr.NewName; f.R.RenameSource = pr.Source; }
                else f.R.CodeHint = CodeHintFor(m, f.T, f.J);
                yield return f.R;
            }
            foreach (var p in persons) yield return p;
            foreach (var kv in ok)
                yield return Pass(id, $"{kv.Key}: {kv.Value} types follow the JKR coding (a code of the JKR list in the name)", kv.Value, SpecCoverage.Full).InCat(kv.Key);
            if (byFamily > 0)
                yield return SpecChecks.Context(id, $"{byFamily} types accepted because the JKR code is in the family name (the type name holds only the size/variant)");
        }

        private static string KeyOf(SpecType t, string name) =>
            (t.Category ?? "").ToLowerInvariant() + "|" + (t.FamilyName ?? "").ToLowerInvariant() + "|" + (name ?? "").Trim().ToLowerInvariant();

        /// <summary>The Kod_Komponen_jkr_stt value of a type (the shared parameter that holds its component code), or "".</summary>
        private static string OwnCodeParam(SpecType t)
        {
            foreach (var kv in t.Texts ?? new Dictionary<string, string>())
            {
                var k = new string((kv.Key ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                if (k != "kodkomponenjkrstt") continue;
                return (kv.Value ?? "").Trim().Trim('(', ')').Trim();
            }
            return "";
        }

        /// <summary>
        /// AI-535: the new name "(CODE) old name" for each failing type whose OWN Kod_Komponen_jkr_stt parameter holds a real code of the JKR
        /// list. Pure and deterministic, the same on every run; the model never picks a code. A name that equals another proposed name in
        /// the same family and category, or the name of a type that already exists there, is dropped for every type in the clash.
        /// </summary>
        internal static Dictionary<SpecType, RenameProposal> ProposeCodeRenames(SpecModel m, IReadOnlyList<SpecType> failing)
        {
            var all = new Dictionary<SpecType, RenameProposal>();
            foreach (var t in failing)
            {
                var code = OwnCodeParam(t);
                if (code.Length == 0 || !JkrCodeList.IsComponentCode(code)) continue;
                var old = (t.TypeName ?? "").Trim();
                if (old.Length == 0) continue;
                all[t] = new RenameProposal { NewName = "(" + code + ") " + old, Source = "the type's own Kod_Komponen_jkr_stt parameter holds the JKR code " + code };
            }
            var existing = new HashSet<string>((m?.Types ?? new List<SpecType>()).Select(x => KeyOf(x, x.TypeName)));
            var counts = all.GroupBy(kv => KeyOf(kv.Key, kv.Value.NewName)).ToDictionary(g => g.Key, g => g.Count());
            var drop = all.Where(kv => { var k = KeyOf(kv.Key, kv.Value.NewName); return counts[k] > 1 || existing.Contains(k); }).Select(kv => kv.Key).ToList();
            foreach (var t in drop) all.Remove(t);
            return all;
        }

        /// <summary>The type-code prefix (three letters) of the list that belong to a Revit category, through the two-letter families of <see cref="CodeCategories"/>.</summary>
        internal static List<string> KodJenisOf(string category)
        {
            var fams = CodeCategories.Where(kv => kv.Value.Any(x => string.Equals(x, (category ?? "").Trim(), StringComparison.OrdinalIgnoreCase)))
                                     .Select(kv => kv.Key).ToList();
            return JkrCodeList.KodJenis.Where(k => fams.Contains(k.Substring(0, 2).ToUpperInvariant())).OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// AI-535: what Copilot may offer when it asks for a type's code, as one line of plain text: the type-code prefix of the category (when
        /// the list has at most six) and the real codes other types of the category already carry in this model, most used first. Both
        /// come from the model and the JKR list; nothing here is invented. A wrong code in the old name is named so the answer replaces it.
        /// </summary>
        internal static string CodeHintFor(SpecModel m, SpecType t, JkrCodeList.NameJudgement j)
        {
            var parts = new List<string>();
            if (j != null && j.Verdict == JkrCodeList.Verdict.UnknownCode)
                parts.Add($"the old name holds the code ({j.Code}), which is not in the JKR list: the answer replaces it");
            parts.Add("codes are case-sensitive (TGc002, not TGC002)");
            var kj = KodJenisOf(t.Category);
            if (kj.Count > 0 && kj.Count <= 6) parts.Add("type-code prefix " + string.Join(", ", kj));
            else if (kj.Count > 6) parts.Add("the type-code prefix starts with " + string.Join(" or ", kj.Select(k => k.Substring(0, 2)).Distinct()));
            var inUse = (m?.Types ?? new List<SpecType>())
                .Where(x => string.Equals(x.Category, t.Category, StringComparison.OrdinalIgnoreCase))
                .SelectMany(x => JkrCodeList.ComponentCodesIn(x.TypeName).Concat(JkrCodeList.ComponentCodesIn(x.FamilyName)).Select(c => (Code: c, N: Math.Max(1, x.InstanceCount))))
                .GroupBy(x => x.Code, StringComparer.Ordinal).Select(g => (Code: g.Key, N: g.Sum(x => x.N)))
                .OrderByDescending(x => x.N).ThenBy(x => x.Code, StringComparer.Ordinal).Take(6).Select(x => "(" + x.Code + ")").ToList();
            if (inUse.Count > 0) parts.Add("codes already in use for " + t.Category + " in this model: " + string.Join(", ", inUse));
            return string.Join("; ", parts);
        }

        // ─── Db.1 — mandatory parameters per Buku Parameter × LOD ───

        /// <summary>
        /// Db.1 (PB per category, T 22:28): every parameter the Buku Parameter ticks for the category
        /// at the project's LOi must be filled. The table is DATA (<see cref="SpecPbParameters"/>,
        /// Services/JkrSpec/data/jkr_pb_ar_parameters.csv, all 20 AR categories, PB p.36–102): LOD → LOi by
        /// <see cref="SpecPbData.LoiOf"/> (350 → 300), required = ticked at that LOi and not starred.
        /// <list type="bullet">
        /// <item>Judged at every LOD from 100: the book requires parameters below LOD 400 too (Wall 27 at
        /// LOi 300). The old hard-coded "12 common from 400" list is gone: it is a subset of the table
        /// (the table has 14 common, and Kod_Jenis_jkr_stt from LOi 300 in 13 of 18 categories).</item>
        /// <item>Starred rows (* / **): LISTED per category, never failed. The book's legend (p.39, 44 … 102):
        /// "*" = "Parameter yang perlu diisi jika berkaitan" (fill if relevant), so it is not mandatory. "**" (3 AR
        /// rows) has no AR legend (ST p.135: fill if the data comes from analysis software) and is listed the
        /// same way. Linear AI-503 D8.</item>
        /// <item>Type vs instance: the snapshot reads a placed type's parameters from the TYPE and from its
        /// FIRST placed instance (SpecModelReader.ReadInstanceTexts / ReadParamNames), not from every
        /// instance. Instance parameters are therefore judged on the first placed instance of each type — a
        /// documented choice, not "all": one blank on a later instance is not seen. Reading every instance
        /// is a reader change (Revit side) and a follow-up.</item>
        /// <item>Judged by value: a text parameter (…_jkr_s?t, …x) empty → "empty"; any parameter the type
        /// does not carry at all (reader saw the names) → "not on the type". Yes/No, number, currency and
        /// material values are not in the snapshot, so a carried one is not judged, and the 86 plain Revit
        /// built-in rows (Name, Base Level, Description …) need a map to Revit's own parameters and are not
        /// judged either. So the check is FailOnly: a fail is proven, a pass is "partly checked".</item>
        /// <item>Room and Area (PB p.36–37) have no type: judged per element on the parameters the snapshot
        /// reads (SpecRoom.Params / SpecArea.Params), blank only.</item>
        /// </list>
        /// The remark says what was required at which LOi ("mandatory LOi 300 (PB p.51–54)", and "dari LOi
        /// 300" per parameter).
        /// </summary>
        private static IEnumerable<SpecResult> DbMandatoryParameters(SpecModel m)
        {
            const string id = "Db.1";
            var loi = SpecPbData.LoiOf(m.Lod);   // LOD 450 sits under the book's LOi 400 column; LOD < 100: nothing is ticked
            if (loi == 0) yield break;

            // Revit types, one book category at a time.
            var ok = new Dictionary<string, int>(StringComparer.Ordinal);
            var judgedParams = new Dictionary<string, int>(StringComparer.Ordinal);
            var starCounts = new Dictionary<string, List<PbParam>>(StringComparer.Ordinal);
            foreach (var t in TypesIn(m, PbElementCategories))
            {
                var book = SpecPbParameters.BookCategoryOf(t.Category);
                if (book == null) continue;
                var rows = SpecPbParameters.At(book, loi);
                var required = rows.Where(p => p.IsShared && !p.Starred).ToList();
                // Judged = the type's text holds it (empty or filled), or the reader saw the type's
                // parameters and it is not among them (missing). Unread snapshots judge only the former.
                var states = required
                    .Select(p => (Param: p, State: SpecExtractionRules.MandatoryState(t, p.Name)))
                    .Where(x => x.State.HasValue).ToList();
                if (states.Count == 0) continue;
                if (!starCounts.ContainsKey(t.Category)) starCounts[t.Category] = rows.Where(p => p.IsShared && p.Starred).ToList();   // list stars only where something was judged
                var empty = states.Where(x => x.State == SpecParamState.Empty).Select(x => x.Param).ToList();
                var missing = states.Where(x => x.State == SpecParamState.Missing).Select(x => x.Param).ToList();
                if (empty.Count + missing.Count == 0)
                {
                    Bump(ok, t.Category);
                    judgedParams[t.Category] = Math.Max(judgedParams.TryGetValue(t.Category, out var j) ? j : 0, states.Count);
                    continue;
                }
                var bad = empty.Count + missing.Count;
                var pages = PageRange(rows);
                var en = $"{t.Category}: type '{t.TypeName}' — {bad}/{states.Count} mandatory LOi {loi} parameters (PB p.{pages}) not filled"
                         + (empty.Count > 0 ? $"; empty: {Names(empty, loi)}" : "")
                         + (missing.Count > 0 ? $"; not on the type: {Names(missing, loi)}" : "");
                var actual = (empty.Count > 0 ? "empty: " + string.Join(", ", empty.Select(p => p.Name)) : "")
                             + (empty.Count > 0 && missing.Count > 0 ? "; " : "")
                             + (missing.Count > 0 ? "missing: " + string.Join(", ", missing.Select(p => p.Name)) : "");
                yield return FailType(id, t, en, SpecCoverage.FailOnly, actual: actual, required: "filled (PB, LOi " + loi + ")");
            }
            foreach (var kv in ok)
            {
                var book = SpecPbParameters.BookCategoryOf(kv.Key);
                var pages = PageRange(SpecPbParameters.At(book, loi));
                yield return Pass(id, $"{kv.Key}: {kv.Value} types: the mandatory LOi {loi} parameters (PB p.{pages}) that can be read are filled — {judgedParams[kv.Key]} checked; Yes/No, number, material values and plain Revit parameters are not checked yet", kv.Value, SpecCoverage.FailOnly).InCat(kv.Key);
            }
            // Starred rows: listed, never failed (D8 open).
            foreach (var kv in starCounts.Where(x => x.Value.Count > 0).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var names = string.Join(", ", kv.Value.Take(8).Select(p => p.Name + new string('*', p.Star))) + (kv.Value.Count > 8 ? " …" : "");
                yield return SpecChecks.Listing(id, $"{kv.Key}: {kv.Value.Count} starred (*) parameters at LOi {loi} listed, not failed (* = fill if relevant, per the book's legend): {names}", kv.Value.Count).InCat(kv.Key);
            }

            foreach (var r in DbRoomArea(m, loi, id)) yield return r;
        }

        /// <summary>"51–54" (the pages the category's rows ticked at the LOi sit on).</summary>
        private static string PageRange(List<PbParam> rows)
        {
            var pages = rows.Select(p => p.Page).Where(x => x > 0).ToList();
            if (pages.Count == 0) return "?";
            return pages.Min() == pages.Max() ? pages.Min().ToString(CultureInfo.InvariantCulture)
                : pages.Min().ToString(CultureInfo.InvariantCulture) + "–" + pages.Max().ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>"Kod_Jenis_jkr_stt (from LOi 300), …": each parameter with the LOi it starts at, so the remark
        /// says what was required at which LOi. Only the ones that start below the project's LOi get the tag.</summary>
        private static string Names(List<PbParam> ps, int loi)
        {
            string Tag(PbParam p) => p.FromLoi < loi ? $" (from LOi {p.FromLoi})" : "";
            var shown = ps.Take(12).Select(p => p.Name + Tag(p));
            return string.Join(", ", shown) + (ps.Count > 12 ? $" … (+{ps.Count - 12})" : "");
        }

        /// <summary>Room (PB p.37) and Area (PB p.36): per element, the parameters the snapshot read, blank = fail.
        /// A parameter the snapshot does not hold is not judged (SpecRoom.Params lists parameters with a value, so
        /// absent cannot be told from not-read).</summary>
        private static IEnumerable<SpecResult> DbRoomArea(SpecModel m, int loi, string id)
        {
            foreach (var group in new[]
            {
                (Book: "Room", Label: "Room", Els: m.Rooms.Select(r => (r.Id, r.Name, r.Params)).ToList()),
                (Book: "Area", Label: "Area", Els: m.Areas.Select(a => (a.Id, a.Name, a.Params)).ToList()),
            })
            {
                if (group.Els.Count == 0) continue;
                var rows = SpecPbParameters.At(group.Book, loi);
                var required = rows.Where(p => p.IsShared && !p.Starred && p.IsTextValued).ToList();
                if (required.Count == 0) continue;
                var pages = PageRange(rows);
                var bad = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                var okCount = 0;
                foreach (var e in group.Els)
                {
                    var anyBad = false; var anyJudged = false;
                    foreach (var p in required)
                        if (e.Params != null && e.Params.TryGetValue(p.Name, out var v))
                        {
                            anyJudged = true;
                            if (SpecExtractionRules.Classify(true, v).State != SpecParamState.Empty) continue;
                            if (!bad.TryGetValue(p.Name, out var ids)) bad[p.Name] = ids = new List<long>();
                            ids.Add(e.Id); anyBad = true;
                        }
                    if (anyJudged && !anyBad) okCount++;
                }
                if (bad.Count > 0)
                {
                    var all = bad.Values.SelectMany(x => x).Distinct().ToList();
                    var list = string.Join(", ", bad.Select(kv => $"{kv.Key} ({kv.Value.Count})"));
                    yield return Fail(id,
                            $"{group.Label}: {all.Count} elements — mandatory LOi {loi} parameters (PB p.{pages}) empty: {list}",
                            all[0], group.Label, actual: "empty: " + string.Join(", ", bad.Keys), required: "filled (PB, LOi " + loi + ")",
                            key: "pb:" + group.Book + ":" + loi, coverage: SpecCoverage.FailOnly, elementIds: all).InCat(group.Label);
                }
                if (okCount > 0)
                    yield return Pass(id, $"{group.Label}: {okCount} elements: the mandatory LOi {loi} parameters (PB p.{pages}) that can be read are filled", okCount, SpecCoverage.FailOnly).InCat(group.Label);
            }
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
        /// material → fail; any material without the prefix → fail. Full (AI-475): the prefix and
        /// presence are what the add-in proves from the model; the code against the JKR code list
        /// stays the backend's, and a backend fail still counts.
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
                        $"{t.Category}: type '{t.TypeName}' has no material",
                        SpecCoverage.Full, actual: "no material", required: "a material jkrAR_…", keySuffix: ":none");
                    continue;
                }
                var bad = mats.Where(x => !IsJkrMaterial(x)).ToList();
                if (bad.Count == 0) { Bump(ok, t.Category); continue; }
                var list = string.Join(", ", bad.Take(5).Select(x => "'" + x + "'")) + (bad.Count > 5 ? " …" : "");
                yield return FailType(id, t,
                    $"{t.Category}: type '{t.TypeName}' uses materials not JKR-coded: {list}",
                    SpecCoverage.Full, actual: string.Join(", ", bad), required: "jkrAR_…", keySuffix: ":code");
            }
            foreach (var kv in ok)
                yield return Pass(id, $"{kv.Key}: {kv.Value} types: materials filled and JKR-prefixed", kv.Value, SpecCoverage.Full).InCat(kv.Key);
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
        /// Height 2325 (see <see cref="NameNumbers"/>). Mismatches only: one fail per
        /// type, "Type name | value in name | parameter | actual". A name number whose parameter
        /// the snapshot lacks is unjudged. Full (AI-475) when every number in a judged category's
        /// names was read; FailOnly when some name has numbers that cannot be read
        /// unambiguously (those types are not checked). AI-504: no JKR document gives a tolerance
        /// (the old ±1 mm was invented), so the name number and the parameter must be equal. The
        /// feet → mm conversion float noise is removed by <see cref="SpecUnits.CleanMm"/> on both
        /// sides before the comparison: that is a unit-conversion fix, not a tolerance, so a correct
        /// name is never failed by 899.9999999999999 vs 900.
        /// </summary>
        private static IEnumerable<SpecResult> DcNameNumbers(SpecModel m)
        {
            const string id = "Dc.1";
            var ok = new Dictionary<string, int>(StringComparer.Ordinal);
            var unread = 0;
            var notApplicable = new List<string>();
            // Localised Revit guard: built-in parameter names are in the Revit UI language. Without
            // any type carrying an English Width / Height / Thickness, "lacks Width" proves nothing.
            var englishNames = m.Types.Any(x => x.ParamNamesRead && (x.ParamNames.Contains("Width") || x.ParamNames.Contains("Height") || x.ParamNames.Contains("Thickness")));
            var results = new List<SpecResult>();
            foreach (var t in TypesIn(m, SectionDCategories))
            {
                var stated = NameNumbers(t.Category, t.TypeName);
                if (stated.Count == 0) { if (HasUnreadNumbers(t)) unread++; continue; }
                // AI-518: a type that has none of the parameters its name number names (a ceiling
                // type with no Width / Height / Thickness) has nothing to compare: Dc.1 does not
                // apply to it. Only when ALL named parameters are absent, and only when the
                // reader listed the parameters (see LacksParameter). Some absent, some present:
                // a visible gap (unjudged), never a pass on half the name. A parameter that is
                // carried but gave no value is unjudged too.
                var lacking = stated.Where(x => LacksParameter(t, x.Param, englishNames)).Select(x => x.Param).ToList();
                if (lacking.Count == stated.Count)
                {
                    notApplicable.Add(Label(t) + ": no " + string.Join("/", lacking) + " parameter");
                    continue;
                }
                if (lacking.Count > 0)
                {
                    results.Add(UnjudgedType(id, t,
                        $"{t.Category}: type '{t.TypeName}' — no {string.Join(", ", lacking)} parameter, so the name number cannot be fully compared"));
                    continue;
                }
                var badEn = new List<string>();
                var missing = new List<string>();
                foreach (var (param, value) in stated)
                {
                    if (!t.Numbers.TryGetValue(param, out var actual) || double.IsNaN(actual)) { missing.Add(param); continue; }
                    if (!SpecUnits.SameMm(actual, value))
                    {
                        badEn.Add($"{param} {MmExact(actual)} (name: {MmExact(value)})");
                    }
                }
                if (badEn.Count > 0)
                {
                    results.Add(FailType(id, t,
                        $"{t.Category}: type '{t.TypeName}' — number in the name does not match: {string.Join("; ", badEn)}",
                        SpecCoverage.Full,
                        actual: string.Join("; ", stated.Where(x => t.Numbers.ContainsKey(x.Param)).Select(x => x.Param + " " + Mm(t.Numbers[x.Param]))),
                        required: string.Join("; ", stated.Select(x => x.Param + " " + Mm(x.Value)))));
                }
                else if (missing.Count > 0)
                {
                    results.Add(UnjudgedType(id, t,
                        $"{t.Category}: type '{t.TypeName}' — {string.Join(", ", missing)} was not read from the model; cannot compare with the name"));
                }
                else Bump(ok, t.Category);
            }
            foreach (var r in results) yield return r;
            var cov = unread > 0 ? SpecCoverage.FailOnly : SpecCoverage.Full;
            foreach (var kv in ok)
                yield return Pass(id, $"{kv.Key}: {kv.Value} types: numbers in the name match the geometry", kv.Value, cov).InCat(kv.Key);
            // One N/A result for the types left out: beside a pass it changes nothing; alone it makes
            // the check "does not apply" instead of leaving it unchecked.
            if (notApplicable.Count > 0)
                yield return NotApplicable(id, $"{notApplicable.Count} {(notApplicable.Count == 1 ? "type" : "types")} left out, name number cannot be checked: {string.Join("; ", notApplicable.Take(5))}{(notApplicable.Count > 5 ? "; …" : "")}");
        }

        /// <summary>The reader listed the type's parameters and <paramref name="param"/> is not one of
        /// them (not a number, not a text). False when the parameters were not read (old snapshot).</summary>
        private static bool LacksParameter(SpecType t, string param, bool englishNames) =>
            englishNames && t.ParamNamesRead && t.ParamNames.Count > 0 && !t.Numbers.ContainsKey(param) && !t.Texts.ContainsKey(param) && !t.ParamNames.Contains(param);

        /// <summary>The type's name has a number in a category Dc.1 judges, but
        /// <see cref="NameNumbers"/> could not read it (several candidates, out of range): that
        /// type is not checked.</summary>
        private static bool HasUnreadNumbers(SpecType t)
        {
            if (!IsDoorOrWindow(t.Category) && SingleParamOf(t.Category) == null) return false;
            var s = Bracketed.Replace(t.TypeName ?? "", " ");
            return Pair.IsMatch(s) || Single.IsMatch(s);
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
        /// ("code (TK…)" / "name" / "family"); null when the name says nothing judgeable.</summary>
        internal static (string[] Categories, string Source)? ExpectedCategories(SpecType t)
        {
            var code = CodeRx.Match(t.TypeName ?? "");
            if (code.Success && CodeCategories.TryGetValue(code.Groups[1].Value.ToUpperInvariant(), out var byCode))
                return (byCode, "code (" + code.Groups[1].Value.ToUpperInvariant() + "…)");
            var accessory = In(AccessoryCategories, t.Category);
            var byType = WordExpectation(t.TypeName, accessory);
            var byFamily = WordExpectation(t.FamilyName, accessory);
            if (byType == null && byFamily == null) return null;
            // Either name matching the category is enough ("Door-Double" family, "Panel 900" type).
            var all = (byType ?? new string[0]).Concat(byFamily ?? new string[0]).Distinct().ToArray();
            return (all, byType != null ? "name" : "family");
        }

        /// <summary>
        /// Dd.1 (T 22:35): the Revit category must be the one the name implies — a window
        /// type is not in Doors, a floor is in Floors. Expected category from the JKR
        /// component code in the type name (strongest; <see cref="CodeCategories"/>), else
        /// from the first word of the type or family name (EN/BM keywords). Mismatches only. Full
        /// (AI-475) when every type's name said something judgeable; FailOnly when some type's
        /// name has no code or keyword (that type is not judged, so a pass is partial).
        /// </summary>
        private static IEnumerable<SpecResult> DdNameCategory(SpecModel m)
        {
            const string id = "Dd.1";
            var ok = new Dictionary<string, int>(StringComparer.Ordinal);
            var unjudged = 0;
            var fails = new List<(SpecType T, SpecResult R)>();
            foreach (var t in TypesIn(m, SectionDCategories))
            {
                var exp = ExpectedCategories(t);
                if (exp == null) { unjudged++; continue; }
                if (In(exp.Value.Categories, t.Category)) { Bump(ok, t.Category); continue; }
                var expected = string.Join(" / ", exp.Value.Categories);
                var srcEn = exp.Value.Source;
                fails.Add((t, FailType(id, t,
                    $"Type '{t.TypeName}' is in {t.Category} — its {srcEn} says {expected}",
                    SpecCoverage.Full, actual: t.Category, required: expected)));
            }
            // WP-6: a type whose NAME alone is the odd one out gets a proposed new name (pure, deterministic; the "Fix via Copilot"
            // button and prompt use it, the model never derives a name). A failing code, a contradicting family and a name that would
            // collide get none: a person decides.
            var proposals = ProposeRenames(m, fails.Select(f => f.T).ToList());
            foreach (var f in fails)
            {
                if (proposals.TryGetValue(f.T, out var pr)) { f.R.RenameFrom = f.T.TypeName; f.R.RenameTo = pr.NewName; f.R.RenameSource = pr.Source; }
                yield return f.R;
            }
            var cov = unjudged > 0 ? SpecCoverage.FailOnly : SpecCoverage.Full;
            foreach (var kv in ok)
                yield return Pass(id, $"{kv.Key}: {kv.Value} types: Revit category matches the name", kv.Value, cov).InCat(kv.Key);
        }

        /// <summary>A proposed new type name and the evidence that makes the NAME the odd one out.</summary>
        public sealed class RenameProposal
        {
            public string NewName = "";
            /// <summary>Plain-English evidence, shown in the confirm dialog and the prompt.</summary>
            public string Source = "";
        }

        /// <summary>English singular word per Revit category: the word a first-word swap writes (never Malay).</summary>
        private static readonly Dictionary<string, string> CategoryWord = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Doors"] = "Door", ["Windows"] = "Window", ["Walls"] = "Wall", ["Floors"] = "Floor", ["Ceilings"] = "Ceiling",
            ["Roofs"] = "Roof", ["Stairs"] = "Stair", ["Railings"] = "Railing", ["Ramps"] = "Ramp",
            ["Columns"] = "Column", ["Structural Columns"] = "Column",
        };

        private static readonly string EvidenceName = "the type name's first word (not the JKR code, and the family name does not contradict the category)";

        /// <summary>
        /// WP-6: the new type name for ONE type, or null when none can be derived without a guess. Pure and deterministic.
        /// Only when the NAME is the odd one out: the evidence is the type name's first word (<see cref="ExpectedCategories"/> source "name"),
        /// the JKR code is not the evidence (a failing code means the family may really be of another category: a person decides),
        /// and the family name says nothing or includes the category the type is in. The new name replaces only that first word
        /// by the English word of the category (capitalisation kept) and is checked to pass Dd.1 for the category.
        /// <paramref name="allowCode"/> (false in production) also swaps the two-letter code prefix for the one table prefix of the
        /// category, keeping the letter case of the original ("Co100", "CO100"); it is not used by the button.
        /// </summary>
        public static RenameProposal ProposeRename(SpecType t, bool allowCode = false)
        {
            if (t == null) return null;
            var cat = (t.Category ?? "").Trim();
            var exp = ExpectedCategories(t);
            if (exp == null || In(exp.Value.Categories, cat)) return null;
            if (exp.Value.Source.StartsWith("code", StringComparison.Ordinal))
            {
                if (!allowCode) return null;
                var swapped = SwapCodePrefix(t.TypeName, cat);
                return swapped == null ? null : Verified(t, swapped, "the JKR code prefix");
            }
            if (exp.Value.Source != "name") return null;                       // "family": the family is the odd one out
            var accessory = In(AccessoryCategories, cat);
            var fam = WordExpectation(t.FamilyName, accessory);
            if (fam != null && !In(fam, cat)) return null;                       // the family contradicts the category too: a real model error
            if (!CategoryWord.TryGetValue(cat, out var word)) return null;
            var name = t.TypeName ?? "";
            // the first word as Dd.1 judges it (brackets blanked, same length, so the index is valid in the original name)
            var m = Words.Match(Bracketed.Replace(name, x => new string(' ', x.Length)));
            if (!m.Success || !WordCategories.ContainsKey(m.Value)) return null;
            var neu = name.Substring(0, m.Index) + MatchCase(word, m.Value) + name.Substring(m.Index + m.Length);
            return Verified(t, neu, EvidenceName);
        }

        private static string MatchCase(string word, string like)
        {
            if (like.Length > 1 && like.All(char.IsUpper)) return word.ToUpperInvariant();
            if (like.Length > 0 && char.IsLower(like[0])) return word.ToLowerInvariant();
            return word;
        }

        /// <summary>The code with its two prefix letters replaced by the category's one table prefix, each letter keeping the case of
        /// the original letter at that place ("Co100" stays mixed). Null: no code, or the category has no single prefix.</summary>
        public static string SwapCodePrefix(string typeName, string category)
        {
            var c = (category ?? "").Trim();
            var hits = CodeCategories.Where(kv => kv.Value.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase))).Select(kv => kv.Key).ToList();
            if (hits.Count != 1) return null;
            var m = CodeRx.Match(typeName ?? "");
            if (!m.Success) return null;
            var g = m.Groups[1];
            var neu = new string(hits[0].Select((ch, i) => char.IsLower(g.Value[i]) ? char.ToLowerInvariant(ch) : char.ToUpperInvariant(ch)).ToArray());
            if (g.Value.Length > 1 && char.IsUpper(g.Value[0]) && char.IsLower(g.Value[1]))   // "Co": capital then small
                neu = char.ToUpperInvariant(neu[0]) + neu.Substring(1).ToLowerInvariant();
            return (typeName ?? "").Substring(0, g.Index) + neu + (typeName ?? "").Substring(g.Index + g.Length);
        }

        /// <summary>The proposal only when the renamed type passes Dd.1 for its category.</summary>
        private static RenameProposal Verified(SpecType t, string newName, string source)
        {
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, t.TypeName, StringComparison.Ordinal)) return null;
            var probe = new SpecType { Category = t.Category, FamilyName = t.FamilyName, TypeName = newName };
            var e = ExpectedCategories(probe);
            if (e == null || !In(e.Value.Categories, t.Category)) return null;
            return new RenameProposal { NewName = newName, Source = source };
        }

        /// <summary>Proposals for a batch of failing types. A name that equals another proposed name in the same family and category, or
        /// the name of a type that already exists there (Revit type names are unique per family), is dropped for every type in the clash.</summary>
        internal static Dictionary<SpecType, RenameProposal> ProposeRenames(SpecModel m, IReadOnlyList<SpecType> failing)
        {
            var all = new Dictionary<SpecType, RenameProposal>();
            foreach (var t in failing) { var p = ProposeRename(t); if (p != null) all[t] = p; }
            string Key(SpecType t, string name) => (t.Category ?? "").ToLowerInvariant() + "|" + (t.FamilyName ?? "").ToLowerInvariant() + "|" + (name ?? "").Trim().ToLowerInvariant();
            var existing = new HashSet<string>((m?.Types ?? new List<SpecType>()).Select(x => Key(x, x.TypeName)));
            var counts = all.GroupBy(kv => Key(kv.Key, kv.Value.NewName)).ToDictionary(g => g.Key, g => g.Count());
            var drop = all.Where(kv => { var k = Key(kv.Key, kv.Value.NewName); return counts[k] > 1 || existing.Contains(k); }).Select(kv => kv.Key).ToList();
            foreach (var t in drop) all.Remove(t);
            return all;
        }

        // ─── Dd.2, De.1–De.3 — human / list rows ───

        /// <summary>Dd.2 (T 22:35–22:36): modelling technique is out of the automatic
        /// check's scope — a person decides. Context only.</summary>
        private static IEnumerable<SpecResult> DdTechnique(SpecModel m)
        {
            yield return Context("Dd.2", "MANUAL CHECK — modelling technique (how surfaces are placed, etc.) is outside the automatic check");
        }

        /// <summary>De.1 (F D.8): Room has no ElementType, so Section D does not judge it;
        /// rooms are checked under A6 and C1.6. List only.</summary>
        private static IEnumerable<SpecResult> DeRoom(SpecModel m)
        {
            var placed = m.Rooms.Count(r => r.Area > 0);
            yield return Listing("De.1", $"Rooms have no element type — checked under A6 / C1.6 ({placed} placed rooms)", placed).InCat("Rooms");
        }

        private static int CountOf(SpecModel m, string category) =>
            m.CategoryCounts.Where(kv => string.Equals(kv.Key, category, StringComparison.OrdinalIgnoreCase)).Sum(kv => Math.Max(0, kv.Value));

        /// <summary>
        /// De.2 (F D.18, D.21): Columns / Structural Columns with 0 elements → "confirm scope"
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
                yield return Context("De.2", both + " — both categories are modelled").InCat(cats[0]);
                yield break;
            }
            var z = string.Join(", ", zero);
            yield return Context("De.2", $"{both} — {z} 0 elements: confirm scope", key: string.Join("|", zero)).InCat(zero[0]);
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
        /// De.3 (C 3.1): categories not modelled are "not applicable", consistent with C 3.1 —
        /// listed, never a shortfall. Lists each Buku Parameter AR category with 0 placed
        /// elements (one listing per category). Nothing when the category counts were not read.
        /// </summary>
        private static IEnumerable<SpecResult> DeNotModelled(SpecModel m)
        {
            if (m.CategoryCounts.Count == 0) yield break;
            var none = PbArCategories.Where(c => c.Revit.Sum(r => CountOf(m, r)) == 0).ToList();
            if (none.Count == 0)
            {
                yield return Listing("De.3", "Every Buku Parameter AR category is modelled");
                yield break;
            }
            // One row per category, filed under its Revit category (Column under Columns).
            foreach (var c in none)
                yield return Listing("De.3", $"{c.Pb}: not modelled — not applicable").InCat(c.Revit[0]);
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
                    yield return Unjudged(id, $"View '{name}' — more than one set ({string.Join(", ", sets)}, {SpecViewSets.SourceText(hit.Source, hit.By)}); confirm the template", v.Id, name).InCat(sets[0]);
                    continue;
                }
                var set = sets[0];
                if (set == "WIP")
                {
                    yield return ViewListing(id,
                        $"WIP | view '{name}' | template: {(tpl.Length == 0 ? "none" : "'" + tpl + "'")} — no WIP template rule (awaiting JKR's confirmation)",
                        v, set);
                    continue;
                }
                judgedSets++;
                var expected = "template " + set;
                if (tpl.Length == 0)
                {
                    yield return Fail(id, $"{set} | view '{name}' has no view template (expected: {expected})", v.Id, name,
                                      actual: "none", required: expected).InCat(set);
                    continue;
                }
                var tplSets = SpecViewSets.SetsIn(tpl);
                if (tplSets.Contains(set)) { Bump(passed, set); continue; }
                var whoseEn = tplSets.Count > 0 ? $" (a {string.Join("/", tplSets)} set template)" : "";
                yield return Fail(id, $"{set} | view '{name}' uses template '{tpl}' — not a {set} template{whoseEn}", v.Id, name,
                                  actual: tpl, required: expected).InCat(set);
            }
            if (legends.Count > 0)
            {
                var names = string.Join(", ", legends.Take(5).Select(l => l.View.Name.Trim())) + (legends.Count > 5 ? " …" : "");
                yield return Listing(id, $"{legends.Count} legends skipped — a legend cannot take a view template ({names})", legends.Count).InCat(legends[0].Set);
            }
            if (judgedSets == 0)
            {
                yield return Unjudged(id, "No BOMBA / PBT / Dokumen view found (browser folder, View_jkr_pit, view name or template) — check the templates by hand", key: "no-set-views");
                yield break;
            }
            // One pass per set; Full only when no BOMBA / PBT / Dokumen view was left unjudged.
            var cov = unjudged > 0 ? SpecCoverage.FailOnly : SpecCoverage.Full;
            foreach (var kv in passed)
                yield return Pass(id, $"{kv.Key}: {kv.Value} views use their set's template", kv.Value, cov).InCat(kv.Key);
        }

        /// <summary>A list row for one view (no verdict): filed under its set, named after the view.</summary>
        private static SpecResult ViewListing(string id, string en, SpecView v, string set)
        {
            var r = Listing(id, en, 1).InCat(set);
            r.ElementName = v.Name.Trim();
            if (v.Id > 0) { r.ElementId = v.Id; r.ElementIds = new List<long> { v.Id }; }
            return r;
        }
    }
}

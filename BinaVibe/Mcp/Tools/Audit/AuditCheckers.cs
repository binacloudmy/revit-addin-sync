// AuditCheckers — deterministic checker library for fill_audit.
//
// Each checker owns: keyword groups that match a checklist row's wording
// (EN + BM), an evaluator that reads the LIVE Revit document, and a remark
// template filled ONLY from that evaluation's evidence. No LLM anywhere: a row
// either matches a checker (≥ MinGroups keyword groups hit) and gets an
// evidence-backed verdict, or it is honestly not_verifiable. Never guess.
//
// Read-only throughout — no Transactions. Evidence lists are capped so one
// bad model cannot blow up the turn.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace BinaVibe.Mcp.Tools.Audit
{
    public sealed class CheckOutcome
    {
        public string Compliance = "not_verifiable";   // "yes" | "no" | "not_verifiable"
        public Dictionary<string, object?> Evidence = new();
        public List<long> ElementIds = new();
        public string Remark = "";
    }

    public sealed class AuditChecker
    {
        public string Id = "";
        /// <summary>Row matches when at least MinGroups groups each have ≥1
        /// keyword present in the row's description+reference (lowercased).</summary>
        public string[][] KeywordGroups = Array.Empty<string[]>();
        public int MinGroups = 2;
        public Func<Document, AuditFormRow, CheckOutcome> Evaluate = (_, _) => new CheckOutcome();

        public int MatchScore(string text)
        {
            int hit = 0;
            foreach (var group in KeywordGroups)
                if (group.Any(k => text.Contains(k))) hit++;
            return hit >= MinGroups ? hit : 0;
        }
    }

    public static class AuditCheckers
    {
        private const int Cap = 15;              // max names/ids listed in evidence
        private const int ElementScanCap = 3000; // material scan bound

        // ─── matching ───────────────────────────────────────────────────

        /// <summary>Best-scoring checker for a row, or null (→ not_verifiable).
        /// Section D rows resolve through the category map instead — their
        /// "description" is just a category name.</summary>
        public static (AuditChecker checker, string? category)? Match(AuditFormRow row)
        {
            if (row.Section == "D")
            {
                var cat = MatchCategory(row.Description);
                return cat == null ? null : (FamilyCategoryChecker, cat);
            }
            var text = (row.Description + " " + row.GuidelineRef).ToLowerInvariant();
            AuditChecker? best = null;
            int bestScore = 0;
            foreach (var c in All)
            {
                var s = c.MatchScore(text);
                if (s > bestScore) { best = c; bestScore = s; }
            }
            return best == null ? null : (best, null);
        }

        // ─── registry ───────────────────────────────────────────────────

        public static readonly List<AuditChecker> All = new()
        {
            new AuditChecker
            {
                Id = "file_naming",
                KeywordGroups = new[]
                {
                    new[] { "files are named", "file name", "named according", "penamaan fail", "nama fail" },
                    new[] { "guidelines", "standards", "garis panduan", "piawaian" },
                },
                Evaluate = FileNaming,
            },
            new AuditChecker
            {
                Id = "base_point",
                KeywordGroups = new[]
                {
                    // "project base" too: a wrapped cell can split "Base Point"
                    // across two text lines, and the row parser's known bleed
                    // limit means only "Project Base" may land in this row.
                    new[] { "base point", "project base", "titik asas" },
                    new[] { "grid", "positioned", "kedudukan" },
                },
                Evaluate = BasePoint,
            },
            // rooms_department sits BEFORE grids_levels: parser line bleed can
            // drag "gridlines and levels" into the rooms row, tying the two
            // scores — first-listed wins the tie, and a row that says "rooms"
            // is a rooms row.
            new AuditChecker
            {
                Id = "rooms_department",
                KeywordGroups = new[]
                {
                    new[] { "rooms", "room", "bilik", "ruang" },
                    new[] { "department", "catagorised", "categorised", "categorized", "jabatan" },
                },
                Evaluate = RoomsDepartment,
            },
            new AuditChecker
            {
                Id = "grids_levels",
                KeywordGroups = new[]
                {
                    new[] { "gridlines and levels", "gridlines", "grid", "grid dan aras" },
                    new[] { "organised", "organized", "floor plan", "levels", "aras", "tersusun" },
                },
                Evaluate = GridsLevels,
            },
            new AuditChecker
            {
                Id = "materials_assigned",
                KeywordGroups = new[]
                {
                    new[] { "materials", "bahan" },
                    new[] { "included in model", "model elements", "assigned", "elemen model" },
                },
                Evaluate = MaterialsAssigned,
            },
            new AuditChecker
            {
                Id = "project_info",
                KeywordGroups = new[]
                {
                    new[] { "project information", "maklumat projek" },
                    new[] { "updated", "dikemaskini", "kemas kini" },
                },
                Evaluate = ProjectInfo,
            },
            new AuditChecker
            {
                Id = "views_template",
                KeywordGroups = new[]
                {
                    new[] { "views" },
                    new[] { "template", "templat" },
                    new[] { "created based", "architectural" },
                },
                MinGroups = 3,
                Evaluate = ViewsFromTemplate,
            },
            new AuditChecker
            {
                Id = "views_wip",
                KeywordGroups = new[] { new[] { "wip" }, new[] { "views", "view" } },
                Evaluate = (doc, row) => ViewBucket(doc, "WIP"),
            },
            // "submission" alone is deliberately NOT in group 2: section E's
            // "BOMBA/PBT Submission" rows must fall through to
            // view_template_applied, not the view-existence buckets.
            new AuditChecker
            {
                Id = "views_pbt",
                KeywordGroups = new[] { new[] { "pbt" }, new[] { "views", "view" } },
                Evaluate = (doc, row) => ViewBucket(doc, "PBT"),
            },
            new AuditChecker
            {
                Id = "views_bomba",
                KeywordGroups = new[] { new[] { "bomba" }, new[] { "views", "view" } },
                Evaluate = (doc, row) => ViewBucket(doc, "BOMBA"),
            },
            new AuditChecker
            {
                Id = "views_dokumen",
                KeywordGroups = new[]
                {
                    new[] { "dokumen" },
                    new[] { "views", "view", "documentation", "contract" },
                },
                Evaluate = (doc, row) => ViewBucket(doc, "Dokumen"),
            },
            new AuditChecker
            {
                Id = "area_plans",
                KeywordGroups = new[]
                {
                    new[] { "spatial", "zoning", "zon" },
                    new[] { "area plan", "pelan keluasan" },
                },
                Evaluate = AreaPlans,
            },
            new AuditChecker
            {
                Id = "legends",
                KeywordGroups = new[]
                {
                    new[] { "legends", "legend", "legenda" },
                    new[] { "components", "general notes", "managed", "contents" },
                },
                Evaluate = Legends,
            },
            new AuditChecker
            {
                Id = "schedules_required",
                KeywordGroups = new[]
                {
                    new[] { "schedules", "schedule", "jadual" },
                    new[] { "accomodation", "accommodation", "component", "takeoff", "quantities" },
                },
                Evaluate = SchedulesRequired,
            },
            new AuditChecker
            {
                Id = "sheets_contents",
                KeywordGroups = new[]
                {
                    new[] { "sheets", "sheet", "helaian" },
                    new[] { "drawings", "managed", "title block", "plans", "elevations" },
                },
                Evaluate = SheetsContents,
            },
            new AuditChecker
            {
                Id = "titleblock_jkr",
                KeywordGroups = new[]
                {
                    new[] { "jkr title block", "title block" },
                    new[] { "jkr", "furnished" },
                },
                Evaluate = TitleblockJkr,
            },
            new AuditChecker
            {
                Id = "links_current",
                KeywordGroups = new[]
                {
                    new[] { "link", "linked", "pautan" },
                    new[] { "up-to-date", "correct", "models linked", "terkini" },
                },
                Evaluate = LinksCurrent,
            },
            new AuditChecker
            {
                Id = "view_template_applied",
                KeywordGroups = new[]
                {
                    new[] { "bomba submission", "pbt submission", "view template", "templat" },
                    new[] { "applied", "submission", "correctly" },
                },
                Evaluate = ViewTemplateApplied,
            },
        };

        // Section D category map: printed row label → Revit category.
        private static readonly (string label, BuiltInCategory bic)[] CategoryMap =
        {
            ("floors", BuiltInCategory.OST_Floors),
            ("walls", BuiltInCategory.OST_Walls),
            ("ceilings", BuiltInCategory.OST_Ceilings),
            ("roofs", BuiltInCategory.OST_Roofs),
            ("stairs", BuiltInCategory.OST_Stairs),
            ("railings", BuiltInCategory.OST_StairsRailing),
            ("ramps", BuiltInCategory.OST_Ramps),
            ("room", BuiltInCategory.OST_Rooms),
            ("curtain", BuiltInCategory.OST_CurtainWallPanels),
            ("doors", BuiltInCategory.OST_Doors),
            ("windows", BuiltInCategory.OST_Windows),
            ("caseworks", BuiltInCategory.OST_Casework),
            ("casework", BuiltInCategory.OST_Casework),
            ("furniture systems", BuiltInCategory.OST_FurnitureSystems),
            ("furniture", BuiltInCategory.OST_Furniture),
            ("plumbing", BuiltInCategory.OST_PlumbingFixtures),
            ("specialty", BuiltInCategory.OST_SpecialityEquipment),
            ("generic", BuiltInCategory.OST_GenericModel),
            ("structural column", BuiltInCategory.OST_StructuralColumns),
            ("columns", BuiltInCategory.OST_Columns),
            ("parking", BuiltInCategory.OST_Parking),
            ("pipes", BuiltInCategory.OST_PipeCurves),
            ("toposurface", BuiltInCategory.OST_Topography),
            ("mass", BuiltInCategory.OST_Mass),
        };

        private static string? MatchCategory(string description)
        {
            var d = description.ToLowerInvariant();
            foreach (var (label, _) in CategoryMap)
                if (d.Contains(label)) return label;
            return null;
        }

        private static readonly AuditChecker FamilyCategoryChecker = new()
        {
            Id = "family_category",
            Evaluate = (_, _) => new CheckOutcome(),   // real call goes through EvaluateCategory
        };

        // ─── evaluators ─────────────────────────────────────────────────

        private static CheckOutcome FileNaming(Document doc, AuditFormRow row)
        {
            var title = doc.Title ?? "";
            // Structured multi-segment name (PRJ-DISC-ZONE-... style): 3+ segments
            // separated by - or _. The exact PWD grammar varies per project, so the
            // check is structural; the remark always shows the actual name.
            var segments = title.Split('-', '_').Where(s => s.Trim().Length > 0).Count();
            bool structured = segments >= 3;
            var o = new CheckOutcome
            {
                Compliance = structured ? "yes" : "no",
                Evidence =
                {
                    ["file_title"] = title,
                    ["segments"] = segments,
                    ["rule"] = ">=3 segments separated by - or _",
                },
                Remark = structured
                    ? $"Nama fail \"{title}\" berstruktur ({segments} segmen)."
                    : $"Nama fail \"{title}\" tidak ikut struktur penamaan ({segments} segmen, perlu >=3 dipisah - atau _). Namakan semula fail mengikut garis panduan.",
            };
            return o;
        }

        private static CheckOutcome BasePoint(Document doc, AuditFormRow row)
        {
            XYZ? pbp = null;
            foreach (BasePoint bp in new FilteredElementCollector(doc).OfClass(typeof(BasePoint)))
                if (!bp.IsShared) pbp = bp.Position;

            Curve? gridA = null, grid1 = null;
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                try
                {
                    if (g.Name.Equals("A", StringComparison.OrdinalIgnoreCase)) gridA = g.Curve;
                    else if (g.Name == "1") grid1 = g.Curve;
                }
                catch { /* multi-segment grid */ }
            }

            var o = new CheckOutcome();
            o.Evidence["base_point_mm"] = pbp == null ? null : MmArr(pbp);
            o.Evidence["grid_a_found"] = gridA != null;
            o.Evidence["grid_1_found"] = grid1 != null;

            if (pbp == null || gridA is not Line la || grid1 is not Line l1)
            {
                o.Compliance = "not_verifiable";
                o.Remark = "Tidak dapat sahkan: "
                    + (pbp == null ? "Project Base Point tidak dijumpai. " : "")
                    + (gridA == null ? "Grid 'A' tiada. " : "")
                    + (grid1 == null ? "Grid '1' tiada. " : "")
                    + "Semak manual.";
                return o;
            }

            // Plan-view intersection of the two (assumed straight) grids.
            var inter = IntersectXy(la, l1);
            if (inter == null)
            {
                o.Compliance = "not_verifiable";
                o.Remark = "Grid A dan Grid 1 tidak bersilang pada pelan — semak manual.";
                return o;
            }
            var dMm = Math.Round(Math.Sqrt(Math.Pow(inter.X - pbp.X, 2) + Math.Pow(inter.Y - pbp.Y, 2)) * 304.8, 1);
            o.Evidence["grid_a1_intersection_mm"] = MmArr(inter);
            o.Evidence["offset_mm"] = dMm;
            o.Evidence["tolerance_mm"] = 10.0;
            bool ok = dMm <= 10.0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Project Base Point berada pada persilangan Grid A/1 (sisihan {dMm} mm)."
                : $"Project Base Point tersisih {dMm} mm dari persilangan Grid A/1 (had 10 mm). Selaraskan kedudukan model ke Grid A dan 1.";
            return o;
        }

        private static CheckOutcome GridsLevels(Document doc, AuditFormRow row)
        {
            int grids = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Grids)
                .WhereElementIsNotElementType().GetElementCount();
            int levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan).ToList();
            var noLevel = plans.Where(v => v.GenLevel == null).Select(v => v.Name).ToList();

            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["grids"] = grids,
                    ["levels"] = levels,
                    ["floor_plans"] = plans.Count,
                    ["floor_plans_without_level"] = noLevel.Take(Cap).ToList(),
                },
            };
            bool ok = grids > 0 && levels > 0 && noLevel.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"{grids} grid, {levels} aras; semua {plans.Count} pelan lantai terikat pada aras."
                : $"{grids} grid, {levels} aras. "
                  + (grids == 0 ? "Tiada grid dalam model. " : "")
                  + (levels == 0 ? "Tiada aras dalam model. " : "")
                  + (noLevel.Count > 0
                      ? $"{noLevel.Count} pelan lantai tanpa aras (cth: {string.Join(", ", noLevel.Take(3))})."
                      : "");
            return o;
        }

        private static CheckOutcome RoomsDepartment(Document doc, AuditFormRow row)
        {
            var rooms = new FilteredElementCollector(doc).OfClass(typeof(SpatialElement))
                .Cast<SpatialElement>().OfType<Room>().Where(r => r.Area > 0).ToList();
            var missing = new List<Room>();
            foreach (var r in rooms)
            {
                var v = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString();
                if (string.IsNullOrWhiteSpace(v)) missing.Add(r);
            }
            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["rooms"] = rooms.Count,
                    ["without_department"] = missing.Count,
                    ["examples"] = missing.Take(Cap).Select(r => r.Name).ToList(),
                },
                ElementIds = missing.Take(50).Select(r => (long)r.Id.Value).ToList(),
            };
            if (rooms.Count == 0)
            {
                o.Compliance = "no";
                o.Remark = "Tiada bilik (Room) dalam model — letak Room dahulu sebelum kategori jabatan.";
                return o;
            }
            bool ok = missing.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Semua {rooms.Count} bilik ada nilai Department."
                : $"{missing.Count}/{rooms.Count} bilik tiada 'Department' (cth: {string.Join(", ", missing.Take(3).Select(r => r.Name))}). Isi parameter Department untuk bilik tersebut.";
            return o;
        }

        private static CheckOutcome MaterialsAssigned(Document doc, AuditFormRow row)
        {
            var cats = new[]
            {
                BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Roofs,
            };
            int scanned = 0, without = 0;
            var offenders = new List<Element>();
            foreach (var bic in cats)
            {
                foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (++scanned > ElementScanCap) break;
                    ICollection<ElementId> mats;
                    try { mats = e.GetMaterialIds(false); } catch { continue; }
                    if (mats == null || mats.Count == 0)
                    {
                        without++;
                        if (offenders.Count < Cap) offenders.Add(e);
                    }
                }
            }
            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["elements_scanned"] = scanned,
                    ["without_material"] = without,
                    ["scanned_categories"] = new List<object> { "Walls", "Floors", "Ceilings", "Roofs" },
                    ["examples"] = offenders.Select(e => e.Name).ToList(),
                },
                ElementIds = offenders.Select(e => (long)e.Id.Value).ToList(),
            };
            bool ok = without == 0 && scanned > 0;
            o.Compliance = scanned == 0 ? "not_verifiable" : ok ? "yes" : "no";
            o.Remark = scanned == 0
                ? "Tiada elemen Walls/Floors/Ceilings/Roofs untuk disemak — semak manual."
                : ok
                    ? $"Semua {scanned} elemen (Walls/Floors/Ceilings/Roofs) ada material."
                    : $"{without}/{scanned} elemen tiada material (cth id: {string.Join(", ", o.ElementIds.Take(3))}). Tetapkan material pada jenis elemen berkenaan.";
            return o;
        }

        private static CheckOutcome ProjectInfo(Document doc, AuditFormRow row)
        {
            var info = doc.ProjectInformation;
            var fields = new (string name, string? val)[]
            {
                ("Project Name", info?.Name),
                ("Project Number", info?.Number),
                ("Address", info?.Address),
                ("Client Name", info?.ClientName),
                ("Status", info?.Status),
            };
            var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "project name", "project number", "enter address here", "project address",
                "owner", "project status",
            };
            var empty = fields
                .Where(f => string.IsNullOrWhiteSpace(f.val) || defaults.Contains(f.val!.Trim()))
                .Select(f => f.name).ToList();
            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["fields_checked"] = fields.Select(f => f.name).ToList(),
                    ["empty_or_default"] = empty,
                },
            };
            bool ok = empty.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? "Semua medan Project Information terisi."
                : $"Medan Project Information belum dikemaskini: {string.Join(", ", empty)}. Isi di Manage > Project Information.";
            return o;
        }

        private static CheckOutcome ViewsFromTemplate(Document doc, AuditFormRow row)
        {
            var views = GraphicalViews(doc);
            var without = views.Where(v => v.ViewTemplateId == ElementId.InvalidElementId)
                               .Select(v => v.Name).ToList();
            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["views"] = views.Count,
                    ["without_view_template"] = without.Count,
                    ["examples"] = without.Take(Cap).ToList(),
                },
            };
            bool ok = views.Count > 0 && without.Count == 0;
            o.Compliance = views.Count == 0 ? "not_verifiable" : ok ? "yes" : "no";
            o.Remark = views.Count == 0
                ? "Tiada view grafik untuk disemak — semak manual."
                : ok
                    ? $"Semua {views.Count} view ada view template."
                    : $"{without.Count}/{views.Count} view tiada view template (cth: {string.Join(", ", without.Take(3))}). Sapukan template daripada templat seni bina.";
            return o;
        }

        private static CheckOutcome ViewBucket(Document doc, string token)
        {
            var views = GraphicalViews(doc);
            var hits = views.Where(v => v.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(v => v.Name).ToList();
            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["token"] = token,
                    ["matching_views"] = hits.Count,
                    ["examples"] = hits.Take(Cap).ToList(),
                    ["total_views"] = views.Count,
                },
            };
            bool ok = hits.Count > 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"{hits.Count} view {token} dijumpai (cth: {string.Join(", ", hits.Take(3))})."
                : $"Tiada view bernama {token} dalam model ({views.Count} view disemak). Wujudkan view {token} untuk tujuan tersebut.";
            return o;
        }

        private static CheckOutcome AreaPlans(Document doc, AuditFormRow row)
        {
            var areaPlans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.AreaPlan)
                .Select(v => v.Name).ToList();
            var o = new CheckOutcome
            {
                Evidence = { ["area_plans"] = areaPlans.Count, ["names"] = areaPlans.Take(Cap).ToList() },
            };
            bool ok = areaPlans.Count > 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"{areaPlans.Count} Area Plan dijumpai ({string.Join(", ", areaPlans.Take(3))})."
                : "Tiada Area Plan dalam model — jana analisis ruang/zon daripada Area Plan.";
            return o;
        }

        private static CheckOutcome Legends(Document doc, AuditFormRow row)
        {
            var legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend)
                .Select(v => v.Name).ToList();
            var o = new CheckOutcome
            {
                Evidence = { ["legends"] = legends.Count, ["names"] = legends.Take(Cap).ToList() },
            };
            bool ok = legends.Count > 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"{legends.Count} Legend dijumpai ({string.Join(", ", legends.Take(3))})."
                : "Tiada view Legend dalam model — sediakan Legend untuk komponen dan nota am.";
            return o;
        }

        private static CheckOutcome SchedulesRequired(Document doc, AuditFormRow row)
        {
            var names = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(v => !v.IsTemplate).Select(v => v.Name).ToList();
            var required = new (string label, string[] tokens)[]
            {
                ("Schedule of Accommodation", new[] { "accommodation", "accomodation", "soa" }),
                ("Building Component Schedule", new[] { "component" }),
                ("Material Takeoff Schedule", new[] { "takeoff", "take off", "material" }),
            };
            var found = new List<object>();
            var missing = new List<string>();
            foreach (var (label, tokens) in required)
            {
                var hit = names.FirstOrDefault(n => tokens.Any(t => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0));
                if (hit != null) found.Add(new Dictionary<string, object?> { ["required"] = label, ["schedule"] = hit });
                else missing.Add(label);
            }
            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["schedules_in_model"] = names.Count,
                    ["found"] = found,
                    ["missing"] = missing,
                },
            };
            bool ok = missing.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Ketiga-tiga jadual wajib dijumpai ({names.Count} jadual dalam model)."
                : $"Jadual belum dijumpai: {string.Join(", ", missing)}. Jana jadual tersebut ({names.Count} jadual sedia ada disemak).";
            return o;
        }

        private static CheckOutcome SheetsContents(Document doc, AuditFormRow row)
        {
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            var emptySheets = sheets
                .Where(s => { try { return s.GetAllPlacedViews().Count == 0; } catch { return false; } })
                .Select(s => s.SheetNumber + " " + s.Name).ToList();
            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["sheets"] = sheets.Count,
                    ["empty_sheets"] = emptySheets.Take(Cap).ToList(),
                    ["empty_count"] = emptySheets.Count,
                },
            };
            bool ok = sheets.Count > 0 && emptySheets.Count == 0;
            o.Compliance = sheets.Count == 0 ? "no" : ok ? "yes" : "no";
            o.Remark = sheets.Count == 0
                ? "Tiada Sheet dalam model — sediakan helaian lukisan seni bina."
                : ok
                    ? $"{sheets.Count} sheet, semuanya mengandungi view."
                    : $"{emptySheets.Count}/{sheets.Count} sheet kosong tanpa view (cth: {string.Join(", ", emptySheets.Take(3))}). Susun view pada sheet berkenaan.";
            return o;
        }

        private static CheckOutcome TitleblockJkr(Document doc, AuditFormRow row)
        {
            var tbs = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().Cast<FamilyInstance>().ToList();
            var offending = new List<string>();
            int jkr = 0;
            foreach (var tb in tbs)
            {
                var famName = tb.Symbol?.FamilyName ?? "";
                var typeName = tb.Symbol?.Name ?? "";
                if ((famName + " " + typeName).IndexOf("JKR", StringComparison.OrdinalIgnoreCase) >= 0) jkr++;
                else
                {
                    var sheet = doc.GetElement(tb.OwnerViewId) as ViewSheet;
                    if (offending.Count < Cap)
                        offending.Add((sheet?.SheetNumber ?? "?") + ": " + famName);
                }
            }
            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["title_blocks"] = tbs.Count,
                    ["jkr_named"] = jkr,
                    ["non_jkr"] = offending,
                },
            };
            bool ok = tbs.Count > 0 && jkr == tbs.Count;
            o.Compliance = tbs.Count == 0 ? "no" : ok ? "yes" : "no";
            o.Remark = tbs.Count == 0
                ? "Tiada title block pada mana-mana sheet — gunakan JKR Title Block."
                : ok
                    ? $"Semua {tbs.Count} sheet guna title block JKR."
                    : $"{tbs.Count - jkr}/{tbs.Count} sheet tidak guna title block JKR (cth: {string.Join("; ", offending.Take(3))}). Tukar kepada JKR Title Block.";
            return o;
        }

        private static CheckOutcome LinksCurrent(Document doc, AuditFormRow row)
        {
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>().ToList();
            var rows = new List<object>();
            var notLoaded = new List<string>();
            foreach (var lt in links)
            {
                string status;
                try { status = lt.GetLinkedFileStatus().ToString(); } catch { status = "Unknown"; }
                rows.Add(new Dictionary<string, object?> { ["name"] = lt.Name, ["status"] = status });
                if (!string.Equals(status, "Loaded", StringComparison.OrdinalIgnoreCase))
                    notLoaded.Add(lt.Name + " (" + status + ")");
            }
            var o = new CheckOutcome
            {
                Evidence = { ["links"] = rows, ["count"] = links.Count, ["not_loaded"] = notLoaded },
            };
            if (links.Count == 0)
            {
                o.Compliance = "not_verifiable";
                o.Remark = "Tiada Revit link dalam model — sahkan secara manual sama ada pautan diperlukan.";
                return o;
            }
            bool ok = notLoaded.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Semua {links.Count} link dimuatkan (Loaded)."
                : $"{notLoaded.Count}/{links.Count} link tidak dimuatkan: {string.Join(", ", notLoaded.Take(3))}. Reload/betulkan laluan link.";
            return o;
        }

        private static CheckOutcome ViewTemplateApplied(Document doc, AuditFormRow row)
        {
            // Row wording carries which submission set it means (BOMBA / PBT).
            var text = row.Description.ToLowerInvariant();
            string token = text.Contains("bomba") ? "BOMBA" : text.Contains("pbt") ? "PBT" : "";
            if (token.Length == 0)
            {
                return new CheckOutcome
                {
                    Compliance = "not_verifiable",
                    Remark = "Baris tidak menyatakan set view (BOMBA/PBT) — semak manual.",
                };
            }
            var views = GraphicalViews(doc)
                .Where(v => v.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var without = views.Where(v => v.ViewTemplateId == ElementId.InvalidElementId)
                               .Select(v => v.Name).ToList();
            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["token"] = token,
                    ["views"] = views.Count,
                    ["without_template"] = without.Take(Cap).ToList(),
                },
            };
            if (views.Count == 0)
            {
                o.Compliance = "no";
                o.Remark = $"Tiada view {token} dalam model — wujudkan view {token} dan sapukan view template.";
                return o;
            }
            bool ok = without.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"Semua {views.Count} view {token} ada view template."
                : $"{without.Count}/{views.Count} view {token} tiada view template (cth: {string.Join(", ", without.Take(3))}). Sapukan template {token}.";
            return o;
        }

        /// <summary>Section D: per-category presence + type-naming structure.
        /// Only the "Standard component file naming" column is automatable;
        /// Quality/Information/Geometry stay manual and the remark says so.</summary>
        public static CheckOutcome EvaluateCategory(Document doc, string label)
        {
            var bic = CategoryMap.First(c => c.label == label).bic;
            var instances = new FilteredElementCollector(doc).OfCategory(bic)
                .WhereElementIsNotElementType().ToList();
            var types = new FilteredElementCollector(doc).OfCategory(bic)
                .WhereElementIsElementType().Cast<ElementType>().ToList();
            // Structured type name: 2+ segments split on - or _ (JKR convention
            // style); system default names like "Generic - 200mm" pass, bare
            // "Wall 1" does not.
            var nonConforming = types
                .Where(t => (t.Name ?? "").Split('-', '_').Count(s => s.Trim().Length > 0) < 2)
                .Select(t => t.Name).ToList();

            var o = new CheckOutcome
            {
                Evidence =
                {
                    ["category"] = label,
                    ["instances"] = instances.Count,
                    ["types"] = types.Count,
                    ["types_nonconforming_naming"] = nonConforming.Take(Cap).ToList(),
                    ["automated_scope"] = "standard naming only; quality/information/geometry manual",
                },
            };
            if (instances.Count == 0)
            {
                o.Compliance = "not_verifiable";
                o.Remark = $"Tiada elemen {label} dalam model — baris ini tidak berkenaan atau semak manual.";
                return o;
            }
            bool ok = nonConforming.Count == 0;
            o.Compliance = ok ? "yes" : "no";
            o.Remark = ok
                ? $"{instances.Count} elemen, {types.Count} jenis — semua nama jenis berstruktur. (Kualiti/maklumat/geometri: semak manual.)"
                : $"{instances.Count} elemen; {nonConforming.Count}/{types.Count} nama jenis tidak berstruktur (cth: {string.Join(", ", nonConforming.Take(3))}). Namakan semula jenis ikut konvensyen. (Kualiti/maklumat/geometri: semak manual.)";
            return o;
        }

        // ─── shared ─────────────────────────────────────────────────────

        private static List<View> GraphicalViews(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted
                            && v.ViewType != ViewType.Schedule
                            && v.ViewType != ViewType.DrawingSheet)
                .ToList();

        private static double[] MmArr(XYZ p) => new[]
        {
            Math.Round(p.X * 304.8, 1), Math.Round(p.Y * 304.8, 1), Math.Round(p.Z * 304.8, 1),
        };

        /// <summary>XY-plane intersection of two lines (infinite extension).</summary>
        private static XYZ? IntersectXy(Line a, Line b)
        {
            var p = a.GetEndPoint(0); var r = a.Direction;
            var q = b.GetEndPoint(0); var s = b.Direction;
            double denom = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(denom) < 1e-9) return null;   // parallel in plan
            double t = ((q.X - p.X) * s.Y - (q.Y - p.Y) * s.X) / denom;
            return new XYZ(p.X + t * r.X, p.Y + t * r.Y, 0);
        }
    }
}

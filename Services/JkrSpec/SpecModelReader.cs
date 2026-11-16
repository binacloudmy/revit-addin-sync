using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitWebAppSync.Services.JkrSpec
{
    /// <summary>
    /// Reads the <see cref="SpecModel"/> snapshot from a Revit document — the only
    /// Revit-dependent part of the spec checks. Runs on Revit's thread; never throws:
    /// whatever it cannot read goes to SpecModel.ReadErrors.
    ///
    /// Conventions every evaluator relies on:
    /// • Categories carry fixed English names (<see cref="Categories"/>), never the
    ///   localized Category.Name.
    /// • SpecType.Numbers in mm: "Width" (doors/windows: type, else instance; walls:
    ///   compound width), "Height" (doors/windows; walls: max unconnected height of the
    ///   instances; railings: top rail height), "Thickness" (doors; floors, roofs,
    ///   ceilings, ramps: type thickness), plus every other length-typed type parameter
    ///   by name.
    /// • SpecType.Texts: every string-valued type parameter, then the first instance's
    ///   string parameters not already present, by parameter name; "Function" as shown.
    /// • SpecType.Materials: compound-layer materials, material-typed type / instance
    ///   parameters, and the first instance's geometry materials (family-internal ones,
    ///   curtain panels / mullions, stair runs / landings, top rail). Empty = no material.
    /// • SpecSheet.Params: the sheet's parameters, then its title block instance's, by the
    ///   names Revit shows ("Sheet Name", "Drawn By", "Sheet Issue Date" …). ViewKinds /
    ///   ViewNames: placed views, plus "Schedule" for each placed schedule (revision
    ///   schedules inside the title block excluded).
    /// • SpecView.FolderPath / FolderBy: the view's folders in the current Project Browser
    ///   organisation and the parameters they group by; SetParam: View_jkr_pit. SpecSchedule
    ///   RowCategories: the categories of the listed elements (multi-category / takeoffs).
    /// • Nested shared components (FamilyInstance.SuperComponent set) are not "placed"
    ///   elements and are not counted.
    /// Read-only: no transactions.
    /// </summary>
    public static class SpecModelReader
    {
        /// <summary>Instance ids kept per type (the scan stays fast on big models).</summary>
        public const int InstanceIdCap = 2000;
        /// <summary>Element ids kept per schedule.</summary>
        public const int ScheduleRowCap = 5000;

        /// <summary>Spec category name → Revit category. The names are what evaluators match on.</summary>
        public static readonly (string Name, BuiltInCategory Bic)[] Categories =
        {
            ("Doors", BuiltInCategory.OST_Doors),
            ("Windows", BuiltInCategory.OST_Windows),
            ("Walls", BuiltInCategory.OST_Walls),
            ("Floors", BuiltInCategory.OST_Floors),
            ("Ceilings", BuiltInCategory.OST_Ceilings),
            ("Roofs", BuiltInCategory.OST_Roofs),
            ("Stairs", BuiltInCategory.OST_Stairs),
            ("Railings", BuiltInCategory.OST_StairsRailing),
            ("Ramps", BuiltInCategory.OST_Ramps),
            ("Curtain Panels", BuiltInCategory.OST_CurtainWallPanels),
            ("Curtain Wall Mullions", BuiltInCategory.OST_CurtainWallMullions),
            ("Casework", BuiltInCategory.OST_Casework),
            ("Furniture", BuiltInCategory.OST_Furniture),
            ("Furniture Systems", BuiltInCategory.OST_FurnitureSystems),
            ("Generic Models", BuiltInCategory.OST_GenericModel),
            ("Plumbing Fixtures", BuiltInCategory.OST_PlumbingFixtures),
            ("Specialty Equipment", BuiltInCategory.OST_SpecialityEquipment),
            ("Planting", BuiltInCategory.OST_Planting),
            ("Columns", BuiltInCategory.OST_Columns),
            ("Structural Columns", BuiltInCategory.OST_StructuralColumns),
            ("Structural Framing", BuiltInCategory.OST_StructuralFraming),
            ("Topography", BuiltInCategory.OST_Topography),
            ("Mass", BuiltInCategory.OST_Mass),
            ("Parking", BuiltInCategory.OST_Parking),
            ("Pipes", BuiltInCategory.OST_PipeCurves),
            ("Rooms", BuiltInCategory.OST_Rooms),
            ("Areas", BuiltInCategory.OST_Areas),
        };

        public static SpecModel Read(Document doc, string discipline, int lod)
        {
            var m = new SpecModel { Discipline = discipline ?? "AR", Lod = lod };
            if (doc == null) { m.ReadErrors.Add("No document"); return m.Normalize(); }
            try { m.FileName = System.IO.Path.GetFileName(doc.PathName ?? ""); }
            catch (Exception ex) { m.ReadErrors.Add("FileName: " + ex.Message); }

            Part(m, "Levels", () => ReadLevels(doc, m));
            Part(m, "Grids", () => ReadGrids(doc, m));
            Part(m, "Rooms", () => ReadRooms(doc, m));
            Part(m, "Floors", () => ReadFloors(doc, m));
            Part(m, "Ceilings", () => ReadCeilings(doc, m));
            Part(m, "Roofs", () => ReadRoofs(doc, m));
            Part(m, "Types", () => ReadTypes(doc, m));
            Part(m, "Materials", () => ReadMaterials(doc, m));
            Part(m, "Views", () => ReadViews(doc, m));
            Part(m, "Schedules", () => ReadSchedules(doc, m));
            Part(m, "Sheets", () => ReadSheets(doc, m));
            Part(m, "Links", () => ReadLinks(doc, m));
            Part(m, "ProjectInfo", () => ReadProjectInfo(doc, m));
            return m.Normalize();
        }

        private static void Part(SpecModel m, string what, Action read)
        {
            try { read(); }
            catch (Exception ex) { m.ReadErrors.Add(what + ": " + ex.Message); }
        }

        // ─── small helpers ───

        private static long IdOf(ElementId id) => id == null ? 0 : (long)id.Value;

        private static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

        private static double M2(double sqft) => UnitUtils.ConvertFromInternalUnits(sqft, UnitTypeId.SquareMeters);

        private static string NameOf(Element e)
        {
            try { return e?.Name ?? ""; } catch { return ""; }
        }

        private static string Shown(Parameter p)
        {
            try
            {
                if (p == null) return "";
                if (p.StorageType == StorageType.String) return p.AsString() ?? "";
                return p.AsValueString() ?? "";
            }
            catch { return ""; }
        }

        private static double? Length(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e?.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                return Mm(p.AsDouble());
            }
            catch { return null; }
        }

        private static double? LengthByName(Element e, string name)
        {
            try
            {
                var p = e?.LookupParameter(name);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                if (!IsLength(p)) return null;
                return Mm(p.AsDouble());
            }
            catch { return null; }
        }

        private static bool IsLength(Parameter p)
        {
            try { var t = p.Definition.GetDataType(); return t != null && t.Equals(SpecTypeId.Length); }
            catch { return false; }
        }

        private static bool IsMaterialParam(Parameter p)
        {
            try
            {
                if (p.StorageType != StorageType.ElementId) return false;
                var t = p.Definition.GetDataType();
                return t != null && t.Equals(SpecTypeId.Reference.Material);
            }
            catch { return false; }
        }

        private static string LevelName(Document doc, ElementId id)
        {
            try
            {
                if (id == null || id == ElementId.InvalidElementId) return "";
                return NameOf(doc.GetElement(id));
            }
            catch { return ""; }
        }

        /// <summary>Elements of a category that are placed: instances, not nested shared components.</summary>
        private static List<Element> Placed(Document doc, BuiltInCategory bic)
        {
            var list = new List<Element>();
            foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                try
                {
                    if (e is FamilyInstance fi && fi.SuperComponent != null) continue;
                    if (e is Room r && !(r.Area > 0)) continue;
                    if (e is Autodesk.Revit.DB.Area a && !(a.Area > 0)) continue;
                }
                catch { continue; }
                list.Add(e);
            }
            return list;
        }

        // ─── levels, grids ───

        private static void ReadLevels(Document doc, SpecModel m)
        {
            foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                var x = new SpecLevel { Id = IdOf(l.Id), Name = NameOf(l) };
                try { x.Elevation = Math.Round(Mm(l.Elevation), 1); } catch { }
                try
                {
                    var p = l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
                    if (p != null && p.HasValue) x.IsStory = p.AsInteger() != 0;
                }
                catch { }
                m.Levels.Add(x);
            }
        }

        private static void ReadGrids(Document doc, SpecModel m)
        {
            foreach (var g in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Grids)
                         .WhereElementIsNotElementType().OfType<Grid>())
            {
                var x = new SpecGrid { Id = IdOf(g.Id), Name = NameOf(g) };
                try
                {
                    if (g.Curve is Line line)
                    {
                        var d = line.Direction;
                        var o = line.GetEndPoint(0);
                        x.IsVertical = Math.Abs(d.Y) > Math.Abs(d.X);
                        x.Offset = Math.Round(Mm(x.IsVertical ? o.X : o.Y), 1);
                    }
                }
                catch { /* multi-segment grid: no straight line to read */ }
                m.Grids.Add(x);
            }
        }

        // ─── rooms, floors, ceilings, roofs ───

        private static Dictionary<string, string> AllParams(Element e)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (Parameter p in e.Parameters)
                {
                    string name;
                    try { name = p.Definition?.Name ?? ""; } catch { continue; }
                    if (name.Length == 0 || d.ContainsKey(name)) continue;
                    d[name] = Shown(p);
                }
            }
            catch { }
            return d;
        }

        private static void ReadRooms(Document doc, SpecModel m)
        {
            foreach (var r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType().OfType<Room>())
            {
                var x = new SpecRoom { Id = IdOf(r.Id) };
                try { x.Name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? ""; } catch { }
                try { x.Number = r.Number ?? ""; } catch { }
                try { x.Department = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? ""; } catch { }
                try { x.LevelName = LevelName(doc, r.LevelId); } catch { }
                try { x.Area = Math.Round(M2(r.Area), 3); } catch { }
                try
                {
                    if (r.Location is LocationPoint lp) { x.X = Math.Round(Mm(lp.Point.X), 1); x.Y = Math.Round(Mm(lp.Point.Y), 1); }
                }
                catch { }
                x.Params = AllParams(r);
                m.Rooms.Add(x);
            }
        }

        private static string TypeNameOf(Document doc, Element e)
        {
            try { return NameOf(doc.GetElement(e.GetTypeId())); } catch { return ""; }
        }

        private static void ReadFloors(Document doc, SpecModel m)
        {
            foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>())
            {
                var x = new SpecFloor { Id = IdOf(f.Id), TypeName = TypeNameOf(doc, f) };
                try { x.LevelName = LevelName(doc, f.LevelId); } catch { }
                try
                {
                    var bb = f.get_BoundingBox(null);
                    if (bb != null)
                    {
                        x.MinX = Math.Round(Mm(bb.Min.X), 1); x.MinY = Math.Round(Mm(bb.Min.Y), 1);
                        x.MaxX = Math.Round(Mm(bb.Max.X), 1); x.MaxY = Math.Round(Mm(bb.Max.Y), 1);
                    }
                }
                catch { }
                try
                {
                    var a = f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                    if (a != null && a.HasValue) x.Area = Math.Round(M2(a.AsDouble()), 3);
                }
                catch { }
                try
                {
                    var s = f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                    x.IsStructural = s != null && s.HasValue && s.AsInteger() != 0;
                }
                catch { }
                m.Floors.Add(x);
            }
        }

        private static void ReadCeilings(Document doc, SpecModel m)
        {
            foreach (var c in new FilteredElementCollector(doc).OfClass(typeof(Ceiling)).Cast<Ceiling>())
            {
                var x = new SpecCeiling { Id = IdOf(c.Id), TypeName = TypeNameOf(doc, c) };
                try { x.LevelName = LevelName(doc, c.LevelId); } catch { }
                x.HeightOffset = Math.Round(Length(c, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM) ?? 0, 1);
                m.Ceilings.Add(x);
            }
        }

        private static void ReadRoofs(Document doc, SpecModel m)
        {
            foreach (var r in new FilteredElementCollector(doc).OfClass(typeof(RoofBase)).Cast<RoofBase>())
            {
                var x = new SpecRoof { Id = IdOf(r.Id), TypeName = TypeNameOf(doc, r) };
                try
                {
                    var lid = r.LevelId;
                    if (lid == null || lid == ElementId.InvalidElementId)
                        lid = r.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM)?.AsElementId();
                    x.LevelName = LevelName(doc, lid);
                }
                catch { }
                try { x.SlopeDegrees = Math.Round(MaxSlopeDegrees(r), 2); } catch { }
                try
                {
                    var cs = r.RoofType?.GetCompoundStructure();
                    if (cs != null)
                        foreach (var layer in cs.GetLayers())
                        {
                            x.LayerFunctions.Add(layer.Function.ToString());
                            var mat = doc.GetElement(layer.MaterialId) as Material;
                            if (mat != null && !x.Materials.Contains(NameOf(mat))) x.Materials.Add(NameOf(mat));
                        }
                }
                catch { }
                m.Roofs.Add(x);
            }
        }

        /// <summary>Steepest upward-facing planar face of the roof, degrees (0 = flat).
        /// Geometry-based, so footprint, extrusion and sloped-glazing roofs read alike.</summary>
        private static double MaxSlopeDegrees(Element roof)
        {
            double max = 0;
            var geo = roof.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse });
            if (geo == null) return 0;
            int faces = 0;
            foreach (var obj in geo)
            {
                var solids = new List<Solid>();
                if (obj is Solid s) solids.Add(s);
                else if (obj is GeometryInstance gi)
                    foreach (var o2 in gi.GetInstanceGeometry()) if (o2 is Solid s2) solids.Add(s2);
                foreach (var solid in solids)
                {
                    if (solid == null || solid.Faces.IsEmpty) continue;
                    foreach (Face f in solid.Faces)
                    {
                        if (++faces > 500) return max;
                        if (!(f is PlanarFace pf)) continue;
                        var nz = pf.FaceNormal.Z;
                        if (nz <= 0.05) continue;   // vertical edges and soffits
                        var deg = Math.Acos(Math.Min(1.0, nz)) * 180.0 / Math.PI;
                        if (deg > max) max = deg;
                    }
                }
            }
            return max;
        }

        // ─── types ───

        private static void ReadTypes(Document doc, SpecModel m)
        {
            foreach (var (name, bic) in Categories)
            {
                List<Element> placed;
                try { placed = Placed(doc, bic); }
                catch (Exception ex) { m.ReadErrors.Add("Types/" + name + ": " + ex.Message); continue; }
                m.CategoryCounts[name] = placed.Count;
                if (bic == BuiltInCategory.OST_Rooms || bic == BuiltInCategory.OST_Areas) continue;

                var byType = new Dictionary<long, List<Element>>();
                foreach (var e in placed)
                {
                    ElementId tid;
                    try { tid = e.GetTypeId(); } catch { continue; }
                    if (tid == null || tid == ElementId.InvalidElementId) continue;
                    var k = IdOf(tid);
                    if (!byType.TryGetValue(k, out var list)) byType[k] = list = new List<Element>();
                    list.Add(e);
                }
                foreach (var kv in byType)
                {
                    try { m.Types.Add(ReadType(doc, name, kv.Value)); }
                    catch (Exception ex) { m.ReadErrors.Add("Types/" + name + " #" + kv.Key + ": " + ex.Message); }
                }
            }
        }

        private static SpecType ReadType(Document doc, string category, List<Element> instances)
        {
            var first = instances[0];
            var type = doc.GetElement(first.GetTypeId()) as ElementType;
            var t = new SpecType
            {
                TypeId = IdOf(first.GetTypeId()),
                Category = category,
                TypeName = NameOf(type),
                InstanceCount = instances.Count,
                InstanceIds = instances.Take(InstanceIdCap).Select(e => IdOf(e.Id)).ToList(),
            };
            try { t.FamilyName = type?.FamilyName ?? ""; } catch { }
            if (type != null) ReadTypeParams(type, t);
            ReadInstanceTexts(first, t);
            ReadNumbers(doc, category, type, instances, t);
            ReadTypeMaterials(doc, type, first, t);
            return t;
        }

        private static void ReadTypeParams(ElementType type, SpecType t)
        {
            foreach (Parameter p in type.Parameters)
            {
                try
                {
                    var name = p.Definition?.Name ?? "";
                    if (name.Length == 0) continue;
                    if (p.StorageType == StorageType.String)
                    {
                        if (!t.Texts.ContainsKey(name)) t.Texts[name] = p.AsString() ?? "";
                    }
                    else if (p.StorageType == StorageType.Double && p.HasValue && IsLength(p))
                    {
                        if (!t.Numbers.ContainsKey(name)) t.Numbers[name] = Math.Round(Mm(p.AsDouble()), 1);
                    }
                }
                catch { }
            }
            // "Function" (walls, floors, roofs …) is an enum parameter: keep it as shown.
            try
            {
                var f = type.LookupParameter("Function");
                if (f != null && !t.Texts.ContainsKey("Function")) t.Texts["Function"] = Shown(f);
            }
            catch { }
        }

        private static void ReadInstanceTexts(Element inst, SpecType t)
        {
            try
            {
                foreach (Parameter p in inst.Parameters)
                {
                    try
                    {
                        if (p.StorageType != StorageType.String) continue;
                        var name = p.Definition?.Name ?? "";
                        if (name.Length == 0 || t.Texts.ContainsKey(name)) continue;
                        t.Texts[name] = p.AsString() ?? "";
                    }
                    catch { }
                }
                var f = inst.LookupParameter("Function");
                if (f != null && !t.Texts.ContainsKey("Function")) t.Texts["Function"] = Shown(f);
            }
            catch { }
        }

        private static void Set(SpecType t, string key, double? mm)
        {
            if (mm.HasValue && !double.IsNaN(mm.Value)) t.Numbers[key] = Math.Round(mm.Value, 1);
        }

        private static double? CompoundWidth(ElementType type)
        {
            try
            {
                var cs = (type as HostObjAttributes)?.GetCompoundStructure();
                return cs == null ? (double?)null : Mm(cs.GetWidth());
            }
            catch { return null; }
        }

        private static void ReadNumbers(Document doc, string category, ElementType type, List<Element> instances, SpecType t)
        {
            var first = instances[0];
            switch (category)
            {
                case "Doors":
                case "Windows":
                {
                    var door = category == "Doors";
                    var w = Length(type, door ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH)
                            ?? Length(type, BuiltInParameter.FAMILY_WIDTH_PARAM)
                            ?? Length(first, door ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH)
                            ?? Length(first, BuiltInParameter.FAMILY_WIDTH_PARAM);
                    var h = Length(type, door ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT)
                            ?? Length(type, BuiltInParameter.FAMILY_HEIGHT_PARAM)
                            ?? Length(first, door ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT)
                            ?? Length(first, BuiltInParameter.FAMILY_HEIGHT_PARAM);
                    Set(t, "Width", w);
                    Set(t, "Height", h);
                    Set(t, "Rough Width", Length(type, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM));
                    Set(t, "Rough Height", Length(type, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM));
                    if (door)
                        Set(t, "Thickness", LengthByName(type, "Thickness") ?? LengthByName(type, "Door Thickness")
                                            ?? LengthByName(first, "Thickness"));
                    break;
                }
                case "Walls":
                {
                    Set(t, "Width", (type as WallType) != null ? SafeMm(() => ((WallType)type).Width) : CompoundWidth(type));
                    double? max = null;
                    foreach (var e in instances.Take(InstanceIdCap))
                    {
                        var h = Length(e, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                        if (h.HasValue && (!max.HasValue || h.Value > max.Value)) max = h;
                    }
                    Set(t, "Height", max);
                    break;
                }
                case "Floors":
                    Set(t, "Thickness", CompoundWidth(type) ?? Length(type, BuiltInParameter.FLOOR_ATTR_DEFAULT_THICKNESS_PARAM));
                    break;
                case "Roofs":
                    Set(t, "Thickness", CompoundWidth(type) ?? Length(type, BuiltInParameter.ROOF_ATTR_DEFAULT_THICKNESS_PARAM));
                    break;
                case "Ceilings":
                    Set(t, "Thickness", CompoundWidth(type) ?? Length(type, BuiltInParameter.CEILING_THICKNESS));
                    break;
                case "Ramps":
                    Set(t, "Thickness", Length(type, BuiltInParameter.RAMP_ATTR_THICKNESS) ?? LengthByName(type, "Thickness"));
                    break;
                case "Railings":
                    if (type is RailingType rt) Set(t, "Height", SafeMm(() => rt.TopRailHeight));
                    break;
            }
        }

        private static double? SafeMm(Func<double> feet)
        {
            try { return Mm(feet()); } catch { return null; }
        }

        private static void AddMaterial(Document doc, ElementId id, SpecType t)
        {
            try
            {
                if (id == null || id == ElementId.InvalidElementId) return;
                if (!(doc.GetElement(id) is Material mat)) return;
                var n = NameOf(mat);
                if (n.Length > 0 && !t.Materials.Contains(n)) t.Materials.Add(n);
            }
            catch { }
        }

        private static void AddMaterialParams(Document doc, Element e, SpecType t)
        {
            if (e == null) return;
            try
            {
                foreach (Parameter p in e.Parameters)
                    if (IsMaterialParam(p)) AddMaterial(doc, p.AsElementId(), t);
            }
            catch { }
        }

        private static void AddGeometryMaterials(Document doc, Element e, SpecType t)
        {
            if (e == null) return;
            try { foreach (var id in e.GetMaterialIds(false)) AddMaterial(doc, id, t); } catch { }
        }

        private static void ReadTypeMaterials(Document doc, ElementType type, Element first, SpecType t)
        {
            try
            {
                var cs = (type as HostObjAttributes)?.GetCompoundStructure();
                if (cs != null) foreach (var layer in cs.GetLayers()) AddMaterial(doc, layer.MaterialId, t);
            }
            catch { }
            AddMaterialParams(doc, type, t);
            AddMaterialParams(doc, first, t);
            AddGeometryMaterials(doc, first, t);
            try
            {
                if (first is Wall w && w.CurtainGrid != null)
                {
                    foreach (var id in w.CurtainGrid.GetPanelIds().Take(20)) { var e = doc.GetElement(id); AddGeometryMaterials(doc, e, t); AddMaterialParams(doc, e, t); }
                    foreach (var id in w.CurtainGrid.GetMullionIds().Take(20)) AddGeometryMaterials(doc, doc.GetElement(id), t);
                }
                else if (first is Stairs st)
                {
                    foreach (var id in st.GetStairsRuns().Take(10)) { var e = doc.GetElement(id); AddGeometryMaterials(doc, e, t); AddMaterialParams(doc, e, t); AddMaterialParams(doc, doc.GetElement(e.GetTypeId()), t); }
                    foreach (var id in st.GetStairsLandings().Take(10)) { var e = doc.GetElement(id); AddGeometryMaterials(doc, e, t); AddMaterialParams(doc, e, t); AddMaterialParams(doc, doc.GetElement(e.GetTypeId()), t); }
                }
                else if (first is Railing r)
                {
                    var top = r.TopRail;
                    if (top != null && top != ElementId.InvalidElementId)
                    {
                        var e = doc.GetElement(top);
                        AddGeometryMaterials(doc, e, t);
                        AddMaterialParams(doc, doc.GetElement(e?.GetTypeId() ?? ElementId.InvalidElementId), t);
                    }
                }
            }
            catch { }
        }

        // ─── materials ───

        private static void ReadMaterials(Document doc, SpecModel m)
        {
            // Usage from the placed types read above (type materials × instances).
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in m.Types)
                foreach (var n in t.Materials.Distinct())
                    used[n] = (used.TryGetValue(n, out var c) ? c : 0) + t.InstanceCount;
            foreach (var mat in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
            {
                var x = new SpecMaterial { Id = IdOf(mat.Id), Name = NameOf(mat) };
                try { x.Class = mat.MaterialClass ?? ""; } catch { }
                x.UsedBy = used.TryGetValue(x.Name, out var u) ? u : 0;
                m.Materials.Add(x);
            }
        }

        // ─── views, schedules, sheets ───

        private static string KindOf(ViewType vt)
        {
            switch (vt)
            {
                case ViewType.FloorPlan: return "FloorPlan";
                case ViewType.CeilingPlan: return "CeilingPlan";
                case ViewType.Elevation: return "Elevation";
                case ViewType.Section: return "Section";
                case ViewType.ThreeD: return "ThreeD";
                case ViewType.DraftingView: return "DraftingView";
                case ViewType.Detail: return "Detail";
                case ViewType.Legend: return "Legend";
                case ViewType.AreaPlan: return "AreaPlan";
                default: return "Other";
            }
        }

        private static bool IsGraphicalView(ViewType vt)
        {
            switch (vt)
            {
                case ViewType.Schedule:
                case ViewType.ColumnSchedule:
                case ViewType.PanelSchedule:
                case ViewType.DrawingSheet:
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.Internal:
                case ViewType.Undefined:
                case ViewType.Report:
                case ViewType.CostReport:
                case ViewType.LoadsReport:
                    return false;
                default: return true;
            }
        }

        /// <summary>View id → number of the first sheet it is placed on.</summary>
        private static Dictionary<long, string> PlacedOnSheets(Document doc)
        {
            var d = new Dictionary<long, string>();
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                string number;
                try { number = s.SheetNumber ?? ""; } catch { number = ""; }
                try
                {
                    foreach (var vid in s.GetAllPlacedViews())
                    {
                        var k = IdOf(vid);
                        if (!d.ContainsKey(k)) d[k] = number;
                    }
                }
                catch { }
            }
            return d;
        }

        private static BrowserOrganization ViewsOrganization(Document doc)
        {
            try { return BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc); }
            catch { return null; }
        }

        /// <summary>Name of the parameter a browser folder groups by: a project / shared
        /// parameter's name, or a built-in parameter's label; "" when unknown.</summary>
        private static string FolderParamName(Document doc, ElementId id, Dictionary<long, string> cache)
        {
            var k = IdOf(id);
            if (cache.TryGetValue(k, out var known)) return known;
            var name = "";
            try
            {
                if (k < 0) name = LabelUtils.GetLabelFor((BuiltInParameter)(int)k) ?? "";
                else if (doc.GetElement(id) is ParameterElement pe) name = pe.GetDefinition()?.Name ?? NameOf(pe);
            }
            catch { name = ""; }
            cache[k] = name;
            return name;
        }

        /// <summary>The parameter a folder item groups by. FolderItemInfo.ElementId is deprecated
        /// from Revit 2026 but still in the 2027 API; it sits in its own method so that, should a
        /// later Revit drop it, only this call fails (caught by the caller), not the view read.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ElementId GroupingParamId(FolderItemInfo item)
        {
#pragma warning disable CS0618
            return item.ElementId;
#pragma warning restore CS0618
        }

        /// <summary>The view's browser folders (FolderPath / FolderBy) and its View_jkr_pit
        /// value. Never throws: whatever cannot be read stays "".</summary>
        private static void ReadViewSet(Document doc, BrowserOrganization org, View v, SpecView x, Dictionary<long, string> paramNames)
        {
            if (org != null)
            {
                try
                {
                    var names = new List<string>();
                    var by = new List<string>();
                    foreach (var item in org.GetFolderItems(v.Id) ?? new List<FolderItemInfo>())
                    {
                        if (item == null) continue;
                        string n;
                        try { n = (item.Name ?? "").Trim(); } catch { n = ""; }
                        names.Add(n.Length == 0 ? "???" : n);
                        string p;
                        try { p = FolderParamName(doc, GroupingParamId(item), paramNames); } catch { p = ""; }
                        by.Add(p);
                    }
                    x.FolderPath = string.Join(" / ", names);
                    x.FolderBy = string.Join(" / ", by);
                }
                catch { x.FolderPath = ""; x.FolderBy = ""; }
            }
            try { x.SetParam = Shown(v.LookupParameter(SpecViewSets.SetParam)).Trim(); }
            catch { x.SetParam = ""; }
        }

        private static void ReadViews(Document doc, SpecModel m)
        {
            var onSheet = PlacedOnSheets(doc);
            var org = ViewsOrganization(doc);
            var paramNames = new Dictionary<long, string>();
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                try
                {
                    if (v.IsTemplate)
                    {
                        var tn = NameOf(v);
                        if (tn.Length > 0) m.ViewTemplates.Add(tn);
                        continue;
                    }
                    if (!IsGraphicalView(v.ViewType)) continue;
                    var x = new SpecView { Id = IdOf(v.Id), Name = NameOf(v), Kind = KindOf(v.ViewType) };
                    try { x.LevelName = v.GenLevel != null ? NameOf(v.GenLevel) : ""; } catch { }
                    try
                    {
                        var tid = v.ViewTemplateId;
                        if (tid != null && tid != ElementId.InvalidElementId) x.TemplateName = NameOf(doc.GetElement(tid));
                    }
                    catch { }
                    if (onSheet.TryGetValue(x.Id, out var sn)) { x.OnSheet = true; x.SheetNumber = sn; }
                    try { ReadViewSet(doc, org, v, x, paramNames); } catch { /* no set data: the checks fall back to template / name */ }
                    m.Views.Add(x);
                }
                catch (Exception ex) { m.ReadErrors.Add("Views #" + IdOf(v.Id) + ": " + ex.Message); }
            }
        }

        /// <summary>Spec category name for a category id: the fixed names above, else the
        /// BuiltInCategory name without "OST_"; "" for none / sheets.</summary>
        private static string CategoryName(ElementId catId)
        {
            if (catId == null || catId == ElementId.InvalidElementId) return "";
            long v = IdOf(catId);
            foreach (var (name, bic) in Categories)
                if ((long)(int)bic == v) return name;
            if (v == (long)(int)BuiltInCategory.OST_Sheets) return "";
            try
            {
                var bic = (BuiltInCategory)(int)v;
                var s = bic.ToString();
                return s.StartsWith("OST_", StringComparison.Ordinal) ? s.Substring(4) : "";
            }
            catch { return ""; }
        }

        private static void ReadSchedules(Document doc, SpecModel m)
        {
            foreach (var vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
            {
                try
                {
                    if (vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule) continue;
                    var x = new SpecSchedule { Id = IdOf(vs.Id), Name = NameOf(vs), IsTemplate = vs.IsTemplate };
                    var def = vs.Definition;
                    try { x.Category = CategoryName(def.CategoryId); } catch { }
                    try { x.IsMaterialTakeoff = def.IsMaterialTakeoff; } catch { }
                    try
                    {
                        foreach (var fid in def.GetFieldOrder())
                        {
                            var f = def.GetField(fid);
                            if (f == null || f.IsHidden) continue;
                            var n = f.GetName() ?? "";
                            if (n.Length > 0) x.Fields.Add(n);
                        }
                    }
                    catch { }
                    if (!vs.IsTemplate)
                    {
                        try
                        {
                            // The rows, and the categories they belong to: a multi-category
                            // schedule or a material takeoff schedules those categories too.
                            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var e in new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType().Take(ScheduleRowCap))
                            {
                                x.RowElementIds.Add(IdOf(e.Id));
                                string c;
                                try { c = CategoryName(e.Category?.Id); } catch { c = ""; }
                                if (c.Length > 0 && cats.Add(c)) x.RowCategories.Add(c);
                            }
                        }
                        catch { }
                    }
                    m.Schedules.Add(x);
                }
                catch (Exception ex) { m.ReadErrors.Add("Schedules #" + IdOf(vs.Id) + ": " + ex.Message); }
            }
        }

        private static void ReadSheets(Document doc, SpecModel m)
        {
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                try
                {
                    if (s.IsTemplate) continue;
                    var x = new SpecSheet { Id = IdOf(s.Id), Name = NameOf(s) };
                    try { x.Number = s.SheetNumber ?? ""; } catch { }
                    x.Params = AllParams(s);
                    try
                    {
                        var tb = new FilteredElementCollector(doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType().OfType<FamilyInstance>().FirstOrDefault();
                        if (tb != null)
                        {
                            var sym = tb.Symbol;
                            x.TitleBlock = sym == null ? NameOf(tb) : (sym.FamilyName ?? "") + " : " + NameOf(sym);
                            foreach (var kv in AllParams(tb))
                                if (!x.Params.ContainsKey(kv.Key)) x.Params[kv.Key] = kv.Value;
                        }
                    }
                    catch { }
                    try
                    {
                        foreach (var vid in s.GetAllPlacedViews())
                        {
                            if (!(doc.GetElement(vid) is View v)) continue;
                            x.ViewKinds.Add(KindOf(v.ViewType));
                            x.ViewNames.Add(NameOf(v));
                        }
                    }
                    catch { }
                    // Schedules are placed as ScheduleSheetInstance, not as viewports: kind "Schedule".
                    try
                    {
                        foreach (var si in new FilteredElementCollector(doc, s.Id).OfClass(typeof(ScheduleSheetInstance))
                                     .Cast<ScheduleSheetInstance>())
                        {
                            if (si.IsTitleblockRevisionSchedule) continue;
                            x.ViewKinds.Add("Schedule");
                            x.ViewNames.Add(NameOf(doc.GetElement(si.ScheduleId)));
                        }
                    }
                    catch { }
                    m.Sheets.Add(x);
                }
                catch (Exception ex) { m.ReadErrors.Add("Sheets #" + IdOf(s.Id) + ": " + ex.Message); }
            }
        }

        // ─── links, project information ───

        private static void ReadLinks(Document doc, SpecModel m)
        {
            foreach (var lt in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
            {
                var x = new SpecLink { Name = NameOf(lt) };
                try
                {
                    var st = lt.GetLinkedFileStatus();
                    x.Status = st == LinkedFileStatus.Loaded ? "Loaded"
                             : st == LinkedFileStatus.NotFound ? "NotFound"
                             : "Unloaded";
                }
                catch { x.Status = "Unloaded"; }
                m.Links.Add(x);
            }
        }

        private static void ReadProjectInfo(Document doc, SpecModel m)
        {
            var pi = doc.ProjectInformation;
            if (pi == null) { m.ReadErrors.Add("ProjectInfo: none"); return; }
            m.ProjectInfo = AllParams(pi);
        }
    }
}

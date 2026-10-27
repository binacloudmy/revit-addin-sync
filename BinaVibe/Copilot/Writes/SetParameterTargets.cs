#nullable enable
// Which host elements a set_parameters call targets (spec §11 #5), resolved
// BEFORE any change. ids, a find_elements handle, a host-only filter (built
// on set_parameter_by_filter's category + level walk, BulkEdit ruling R2),
// or the rows of an attached table (by element id or one match column).
// Nothing found is never a silent zero: it is not_found with near matches.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Copilot.Writes
{
    internal sealed class PickedTarget
    {
        public Element Element = null!;
        public JsonElement Value;
        /// <summary>1-based table row, for messages.</summary>
        public int? Row;
    }

    internal sealed class SkipNote
    {
        public long? Id;
        public int? Row;
        public string Reason = "";
    }

    internal static class SetParameterTargets
    {
        private const int MaxCandidates = 10;

        public static List<PickedTarget> Resolve(UIDocument uidoc, Document doc, IHandleStore handles, SetParametersArgs args, List<SkipNote> skips)
        {
            if (args.Table != null) return FromTable(doc, args.Table, skips);
            var t = args.Target!;
            var elements = t.Ids != null ? FromIds(doc, t.Ids, skips)
                : t.Handle != null ? FromHandle(doc, handles, t.Handle, skips)
                : FromFilter(uidoc, doc, t.Filter!);
            return elements.Select(e => new PickedTarget { Element = e, Value = args.Value!.Value }).ToList();
        }

        private static List<Element> FromIds(Document doc, IEnumerable<long> ids, List<SkipNote> skips)
        {
            var found = new List<Element>();
            var missing = new List<long>();
            foreach (var id in ids)
            {
                var e = doc.GetElement(ElemIds.From(id));
                if (e == null) { missing.Add(id); skips.Add(new SkipNote { Id = id, Reason = "no such element in the host model" }); }
                else found.Add(e);
            }
            if (found.Count == 0)
                throw new WriteRefusal(ErrorCodes.NotFound,
                    "None of the ids (" + string.Join(", ", missing.Take(10).Select(i => i.ToString(CultureInfo.InvariantCulture))) +
                    (missing.Count > 10 ? ", …" : "") + ") is an element of the host model; use find_elements to get current ids.",
                    Array.Empty<string>());
            return found;
        }

        private static List<Element> FromHandle(Document doc, IHandleStore handles, string handle, List<SkipNote> skips)
        {
            var refs = handles.Get(handle) ?? throw HandleRefusals.Unknown(handle);
            var host = refs.Where(r => r.Link == null).Select(r => r.Id).ToList();
            var linked = refs.Count - host.Count;
            if (host.Count == 0)
                throw new WriteRefusal(ErrorCodes.NotOwned,
                    "Every element of handle '" + handle + "' is in a linked model; writes apply to the host model only. Open the linked model to edit them.");
            if (linked > 0)
                skips.Add(new SkipNote { Reason = linked.ToString(CultureInfo.InvariantCulture) + " element(s) of the handle are in a linked model (host only)" });
            return FromIds(doc, host, skips);
        }

        private static List<Element> FromFilter(UIDocument uidoc, Document doc, FilterSpec f)
        {
            IEnumerable<Element> elements;
            if (f.Category != null)
            {
                var list = global::BinaVibe.Mcp.Tools.BulkEdit.CopilotTargets(doc, f.Category, f.Level, out var err);
                if (err != null) throw Refusal(err, doc, f);
                elements = list;
            }
            else
            {
                elements = new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e => e.Category != null);
                if (f.Level != null)
                {
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                    var level = levels.FirstOrDefault(l => string.Equals(l.Name, f.Level, StringComparison.OrdinalIgnoreCase))
                                ?? throw new WriteRefusal(ErrorCodes.NotFound, "Level '" + f.Level + "' is not in the model.", levels.Select(l => l.Name).Take(MaxCandidates));
                    var levelId = level.Id;
                    elements = elements.Where(e => e.LevelId == levelId);
                }
            }

            if (f.InView)
            {
                var view = doc.ActiveView ?? throw new WriteRefusal(ErrorCodes.InvalidArgs, "in_view needs an active view.");
                var visible = new HashSet<long>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds().Select(id => (long)id.Value));
                elements = elements.Where(e => visible.Contains((long)e.Id.Value));
            }

            var pool = elements.ToList();
            var result = pool;
            if (f.Type != null)
            {
                result = result.Where(e => Inspectors.PredicateMatches(e, doc, "type_name=" + f.Type)).ToList();
                if (result.Count == 0)
                    throw new WriteRefusal(ErrorCodes.NotFound, "No " + Scope(f) + " has a type matching '" + f.Type + "'.", TypeNames(doc, pool));
            }
            if (f.Room != null)
            {
                var byRoom = result.Where(e => InRoom(doc, e, f.Room)).ToList();
                if (byRoom.Count == 0)
                    throw new WriteRefusal(ErrorCodes.NotFound, "No " + Scope(f) + " is in a room named or numbered '" + f.Room + "'.", RoomNames(doc, f.Room));
                result = byRoom;
            }
            if (f.Parameter != null)
            {
                var cond = f.Parameter;
                result = result.Where(e => cond.Matches(RevitParamValues.Read(doc, e.LookupParameter(cond.Name) ?? TypeOf(doc, e)?.LookupParameter(cond.Name)))).ToList();
            }
            if (f.Text != null)
                result = result.Where(e => TextMatches(doc, e, f.Text)).ToList();

            if (result.Count == 0)
                throw new WriteRefusal(ErrorCodes.NotFound,
                    "Nothing in the host model matches " + f.Describe() + " (" + pool.Count.ToString(CultureInfo.InvariantCulture) +
                    " element(s) before the last conditions). Check the names with model_overview or find_elements.",
                    TypeNames(doc, pool));
            return result;
        }

        private static List<PickedTarget> FromTable(Document doc, TableSpec table, List<SkipNote> skips)
        {
            var picked = new List<PickedTarget>();
            if (table.Key == TableKey.Id)
            {
                foreach (var row in table.Rows)
                {
                    var e = doc.GetElement(ElemIds.From(row.Id!.Value));
                    if (e == null) { skips.Add(new SkipNote { Id = row.Id, Row = row.Row, Reason = "no such element in the host model" }); continue; }
                    picked.Add(new PickedTarget { Element = e, Value = row.Value, Row = row.Row });
                }
                if (picked.Count == 0)
                    throw new WriteRefusal(ErrorCodes.NotFound, "None of the table's element ids is in the host model; the table may be from another model or an older copy.", Array.Empty<string>());
                return picked;
            }

            var match = table.MatchParam!;
            var index = new Dictionary<string, List<Element>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(x => x.Category != null))
            {
                var p = e.LookupParameter(match);
                if (p == null) continue;
                var key = (RevitParamValues.Display(p) ?? "").Trim();
                if (key.Length == 0) continue;
                if (!index.TryGetValue(key, out var list)) index[key] = list = new List<Element>();
                list.Add(e);
            }
            if (index.Count == 0)
                throw new WriteRefusal(ErrorCodes.NotFound, "No element in the host model has a filled '" + match + "' to match the table by.", Array.Empty<string>());

            foreach (var row in table.Rows)
            {
                if (!index.TryGetValue(row.MatchValue!, out var hits))
                {
                    skips.Add(new SkipNote { Row = row.Row, Reason = "no element has " + match + " '" + row.MatchValue + "'" });
                    continue;
                }
                foreach (var e in hits) picked.Add(new PickedTarget { Element = e, Value = row.Value, Row = row.Row });
            }
            if (picked.Count == 0)
                throw new WriteRefusal(ErrorCodes.NotFound, "No row of the table matches an element by '" + match + "'.", index.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(MaxCandidates));
            return picked;
        }

        internal static Element? TypeOf(Document doc, Element e)
        {
            var typeId = e.GetTypeId();
            return typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId);
        }

        private static WriteRefusal Refusal(Dictionary<string, object?> err, Document doc, FilterSpec f)
        {
            var message = err.TryGetValue("error", out var m) ? m?.ToString() ?? "target not found" : "target not found";
            var candidates = err.TryGetValue("candidates", out var c) && c is IEnumerable<string> list
                ? list.Take(MaxCandidates).ToList()
                : CategoryNames(doc, f.Category ?? "");
            return new WriteRefusal(ErrorCodes.NotFound, Capitalise(message) + ".", candidates);
        }

        private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string Scope(FilterSpec f) =>
            (f.Category ?? "element") + (f.Level != null ? " on '" + f.Level + "'" : "") + (f.InView ? " in the active view" : "");

        private static List<string> TypeNames(Document doc, IEnumerable<Element> pool) =>
            pool.Select(e => TypeOf(doc, e)?.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal).Take(MaxCandidates).ToList();

        private static List<string> CategoryNames(Document doc, string asked)
        {
            var names = new List<string>();
            foreach (Category c in doc.Settings.Categories)
            {
                if (c?.Name == null) continue;
                if (asked.Length == 0 || c.Name.IndexOf(asked, StringComparison.OrdinalIgnoreCase) >= 0 || asked.IndexOf(c.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                    names.Add(c.Name);
            }
            return names.Distinct().OrderBy(n => n, StringComparer.Ordinal).Take(MaxCandidates).ToList();
        }

        private static List<string> RoomNames(Document doc, string asked) =>
            new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<SpatialElement>()
                .Select(r => (r.Name ?? "").Trim()).Where(n => n.Length > 0)
                .OrderByDescending(n => n.IndexOf(asked, StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct().Take(MaxCandidates).ToList();

        private static bool InRoom(Document doc, Element e, string room)
        {
            Room? r = null;
            try
            {
                if (e is Room self) r = self;
                else if (e is FamilyInstance fi) r = fi.Room;
                if (r == null)
                {
                    XYZ? point = e.Location switch
                    {
                        LocationPoint lp => lp.Point,
                        LocationCurve lc => lc.Curve.Evaluate(0.5, true),
                        _ => null,
                    };
                    if (point != null) r = doc.GetRoomAtPoint(point);
                }
            }
            catch (Exception)
            {
                return false;
            }
            if (r == null) return false;
            var name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
            return string.Equals(name.Trim(), room, StringComparison.OrdinalIgnoreCase)
                   || string.Equals((r.Number ?? "").Trim(), room, StringComparison.OrdinalIgnoreCase)
                   || string.Equals((r.Name ?? "").Trim(), room, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TextMatches(Document doc, Element e, string text)
        {
            bool Has(string? s) => !string.IsNullOrEmpty(s) && s!.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            var type = TypeOf(doc, e) as ElementType;
            return Has(e.Name) || Has(type?.Name) || Has(type?.FamilyName)
                   || Has(e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString())
                   || Has(e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString());
        }
    }
}

#nullable enable
// Revit-bound inspect executor (spec §11 #3): resolves the target (ids, a
// handle, the selection; linked refs through their link instance), reads
// parameters in display units with storage type and read-only flag (type
// parameters with include_type, or when a requested field lives on the type),
// reads schedule rows (TableData body, labelled by ScheduleRows), and runs
// VisibilityExplainer for explain_visibility. Read-only: no transaction.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Copilot.Read
{
    internal static class InspectExecutor
    {
        private const int MaxScheduleRowsRead = 500;
        private const int MaxVisibilityElements = 200;

        private sealed class Target
        {
            public Element El = null!;
            public RevitLinkInstance? Link;
            public string? LinkName;
        }

        public static Envelope Run(ToolContext ctx, InspectArgs a)
        {
            var doc = ctx.Doc;
            var payload = new InspectPayload();

            var refs = new List<ElementRef>();
            if (a.Handle != null)
            {
                var held = ctx.Handles.Get(a.Handle);
                if (held == null)
                    return ReadEnvelope.Fail(ErrorCodes.NotFound,
                        "Handle " + a.Handle + " is unknown or expired in this session. Run find_elements again.", Array.Empty<string>());
                refs.AddRange(held);
                payload.Handle = a.Handle;
            }
            refs.AddRange(a.Ids.Select(id => new ElementRef(id, null)));
            if (a.Selection) refs.AddRange(SelectedRefs(ctx));
            refs = refs.Distinct().ToList();

            var targets = Resolve(doc, refs, payload);
            payload.TotalElements = targets.Count;
            if (targets.Count > InspectResultBuilder.MaxElements && payload.Handle == null)
                payload.Handle = ctx.Handles.Put(targets.Select(t => new ElementRef(t.El.Id.Value, t.Link?.Id.Value)).ToList(),
                    targets.Count + " inspected elements");

            if (!a.ExplainVisibility || a.Fields != null)
                for (var i = 0; i < Math.Min(targets.Count, InspectResultBuilder.MaxElements); i++)
                    payload.Elements.Add(Read(targets[i], a, withParams: a.Fields != null || i < InspectResultBuilder.MaxElementsWithAllParams));
            else
                foreach (var t in targets.Take(InspectResultBuilder.MaxElements)) payload.Elements.Add(Identity(t));

            if (a.Schedule != null)
            {
                var sched = Schedules.Resolve(doc, a.Schedule);
                if (sched == null)
                {
                    var names = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                        .Where(s => !s.IsTemplate).Select(s => s.Name).ToList();
                    var near = Fuzzy.Rank(a.Schedule, names, 5);
                    if (!a.HasTarget)
                        return ReadEnvelope.Fail(ErrorCodes.NotFound, "No schedule '" + a.Schedule + "' in this model.", near);
                    payload.Notes.Add("No schedule '" + a.Schedule + "'" + (near.Count > 0 ? "; near: " + string.Join(", ", near) : "") + ".");
                }
                else payload.Schedule = ReadSchedule(doc, sched);
            }

            if (a.ExplainVisibility)
            {
                var view = ResolveView(ctx, a.View, out var candidates);
                if (view == null)
                    return ReadEnvelope.Fail(ErrorCodes.NotFound,
                        a.View == null ? "There is no active view to check." : "No view '" + a.View + "' in this model.", candidates);
                var template = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId)?.Name : null;
                var info = VisibilityExplainer.ForView(doc, view);
                var facts = targets.Take(MaxVisibilityElements)
                    .Select(t => VisibilityExplainer.Facts(info, t.El, t.Link, t.LinkName)).ToList();
                if (targets.Count > MaxVisibilityElements)
                    payload.Notes.Add("Visibility was checked for the first " + MaxVisibilityElements + " of " + targets.Count + " elements.");
                payload.Visibility = VisibilityReasons.Report(view.Name, view.ViewType.ToString(), template, facts);
            }

            return InspectResultBuilder.Build(a, payload);
        }

        /// <summary>Host picks plus linked picks (review M2): an element picked inside a link,
        /// or selected by find_elements highlight through SetReferences, is a Reference with a
        /// LinkedElementId; GetElementIds alone does not see it.</summary>
        private static List<ElementRef> SelectedRefs(ToolContext ctx)
        {
            var refs = new List<ElementRef>();
            foreach (var id in ctx.UiDoc.Selection.GetElementIds()) refs.Add(new ElementRef(id.Value, null));
            try
            {
                foreach (var r in ctx.UiDoc.Selection.GetReferences())
                {
                    if (r == null) continue;
                    refs.Add(r.LinkedElementId != ElementId.InvalidElementId
                        ? new ElementRef(r.LinkedElementId.Value, r.ElementId.Value)
                        : new ElementRef(r.ElementId.Value, null));
                }
            }
            catch { /* older hosts: host picks only */ }
            return refs.Distinct().ToList();
        }

        private static List<Target> Resolve(Document doc, List<ElementRef> refs, InspectPayload payload)
        {
            var list = new List<Target>();
            var unloaded = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in refs)
            {
                if (r.Link == null)
                {
                    var el = doc.GetElement(ElemIds.From(r.Id));
                    if (el == null) payload.MissingIds.Add(r.Id);
                    else list.Add(new Target { El = el });
                    continue;
                }
                var inst = doc.GetElement(ElemIds.From(r.Link.Value)) as RevitLinkInstance;
                var name = inst == null ? "link " + r.Link : LinkNames.Display(SnapshotService.LinkTypeName(doc, inst));
                Document? ld = null;
                try { ld = inst?.GetLinkDocument(); } catch { }
                if (ld == null)
                {
                    unloaded[name] = unloaded.TryGetValue(name, out var n) ? n + 1 : 1;
                    continue;
                }
                var linked = ld.GetElement(ElemIds.From(r.Id));
                if (linked == null) payload.MissingIds.Add(r.Id);
                else list.Add(new Target { El = linked, Link = inst, LinkName = name });
            }
            foreach (var kv in unloaded)
                payload.Notes.Add(kv.Value + " element(s) in link " + kv.Key + " were not read: the link is unloaded.");
            return list;
        }

        private static InspectedElement Identity(Target t)
        {
            var d = t.El.Document;
            return new InspectedElement
            {
                Ref = new ElementRef(t.El.Id.Value, t.Link?.Id.Value),
                Name = RevitValues.ElementName(t.El),
                Category = t.El.Category?.Name,
                Type = RevitValues.TypeName(d, t.El),
                Family = RevitValues.FamilyName(d, t.El),
                Level = RevitValues.LevelName(d, t.El),
                LinkName = t.LinkName,
            };
        }

        private static Dictionary<string, Parameter> ByName(Element? el)
        {
            var map = new Dictionary<string, Parameter>(StringComparer.OrdinalIgnoreCase);
            if (el == null) return map;
            foreach (Parameter p in el.Parameters)
            {
                var n = p?.Definition?.Name;
                if (string.IsNullOrEmpty(n) || map.ContainsKey(n!)) continue;
                map[n!] = p!;
            }
            return map;
        }

        private static ParamRow Row(Document doc, Parameter p)
        {
            var (value, storage, unit) = RevitValues.ParamValue(doc, p);
            return new ParamRow
            {
                Name = p.Definition?.Name ?? "",
                Value = value,
                Storage = storage,
                ReadOnly = p.IsReadOnly,
                Unit = unit.Length == 0 ? null : unit,
            };
        }

        private static InspectedElement Read(Target t, InspectArgs a, bool withParams)
        {
            var e = Identity(t);
            if (!withParams) return e;
            var d = t.El.Document;
            var inst = ByName(t.El);
            var typeEl = RevitValues.TypeOf(d, t.El);
            var type = ByName(typeEl);

            if (a.Fields == null)
            {
                e.Params.AddRange(inst.Values.Select(p => Row(d, p)));
                if (a.IncludeType) e.TypeParams.AddRange(type.Values.Select(p => Row(d, p)));
                return e;
            }

            foreach (var f in a.Fields)
            {
                if (inst.TryGetValue(f, out var ip)) e.Params.Add(Row(d, ip));
                if (type.TryGetValue(f, out var tp) && (a.IncludeType || !inst.ContainsKey(f))) e.TypeParams.Add(Row(d, tp));
                if (!inst.ContainsKey(f) && !type.ContainsKey(f))
                    e.Missing.Add(new MissingField
                    {
                        Field = f,
                        Suggestions = Fuzzy.Rank(f, inst.Keys.Concat(type.Keys), 5).ToList(),
                    });
            }
            return e;
        }

        private static ScheduleTable ReadSchedule(Document doc, ViewSchedule sched)
        {
            var t = new ScheduleTable { Name = sched.Name, Id = sched.Id.Value };
            var def = sched.Definition;
            try
            {
                for (var i = 0; i < def.GetFieldCount(); i++)
                {
                    var field = def.GetField(i);
                    if (!field.IsHidden) t.Columns.Add(field.ColumnHeading ?? "");
                }
            }
            catch { }

            var body = sched.GetTableData().GetSectionData(SectionType.Body);
            int nRows = body.NumberOfRows, nCols = body.NumberOfColumns;
            var read = Math.Min(nRows, MaxScheduleRowsRead);
            for (var r = 0; r < read; r++)
            {
                var row = new List<string>(nCols);
                for (var c = 0; c < nCols; c++)
                {
                    string cell;
                    try { cell = sched.GetCellText(SectionType.Body, r, c) ?? ""; } catch { cell = ""; }
                    row.Add(cell);
                }
                t.Rows.Add(row);
            }
            if (nRows > read) t.TotalRows = nRows;
            if (t.Columns.Count != nCols && t.Rows.Count > 0) t.Columns = t.Rows[0].ToList();

            try { t.Grouped = def.GetSortGroupFields().Any(s => s.ShowHeader || s.ShowFooter || s.ShowBlankLine); } catch { }
            try { t.Itemized = def.IsItemized; } catch { }
            try { t.ElementCount = new FilteredElementCollector(doc, sched.Id).WhereElementIsNotElementType().GetElementCount(); } catch { }
            return t;
        }

        private static View? ResolveView(ToolContext ctx, string? name, out IReadOnlyList<string> candidates)
        {
            candidates = Array.Empty<string>();
            if (name == null) return ctx.UiDoc.ActiveView ?? ctx.Doc.ActiveView;
            var views = new FilteredElementCollector(ctx.Doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var exact = views.FirstOrDefault(v => string.Equals(v.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var folded = Fuzzy.Fold(name);
            var contains = views.Where(v => Fuzzy.Fold(v.Name).Contains(folded)).ToList();
            if (contains.Count == 1) return contains[0];
            candidates = Fuzzy.Rank(name, views.Select(v => v.Name), 5);
            if (candidates.Count == 0 && contains.Count > 1) candidates = contains.Select(v => v.Name).Take(5).ToList();
            return null;
        }
    }
}

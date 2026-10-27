#nullable enable
// Revit-bound find_elements executor (spec §11 #2). It only collects: what
// matches is decided by the Revit-free QueryParser/TextMatch/LevelMatcher/
// ParamCondition, the scope by FindScope, and the envelope by FindResultBuilder.
//
//   scope     every collector is filtered Revit-side by category
//             (OfCategory / ElementMulticategoryFilter): the query's categories,
//             or for categoryless free text the common-categories set, or the
//             handle's elements. Never the whole model (review C1). At most
//             FindScope.ScanBudget elements are looked at per call; past it the
//             result is partial.
//   sources   host | all | link:<name>; links a handle points into are searched
//             too (review I3); a link with no document (unloaded) and closed
//             worksets go into not_searched, never silently skipped
//   levels    resolved per source document; not_found only when no searched
//             document has the level (review M4)
//   highlight Selection.SetElementIds (SetReferences when links are involved),
//             no transaction; zoom uses UIView.ZoomAndCenterRectangle
//   near      on zero results, one condition at a time is dropped (never the
//             category) and the closest groups are reported
//
// Linked refs carry the RevitLinkInstance id (linked ids collide with host ids).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Copilot.Read
{
    internal static class FindExecutor
    {
        private const int NearMatchSteps = 4;
        private const int MaxNearMatches = 5;

        private sealed class Target
        {
            public Document Doc = null!;
            public RevitLinkInstance? Link;
            public string LinkName = "";
            public string Label => Link == null ? "host" : "link: " + LinkName;
        }

        private sealed class LinkInfo
        {
            public RevitLinkInstance Instance = null!;
            public long Id;
            public string Name = "";
            public Document? Doc;
        }

        private sealed class Hit
        {
            public Element El = null!;
            public Target Target = null!;
            public FoundRow Row = null!;
            public bool RoomDone;
        }

        /// <summary>Elements looked at in this call, shared by all queries, sources and near matches.</summary>
        private sealed class Budget
        {
            public int Used;
            public bool Exhausted;

            public bool Take()
            {
                if (Used >= FindScope.ScanBudget) { Exhausted = true; return false; }
                Used++;
                return true;
            }
        }

        /// <summary>A not_found (or other) failure raised while resolving names.</summary>
        private sealed class FindFailure : Exception
        {
            public FindFailure(Envelope envelope) : base(envelope.Summary) { Envelope = envelope; }
            public Envelope Envelope { get; }
        }

        public static Envelope Run(ToolContext ctx, FindPlan plan)
        {
            try { return RunCore(ctx, plan); }
            catch (FindFailure f) { return f.Envelope; }
        }

        private static Envelope RunCore(ToolContext ctx, FindPlan plan)
        {
            var doc = ctx.Doc;
            var links = Links(doc);

            HashSet<(long, long?)>? scope = null;
            IReadOnlyList<ElementRef>? handleRefs = null;
            if (plan.Handle != null)
            {
                handleRefs = ctx.Handles.Get(plan.Handle);
                if (handleRefs == null)
                    return ReadEnvelope.Fail(ErrorCodes.NotFound, HandleStore.NotFoundMessage(plan.Handle), Array.Empty<string>());
                scope = new HashSet<(long, long?)>(handleRefs.Select(r => (r.Id, r.Link)));
            }

            var outcome = new FindOutcome();
            var budget = new Budget();
            var hits = new Dictionary<(long, long?), Hit>();
            var order = new List<Hit>();
            var perQueryRefs = new List<List<ElementRef>>();

            foreach (var q in plan.Queries)
            {
                var decision = FindScope.Decide(q, scope != null);
                var (bics, catLabel) = ResolveCategories(doc, q, decision);
                outcome.Categories.Add(catLabel ?? (decision.DefaultSet ? FindScope.DefaultSetLabel : "the handle's elements"));
                var targets = Targets(doc, q, links, outcome, handleRefs);

                // Levels per searched document; a level only some documents have is fine (M4).
                var levelNames = new List<string>();
                var levelsByTarget = targets.Select(t => (Target: t, Ids: LevelIds(t, q, levelNames))).ToList();
                if ((q.Level ?? q.LevelHint) != null && levelsByTarget.Count > 0 && levelsByTarget.All(x => x.Ids != null && x.Ids.Count == 0))
                {
                    var all = targets.SelectMany(t => LevelNamesOf(t.Doc)).Distinct().ToList();
                    var want = q.Level ?? q.LevelHint!;
                    var near = Fuzzy.Rank(want, all, 5);
                    throw new FindFailure(ReadEnvelope.Fail(ErrorCodes.NotFound,
                        "No level '" + want + "' in the searched model" + (targets.Count > 1 ? "s" : "") + ".", near.Count > 0 ? near : all.Take(10)));
                }

                var matched = new List<ElementRef>();
                foreach (var (t, levelIds) in levelsByTarget)
                {
                    outcome.Sources.Add(t.Label);
                    var found = Collect(ctx, t, q, bics, decision, scope, levelIds, budget, strictNames: true, outcome);
                    foreach (var (el, row) in found)
                    {
                        var key = (row.Ref.Id, row.Ref.Link);
                        if (!hits.TryGetValue(key, out var hit))
                        {
                            hit = new Hit { El = el, Target = t, Row = row };
                            hits[key] = hit;
                            order.Add(hit);
                        }
                        hit.Row.Queries.Add(q.Index);
                        foreach (var kv in row.Fields) hit.Row.Fields[kv.Key] = kv.Value;
                        matched.Add(row.Ref);
                    }
                }

                var distinctLevels = levelNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                outcome.Queries.Add(new QueryOutcome
                {
                    Count = matched.Count,
                    Filter = FindText.Filter(q, catLabel, distinctLevels),
                    Subject = FindText.Subject(q, catLabel, distinctLevels),
                });
                perQueryRefs.Add(matched);
            }
            outcome.Sources = outcome.Sources.Distinct().ToList();
            // Sources stays empty when every requested source was unavailable (an unloaded link).

            AddClosedWorksets(doc, outcome);
            AddLinksOutsideScope(plan, links, outcome, includeLoaded: hits.Count == 0);
            if (hits.Count == 0)
                outcome.Categories = FindScope.Itemise(outcome.Categories, FindScope.DefaultCategories
                    .Select(o => RevitValues.TryParseBic(o, out var b) ? RevitValues.CategoryLabel(doc, b) : o));

            // Display order: host first, then by category, level, type and id.
            var sorted = order
                .OrderBy(h => h.Row.LinkName ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Row.Category ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Row.Level ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Row.Type ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Row.Ref.Id)
                .ToList();

            // Group keys for every match; rooms only when needed (they cost a point lookup).
            foreach (var h in sorted)
            {
                if (plan.GroupBy?.Kind == "room") EnsureRoom(h);
                h.Row.GroupKey = GroupKey(plan.GroupBy, h);
            }
            foreach (var h in sorted.Take(plan.Limit)) EnsureRoom(h);
            outcome.Rows = sorted.Select(h => h.Row).ToList();
            outcome.Partial = budget.Exhausted;
            outcome.Scanned = budget.Used;

            if (outcome.Rows.Count > 0)
            {
                var label = outcome.Queries.Count == 1
                    ? outcome.Rows.Count + " " + outcome.Queries[0].Subject
                    : outcome.Rows.Count + " elements from " + outcome.Queries.Count + " queries";
                outcome.Handle = ctx.Handles.Put(outcome.Rows.Select(r => r.Ref).ToList(), label);
                if (plan.Queries.Count > 1)
                    for (var i = 0; i < perQueryRefs.Count; i++)
                        if (perQueryRefs[i].Count > 0)
                            outcome.Queries[i].Handle = ctx.Handles.Put(perQueryRefs[i], outcome.Queries[i].Count + " " + outcome.Queries[i].Subject);
            }
            else if (!budget.Exhausted)
            {
                outcome.NearMatches = NearMatches(ctx, plan, links, scope, handleRefs, budget);
            }

            if ((plan.Highlight || plan.Zoom) && sorted.Count > 0)
                outcome.Highlight = Highlight(ctx, sorted, plan.Zoom, outcome);

            return FindResultBuilder.Build(plan, outcome);
        }

        // ── names ──────────────────────────────────────────────────────────

        private static (List<BuiltInCategory>, string?) ResolveCategories(Document doc, FindQuery q, ScopeDecision d)
        {
            var list = new List<BuiltInCategory>();
            if (d.ResolveCategoryText)
            {
                var bic = RevitValues.ResolveCategory(doc, q.CategoryText!, out var candidates);
                if (!bic.HasValue)
                    throw new FindFailure(ReadEnvelope.Fail(ErrorCodes.NotFound,
                        "No category '" + q.CategoryText + "' in this model.", candidates));
                list.Add(bic.Value);
                return (list, RevitValues.CategoryLabel(doc, bic.Value));
            }
            foreach (var ost in d.Categories)
                if (RevitValues.TryParseBic(ost, out var b) && !list.Contains(b)) list.Add(b);
            if (d.DefaultSet || list.Count == 0) return (list, null);
            return (list, string.Join(" + ", list.Select(b => RevitValues.CategoryLabel(doc, b))));
        }

        private static List<LinkInfo> Links(Document doc)
        {
            var list = new List<LinkInfo>();
            try
            {
                foreach (var inst in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document? linkDoc = null;
                    try { linkDoc = inst.GetLinkDocument(); } catch { }
                    list.Add(new LinkInfo
                    {
                        Instance = inst,
                        Id = inst.Id.Value,
                        Name = LinkNames.Display(SnapshotService.LinkTypeName(doc, inst)),
                        Doc = linkDoc,
                    });
                }
            }
            catch { }
            return list;
        }

        private static List<Target> Targets(Document doc, FindQuery q, List<LinkInfo> links, FindOutcome outcome, IReadOnlyList<ElementRef>? handleRefs)
        {
            var targets = new List<Target>();
            if (q.Source.Kind != FindSourceKind.Link) targets.Add(new Target { Doc = doc });
            var wanted = new List<LinkInfo>();
            if (q.Source.Kind == FindSourceKind.All) wanted.AddRange(links);
            else if (q.Source.Kind == FindSourceKind.Link)
            {
                var names = LinkNames.Match(q.Source.LinkName ?? "", links.Select(l => l.Name).Distinct());
                if (names.Count == 0)
                    throw new FindFailure(ReadEnvelope.Fail(ErrorCodes.NotFound,
                        "No Revit link named '" + q.Source.LinkName + "' in this model.",
                        Fuzzy.Rank(q.Source.LinkName, links.Select(l => l.Name), 5).Concat(links.Select(l => l.Name)).Distinct().Take(10)));
                wanted.AddRange(links.Where(l => names.Contains(l.Name)));
            }

            // Review I3: the links a handle points into are part of its scope.
            foreach (var linkId in FindScope.HandleLinks(handleRefs))
            {
                var l = links.FirstOrDefault(x => x.Id == linkId);
                var n = handleRefs!.Count(r => r.Link == linkId);
                if (l == null) { outcome.NotSearched.Add("link instance " + linkId + " (" + n + " handle elements; the link is gone)"); continue; }
                if (wanted.Contains(l)) continue;
                if (q.Source.Kind == FindSourceKind.Link)
                {
                    outcome.NotSearched.Add("link: " + l.Name + " (" + n + " handle elements outside source link:" + q.Source.LinkName + ")");
                    continue;
                }
                wanted.Add(l);
            }

            foreach (var l in wanted)
            {
                if (l.Doc == null)
                {
                    var n = handleRefs?.Count(r => r.Link == l.Id) ?? 0;
                    outcome.NotSearched.Add("link: " + l.Name + " (unloaded" + (n > 0 ? "; " + n + " handle elements" : "") + ")");
                    continue;
                }
#if REVIT2023_24
                if (q.InView)
                {
                    outcome.NotSearched.Add("link: " + l.Name + " (in_view on links needs Revit 2025 or later)");
                    continue;
                }
#endif
                targets.Add(new Target { Doc = l.Doc, Link = l.Instance, LinkName = l.Name });
            }
            return targets;
        }

        private static void AddClosedWorksets(Document doc, FindOutcome outcome)
        {
            if (!doc.IsWorkshared) return;
            try
            {
                foreach (var ws in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                    if (!ws.IsOpen) outcome.NotSearched.Add("workset: " + ws.Name + " (closed)");
            }
            catch { }
        }

        // A host-only search never silently skips links: unloaded ones are always listed;
        // on an empty result the loaded links that were not asked for are listed too.
        private static void AddLinksOutsideScope(FindPlan plan, List<LinkInfo> links, FindOutcome outcome, bool includeLoaded)
        {
            if (plan.Queries.Any(q => q.Source.Kind != FindSourceKind.Host)) return;
            foreach (var n in links.Where(l => l.Doc == null).Select(l => l.Name).Distinct())
                if (!outcome.NotSearched.Any(s => s.StartsWith("link: " + n + " (unloaded", StringComparison.Ordinal)))
                    outcome.NotSearched.Add("link: " + n + " (unloaded)");
            if (!includeLoaded) return;
            var searched = new HashSet<string>(outcome.Sources);
            var loaded = links.Where(l => l.Doc != null && !searched.Contains("link: " + l.Name)).Select(l => l.Name).Distinct().ToList();
            foreach (var n in loaded.Take(5)) outcome.NotSearched.Add("link: " + n + " (not requested; source:'all' searches it)");
            if (loaded.Count > 5) outcome.NotSearched.Add("links: " + (loaded.Count - 5) + " more loaded links (not requested)");
        }

        private static IEnumerable<string> LevelNamesOf(Document d) =>
            new FilteredElementCollector(d).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Select(l => l.Name);

        /// <summary>Null when the query has no level; empty when this document lacks it.</summary>
        private static List<ElementId>? LevelIds(Target t, FindQuery q, List<string> resolvedNames)
        {
            var want = q.Level ?? q.LevelHint;
            if (want == null) return null;
            var levels = new FilteredElementCollector(t.Doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            var exact = levels.Where(l => string.Equals(l.Name, want.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            var chosen = exact.Count > 0 ? exact : LevelMatcher.Match(want, levels.Select(l => l.Name)).Select(n => levels.First(l => l.Name == n)).ToList();
            resolvedNames.AddRange(chosen.Select(l => l.Name));
            return chosen.Select(l => l.Id).ToList();
        }

        // ── collection ─────────────────────────────────────────────────────

        private static List<(Element, FoundRow)> Collect(ToolContext ctx, Target t, FindQuery q, List<BuiltInCategory> bics,
            ScopeDecision decision, HashSet<(long, long?)>? scope, List<ElementId>? levelIds, Budget budget, bool strictNames, FindOutcome? outcome)
        {
            var result = new List<(Element, FoundRow)>();
            if (levelIds != null && levelIds.Count == 0) return result;
            if (bics.Count == 0 && scope == null) return result;   // never an unfiltered collector

            FilteredElementCollector col;
            try
            {
                if (q.InView)
                {
                    var view = ctx.UiDoc.ActiveView ?? ctx.Doc.ActiveView;
                    if (view == null) return result;
#if REVIT2023_24
                    if (t.Link != null) return result;
                    col = new FilteredElementCollector(ctx.Doc, view.Id);
#else
                    col = t.Link == null ? new FilteredElementCollector(ctx.Doc, view.Id) : new FilteredElementCollector(ctx.Doc, view.Id, t.Link.Id);
#endif
                }
                else col = new FilteredElementCollector(t.Doc);
            }
            catch (Exception ex)
            {
                outcome?.Notes.Add("in_view could not be applied to the active view (" + ex.Message + ").");
                return result;
            }

            col = col.WhereElementIsNotElementType();
            if (bics.Count == 1) col = col.OfCategory(bics[0]);
            else if (bics.Count > 1) col = col.WherePasses(new ElementMulticategoryFilter(bics));

            long? linkId = t.Link?.Id.Value;
            if (scope != null)
            {
                var ids = scope.Where(s => s.Item2 == linkId).Select(s => ElemIds.From(s.Item1)).ToList();
                if (ids.Count == 0) return result;
                col = col.WherePasses(new ElementIdSetFilter(ids));
            }

            var levelSet = levelIds == null ? null : new HashSet<long>(levelIds.Select(i => (long)i.Value));
            var p = q.Parameter;
            int lacking = 0, seen = 0;
            Element? sample = null;

            foreach (var el in col)
            {
                if (!budget.Take()) break;
                if (el is ElementType) continue;
                if (levelSet != null && !levelSet.Contains(RevitValues.LevelIdOf(el).Value)) continue;

                string? typeName = null, family = null;
                if (q.Type != null)
                {
                    typeName = RevitValues.TypeName(t.Doc, el);
                    family = RevitValues.FamilyName(t.Doc, el);
                    if (!NameMatches(typeName, q.Type) && !NameMatches(family, q.Type)) continue;
                }

                string? paramValue = null;
                if (p != null)
                {
                    seen++;
                    sample ??= el;
                    var param = el.LookupParameter(p.Name) ?? RevitValues.TypeOf(t.Doc, el)?.LookupParameter(p.Name);
                    if (param == null) { lacking++; continue; }
                    var (display, _, unit) = RevitValues.ParamValue(t.Doc, param);
                    if (!p.Evaluate(display, unit)) continue;
                    paramValue = display;
                }

                string? room = null;
                if (q.Room != null)
                {
                    room = RoomLabel(t.Doc, el);
                    if (room == null || !Fuzzy.Fold(room).Contains(Fuzzy.Fold(q.Room))) continue;
                }

                if (q.Terms.Count > 0)
                {
                    typeName ??= RevitValues.TypeName(t.Doc, el);
                    family ??= RevitValues.FamilyName(t.Doc, el);
                    var fields = TextFields(t.Doc, el, typeName, family);
                    if (!TextMatch.Matches(q.Terms, fields))
                    {
                        // The room fallback only when the query has a category or a room (review C1).
                        if (!decision.RoomFallback) continue;
                        room ??= RoomLabel(t.Doc, el);
                        if (room == null || !TextMatch.Matches(q.Terms, fields.Append(room))) continue;
                    }
                }

                var row = RowOf(t, el, typeName, family, room);
                if (p != null) row.Fields[p.Name] = paramValue;
                result.Add((el, row));
            }

            if (p != null && strictNames && result.Count == 0 && seen > 0 && lacking == seen && t.Link == null && sample != null)
            {
                List<string> names;
                try { names = Inspectors.SuggestParamNames(t.Doc, sample, p.Name); } catch { names = new List<string>(); }
                throw new FindFailure(ReadEnvelope.Fail(ErrorCodes.NotFound,
                    "No parameter '" + p.Name + "' on the elements searched (instance or type).", names));
            }
            return result;
        }

        private static bool NameMatches(string? actual, string want)
        {
            if (string.IsNullOrWhiteSpace(actual)) return false;
            var a = Fuzzy.Fold(actual);
            var w = Fuzzy.Fold(want);
            if (w.Length == 0) return false;
            return a.Contains(w) || a.Replace(" ", "").Contains(w.Replace(" ", "")) || (a.Length > 0 && w.Contains(a));
        }

        private static List<string?> TextFields(Document doc, Element el, string? typeName, string? family)
        {
            var type = RevitValues.TypeOf(doc, el);
            string? S(Element? e, BuiltInParameter bip)
            {
                try { return e?.get_Parameter(bip)?.AsString(); } catch { return null; }
            }
            return new List<string?>
            {
                RevitValues.ElementName(el),
                typeName,
                family,
                S(el, BuiltInParameter.ALL_MODEL_MARK),
                S(type, BuiltInParameter.ALL_MODEL_TYPE_MARK),
                S(el, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS),
                S(type, BuiltInParameter.ALL_MODEL_DESCRIPTION),
                el.Category?.Name,
                el is SpatialElement sp ? sp.Number : null,
            };
        }

        private static FoundRow RowOf(Target t, Element el, string? typeName, string? family, string? room)
        {
            string? mark = null;
            try { mark = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(); } catch { }
            return new FoundRow
            {
                Ref = new ElementRef(el.Id.Value, t.Link?.Id.Value),
                Name = RevitValues.ElementName(el),
                Category = el.Category?.Name,
                Type = typeName ?? RevitValues.TypeName(t.Doc, el),
                Family = family ?? RevitValues.FamilyName(t.Doc, el),
                Level = RevitValues.LevelName(t.Doc, el),
                Mark = mark,
                Room = room,
                LinkName = t.Link == null ? null : t.LinkName,
            };
        }

        private static void EnsureRoom(Hit h)
        {
            if (h.RoomDone) return;
            h.RoomDone = true;
            h.Row.Room ??= RoomLabel(h.Target.Doc, h.El);
        }

        /// <summary>"2.03 Tandas Lelaki": the room an element is in (its own for rooms).</summary>
        internal static string? RoomLabel(Document doc, Element el)
        {
            try
            {
                SpatialElement? room = el as SpatialElement;
                if (room == null && el is FamilyInstance fi)
                    room = fi.Room ?? fi.ToRoom ?? fi.FromRoom;
                if (room == null)
                {
                    XYZ? pt = el.Location switch
                    {
                        LocationPoint lp => lp.Point,
                        LocationCurve lc => lc.Curve.Evaluate(0.5, true),
                        _ => null,
                    };
                    if (pt == null)
                    {
                        var bb = el.get_BoundingBox(null);
                        if (bb != null) pt = (bb.Min + bb.Max) / 2;
                    }
                    if (pt != null) room = doc.GetRoomAtPoint(pt);
                }
                if (room == null) return null;
                var name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                var number = room.Number ?? "";
                var label = (number + " " + name).Trim();
                return label.Length == 0 ? null : label;
            }
            catch { return null; }
        }

        private static string? GroupKey(GroupBySpec? g, Hit h)
        {
            if (g == null) return h.Row.Type;
            switch (g.Kind)
            {
                case "level": return h.Row.Level ?? "(no level)";
                case "type": return h.Row.Type ?? "(no type)";
                case "category": return h.Row.Category ?? "(no category)";
                case "room": return h.Row.Room ?? "(no room)";
                case "param":
                    try { return RevitValues.ParamDisplay(h.Target.Doc, h.El, g.Param!) ?? "(empty)"; }
                    catch { return "(unreadable)"; }
                default: return null;
            }
        }

        // ── near matches ───────────────────────────────────────────────────

        private static List<string> NearMatches(ToolContext ctx, FindPlan plan, List<LinkInfo> links,
            HashSet<(long, long?)>? scope, IReadOnlyList<ElementRef>? handleRefs, Budget budget)
        {
            var near = new List<string>();
            var q = plan.Queries.FirstOrDefault();
            if (q == null) return near;
            var hasLoadedLinks = links.Any(l => l.Doc != null);
            foreach (var step in NearMatchPlanner.Relaxations(q, hasLoadedLinks).Take(NearMatchSteps))
            {
                if (near.Count >= MaxNearMatches || budget.Exhausted) break;
                if (step.Query.IsEmpty) continue;
                try
                {
                    var decision = FindScope.Decide(step.Query, scope != null);
                    var (bics, _) = ResolveCategories(ctx.Doc, step.Query, decision);
                    var scratch = new FindOutcome();
                    var found = new List<(Element, FoundRow)>();
                    foreach (var t in Targets(ctx.Doc, step.Query, links, scratch, handleRefs))
                        found.AddRange(Collect(ctx, t, step.Query, bics, decision, scope, LevelIds(t, step.Query, new List<string>()),
                            budget, strictNames: false, null));
                    foreach (var g in found
                        .GroupBy(f => (Label: f.Item2.Type ?? f.Item2.Category ?? "elements", Level: step.Dropped == "source" ? null : f.Item2.Level, f.Item2.LinkName))
                        .OrderByDescending(g => g.Count())
                        .Take(2))
                        near.Add(NearMatchPlanner.Format(g.Key.Label, g.Count(), g.Key.Level, g.Key.LinkName));
                }
                catch { /* a near match is a hint; never fail the call for it */ }
            }
            return near.Distinct().Take(MaxNearMatches).ToList();
        }

        // ── highlight / zoom (no transaction) ─────────────────────────────

        private static HighlightDto Highlight(ToolContext ctx, List<Hit> hits, bool zoom, FindOutcome outcome)
        {
            var uidoc = ctx.UiDoc;
            var doc = ctx.Doc;
            var hostIds = hits.Where(h => h.Target.Link == null).Select(h => h.El.Id).ToList();
            var linked = hits.Where(h => h.Target.Link != null).ToList();
            int selected = 0, linkedSelected = 0;
            try
            {
                if (linked.Count > 0)
                {
                    var refs = new List<Reference>();
                    foreach (var h in hits)
                    {
                        try
                        {
                            var r = new Reference(h.El);
                            refs.Add(h.Target.Link == null ? r : r.CreateLinkReference(h.Target.Link));
                            if (h.Target.Link != null) linkedSelected++;
                        }
                        catch { }
                    }
                    uidoc.Selection.SetReferences(refs);
                    selected = refs.Count;
                }
                else
                {
                    uidoc.Selection.SetElementIds(hostIds);
                    selected = hostIds.Count;
                }
            }
            catch
            {
                linkedSelected = 0;
                try { uidoc.Selection.SetElementIds(hostIds); selected = hostIds.Count; } catch { selected = 0; }
                if (linked.Count > 0) outcome.Notes.Add("Linked elements could not be selected; only host elements are selected.");
            }

            int visible = 0;
            var view = uidoc.ActiveView;
            bool graphical = view != null && !(view is ViewSchedule) && !(view is ViewSheet) && !view.IsTemplate;
            if (graphical && hostIds.Count > 0)
            {
                try { visible += new FilteredElementCollector(doc, view!.Id).WherePasses(new ElementIdSetFilter(hostIds)).GetElementCount(); }
                catch { }
            }
#if !REVIT2023_24
            if (graphical)
                foreach (var g in linked.GroupBy(h => h.Target.Link!.Id))
                {
                    try
                    {
                        var ids = g.Select(h => h.El.Id).ToList();
                        visible += new FilteredElementCollector(doc, view!.Id, g.Key).WherePasses(new ElementIdSetFilter(ids)).GetElementCount();
                    }
                    catch { }
                }
#else
            // Review M3: cannot be checked on 2023/2024; reported as unknown, never as not visible.
            if (graphical) outcome.HighlightUnknown = linkedSelected;
#endif
            if (!graphical) outcome.Notes.Add("The active view is not a model view; open a plan, section or 3D view to see the selection.");

            if (zoom && graphical) Zoom(uidoc, view!, hits, outcome);
            return new HighlightDto { Selected = selected, VisibleInView = Math.Min(visible, selected) };
        }

        private static void Zoom(UIDocument uidoc, View view, List<Hit> hits, FindOutcome outcome)
        {
            try
            {
                XYZ? min = null, max = null;
                foreach (var h in hits)
                {
                    var bb = h.El.get_BoundingBox(null);
                    if (bb == null) continue;
                    var tf = h.Target.Link?.GetTotalTransform();
                    foreach (var p in new[] { bb.Min, bb.Max })
                    {
                        var q = tf == null ? p : tf.OfPoint(p);
                        min = min == null ? q : new XYZ(Math.Min(min.X, q.X), Math.Min(min.Y, q.Y), Math.Min(min.Z, q.Z));
                        max = max == null ? q : new XYZ(Math.Max(max.X, q.X), Math.Max(max.Y, q.Y), Math.Max(max.Z, q.Z));
                    }
                }
                if (min == null || max == null) return;
                var pad = (max - min) * 0.1 + new XYZ(1, 1, 1);
                var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                ui?.ZoomAndCenterRectangle(min - pad, max + pad);
            }
            catch (Exception ex)
            {
                outcome.Notes.Add("Zoom failed: " + ex.Message);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Builds the @-mention tree from the live Revit document, mirroring the
    /// Project Browser: Levels, Views (grouped by view type), Sheets, and
    /// Families as Category → Family → Type. Called once per picker open on
    /// Revit's thread (the pane's own; MentionInput caches while the popup is up).
    /// Reads only: no transaction, no model change.
    ///
    /// Each type carries its family, category, Type Mark and how many instances
    /// the model and the active view hold. The counts are one pass over the
    /// model's instances of the listed categories under a time budget
    /// (<see cref="ModelCountBudgetMs"/>, <see cref="ViewCountBudgetMs"/>); a pass
    /// that runs out is thrown away (no partial, wrong numbers) and the document
    /// is not counted again while it stays open, so a huge model pays the budget
    /// once, not on every "@".
    ///
    /// Everything below the selection is kept (MentionTreeCache) and reused on the next "@" while its stamp holds: same document
    /// session, no model event since (SnapshotService.ModelEventSeq), same active view, same open worksets, no temporary view mode.
    /// The selection is read every time.
    /// </summary>
    public class RevitMentionProvider : IMentionProvider, IMentionSelectionSource
    {
        private readonly Func<UIApplication> _getApp;
        private static readonly IMentionProvider _fallback = new StaticMentionProvider();

        public const int ModelCountBudgetMs = 250;
        public const int ViewCountBudgetMs = 120;
        // Documents whose model-wide count ran past its budget (by path, else title).
        private static readonly HashSet<string> _tooBigToCount = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _viewTooBigToCount = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The last document tree (everything but the selection), reused while nothing in the model changed (TreeStamp). Revit's thread only.
        private static readonly MentionTreeCache _trees = new MentionTreeCache();

        public RevitMentionProvider(Func<UIApplication> getApp) => _getApp = getApp;

        public List<MentionNode> GetTree()
        {
            try
            {
                var uidoc = _getApp()?.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return _fallback.GetTree();

                var roots = new List<MentionNode>();

                // The selection is read on every "@": selecting raises no model event, so it is never part of the kept tree.
                var sel = ReadSelection(uidoc);
                if (sel != null && sel.Total > 0)
                    roots.Add(new MentionNode("selection", "Current selection · " + MentionText.SelectionLabel(sel)) { Selection = sel });

                var active = doc.ActiveView;
                roots.AddRange(_trees.Get(TreeStamp(doc, active), () =>
                {
                    int tooBig = _tooBigToCount.Count, viewTooBig = _viewTooBigToCount.Count;
                    var built = BuildDocTree(doc, active);
                    // A count pass that finished but ran past its budget counts now and is skipped from the next build on: keeping this
                    // build would show counts a fresh build no longer would, so it is not kept.
                    return (built, _tooBigToCount.Count == tooBig && _viewTooBigToCount.Count == viewTooBig);
                }));
                MentionTreeDump.Write(roots);
                return roots;
            }
            catch
            {
                return _fallback.GetTree();
            }
        }

        /// <summary>What the kept tree depends on besides the model's content (MentionTreeCache.Stamp): the document's session (a reopen
        /// is a new one), every model event of the session (SnapshotService.ModelEventSeq), the active view (its root, IsActive flags and
        /// InView counts), the open worksets, the active design option and the current browser organizations. Null, so nothing is reused
        /// or kept, when the events are not hooked, the stamp cannot be read, or the active view is in a temporary mode (hide/isolate,
        /// reveal hidden ...): those change what the view shows without a model event.</summary>
        private static string TreeStamp(Document doc, View active)
        {
            try
            {
                var worksets = doc.IsWorkshared
                    ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Where(w => w.IsOpen).Select(w => w.Id.IntegerValue).ToList()
                    : new List<int>();
                long? Id(Func<ElementId> read)
                {
                    try { return read()?.Value; }
                    catch { return null; }
                }
                var settings = new[]
                {
                    Id(() => DesignOption.GetActiveDesignOptionId(doc)),
                    Id(() => BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc)?.Id),
                    Id(() => BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc)?.Id),
                    Id(() => BrowserOrganization.GetCurrentBrowserOrganizationForSchedules(doc)?.Id),
                    Id(() => PanelScheduleOrganization(doc)?.Id),
                };
                return MentionTreeCache.Stamp(BinaVibe.Copilot.Read.SnapshotService.IsAttached, active != null && InTemporaryMode(active),
                    BinaVibe.Copilot.Read.SnapshotService.SessionNonceOf(doc), BinaVibe.Copilot.Read.SnapshotService.ModelEventSeq,
                    active?.Id.Value, worksets, settings);
            }
            catch { return null; }
        }

        private static bool InTemporaryMode(View v)
        {
            try
            {
                if (v.IsTemporaryViewPropertiesModeEnabled()) return true;
                foreach (TemporaryViewMode mode in Enum.GetValues(typeof(TemporaryViewMode)))
                    if (v.IsInTemporaryViewMode(mode)) return true;
                return false;
            }
            catch { return true; }   // cannot tell: do not reuse
        }

        // Everything the picker shows below the selection, read from the document. Throws on a failure (GetTree falls back, nothing kept).
        private static List<MentionNode> BuildDocTree(Document doc, View active)
        {
            var roots = new List<MentionNode>();
            var activeName = active?.Name;
            // The Project Browser pseudo-view ("Project View") is the active view after a click in the browser: not a view to mention.
            if (!string.IsNullOrEmpty(activeName) && (active is ViewSheet || BrowserViews.Place(active.ViewType.ToString()) != null))
                roots.Add(new MentionNode("view", activeName) { IsActive = true, ElementId = active.Id.Value });

            roots.Add(MentionNode.Group("Levels",
                new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => new MentionNode("level", l.Name) { ElementId = l.Id.Value })));

            // Only what the Project Browser lists (BrowserViews), in its branches and its order.
            var placed = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && !(v is ViewSheet) && !IsHiddenSchedule(v))
                .Select(v => (View: v, Place: BrowserViews.Place(v.ViewType.ToString())))
                .Where(x => x.Place != null).Select(x => (x.View, Place: x.Place.Value)).ToList();
            MentionNode ViewNode(IGrouping<string, View> n) =>
                new MentionNode("view", n.Key) { IsActive = n.Key == activeName, ElementId = n.Count() == 1 ? n.First().Id.Value : (long?)null };
            IEnumerable<MentionNode> ViewsIn(IEnumerable<(View View, (string Branch, string Group, int Rank) Place)> rows) =>
                rows.Select(x => x.View).GroupBy(v => v.Name).OrderBy(n => n.Key, StringComparer.CurrentCultureIgnoreCase).Select(ViewNode);

            // Views and sheets follow the project's own Browser Organization: only what its filters let through, in its folders.
            // No organization (or a failure reading it): the view-type groups, the browser's default.
            var viewOrg = Organization(() => BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc));
            var viewRows = viewOrg == null ? null : OrganizedRows(viewOrg,
                placed.Where(x => x.Place.Branch == BrowserViews.ViewsBranch).Select(x => x.View).ToList(),
                v => new MentionNode("view", v.Name) { IsActive = v.Id == active?.Id, ElementId = v.Id.Value });
            roots.Add(MentionNode.Group(Labelled(BrowserViews.ViewsBranch, viewOrg), viewRows != null ? BrowserTree.Nest(viewRows) :
                placed.Where(x => x.Place.Branch == BrowserViews.ViewsBranch)
                    .GroupBy(x => (x.Place.Group, x.Place.Rank)).OrderBy(g => g.Key.Rank)
                    .Select(g => MentionNode.Group(g.Key.Group, ViewsIn(g))).ToList()));
            roots.Add(MentionNode.Group(BrowserViews.LegendsBranch, ViewsIn(placed.Where(x => x.Place.Branch == BrowserViews.LegendsBranch))));
            roots.Add(ScheduleBranch(BrowserViews.SchedulesBranch, placed, activeName, () => BrowserOrganization.GetCurrentBrowserOrganizationForSchedules(doc)));
            roots.Add(ScheduleBranch(BrowserViews.PanelSchedulesBranch, placed, activeName, () => PanelScheduleOrganization(doc)));

            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !IsPlaceholder(s)).ToList();
            MentionNode SheetNode(ViewSheet s) => new MentionNode("sheet", $"{s.SheetNumber} - {s.Name}") { ElementId = s.Id.Value };
            var sheetOrg = Organization(() => BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc));
            var sheetRows = sheetOrg == null ? null : OrganizedRows(sheetOrg, sheets, SheetNode);
            roots.Add(MentionNode.Group(Labelled("Sheets", sheetOrg), sheetRows != null ? BrowserTree.Nest(sheetRows)
                : sheets.OrderBy(s => s.SheetNumber).Select(SheetNode).ToList()));

            var types = new FilteredElementCollector(doc).WhereElementIsElementType()
                .Cast<ElementType>()
                .Where(t => t.Category != null && !string.IsNullOrEmpty(t.FamilyName)
                            && BrowserFamilies.Listed(CategoryKey(t.Category) ?? "", t.Category.CategoryType.ToString(), t.GetType().Name))
                .ToList();

            if (MentionTreeDump.Enabled) DumpSkipped(doc, types);
            var catIds = types.Where(t => t.Category.CategoryType == CategoryType.Model).Select(t => t.Category.Id).Distinct().ToList();   // instance counts are for model categories
            string docKey = string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
            var inModel = CountByType(doc, null, catIds, docKey, _tooBigToCount, ModelCountBudgetMs);
            var inView = active != null && IsCountableView(active)
                ? CountByType(doc, active, catIds, docKey + "|" + active.Id, _viewTooBigToCount, ViewCountBudgetMs)
                : null;

            roots.Add(MentionNode.Group("Families",
                types.GroupBy(t => BrowserFamilies.CategoryLabel(t.Category.Name, t.Category.CategoryType.ToString())).OrderBy(g => g.Key)
                    .Select(g =>
                    {
                        string catKey = g.Key == BrowserFamilies.AnnotationLabel ? null : CategoryKey(g.First().Category);
                        return new MentionNode("category", g.Key,
                            g.GroupBy(t => t.FamilyName).OrderBy(f => f.Key)
                                .Select(f => new MentionNode("family", f.Key,
                                    f.GroupBy(t => t.Name).OrderBy(n => n.Key)
                                        .Select(n => TypeNode(n.Key, f.Key, g.Key, catKey, n.ToList(), inModel, inView)),
                                    pickable: true) { Category = g.Key, CategoryKey = catKey }),
                            pickable: true) { CategoryKey = catKey };
                    })));

            roots.RemoveAll(r => !r.Pickable && r.Children.Count == 0);
            return roots;
        }

        // One node per (family, type name); a name Revit holds twice in one
        // family (rare) sums its counts.
        private static MentionNode TypeNode(string name, string family, string category, string catKey,
                                            List<ElementType> same, Dictionary<ElementId, int> inModel, Dictionary<ElementId, int> inView)
        {
            string mark = null;
            try { mark = same[0].get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString(); }
            catch { /* no Type Mark on this type */ }
            return new MentionNode("type", name)
            {
                ClassName = same[0].GetType().Name, Family = family, Category = category, CategoryKey = catKey,
                TypeMark = string.IsNullOrWhiteSpace(mark) ? null : mark.Trim(),
                Count = inModel == null ? (int?)null : same.Sum(t => inModel.TryGetValue(t.Id, out var c) ? c : 0),
                InView = inView == null ? (int?)null : same.Sum(t => inView.TryGetValue(t.Id, out var c) ? c : 0),
            };
        }

        private static string CategoryKey(Category c)
        {
            try { return c.BuiltInCategory == BuiltInCategory.INVALID ? null : c.BuiltInCategory.ToString(); }
            catch { return null; }
        }

        private static bool IsCountableView(View v) =>
            !v.IsTemplate && !(v is ViewSheet) && !(v is ViewSchedule)
            && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser
            && v.ViewType != ViewType.Internal && v.ViewType != ViewType.Undefined;

        /// <summary>
        /// Instances per type id among the given categories, model-wide (view null)
        /// or in a view. Null when the pass ran past <paramref name="budgetMs"/>
        /// (the key then goes into <paramref name="tooBig"/> so later openings skip
        /// it) or failed. The time check runs every 512 elements.
        /// </summary>
        private static Dictionary<ElementId, int> CountByType(Document doc, View view, ICollection<ElementId> catIds,
                                                              string key, HashSet<string> tooBig, int budgetMs)
        {
            if (catIds == null || catIds.Count == 0 || tooBig.Contains(key)) return null;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var collector = view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);
                collector.WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(catIds));
                var counts = new Dictionary<ElementId, int>();
                int n = 0;
                foreach (Element e in collector)
                {
                    var tid = e?.GetTypeId();
                    if (tid != null && tid != ElementId.InvalidElementId)
                        counts[tid] = counts.TryGetValue(tid, out var c) ? c + 1 : 1;
                    if ((++n & 511) == 0 && sw.ElapsedMilliseconds > budgetMs)
                    {
                        tooBig.Add(key);
                        return null;
                    }
                }
                if (sw.ElapsedMilliseconds > budgetMs) tooBig.Add(key);   // done, but next time skip
                return counts;
            }
            catch
            {
                return null;
            }
        }

        public MentionSelection ReadSelection()
        {
            try { return ReadSelection(_getApp()?.ActiveUIDocument); }
            catch { return null; }
        }

        /// <summary>The selection grouped by category, family and type. Elements
        /// without a type (rooms, areas) group by category only.</summary>
        private static MentionSelection ReadSelection(UIDocument uidoc)
        {
            var doc = uidoc?.Document;
            if (doc == null) return null;
            var groups = new Dictionary<string, MentionSelectionGroup>(StringComparer.Ordinal);
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var e = doc.GetElement(id);
                if (e == null) continue;
                string cat = e.Category?.Name ?? "";
                string fam = "", type = "";
                var tid = e.GetTypeId();
                if (tid != null && tid != ElementId.InvalidElementId && doc.GetElement(tid) is ElementType et)
                {
                    fam = et.FamilyName ?? "";
                    type = et.Name ?? "";
                }
                string k = cat + "\u001f" + fam + "\u001f" + type;
                if (!groups.TryGetValue(k, out var g)) groups[k] = g = new MentionSelectionGroup(cat, fam, type, 0);
                g.Count++;
            }
            return new MentionSelection(groups.Values);
        }

        // One schedule branch: the browser organization's filters and folders when it can be read, else the plain name list.
        private static MentionNode ScheduleBranch(string branch, List<(View View, (string Branch, string Group, int Rank) Place)> placed, string activeName,
                                                  Func<BrowserOrganization> getOrg)
        {
            var views = placed.Where(x => x.Place.Branch == branch).Select(x => x.View).ToList();
            MentionNode Node(View v) => new MentionNode("view", v.Name) { IsActive = v.Name == activeName, ElementId = v.Id.Value };
            var org = Organization(getOrg);
            var rows = org == null ? null : OrganizedRows(org, views, Node);
            return MentionNode.Group(Labelled(branch, org), rows != null ? BrowserTree.Nest(rows)
                : views.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).Select(Node).ToList());
        }

        // Test aid (BINA_MENTION_DUMP set): the element types the picker leaves out, by category, category type and class.
        private static void DumpSkipped(Document doc, List<ElementType> kept)
        {
            try
            {
                var keptIds = new HashSet<ElementId>(kept.Select(t => t.Id));
                var rows = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                    .Where(t => !keptIds.Contains(t.Id))
                    .GroupBy(t => new { Cat = t.Category?.Name ?? "(none)", Kind = t.Category?.CategoryType.ToString() ?? "-", Cls = t.GetType().Name, HasFamily = !string.IsNullOrEmpty(t.FamilyName), Key = CategoryKey(t.Category) ?? "" })
                    .Select(g => new Newtonsoft.Json.Linq.JObject { ["category"] = g.Key.Cat, ["categoryType"] = g.Key.Kind, ["class"] = g.Key.Cls, ["hasFamilyName"] = g.Key.HasFamily, ["categoryKey"] = g.Key.Key, ["count"] = g.Count() });
                MentionTreeDump.WriteExtra(".skipped.json", new Newtonsoft.Json.Linq.JArray(rows));
            }
            catch { /* a test aid never fails the picker */ }
        }

        // GetCurrentBrowserOrganizationForPanelSchedules is not in every Revit year's API (it is missing from 2023-2026), so it is called by name:
        // null where it does not exist, and the panel schedules are then listed by name with no folders.
        private static BrowserOrganization PanelScheduleOrganization(Document doc)
        {
            var m = typeof(BrowserOrganization).GetMethod("GetCurrentBrowserOrganizationForPanelSchedules", new[] { typeof(Document) });
            return m?.Invoke(null, new object[] { doc }) as BrowserOrganization;
        }

        // The browser shows its organization's name after the branch: "Views (00a_All)", "Sheets (00a_All)", "Schedules/Quantities (all)".
        private static string Labelled(string branch, BrowserOrganization org)
        {
            try { return org == null || string.IsNullOrWhiteSpace(org.Name) ? branch : branch + " (" + org.Name + ")"; }
            catch { return branch; }
        }

        private static BrowserOrganization Organization(Func<BrowserOrganization> get)
        {
            try { return get(); }
            catch { return null; }
        }

        // Each element the browser's filters let through, with its folders (outermost first). Null when the organization cannot be read,
        // so the caller falls back to the default grouping.
        private static List<(IList<string> Path, MentionNode Leaf)> OrganizedRows<T>(BrowserOrganization org, IEnumerable<T> items, Func<T, MentionNode> node)
            where T : Element
        {
            try
            {
                var rows = new List<(IList<string>, MentionNode)>();
                foreach (var item in items)
                {
                    if (!org.AreFiltersSatisfied(item.Id)) continue;
                    var path = org.GetFolderItems(item.Id).Select(f => f.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
                    rows.Add((path, node(item)));
                }
                return rows;
            }
            catch { return null; }
        }

        // The browser does not list the title block's revision schedule or the internal keynote schedule.
        private static bool IsHiddenSchedule(View v)
        {
            try { return v is ViewSchedule vs && (vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule); }
            catch { return false; }
        }

        private static bool IsPlaceholder(ViewSheet s)
        {
            try { return s.IsPlaceholder; }
            catch { return false; }
        }
    }
}

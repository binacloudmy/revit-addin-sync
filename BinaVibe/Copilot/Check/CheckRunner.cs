#nullable enable
// Runs a parsed rule set against the open model, one RuleRun per rule in
// scope. Per rule: a rule this build cannot run (R22, R28) is one
// not_verified outcome; otherwise pick the sources (host, every link, or the
// scope's link), turn every coverage gap (an unloaded link, a closed workset
// in the host or a loaded link) into not_verified outcomes, collect the
// selector's categories, read ElementValues, apply the scope and selector
// (RuleSelection: undecided elements are never dropped), then evaluate the
// kind (GenericAssertions or RevitGeometricKinds).
// Read-only: no transactions, nothing selected or changed. A rule that
// throws becomes one not_verified outcome; the other rules still run.
// Revit-bound.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    internal sealed class CheckRunner
    {
        private readonly Document _doc;
        private readonly UIDocument? _uidoc;
        private readonly CheckScope _scope;
        private readonly CheckSource _host;
        private readonly List<CheckSource> _links;
        private readonly HashSet<long>? _selection;
        private readonly Dictionary<string, BuiltInCategory?> _categories = new Dictionary<string, BuiltInCategory?>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Source, BuiltInCategory Category), IList<Element>> _collected = new Dictionary<(string, BuiltInCategory), IList<Element>>();

        /// <summary>What was searched, for the envelope (in first-seen order).</summary>
        public List<string> Sources { get; } = new List<string>();
        public List<string> Categories { get; } = new List<string>();
        public List<string> NotSearched { get; } = new List<string>();
        public List<SkippedRule> Skipped { get; } = new List<SkippedRule>();

        public CheckRunner(Document doc, UIDocument? uidoc, CheckScope scope)
        {
            _doc = doc;
            _uidoc = uidoc;
            _scope = scope;
            _host = RevitCheckSources.Host(doc);
            _links = RevitCheckSources.Links(doc);
            if (scope.Selection)
                _selection = new HashSet<long>((uidoc?.Selection.GetElementIds() ?? new List<ElementId>()).Select(id => (long)id.Value));
        }

        /// <param name="unsupported">Rules this build cannot run: rule id → reason (LoadedRuleSet).</param>
        public List<RuleRun> Run(RuleSet set, IReadOnlyDictionary<string, string> unsupported)
        {
            var runs = new List<RuleRun>();
            foreach (var rule in set.Rules)
            {
                if (!_scope.Applies(rule, out var why))
                {
                    Skipped.Add(new SkippedRule(rule.Id, why ?? "outside the scope"));
                    continue;
                }
                if (unsupported.TryGetValue(rule.Id, out var cannotRun))
                {
                    runs.Add(RuleOutcomes.Unsupported(rule, cannotRun));
                    continue;
                }
                var run = new RuleRun(rule);
                try
                {
                    RunRule(run);
                }
                catch (Exception ex)
                {
                    run.AddDocument(Outcome.NotVerified($"the rule could not run: {ex.GetType().Name}: {ex.Message}", null, OutcomeCodes.EngineError));
                }
                if (run.SkippedBecause != null && run.Results.Count == 0) Skipped.Add(new SkippedRule(rule.Id, run.SkippedBecause));
                else runs.Add(run);
            }
            return runs;
        }

        private void RunRule(RuleRun run)
        {
            var rule = run.Rule;
            var a = rule.Assertion;

            if (CheckScope.IsDocumentWide(a.Kind))
            {
                Seen(Sources, _host.Label);
                foreach (var gap in _host.ClosedWorksets)
                {
                    run.AddDocument(Outcome.NotVerified(gap, null, OutcomeCodes.SourceUnavailable));
                    Seen(NotSearched, gap);
                }
                if (a.Kind == AssertionKinds.WarningsGroup) RevitGeometricKinds.Warnings(run, _doc);
                else RevitGeometricKinds.ImportedCad(run, _doc);
                return;
            }

            // ---- sources -------------------------------------------------
            if (!TrySources(rule, out var sources, out var sourceProblem))
            {
                run.AddDocument(Outcome.NotVerified(sourceProblem!, null, OutcomeCodes.SourceUnavailable));
                return;
            }
            var gaps = new List<string>();
            foreach (var src in sources.Where(s => s.Doc == null))
            {
                var reason = src.UnavailableReason ?? $"{src.Label} could not be read";
                gaps.Add(reason);
                Seen(NotSearched, reason);
            }
            foreach (var gap in sources.Where(s => s.Doc != null).SelectMany(s => s.ClosedWorksets))
            {
                gaps.Add(gap);
                Seen(NotSearched, gap);
            }

            // ---- categories ----------------------------------------------
            var names = rule.Selector.Categories
                .Where(c => _scope.Category == null || CheckScope.SameCategory(c, _scope.Category))
                .ToList();
            if (names.Count == 0)
            {
                run.AddDocument(Outcome.NotVerified("the rule selects no categories", null, OutcomeCodes.NotApplicable));
                return;
            }
            var bics = new List<BuiltInCategory>();
            var setKind = GenericAssertions.IsSetKind(a);
            foreach (var name in names)
            {
                var bic = Resolve(name);
                if (bic != null)
                {
                    bics.Add(bic.Value);
                    Seen(Categories, name);
                    continue;
                }
                var unknownCategory = $"'{name}' is not a Revit category in this model";
                // A set count over a category it cannot read is incomplete, not smaller.
                if (setKind) gaps.Add(unknownCategory);
                else run.AddDocument(Outcome.NotVerified(unknownCategory, null, OutcomeCodes.NotApplicable));
            }

            // ---- tags need a view ---------------------------------------
            View? view = null;
            if (a.Kind == AssertionKinds.TaggedInView)
            {
                view = _uidoc?.ActiveView ?? _doc.ActiveView;
                var notTaggable = RevitGeometricKinds.UntaggableViewReason(view);
                if (notTaggable != null)
                {
                    run.AddDocument(Outcome.NotVerified(notTaggable, null, OutcomeCodes.NotApplicable));
                    return;
                }
                // Linked elements are not tag-checked; an unloaded link keeps its own reason.
                foreach (var link in sources.Where(s => !s.IsHost))
                    run.Add(link.InstanceRef, link.Doc == null
                        ? Outcome.NotVerified(link.UnavailableReason ?? $"{link.Label} could not be read", null, OutcomeCodes.SourceUnavailable)
                        : Outcome.NotVerified($"tags on elements of {link.Label} are not checked", null, OutcomeCodes.NotApplicable));
                sources = sources.Where(s => s.IsHost).ToList();
            }

            // ---- select ---------------------------------------------------
            var wanted = ParamsFor(rule);
            var selection = new RuleSelection<(Element Element, CheckSource Source)>(rule.Selector, _scope, _selection);
            foreach (var src in sources.Where(s => s.Doc != null))
            {
                Seen(Sources, src.Label);
                foreach (var bic in bics)
                    foreach (var e in Elements(src, bic, view))
                        selection.Offer((e, src), RevitCheckReader.Values(src.Doc!, e, src, wanted));
            }
            var selected = selection.Selected
                .Select(x => new SelectedElement { Element = x.Item.Element, Source = x.Item.Source, Values = x.Values })
                .ToList();

            run.Notes.AddRange(selection.FallbackNotes());

            // R39: a level scope does not apply to grids, levels, links, views...: noted, never
            // not_verified. When it applies to none of the rule's elements, the rule is skipped.
            run.ScopeNote = selection.ScopeNote();
            if (selection.Selected.Count == 0 && selection.Undecided.Count == 0 && selection.NotApplicable.Count > 0 && run.Results.Count == 0)
            {
                run.SkippedBecause = run.ScopeNote;
                return;
            }
            // R64: a name selector that matched none of the elements seen is not_verified.
            if (selection.ApplyMatchedNone(run)) return;

            // ---- evaluate -------------------------------------------------
            if (setKind)
            {
                // Undecided elements make a count incomplete, like an unloaded link.
                gaps.AddRange(selection.Gaps());
                var outcome = GenericAssertions.EvaluateSet(a, selection.SelectedValues, gaps);
                run.AddDocument(outcome);
                if (outcome.IsFail) run.FailRefs.AddRange(selected.Select(s => s.Values.Ref));
                return;
            }

            selection.AddUndecidedTo(run);
            foreach (var src in sources.Where(s => s.Doc == null))
                run.Add(src.InstanceRef, Outcome.NotVerified(src.UnavailableReason ?? $"{src.Label} could not be read", null, OutcomeCodes.SourceUnavailable));
            foreach (var src in sources.Where(s => s.Doc != null))
                foreach (var gap in src.ClosedWorksets)
                {
                    if (src.IsHost) run.AddDocument(Outcome.NotVerified(gap, null, OutcomeCodes.SourceUnavailable));
                    else run.Add(src.InstanceRef, Outcome.NotVerified(gap, null, OutcomeCodes.SourceUnavailable));
                }

            Evaluate(run, selected, view);

            if (run.Results.Count == 0)
            {
                var where = string.Join(", ", sources.Select(s => s.Label));
                var filter = _scope.Describe();
                run.NothingToCheck = $"nothing to check: no {string.Join(", ", names)} matched in {where}" + (filter != null ? $" ({filter})" : "");
            }
        }

        private void Evaluate(RuleRun run, List<SelectedElement> selected, View? view)
        {
            var a = run.Rule.Assertion;
            switch (a.Kind)
            {
                case AssertionKinds.RoomPlaced: RevitGeometricKinds.RoomPlaced(run, selected); return;
                case AssertionKinds.RoomEnclosed: RevitGeometricKinds.RoomEnclosed(run, selected); return;
                case AssertionKinds.TurningCircleGe: RevitGeometricKinds.TurningCircle(run, selected); return;
                case AssertionKinds.ClearWidthGe: RevitGeometricKinds.ClearWidth(run, selected); return;
                case AssertionKinds.DimensionOverridden: RevitGeometricKinds.DimensionOverridden(run, selected); return;
                case AssertionKinds.InPlaceFamily: RevitGeometricKinds.InPlaceFamily(run, selected); return;
                case AssertionKinds.DuplicateInstances: RevitGeometricKinds.Duplicates(run, selected); return;
                case AssertionKinds.TaggedInView: RevitGeometricKinds.TaggedInView(run, selected, _doc, view!); return;
                case AssertionKinds.LevelMatchesLink:
                case AssertionKinds.GridAlignedWithLink:
                {
                    if (selected.Count == 0) return;
                    if (!TryCompareLink(a.Link, out var link, out var problem))
                    {
                        foreach (var s in selected) run.Add(s.Values.Ref, Outcome.NotVerified(problem!, null, OutcomeCodes.SourceUnavailable));
                        return;
                    }
                    if (link!.Doc == null) Seen(NotSearched, link.UnavailableReason ?? link.Label);
                    else Seen(Sources, link.Label);
                    if (link.ClosedWorksets.Count > 0)
                    {
                        // A level or grid on a closed workset of the link is invisible to the comparison.
                        var closed = string.Join("; ", link.ClosedWorksets);
                        foreach (var gap in link.ClosedWorksets) Seen(NotSearched, gap);
                        foreach (var s in selected) run.Add(s.Values.Ref, Outcome.NotVerified(closed, null, OutcomeCodes.SourceUnavailable));
                        return;
                    }
                    if (a.Kind == AssertionKinds.LevelMatchesLink) RevitGeometricKinds.LevelsAgainstLink(run, selected, link);
                    else RevitGeometricKinds.GridsAgainstLink(run, selected, link);
                    return;
                }
                default:
                    foreach (var s in selected) run.Add(s.Values.Ref, GenericAssertions.Evaluate(a, s.Values));
                    return;
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static bool ComparesWithLink(string kind) =>
            kind == AssertionKinds.LevelMatchesLink || kind == AssertionKinds.GridAlignedWithLink;

        private bool TrySources(Rule rule, out List<CheckSource> sources, out string? problem)
        {
            problem = null;
            sources = new List<CheckSource>();
            // Host levels and grids are compared with assertion.link; scope.link does not move them.
            if (ComparesWithLink(rule.Assertion.Kind))
            {
                sources.Add(_host);
                return true;
            }
            if (_scope.Link != null)
            {
                var picked = LinkNames.Pick(_scope.Link, _links.Select(l => l.Name));
                if (picked == null)
                {
                    problem = $"no link named '{_scope.Link}' (links: {LinkList()})";
                    return false;
                }
                sources.AddRange(_links.Where(l => l.Name == picked));
                return true;
            }
            sources.Add(_host);
            if (rule.Selector.Link == SelectorLinks.All) sources.AddRange(_links);
            return true;
        }

        /// <summary>assertion.link, else scope.link; neither → not_verified "choose a link".</summary>
        private bool TryCompareLink(string? name, out CheckSource? link, out string? problem)
        {
            link = null;
            if (!LinkNames.ChooseCompareLink(name, _scope.Link, _links.Select(l => l.Name).ToList(), out var picked, out problem))
                return false;
            // Several instances of one link: compare with the first loaded one.
            link = _links.Where(l => l.Name == picked).OrderBy(l => l.Doc == null ? 1 : 0).First();
            return true;
        }

        private string LinkList() => _links.Count == 0 ? "none" : string.Join(", ", _links.Select(l => LinkNames.Clean(l.Name)).Distinct());

        private BuiltInCategory? Resolve(string name)
        {
            if (!_categories.TryGetValue(name, out var bic))
            {
                bic = RevitCheckReader.ResolveCategory(_doc, name);
                _categories[name] = bic;
            }
            return bic;
        }

        private IList<Element> Elements(CheckSource src, BuiltInCategory bic, View? view)
        {
            if (view != null && src.IsHost)
                return new FilteredElementCollector(_doc, view.Id).OfCategory(bic).WhereElementIsNotElementType().ToElements();
            var key = (src.Label, bic);
            if (!_collected.TryGetValue(key, out var elements))
            {
                elements = RevitCheckReader.Collect(src.Doc!, bic);
                _collected[key] = elements;
            }
            return elements;
        }

        private static List<string> ParamsFor(Rule rule)
        {
            var a = rule.Assertion;
            var names = new List<string>();
            void Add(string? n)
            {
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n!);
            }
            Add(rule.Selector.HasParam);
            Add(a.Param);
            Add(a.OtherParam);
            if (a.Kind == AssertionKinds.ClearWidthGe)
            {
                foreach (var n in GeometricOutcomes.ClearWidthParams(a)) Add(n);
                Add("Width");
            }
            return names;
        }

        private static void Seen(List<string> list, string item)
        {
            if (!list.Contains(item, StringComparer.OrdinalIgnoreCase)) list.Add(item);
        }
    }
}

#nullable enable
// Runs a parsed rule set against the open model, one RuleRun per rule in
// scope. Per rule: pick the sources (host, every link, or the scope's link),
// turn every unreadable source into not_verified outcomes, collect the
// selector's categories, read ElementValues, apply the selector and scope,
// then evaluate the kind (GenericAssertions or RevitGeometricKinds).
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
        private readonly List<string> _closedWorksets;
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
            _closedWorksets = RevitCheckSources.ClosedWorksets(doc);
            if (scope.Selection)
                _selection = new HashSet<long>((uidoc?.Selection.GetElementIds() ?? new List<ElementId>()).Select(id => (long)id.Value));
        }

        public List<RuleRun> Run(RuleSet set)
        {
            var runs = new List<RuleRun>();
            foreach (var rule in set.Rules)
            {
                if (!_scope.Applies(rule, out var why))
                {
                    Skipped.Add(new SkippedRule(rule.Id, why ?? "outside the scope"));
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
                runs.Add(run);
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
                foreach (var gap in _closedWorksets)
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
            if (sources.Any(s => s.IsHost && s.Doc != null))
                foreach (var gap in _closedWorksets)
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
            var selected = new List<SelectedElement>();
            foreach (var src in sources.Where(s => s.Doc != null))
            {
                Seen(Sources, src.Label);
                foreach (var bic in bics)
                {
                    foreach (var e in Elements(src, bic, view))
                    {
                        var values = RevitCheckReader.Values(src.Doc!, e, src, wanted);
                        if (!_scope.Admits(values, _selection)) continue;
                        switch (SelectorFilter.Test(rule.Selector, values, out var unknown))
                        {
                            case SelectorMatch.Excluded:
                                continue;
                            case SelectorMatch.Unknown:
                                run.Add(values.Ref, Outcome.NotVerified(unknown ?? "the selector could not be applied", null, OutcomeCodes.Unreadable));
                                continue;
                        }
                        selected.Add(new SelectedElement { Element = e, Values = values, Source = src });
                    }
                }
            }

            // ---- evaluate -------------------------------------------------
            if (setKind)
            {
                var outcome = GenericAssertions.EvaluateSet(a, selected.Select(s => s.Values).ToList(), gaps);
                run.AddDocument(outcome);
                if (outcome.IsFail) run.FailRefs.AddRange(selected.Select(s => s.Values.Ref));
                return;
            }

            foreach (var src in sources.Where(s => s.Doc == null))
                run.Add(src.InstanceRef, Outcome.NotVerified(src.UnavailableReason ?? $"{src.Label} could not be read", null, OutcomeCodes.SourceUnavailable));
            if (sources.Any(s => s.IsHost && s.Doc != null))
                foreach (var gap in _closedWorksets)
                    run.AddDocument(Outcome.NotVerified(gap, null, OutcomeCodes.SourceUnavailable));

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

        private bool TryCompareLink(string? name, out CheckSource? link, out string? problem)
        {
            link = null;
            problem = null;
            if (name == null)
            {
                if (_links.Count == 1)
                {
                    link = _links[0];
                    return true;
                }
                problem = _links.Count == 0
                    ? "the model has no Revit links to compare with"
                    : $"the rule names no link and the model has {_links.Count} (links: {LinkList()})";
                return false;
            }
            var picked = LinkNames.Pick(name, _links.Select(l => l.Name));
            if (picked == null)
            {
                problem = $"no link named '{name}' (links: {LinkList()})";
                return false;
            }
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

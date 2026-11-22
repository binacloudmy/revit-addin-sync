#nullable enable
// bind_parameters (AI-530, WP-8; spec in the JKR copilot new-tools plan §2.4). Binds existing JKR shared parameters
// (definitions in the loaded JKR shared parameter file) to the Revit categories the JKR spec lists them for.
//
//   Plan     refuses without a loaded, official JKR shared parameter file; reads each requested parameter's state
//            (in the file? bound now? to which categories, instance or type?) and decides with BindPlanner (Revit-free).
//            No change is made. Previewable = false: the card is estimate-only, so Revit's rolled-back preview group never
//            touches ParameterBindings.
//   Execute  (commit, in the engine's Transaction, so ONE Undo restores it) Insert for a parameter that is not bound,
//            ReInsert with a NEW binding for one bound to only some of the wanted categories. Never creates a definition,
//            never flips Instance/Type, never calls Remove (that would delete the parameter's values).
//            Reports bound / extended / already bound / failed with the reason per parameter, reads every binding back from
//            a fresh map, and re-reads again after the commit (a mismatch there is a warning on the card).
//   dry_run  returns the plan as a no-op result; nothing changes.
//
// Not verified in a live Revit (AI-530 notes): that a binding change is recorded by the engine's change recorder (the engine
// treats this tool's non-noop result as a change), and why ReInsert returned false for 'Occupant' in the WP-1 spike (the spike
// mutated the iterated binding in place; this tool always builds a new binding object). Each failed row carries the API call,
// what it returned and the categories before and after, so the first live run answers it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.Services.JkrSpec;

namespace BinaVibe.Copilot.Tools
{
    public sealed class BindParametersTool : IWriteTool
    {
        private sealed class PlanNotes
        {
            public BindParametersArgs Args = null!;
            public List<BindItem> Items = new();
            public string FilePath = "";
        }

        private static readonly ConditionalWeakTable<WritePlan, PlanNotes> Notes = new();

        public string Name => ToolNames.BindParameters;

        public ToolKind Kind => ToolKind.Write;

        /// <summary>bind_parameters changes the model, so it never runs outside a write request.</summary>
        public Envelope Run(ToolContext ctx, JsonElement args) =>
            WriteEnvelopes.Failure(ErrorCodes.NotAllowed,
                "bind_parameters changes the model, so it runs only in a write request.",
                EnvelopePhase.Read);

        public WritePlan Plan(ToolContext ctx, JsonElement args)
        {
            var a = BindParametersArgs.Parse(args);
            var doc = ctx.Doc;
            if (doc.IsFamilyDocument)
                throw new WriteRefusal(ErrorCodes.NotAllowed, "Parameters are bound in a project, and the active document is a family; nothing was changed.");

            var app = doc.Application;
            var filePath = app.SharedParametersFilename ?? "";
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new WriteRefusal(ErrorCodes.NotAllowed,
                    "No shared parameter file is loaded, so there is nothing to bind from. Load the official JKR shared parameter file (Manage > Shared Parameters > Browse), then try again. Nothing was changed.");
            if (BindParametersText.IsProvisionedStub(ReadHead(filePath)))
                throw new WriteRefusal(ErrorCodes.NotAllowed,
                    "The loaded shared parameter file is not the official JKR file (it is the empty file this add-in makes for its older auto-fix). Load the official JKR shared parameter file (Manage > Shared Parameters > Browse), then try again. Nothing was changed.");

            DefinitionFile? file;
            try { file = app.OpenSharedParameterFile(); }
            catch (Exception ex)
            {
                throw new WriteRefusal(ErrorCodes.NotAllowed, "The shared parameter file cannot be opened (" + ex.Message + "). Nothing was changed.");
            }
            if (file == null)
                throw new WriteRefusal(ErrorCodes.NotAllowed, "The shared parameter file cannot be opened. Load the official JKR shared parameter file, then try again. Nothing was changed.");

            var definitions = ReadDefinitions(file);
            var bound = ReadBindings(doc);
            var spec = SpecPbParameters.All.Where(p => p.IsShared).Select(p => new BindSpecRow(p.Name, p.Category, p.IsInstance)).ToList();

            BindingState StateOf(string name)
            {
                var s = new BindingState { InFile = definitions.TryGetValue(name, out _) };
                var hits = bound.Where(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0) return s;
                definitions.TryGetValue(name, out var ed);
                var same = ed == null ? null : hits.FirstOrDefault(h => h.Guid != null && h.Guid == ed.GUID);
                if (same == null)
                {
                    s.NameTaken = true;   // a project parameter or another GUID already owns this name
                    return s;
                }
                s.Bound = true;
                s.BoundAsInstance = same.Instance;
                s.BoundCategories = same.Categories;
                return s;
            }

            var items = BindPlanner.Plan(a, spec, StateOf);
            var intended = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var i in items) intended[i.Name] = i.Status + "|" + string.Join(",", i.ToAdd);

            var plan = new WritePlan
            {
                Tool = ToolNames.BindParameters,
                CanonicalArgs = args.GetRawText(),
                TargetIds = Array.Empty<long>(),   // a binding is not an element: no ownership check applies
                IntendedValues = new Dictionary<string, string>(intended),
                Previewable = false,
            };
            Notes.Remove(plan);
            Notes.Add(plan, new PlanNotes { Args = a, Items = items, FilePath = filePath });
            return plan;
        }

        public Envelope Execute(ToolContext ctx, JsonElement args, WritePlan plan, WritePhase phase)
        {
            if (!Notes.TryGetValue(plan, out var notes))
                throw new WriteRefusal(ErrorCodes.RuntimeError, "bind_parameters lost its plan; nothing was changed.");
            var doc = ctx.Doc;
            var app = doc.Application;

            var rows = new List<Dictionary<string, object?>>();
            foreach (var item in notes.Items) rows.Add(RowOf(item));

            if (notes.Args.DryRun)
                return Result(notes, rows, applied: false, bound: 0, failed: 0, mismatches: new List<MismatchDto>(), plan: true);

            // Apply. A definition is looked up in the file again here (Execute may run with a re-planned copy).
            var file = app.OpenSharedParameterFile()
                       ?? throw new WriteRefusal(ErrorCodes.NotAllowed, "The shared parameter file cannot be opened; nothing was changed.");
            var definitions = ReadDefinitions(file);
            var map = doc.ParameterBindings;
            var existing = ReadBindings(doc);
            var changed = new List<BindItem>();
            var failed = 0;
            for (var n = 0; n < notes.Items.Count; n++)
            {
                var item = notes.Items[n];
                var row = rows[n];
                if (!item.Changes) continue;
                try
                {
                    var wanted = Categories(doc, app, item.Wanted, out var missing);
                    if (missing.Count > 0) throw new InvalidOperationException("this model has no category " + string.Join(", ", missing));
                    var blocked = wanted.Cast<Category>().Where(c => !c.AllowsBoundParameters).Select(c => c.Name).ToList();
                    if (blocked.Count > 0) throw new InvalidOperationException("Revit does not allow bound parameters on " + string.Join(", ", blocked));

                    if (!definitions.TryGetValue(item.Name, out var ed))
                        throw new InvalidOperationException("the definition is no longer in the shared parameter file");

                    bool returned;
                    string call;
                    if (item.Status == BindStatus.Bind)
                    {
                        var binding = item.Instance ? (Binding)app.Create.NewInstanceBinding(wanted) : app.Create.NewTypeBinding(wanted);
                        call = "Insert";
                        returned = map.Insert(ed, binding, GroupTypeId.Data);
                    }
                    else
                    {
                        var current = existing.First(b => string.Equals(b.Name, item.Name, StringComparison.OrdinalIgnoreCase) && b.Guid == ed.GUID);
                        // Keep what is bound now, add the rest. A NEW binding object: the one the iterator returned is not edited in place.
                        var all = app.Create.NewCategorySet();
                        foreach (Category c in current.Binding.Categories) all.Insert(c);
                        foreach (Category c in wanted) if (!all.Contains(c)) all.Insert(c);
                        var binding = item.Instance ? (Binding)app.Create.NewInstanceBinding(all) : app.Create.NewTypeBinding(all);
                        call = "ReInsert";
                        returned = map.ReInsert(current.Definition, binding, current.Definition.GetGroupTypeId());
                    }
                    row["api_call"] = call;
                    row["api_returned"] = returned;
                    if (!returned)
                    {
                        row["result"] = "failed: Revit " + call + " returned false (it refused the change; nothing was kept for this parameter)";
                        row["after_categories"] = BoundNow(doc, ed.GUID, item.Name)?.Categories;
                        failed++;
                        continue;
                    }
                    row["result"] = item.Status == BindStatus.Bind ? "bound" : "extended";
                    changed.Add(item);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException || ex is ArgumentException)
                {
                    row["result"] = "failed: " + ex.Message;
                    failed++;
                }
            }

            // Read back from a fresh read of the map, in the same open transaction.
            var mismatches = new List<MismatchDto>();
            var verified = new List<BindItem>();
            foreach (var item in changed)
            {
                var row = rows[notes.Items.IndexOf(item)];
                var ed = definitions[item.Name];
                var now = BoundNow(doc, ed.GUID, item.Name);
                var missing = now == null ? item.Wanted : item.Wanted.Where(w => !now.Categories.Contains(w)).ToList();
                if (now == null || missing.Count > 0 || now.Instance != item.Instance)
                {
                    row["result"] = "failed: bound by Revit but the read-back does not show it (" + (now == null ? "not bound" : "missing " + string.Join(", ", missing)) + ")";
                    row["after_categories"] = now?.Categories;
                    mismatches.Add(new MismatchDto { Id = now?.ElementId ?? 0, Field = item.Name + " binding", Expected = string.Join(",", item.Wanted), Actual = now == null ? "not bound" : string.Join(",", now.Categories) });
                }
                else
                {
                    row["after_categories"] = now.Categories;
                    row["bound_as"] = now.Instance ? "instance" : "type";
                    verified.Add(item);
                }
            }

            // After the commit, read once more: a mismatch is a warning on the card, the change itself stands.
            var after = verified.Select(i => (Name: i.Name, Guid: definitions[i.Name].GUID, Wanted: i.Wanted.ToList())).ToList();
            if (after.Count > 0)
                ctx.AfterCommit.Add(() =>
                {
                    var bad = after.Where(a => { var b = BoundNow(doc, a.Guid, a.Name); return b == null || a.Wanted.Any(w => !b.Categories.Contains(w)); }).Select(a => a.Name).ToList();
                    if (bad.Count > 0) throw new InvalidOperationException("after the commit these bindings no longer read back: " + string.Join(", ", bad.Take(5)));
                });

            var bound = verified.Count;
            var problems = failed + mismatches.Count;
            if (bound == 0 && problems > 0)
                return WriteEnvelopes.Failure(ErrorCodes.RuntimeError,
                    "Nothing was bound. " + string.Join("; ", rows.Where(r => ((string?)r["result"] ?? "").StartsWith("failed", StringComparison.Ordinal))
                        .Select(r => r["name"] + " " + r["result"]).Take(5)) + ".",
                    EnvelopePhase.Commit);
            return Result(notes, rows, applied: true, bound: bound, failed: problems, mismatches: mismatches, plan: false);
        }

        // ------------------------------------------------------------------

        private static Envelope Result(PlanNotes notes, List<Dictionary<string, object?>> rows, bool applied, int bound, int failed, List<MismatchDto> mismatches, bool plan)
        {
            var already = notes.Items.Count(i => i.Status == BindStatus.AlreadyBound);
            var refused = notes.Items.Count(i => !i.Changes && i.Status != BindStatus.AlreadyBound);
            var willChange = notes.Items.Count(i => i.Changes);
            var data = new Dictionary<string, object?>
            {
                ["parameters"] = rows.Cast<object>().ToList(),
                ["dry_run"] = plan,
                ["bound"] = bound,
                ["already_bound"] = already,
                ["not_bound"] = refused + failed,
                ["shared_parameter_file"] = notes.FilePath,
                ["undo"] = applied ? "one Undo restores the binding change" : "nothing was changed",
            };
            if (plan)
            {
                data["would_change"] = willChange;
                return EnvelopeBuilder.Write(WritePhase.Commit,
                        "Plan only (dry run): " + willChange.ToString(CultureInfo.InvariantCulture) + " to bind or extend, " + already.ToString(CultureInfo.InvariantCulture) + " already bound, " +
                        refused.ToString(CultureInfo.InvariantCulture) + " cannot be bound. Nothing was changed.")
                    .Status(EnvelopeStatus.Noop).WithData(data).WithNext(WriteEnvelopes.NoChangeNext).Build();
            }
            if (bound == 0)
                return EnvelopeBuilder.Write(WritePhase.Commit,
                        "Nothing to bind: " + already.ToString(CultureInfo.InvariantCulture) + " already bound, " + refused.ToString(CultureInfo.InvariantCulture) + " cannot be bound (see each reason).")
                    .Status(EnvelopeStatus.Noop).WithData(data).WithNext(WriteEnvelopes.NoChangeNext).Build();

            var problems = failed;
            var summary = "Bound " + bound.ToString(CultureInfo.InvariantCulture) + (bound == 1 ? " parameter" : " parameters") + "." +
                          (already > 0 ? " " + already.ToString(CultureInfo.InvariantCulture) + " already bound." : "") +
                          (refused + problems > 0 ? " " + (refused + problems).ToString(CultureInfo.InvariantCulture) + " not bound (see each reason)." : "");
            return EnvelopeBuilder.Write(WritePhase.Commit, EnvelopeFactory.Shorten(summary, EnvelopeLimits.SummaryMaxChars))
                .Status(problems + refused > 0 ? EnvelopeStatus.Partial : EnvelopeStatus.Done)
                .WithCount(bound)
                .WithData(data)
                .WithVerified(new VerifiedDto { Method = VerifyMethods.Exists, Checked = bound + mismatches.Count, Mismatches = mismatches })
                .WithNext("The parameters are bound; set their values with set_parameters. One Undo restores the binding change. Say which parameters were not bound and why.")
                .Build();
        }

        private static Dictionary<string, object?> RowOf(BindItem i) => new Dictionary<string, object?>
        {
            ["name"] = i.Name,
            ["status"] = BindParametersText.StatusWord(i.Status),
            ["reason"] = i.Reason,
            ["kind"] = i.Instance ? "instance" : "type",
            ["categories"] = i.WantedDisplay,
            ["result"] = i.Changes ? "planned" : i.Status == BindStatus.AlreadyBound ? "already bound" : "not bound: " + BindParametersText.StatusWord(i.Status),
        };

        private static string ReadHead(string path)
        {
            try { return string.Join("\n", File.ReadLines(path).Take(6)); }
            catch (Exception) { return ""; }
        }

        private static Dictionary<string, ExternalDefinition> ReadDefinitions(DefinitionFile file)
        {
            var map = new Dictionary<string, ExternalDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (DefinitionGroup g in file.Groups)
                foreach (Definition d in g.Definitions)
                    if (d is ExternalDefinition ed && !map.ContainsKey(ed.Name)) map[ed.Name] = ed;
            return map;
        }

        private sealed class BoundRow
        {
            public string Name = "";
            public Guid? Guid;
            public bool Instance;
            public List<string> Categories = new();
            public long ElementId;
            public InternalDefinition Definition = null!;
            public ElementBinding Binding = null!;
        }

        /// <summary>Every binding in the project, read fresh from the map (never cached across a change).</summary>
        private static List<BoundRow> ReadBindings(Document doc)
        {
            var rows = new List<BoundRow>();
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (!(it.Key is InternalDefinition def) || !(it.Current is ElementBinding binding)) continue;
                var row = new BoundRow { Name = def.Name, Instance = binding is InstanceBinding, Definition = def, Binding = binding, ElementId = (long)def.Id.Value };
                if (doc.GetElement(def.Id) is SharedParameterElement spe) row.Guid = spe.GuidValue;
                foreach (Category c in binding.Categories)
                    if (c != null) row.Categories.Add(((BuiltInCategory)(long)c.Id.Value).ToString());
                rows.Add(row);
            }
            return rows;
        }

        private static BoundRow? BoundNow(Document doc, Guid guid, string name) =>
            ReadBindings(doc).FirstOrDefault(b => b.Guid == guid) ?? ReadBindings(doc).FirstOrDefault(b => b.Guid == null && string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

        private static CategorySet Categories(Document doc, Autodesk.Revit.ApplicationServices.Application app, IEnumerable<string> built, out List<string> missing)
        {
            var set = app.Create.NewCategorySet();
            missing = new List<string>();
            foreach (var name in built)
            {
                if (!Enum.TryParse<BuiltInCategory>(name, out var bic)) { missing.Add(name); continue; }
                var cat = Category.GetCategory(doc, bic);
                if (cat == null) missing.Add(name); else set.Insert(cat);
            }
            return set;
        }
    }
}

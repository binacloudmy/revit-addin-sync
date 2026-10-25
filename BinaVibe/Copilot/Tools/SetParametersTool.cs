#nullable enable
// set_parameters (spec §11 #5; plan §5 W3 Task 4). Body owned by W3.
//
// One parameter on many elements, through the write batch runner only:
//   Plan     resolves the targets (filter / handle / ids / table_rows) BEFORE
//            any change, picks the instance or (type_param) its type, parses
//            each value against that parameter's kind and display unit
//            (ValueParser), drops elements that already hold the value, and
//            refuses with a reason instead of a silent zero: wrong parameter
//            name -> not_found with suggestions; a type parameter without
//            type_param -> invalid_args with the blast radius; read-only ->
//            not_allowed. The plan's ids and canonical values are what the
//            plan hash binds.
//   Execute  sets exactly the planned values inside the runner's transaction;
//            Parameter.Set returning false throws, so the batch rolls back;
//            then regenerates and reads every value back into `verified`
//            (param_readback), with before/after in display units.
// Built on set_parameter_by_filter's internals (BulkEdit.CopilotTargets,
// Inspectors.PredicateMatches / SuggestParamNames, ruling R2).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using BinaVibe.Copilot.Writes;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Copilot.Tools
{
    public sealed class SetParametersTool : IWriteTool
    {
        private const int MaxTypesListed = 20;

        /// <summary>What Plan learned that the envelope reports (skips, counts, labels),
        /// kept per plan object: Execute writes from the plan alone.</summary>
        private sealed class PlanNotes
        {
            public string Param = "";
            public bool TypeParam;
            public bool FromTable;
            public string ValueDisplay = "";
            public string Scope = "elements";
            public int Matched;
            public int Unchanged;
            public List<SkipNote> Skips = new();
            public List<Dictionary<string, object?>> Types = new();
            public int InstancesAffected;
        }

        private static readonly ConditionalWeakTable<WritePlan, PlanNotes> Notes = new();

        public string Name => ToolNames.SetParameters;

        public ToolKind Kind => ToolKind.Write;

        /// <summary>set_parameters changes the model, so it never runs outside a write request.</summary>
        public Envelope Run(ToolContext ctx, JsonElement args) =>
            WriteEnvelopes.Failure(ErrorCodes.NotAllowed,
                "set_parameters changes the model, so it runs only in a write request, through Revit's confirm card.",
                EnvelopePhase.Read);

        public WritePlan Plan(ToolContext ctx, JsonElement args)
        {
            var a = SetParametersArgs.Parse(args);
            var doc = ctx.Doc;
            var notes = new PlanNotes { Param = a.Param, TypeParam = a.TypeParam, FromTable = a.Table != null };

            var picked = SetParameterTargets.Resolve(ctx.UiDoc, doc, ctx.Handles, a, notes.Skips);
            if (a.Table != null)
                foreach (var row in a.Table.BlankRowNumbers)
                    notes.Skips.Add(new SkipNote { Row = row, Reason = "has an empty key or value cell" });
            notes.Matched = picked.Count;
            notes.Scope = ScopeLabel(picked.Select(p => p.Element), a.Target?.Filter);

            var edits = new SortedDictionary<long, string>();
            var seen = new Dictionary<long, (string canonical, int? row)>();
            int typeOnly = 0, instanceOnly = 0, missing = 0, readOnly = 0, grouped = 0;
            string? display = null;
            foreach (var t in picked)
            {
                var el = t.Element;
                Element owner;
                if (a.TypeParam)
                {
                    owner = el as ElementType ?? SetParameterTargets.TypeOf(doc, el)!;
                    if (owner == null) { Skip(notes, el, t.Row, "has no type"); continue; }
                }
                else
                {
                    if (el is ElementType)
                        throw new WriteRefusal(ErrorCodes.InvalidArgs,
                            "Element " + Id(el) + " is a type ('" + el.Name + "'); set its parameters with type_param: true, which changes every instance of the type.");
                    owner = el;
                }

                var p = owner.LookupParameter(a.Param);
                if (p == null)
                {
                    if (!a.TypeParam && SetParameterTargets.TypeOf(doc, el)?.LookupParameter(a.Param) != null) typeOnly++;
                    else if (a.TypeParam && el.LookupParameter(a.Param) != null) instanceOnly++;
                    else missing++;
                    Skip(notes, owner, t.Row, "has no parameter '" + a.Param + "'");
                    continue;
                }
                if (p.IsReadOnly) { readOnly++; Skip(notes, owner, t.Row, "'" + a.Param + "' is read-only"); continue; }
                if (!a.TypeParam && el.GroupId != ElementId.InvalidElementId) { grouped++; Skip(notes, el, t.Row, "is in a group (edit the group or ungroup it)"); continue; }

                var parsed = ValueParser.Parse(t.Value, RevitParamValues.KindOf(p), RevitParamValues.DisplayUnitOf(doc, p));
                if (!parsed.Ok)
                    throw new WriteRefusal(ErrorCodes.InvalidArgs,
                        "Value " + Raw(t.Value) + " for '" + a.Param + "'" + (t.Row != null ? " (table row " + t.Row.Value.ToString(CultureInfo.InvariantCulture) + ")" : "") + ": " + parsed.Error + ".");
                parsed = RevitParamValues.Resolve(doc, parsed, a.Param);
                display ??= parsed.Kind == ValueKind.ElementId ? Raw(t.Value).Trim('\'') : parsed.Display;

                var ownerId = (long)owner.Id.Value;
                if (seen.TryGetValue(ownerId, out var earlier))
                {
                    if (earlier.canonical != parsed.Canonical)
                        throw new WriteRefusal(ErrorCodes.InvalidArgs,
                            "Element " + ownerId.ToString(CultureInfo.InvariantCulture) + " would get two different values for '" + a.Param + "'" +
                            (a.TypeParam ? " (several rows or instances share its type)" : "") + ".");
                    continue;
                }
                seen[ownerId] = (parsed.Canonical, t.Row);

                if (RevitParamValues.Holds(doc, p, parsed)) { notes.Unchanged++; continue; }
                edits[ownerId] = parsed.Canonical;
            }

            if (edits.Count == 0 && notes.Unchanged == 0)
            {
                if (typeOnly > 0)
                    throw new WriteRefusal(ErrorCodes.InvalidArgs,
                        "'" + a.Param + "' is a type parameter of the matched elements. Resend with type_param: true and tell the drafter it changes every instance of " +
                        TypeBlast(doc, picked.Select(t => SetParameterTargets.TypeOf(doc, t.Element)).Where(x => x != null).Select(x => x!)) + ".");
                if (instanceOnly > 0)
                    throw new WriteRefusal(ErrorCodes.InvalidArgs, "'" + a.Param + "' is an instance parameter, not a type parameter; resend with type_param: false.");
                if (missing > 0 && readOnly == 0 && grouped == 0)
                    throw new WriteRefusal(ErrorCodes.NotFound,
                        "No parameter named '" + a.Param + "' on the " + picked.Count.ToString(CultureInfo.InvariantCulture) + " matched element(s) or their types; the name is wrong.",
                        picked.Count > 0 ? Inspectors.SuggestParamNames(doc, picked[0].Element, a.Param) : new List<string>());
                if (readOnly > 0 && grouped == 0)
                    throw new WriteRefusal(ErrorCodes.NotAllowed, "'" + a.Param + "' is read-only on every matched element (Revit computes or locks it).");
                if (grouped > 0)
                    throw new WriteRefusal(ErrorCodes.NotAllowed, "Every matched element is in a group; edit the group (or ungroup) to change '" + a.Param + "'.");
            }

            if (a.TypeParam)
            {
                foreach (var id in edits.Keys.Take(MaxTypesListed))
                {
                    var type = doc.GetElement(ElemIds.From(id));
                    var n = InstancesOf(doc, type);
                    notes.InstancesAffected += n;
                    notes.Types.Add(new Dictionary<string, object?> { ["id"] = id, ["name"] = type?.Name, ["instances_in_model"] = n });
                }
            }

            notes.ValueDisplay = notes.FromTable ? "values from the attached table" : display ?? Raw(a.Value!.Value);
            var plan = new WritePlan
            {
                Tool = ToolNames.SetParameters,
                CanonicalArgs = args.GetRawText(),
                TargetIds = edits.Keys.ToArray(),
                IntendedValues = edits.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
                Previewable = true,
            };
            Notes.Remove(plan);
            Notes.Add(plan, notes);
            return plan;
        }

        public Envelope Execute(ToolContext ctx, JsonElement args, WritePlan plan, WritePhase phase)
        {
            if (!Notes.TryGetValue(plan, out var notes)) notes = NotesFromArgs(args);
            var doc = ctx.Doc;

            var written = new List<(long id, Parameter p, ParsedValue v, string? before)>();
            foreach (var id in plan.TargetIds)
            {
                var key = id.ToString(CultureInfo.InvariantCulture);
                var el = doc.GetElement(ElemIds.From(id))
                         ?? throw new WriteRefusal(ErrorCodes.NotFound, "Element " + key + " no longer exists; nothing was changed.", Array.Empty<string>());
                var p = el.LookupParameter(notes.Param)
                        ?? throw new WriteRefusal(ErrorCodes.NotFound, "Element " + key + " no longer has '" + notes.Param + "'; nothing was changed.", Array.Empty<string>());
                var v = ParsedValue.FromCanonical(plan.IntendedValues.TryGetValue(key, out var c) ? c : null);
                if (!v.Ok) throw new WriteRefusal(ErrorCodes.InvalidArgs, "The plan holds no value for element " + key + ".");
                var before = RevitParamValues.Display(p);
                if (!RevitParamValues.Set(doc, p, v))
                    throw new WriteRefusal(ErrorCodes.RolledBack,
                        "Revit refused to set '" + notes.Param + "' to " + v.Display + " on element " + key +
                        " (Parameter.Set returned false), so the whole batch was rolled back and nothing changed.");
                written.Add((id, p, v, before));
            }

            if (written.Count > 0) doc.Regenerate();

            var rows = new List<FieldChangeDto>();
            var mismatches = new List<MismatchDto>();
            var matched = 0;
            foreach (var w in written)
            {
                var after = RevitParamValues.Display(w.p);
                if (rows.Count < EnvelopeLimits.MaxRows)
                    rows.Add(new FieldChangeDto { Id = w.id, Field = notes.Param, Before = w.before, After = after });
                if (RevitParamValues.Holds(doc, w.p, w.v)) matched++;
                else if (mismatches.Count < EnvelopeLimits.MaxRows)
                    mismatches.Add(new MismatchDto { Id = w.id, Field = notes.Param, Expected = w.v.Display, Actual = after });
            }

            var handle = TryHandle(ctx, plan.TargetIds, notes);
            var skipped = notes.Skips.Count;
            var status = written.Count == 0 ? EnvelopeStatus.Noop
                : mismatches.Count > 0 || skipped > 0 ? EnvelopeStatus.Partial
                : EnvelopeStatus.Done;

            var builder = EnvelopeBuilder.Write(phase, Summary(notes, phase, written.Count, matched, skipped))
                .Status(status)
                .WithCount(written.Count)
                .WithChanges(new ChangesDto { Modified = rows })
                .WithVerified(new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = written.Count, Mismatches = mismatches })
                .WithData(Data(notes, written.Count, skipped))
                .WithNext(Next(notes, phase, handle, mismatches.Count));
            if (handle != null) builder = builder.WithHandle(handle);
            return builder.Build();
        }

        // ------------------------------------------------------------------

        private static string Summary(PlanNotes n, WritePhase phase, int count, int readBack, int skipped)
        {
            var what = n.Param + " = " + n.ValueDisplay;
            if (n.FromTable) what = n.Param + " (" + n.ValueDisplay + ")";
            var on = count.ToString(CultureInfo.InvariantCulture) + " " + (n.TypeParam ? "type" + (count == 1 ? "" : "s") : n.Scope);
            var typeNote = n.TypeParam && count > 0
                ? " Type parameter: changes every instance of " + (count == 1 ? "that type" : "those types") + " (" + n.InstancesAffected.ToString(CultureInfo.InvariantCulture) + " in the model)."
                : "";
            var skipNote = skipped > 0 ? " " + skipped.ToString(CultureInfo.InvariantCulture) + " skipped (" + SkipReasons(n) + ")." : "";
            var unchanged = n.Unchanged > 0 ? " " + n.Unchanged.ToString(CultureInfo.InvariantCulture) + " already had it." : "";

            string text;
            if (count == 0)
                text = "Nothing to change: " + (n.Unchanged > 0 ? "every matched element already has " + what + "." : "no matched element can take " + n.Param + ".") + skipNote;
            else if (phase == WritePhase.Preview)
                text = "Preview: set " + what + " on " + on + ". Nothing has changed yet." + typeNote + unchanged + skipNote;
            else
                text = "Set " + what + " on " + on + "; read back " + readBack.ToString(CultureInfo.InvariantCulture) + " of " + count.ToString(CultureInfo.InvariantCulture) + "." + typeNote + unchanged + skipNote;
            return EnvelopeFactory.Shorten(text, EnvelopeLimits.SummaryMaxChars);
        }

        private static string SkipReasons(PlanNotes n) =>
            string.Join("; ", n.Skips.GroupBy(s => s.Reason).OrderByDescending(g => g.Count()).Take(3)
                .Select(g => g.Count().ToString(CultureInfo.InvariantCulture) + " " + g.Key));

        private static string Next(PlanNotes n, WritePhase phase, string? handle, int mismatches)
        {
            if (phase == WritePhase.Preview) return "The drafter is reviewing this change in Revit's confirm card.";
            if (mismatches > 0) return "Some values did not read back as set; inspect those ids before telling the drafter it is done.";
            return handle != null
                ? "Handle " + handle + " holds the changed elements. One Undo in the receipt reverts this batch."
                : "One Undo in the receipt reverts this batch.";
        }

        private static Dictionary<string, object?> Data(PlanNotes n, int changed, int skipped) => new()
        {
            ["param"] = n.Param,
            ["type_param"] = n.TypeParam,
            ["matched"] = n.Matched,
            ["changed"] = changed,
            ["unchanged"] = n.Unchanged,
            ["skipped_count"] = skipped,
            ["skipped"] = n.Skips.Take(EnvelopeLimits.MaxRows).Select(s => (object)new Dictionary<string, object?>
            {
                ["id"] = s.Id,
                ["row"] = s.Row,
                ["reason"] = s.Reason,
            }).ToList(),
            ["types"] = n.TypeParam ? n.Types : null,
            ["instances_affected"] = n.TypeParam ? n.InstancesAffected : null,
        };

        private string? TryHandle(ToolContext ctx, IReadOnlyCollection<long> ids, PlanNotes n)
        {
            if (ids.Count == 0 || ctx.Handles == null) return null;
            try
            {
                return ctx.Handles.Put(ids.Select(id => new ElementRef(id, null)).ToList(), "set_parameters " + n.Param);
            }
            catch (Exception)
            {
                // A handle is a convenience for the next call; the write stands without one.
                return null;
            }
        }

        private static PlanNotes NotesFromArgs(JsonElement args)
        {
            var a = SetParametersArgs.Parse(args);
            return new PlanNotes
            {
                Param = a.Param,
                TypeParam = a.TypeParam,
                FromTable = a.Table != null,
                ValueDisplay = a.Table != null ? "values from the attached table" : Raw(a.Value!.Value),
            };
        }

        private static void Skip(PlanNotes n, Element e, int? row, string reason) =>
            n.Skips.Add(new SkipNote { Id = (long)e.Id.Value, Row = row, Reason = reason });

        private static string ScopeLabel(IEnumerable<Element> elements, FilterSpec? filter)
        {
            var cats = elements.Select(e => e.Category?.Name).Where(c => !string.IsNullOrEmpty(c)).Distinct().Take(2).ToList();
            var label = cats.Count == 1 ? cats[0]! : "elements";
            if (filter?.Level != null) label += " (" + filter.Level + ")";
            return label;
        }

        private static string TypeBlast(Document doc, IEnumerable<Element> types)
        {
            var distinct = types.GroupBy(t => (long)t.Id.Value).Select(g => g.First()).ToList();
            var instances = distinct.Take(MaxTypesListed).Sum(t => InstancesOf(doc, t));
            return distinct.Count.ToString(CultureInfo.InvariantCulture) + " type(s) (" + instances.ToString(CultureInfo.InvariantCulture) + " instance(s) in the model)";
        }

        private static int InstancesOf(Document doc, Element? type)
        {
            if (type == null) return 0;
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (type.Category != null) collector = collector.OfCategoryId(type.Category.Id);
            var typeId = type.Id;
            return collector.Count(e => e.GetTypeId() == typeId);
        }

        private static string Id(Element e) => ((long)e.Id.Value).ToString(CultureInfo.InvariantCulture);

        private static string Raw(JsonElement v) => v.ValueKind == JsonValueKind.String ? "'" + v.GetString() + "'" : v.GetRawText();
    }
}

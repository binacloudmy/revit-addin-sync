#nullable enable
// load_link (AI-529, WP-7; spec in the JKR copilot new-tools plan §2.3). Loads Revit links that are not loaded.
// It lives in the add-in OUTSIDE the code sandbox: code-policy.json denies link loads and file paths to run_revit_code.
//
//   Plan     resolves each named link type (name or id, host model only, top-level links only), reads its state and
//            decides: already loaded (nothing), Reload from its stored path, or the stored file is missing / the link
//            is not found -> the host file picker (Windows open-file dialog; ask_user has no file field) -> LoadFrom.
//            With no links and a path it plans a new link (RevitLinkType.Create + RevitLinkInstance.Create).
//            Every refusal names its reason and nothing has changed. The picked path is remembered per call key, so
//            the re-plan at commit does not open the dialog again.
//   Execute  runs Revit's own Load / LoadFrom / Create with NO transaction or group open (Revit forbids it inside one),
//            reads every link's status back (verified: exists), and reports what loaded and what did not.
//
// IDirectWriteTool: Revit clears its whole Undo list when a link loads, so this write has no preview, no Undo entry and
// never shares a batch with other writes. The envelope says so. Worksharing and a linked file of another Revit version
// are not verified in a live Revit yet (AI-529 notes).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using BinaVibe.Copilot.Read;
using BinaVibe.Copilot.Undo;
using BinaVibe.Copilot.Writes;

namespace BinaVibe.Copilot.Tools
{
    public sealed class LoadLinkTool : IDirectWriteTool
    {
        private sealed class Item
        {
            public long TypeId;           // 0 for a link that does not exist yet
            public string Name = "";
            public LinkAction Action;
            public string StatusBefore = "";
            public string? Path;          // the file LoadFrom / Create reads
            public bool Picked;           // the drafter chose Path in the file picker
        }

        private sealed class PlanNotes
        {
            public List<Item> Items = new();
        }

        private static readonly ConditionalWeakTable<WritePlan, PlanNotes> Notes = new();

        /// <summary>Paths the drafter picked, per call key and link, so the commit's re-plan does not ask again.</summary>
        private static readonly ConcurrentDictionary<string, string> Picked = new();

        /// <summary>The host file picker; Windows' open-file dialog unless a test (or another host) sets one.</summary>
        public static ILinkFilePicker? Picker { get; set; }

        public string Name => ToolNames.LoadLink;

        public ToolKind Kind => ToolKind.Write;

        /// <summary>load_link changes the model, so it never runs outside a write request.</summary>
        public Envelope Run(ToolContext ctx, JsonElement args) =>
            WriteEnvelopes.Failure(ErrorCodes.NotAllowed,
                "load_link changes the model, so it runs only in a write request.",
                EnvelopePhase.Read);

        public WritePlan Plan(ToolContext ctx, JsonElement args)
        {
            var a = LoadLinkArgs.Parse(args);
            var doc = ctx.Doc;
            if (doc.IsFamilyDocument)
                throw new WriteRefusal(ErrorCodes.NotAllowed, "Links load into a project, and the active document is a family; nothing was loaded.");

            var notes = new PlanNotes();
            var intended = new SortedDictionary<string, string>(StringComparer.Ordinal);

            if (a.Links.Count == 0)
            {
                var path = a.Path!;
                if (!File.Exists(path))
                    throw new WriteRefusal(ErrorCodes.NotFound, "There is no file at '" + path + "'; nothing was loaded.", Array.Empty<string>());
                var item = new Item { Name = LinkNames.Display(System.IO.Path.GetFileName(path)), Action = LinkAction.LoadFrom, StatusBefore = "new", Path = path };
                notes.Items.Add(item);
                intended["new:" + path] = "create|" + path;
            }
            else
            {
                var types = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                    .Where(t => !t.IsNestedLink).OrderBy(t => (long)t.Id.Value).ToList();
                foreach (var query in a.Links)
                {
                    var type = Resolve(types, query);
                    var name = LinkNames.Display(type.Name);
                    var status = KindOf(type.GetLinkedFileStatus());
                    var stored = StoredFileExists(doc, type);
                    var decision = LinkLoadRules.Decide(status, stored, a.Path, name);
                    var item = new Item { TypeId = (long)type.Id.Value, Name = name, Action = decision.Action, StatusBefore = type.GetLinkedFileStatus().ToString() };
                    switch (decision.Action)
                    {
                        case LinkAction.Refuse:
                            throw new WriteRefusal(ErrorCodes.NotAllowed, decision.Reason ?? "load_link cannot load link '" + name + "'; nothing was loaded.");
                        case LinkAction.LoadFrom:
                            item.Path = a.Path;
                            break;
                        case LinkAction.Pick:
                            item.Path = PickFile(ctx, item);
                            item.Action = LinkAction.LoadFrom;
                            item.Picked = true;
                            break;
                    }
                    if (item.Path != null && !File.Exists(item.Path))
                        throw new WriteRefusal(ErrorCodes.NotFound, "There is no file at '" + item.Path + "' for link '" + name + "'; nothing was loaded.", Array.Empty<string>());
                    notes.Items.Add(item);
                    intended[item.TypeId.ToString(CultureInfo.InvariantCulture)] = item.Action + (item.Path != null ? "|" + item.Path : "");
                }
            }

            var plan = new WritePlan
            {
                Tool = ToolNames.LoadLink,
                CanonicalArgs = args.GetRawText(),
                // No element is edited (a link load changes no owned element), so no ownership check applies.
                TargetIds = Array.Empty<long>(),
                IntendedValues = new Dictionary<string, string>(intended),
                Previewable = false,
            };
            Notes.Remove(plan);
            Notes.Add(plan, notes);
            return plan;
        }

        public Envelope Execute(ToolContext ctx, JsonElement args, WritePlan plan, WritePhase phase)
        {
            if (!Notes.TryGetValue(plan, out var notes))
                throw new WriteRefusal(ErrorCodes.RuntimeError, "load_link lost its plan; nothing was loaded.");
            var doc = ctx.Doc;
            ForgetPicks(ctx);

            var rows = new List<object>();
            var failures = new List<string>();
            var loadedIds = new List<(long id, string name)>();
            var already = 0;
            foreach (var item in notes.Items)
            {
                var row = new Dictionary<string, object?>
                {
                    ["name"] = item.Name,
                    ["id"] = item.TypeId == 0 ? (long?)null : item.TypeId,
                    ["status_before"] = item.StatusBefore,
                    ["action"] = ActionText(item),
                    ["path"] = item.Path,
                    ["picked_by_drafter"] = item.Picked ? (bool?)true : null,
                };
                rows.Add(row);
                if (item.Action == LinkAction.None) { already++; row["result"] = "already loaded"; continue; }
                try
                {
                    LinkLoadResult result;
                    if (item.TypeId == 0)
                    {
                        var created = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(item.Path!), new RevitLinkOptions(false));
                        result = created;
                        if (created.LoadResult == LinkLoadResultType.LinkLoaded) PlaceInstance(doc, created.ElementId, item.Name);
                    }
                    else
                    {
                        var type = doc.GetElement(ElemIds.From(item.TypeId)) as RevitLinkType
                                   ?? throw new WriteRefusal(ErrorCodes.NotFound, "Link '" + item.Name + "' no longer exists; nothing was loaded.", Array.Empty<string>());
                        result = item.Action == LinkAction.Reload
                            ? type.Load()
                            : type.LoadFrom(ModelPathUtils.ConvertUserVisiblePathToModelPath(item.Path!), new WorksetConfiguration());
                    }

                    var ok = result.LoadResult == LinkLoadResultType.LinkLoaded;
                    row["result"] = ok ? "loaded" : LinkLoadRules.Explain(result.LoadResult.ToString());
                    if (ok) loadedIds.Add((result.ElementId == ElementId.InvalidElementId ? item.TypeId : (long)result.ElementId.Value, item.Name));
                    else failures.Add("'" + item.Name + "': " + LinkLoadRules.Explain(result.LoadResult.ToString()));
                }
                catch (WriteRefusal)
                {
                    throw;
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.InvalidOperationException || ex is Autodesk.Revit.Exceptions.ArgumentException || ex is System.IO.IOException)
                {
                    row["result"] = "failed: " + ex.Message;
                    failures.Add("'" + item.Name + "': " + ex.Message);
                }
            }

            // Read every status back from the document: loaded means Revit says Loaded now.
            var mismatches = new List<MismatchDto>();
            foreach (var (id, name) in loadedIds)
            {
                var now = (doc.GetElement(ElemIds.From(id)) as RevitLinkType)?.GetLinkedFileStatus();
                if (now != LinkedFileStatus.Loaded)
                    mismatches.Add(new MismatchDto { Id = id, Field = "link status", Expected = "Loaded", Actual = now?.ToString() ?? "missing" });
            }
            var loaded = loadedIds.Count - mismatches.Count;

            var data = new Dictionary<string, object?>
            {
                ["links"] = rows,
                ["loaded"] = loaded,
                ["failed"] = failures.Count + mismatches.Count,
                ["already_loaded"] = already,
                ["undo"] = "not available: Revit clears its Undo list when a link loads",
            };

            if (loaded == 0 && already == 0)
                return WriteEnvelopes.Failure(ErrorCodes.RuntimeError,
                    "No link was loaded, and nothing changed. " + string.Join("; ", failures.Concat(mismatches.Select(m => "'" + m.Id + "' reads " + m.Actual))) + ".",
                    EnvelopePhase.Commit);
            if (loaded == 0)
            {
                var noop = EnvelopeBuilder.Write(WritePhase.Commit, "Nothing to load: " + (already == 1 ? "the link is" : "all " + already.ToString(CultureInfo.InvariantCulture) + " links are") + " already loaded.")
                    .Status(EnvelopeStatus.Noop).WithData(data).WithNext(WriteEnvelopes.NoChangeNext);
                return noop.Build();
            }

            var problems = failures.Count + mismatches.Count;
            var summary = "Loaded " + loaded.ToString(CultureInfo.InvariantCulture) + (loaded == 1 ? " link" : " links") +
                          " (" + string.Join(", ", loadedIds.Where(l => mismatches.All(m => m.Id != l.id)).Select(l => l.name).Take(5)) + ")." +
                          (already > 0 ? " " + already.ToString(CultureInfo.InvariantCulture) + " already loaded." : "") +
                          (problems > 0 ? " " + problems.ToString(CultureInfo.InvariantCulture) + " not loaded: " + string.Join("; ", failures.Take(3)) + "." : "");
            return EnvelopeBuilder.Write(WritePhase.Commit, EnvelopeFactory.Shorten(summary, EnvelopeLimits.SummaryMaxChars))
                .Status(problems > 0 ? EnvelopeStatus.Partial : EnvelopeStatus.Done)
                .WithCount(loaded)
                .WithData(data)
                .WithVerified(new VerifiedDto { Method = VerifyMethods.Exists, Checked = loadedIds.Count, Mismatches = mismatches })
                .WithNext("Revit cleared its Undo list, so this load cannot be undone with Undo and earlier Undo entries are gone: tell the drafter. Check with model_overview sections [\"links\"].")
                .Build();
        }

        // ------------------------------------------------------------------

        private static RevitLinkType Resolve(List<RevitLinkType> types, string query)
        {
            if (long.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                var byId = types.FirstOrDefault(t => (long)t.Id.Value == id);
                if (byId != null) return byId;
            }
            var displays = types.Select(t => LinkNames.Display(t.Name)).ToList();
            var hit = LinkNames.Match(query, displays);
            var matched = types.Where(t => hit.Contains(LinkNames.Display(t.Name), StringComparer.OrdinalIgnoreCase)).ToList();
            if (matched.Count == 1) return matched[0];
            if (matched.Count == 0)
                throw new WriteRefusal(ErrorCodes.NotFound,
                    "No Revit link named or numbered '" + query + "' in this model; nothing was loaded. Links: " + (displays.Count == 0 ? "none" : string.Join(", ", displays.Take(20))) + ".",
                    displays.Take(20));
            throw new WriteRefusal(ErrorCodes.InvalidArgs,
                "'" + query + "' names " + matched.Count.ToString(CultureInfo.InvariantCulture) + " links (" + string.Join(", ", matched.Select(t => LinkNames.Display(t.Name) + " #" + ((long)t.Id.Value).ToString(CultureInfo.InvariantCulture)).Take(10)) +
                "); give the exact name or the id.");
        }

        private static LinkStatusKind KindOf(LinkedFileStatus s) => s switch
        {
            LinkedFileStatus.Loaded => LinkStatusKind.Loaded,
            LinkedFileStatus.Unloaded or LinkedFileStatus.LocallyUnloaded => LinkStatusKind.Unloaded,
            LinkedFileStatus.NotFound => LinkStatusKind.NotFound,
            LinkedFileStatus.InClosedWorkset => LinkStatusKind.ClosedWorkset,
            _ => LinkStatusKind.Other,
        };

        /// <summary>False only when the link's stored path is a local path with no file there. A server or cloud path (or one Revit
        /// cannot tell us) counts as present: Revit decides when it loads.</summary>
        private static bool StoredFileExists(Document doc, RevitLinkType type)
        {
            try
            {
                var reference = ExternalFileUtils.GetExternalFileReference(doc, type.Id);
                var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
                return !LinkLoadRules.IsLocalPath(visible) || File.Exists(visible);
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static string PickFile(ToolContext ctx, Item item)
        {
            var key = ctx.IdempotencyKey + "|" + item.TypeId.ToString(CultureInfo.InvariantCulture);
            if (Picked.TryGetValue(key, out var kept)) return kept;
            var picker = Picker ?? new WindowsLinkFilePicker();
            var chosen = picker.Pick(item.Name);
            if (string.IsNullOrWhiteSpace(chosen))
                throw new WriteRefusal(ErrorCodes.NotAllowed, "No file was chosen for link '" + item.Name + "' (the file picker was closed); nothing was loaded.");
            var problem = LoadLinkArgs.PathProblem(chosen!);
            if (problem != null) throw new WriteRefusal(ErrorCodes.InvalidArgs, "The chosen file for link '" + item.Name + "' cannot be used: " + problem);
            if (Picked.Count > 64) Picked.Clear();
            Picked[key] = chosen!;
            return chosen!;
        }

        private static void ForgetPicks(ToolContext ctx)
        {
            var prefix = ctx.IdempotencyKey + "|";
            foreach (var k in Picked.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) Picked.TryRemove(k, out _);
        }

        /// <summary>RevitLinkType.Create loads the file but places nothing: the instance is a normal model change, so it gets its own transaction.</summary>
        private static void PlaceInstance(Document doc, ElementId typeId, string name)
        {
            using var tx = new Transaction(doc, BinaTransactionNames.For("Place link " + name));
            try
            {
                BinaVibe.Mcp.Tools.TxGuard.StartSwallowing(tx);
                RevitLinkInstance.Create(doc, typeId);
                BinaVibe.Mcp.Tools.TxGuard.CommitOrThrow(tx);
            }
            finally
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            }
        }

        private static string ActionText(Item item) => item.Action switch
        {
            LinkAction.None => "none",
            LinkAction.Reload => "reload from the stored path",
            LinkAction.LoadFrom => item.TypeId == 0 ? "add a new link" : item.Picked ? "load from the file the drafter picked" : "load from the given path",
            _ => item.Action.ToString(),
        };
    }

    /// <summary>The default picker: Windows' open-file dialog on Revit's thread.</summary>
    internal sealed class WindowsLinkFilePicker : ILinkFilePicker
    {
        public string? Pick(string linkName)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the Revit file for link '" + linkName + "'",
                Filter = "Revit models (*.rvt)|*.rvt",
                CheckFileExists = true,
                Multiselect = false,
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }
}

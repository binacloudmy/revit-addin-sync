#nullable enable
// Mode decision for run_revit_code (W4 Task 3, spec §7.8 "The mode is decided by
// the add-in, whatever the request says"): any symbol that could change the
// document makes the call a write (previewed, approved, committed); otherwise
// it is a read (run at once inside a rolled-back group). Also the typed
// arguments (ruling R14), the WritePlan handed to W3's runner (ruling R17:
// IntendedValues["mode"]), and the envelopes the tool returns. Snippets bind
// against the Nice3point reference assemblies; nothing runs in Revit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class ModeClassifierTests
    {
        private static ModeDecision Classify(string code)
        {
            var a = CodeTestSupport.AnalyzeProduction(code);
            Assert.True(a.Allowed, CodeTestSupport.Describe(a));
            return a.Mode!;
        }

        [Theory]
        [InlineData("var n = new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount();\nPrint(n);")]
        [InlineData("var line = Line.CreateBound(XYZ.Zero, new XYZ(1, 0, 0));\nvar t = Transform.CreateTranslation(new XYZ(0, 1, 0));\nPrint(line.CreateTransformed(t).Length);")]
        [InlineData("var ogs = new OverrideGraphicSettings();\nogs.SetProjectionLineColor(new Color(255, 0, 0));\nPrint(ogs.ProjectionLineColor.Red);")]
        [InlineData("var opt = new Options { ComputeReferences = true };\nvar box = new BoundingBoxXYZ { Min = XYZ.Zero, Max = new XYZ(1, 1, 1) };\nPrint(opt.ComputeReferences + \" \" + box.Max.X);")]
        [InlineData("var ids = uidoc.Selection.GetElementIds();\nuidoc.Selection.SetElementIds(ids);\nuidoc.ShowElements(ids);")]
        [InlineData("var mm = UnitUtils.ConvertFromInternalUnits(1.0, UnitTypeId.Millimeters);\nvar rule = ParameterFilterRuleFactory.CreateEqualsRule(new ElementId(BuiltInParameter.ALL_MODEL_MARK), \"A\");\nvar f = new ElementParameterFilter(rule);\nPrint(mm + \" \" + new FilteredElementCollector(doc).WherePasses(f).GetElementCount());")]
        [InlineData("var outline = new Outline(XYZ.Zero, XYZ.Zero);\noutline.AddPoint(new XYZ(1, 1, 1));\nvar loop = new CurveLoop();\nloop.Append(Line.CreateBound(XYZ.Zero, new XYZ(1, 0, 0)));\nvar arr = new CurveArray();\narr.Append(Line.CreateBound(XYZ.Zero, new XYZ(0, 1, 0)));\nPrint(arr.Size);")]
        [InlineData("var e = doc.GetElement(new ElementId(1L));\nPrint(e?.LookupParameter(\"Mark\")?.AsString() ?? \"-\");\nPrint(e?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsValueString());")]
        [InlineData(CodePolicyTests.TypicalReadQuery)]
        public void ReadOnlyCodeIsRead(string code)
        {
            var d = Classify(code);
            Assert.True(d.Mode == CodeMode.Read, "expected read, got write because " + string.Join("; ", d.Reasons));
            Assert.Empty(d.Reasons);
        }

        [Theory]
        [InlineData("var w = new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstElement();\nw.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(\"x\");", "Set", 2)]
        [InlineData("doc.Delete(new ElementId(1L));", "Delete", 1)]
        [InlineData("var e = doc.GetElement(new ElementId(1L));\ne.Name = \"renamed\";", "Name", 2)]
        [InlineData("var l = Line.CreateBound(XYZ.Zero, new XYZ(1, 0, 0));\nvar c = doc.Create.NewDetailCurve(view, l);", "Create", 2)]
        [InlineData("ElementTransformUtils.MoveElement(doc, new ElementId(1L), new XYZ(1, 0, 0));", "MoveElement", 1)]
        [InlineData("view.SetElementOverrides(new ElementId(1L), new OverrideGraphicSettings());", "SetElementOverrides", 1)]
        [InlineData("var tb = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElementId();\nvar s = ViewSheet.Create(doc, tb);", "Create", 2)]
        [InlineData("doc.Regenerate();", "Regenerate", 1)]
        [InlineData("var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).FirstElementId();\nvar line = Line.CreateBound(XYZ.Zero, new XYZ(10, 0, 0));\nvar wall = Wall.Create(doc, line, level, false);", "Create", 3)]
        [InlineData("var p = doc.GetElement(new ElementId(1L)).LookupParameter(\"Mark\");\np.SetValueString(\"A-1\");", "SetValueString", 2)]
        [InlineData("var fi = doc.GetElement(new ElementId(1L)) as FamilyInstance;\nfi.flipHand();", "flipHand", 2)]
        [InlineData("var pf = PhaseFilter.Create(doc, \"x\");", "Create", 1)]
        [InlineData("var e = doc.GetElement(new ElementId(1L));\ne.Pinned = true;", "Pinned", 2)]
        [InlineData("var cat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Walls);\ncat.LineColor = new Color(1, 2, 3);", "LineColor", 2)]
        public void DocumentChangingCodeIsWrite(string code, string symbol, int line)
        {
            var d = Classify(code);
            Assert.Equal(CodeMode.Write, d.Mode);
            Assert.Contains(d.Reasons, r => r.Symbol.Contains(symbol) && r.Line == line);
        }

        /// <summary>W5's vetted commands moved onto the host: a rename or parameter edit run as a read would be
        /// rolled back while printing "N renamed". They must take the preview/confirm path.</summary>
        [Theory]
        [InlineData("var n = 0;\nforeach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate))\n{\n    v.Name = \"BINA - \" + v.Name;\n    n++;\n}\nPrint(n + \" renamed\");", "Name", 4)]
        [InlineData("var e = doc.GetElement(new ElementId(1L));\ne.Name = \"renamed\";", "Name", 2)]
        [InlineData("var e = doc.GetElement(new ElementId(1L));\ne.LookupParameter(\"Mark\").Set(\"A-1\");", "Set", 2)]
        [InlineData("var e = doc.GetElement(new ElementId(1L));\ne.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS).Set(\"x\");", "Set", 2)]
        [InlineData("var e = doc.GetElement(new ElementId(1L));\ne.LookupParameter(\"Height\").SetValueString(\"2700 mm\");", "SetValueString", 2)]
        [InlineData("var e = doc.GetElement(new ElementId(1L));\nvar t = e.GetValidTypes().First();\ne.ChangeTypeId(t);", "ChangeTypeId", 3)]
        [InlineData("var ids = new List<ElementId> { new ElementId(1L) };\nElement.ChangeTypeId(doc, ids, new ElementId(2L));", "ChangeTypeId", 2)]
        // fix round 1, I1: deconstruction assignments are writes too
        [InlineData("var w = doc.GetElement(new ElementId(1L));\nint k;\n(w.Name, k) = (\"X\", 0);", "Name", 3)]
        [InlineData("var w = doc.GetElement(new ElementId(1L));\nint k, j;\n((w.Name, k), j) = ((\"X\", 0), 1);", "Name", 3)]
        [InlineData("var w = doc.GetElement(new ElementId(1L));\nint k;\n(k, w.Pinned) = (0, true);", "Pinned", 3)]
        public void RenamesAndParameterEditsAreWrites(string code, string symbol, int line)
        {
            var d = Classify(code);
            Assert.Equal(CodeMode.Write, d.Mode);
            Assert.Contains(d.Reasons, r => r.Symbol.Contains(symbol) && r.Line == line);
            RunRevitCodeArgs.TryParse(Json(JsonSerializer.Serialize(new { code, intent = "edit" })), out var args, out _);
            Assert.Equal(CodePlans.Write, CodePlans.Build(args!, CodeRunner.Prepare(code, CodeTestSupport.ProductionRefs())).IntendedValues[CodePlans.ModeKey]);
        }

        /// <summary>UI requests are not model writes: read mode. Per ruling R59 they never run inside the code:
        /// the host shims queue them and apply them after the always-rolled-back group (RevitCodeHost).</summary>
        [Theory]
        [InlineData("var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate);\nuidoc.RequestViewChange(plan);")]
        [InlineData("var ids = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Doors).ToElementIds();\nuidoc.ShowElements(ids);")]
        [InlineData("uidoc.Selection.SetElementIds(new List<ElementId> { new ElementId(1L) });")]
        [InlineData("uidoc.Selection.SetReferences(new List<Reference>());")]
        [InlineData("foreach (var v in uidoc.GetOpenUIViews()) { v.ZoomToFit(); v.Zoom(1.5); v.ZoomSheetSize(); v.ZoomAndCenterRectangle(XYZ.Zero, new XYZ(1, 1, 0)); }")]
        public void UiRequestsAreReadAndQueued(string code)
        {
            var d = Classify(code);
            Assert.Equal(CodeMode.Read, d.Mode);
            Assert.True(d.RequestsUi);
            RunRevitCodeArgs.TryParse(Json(JsonSerializer.Serialize(new { code, intent = "open view" })), out var args, out _);
            var plan = CodePlans.Build(args!, CodeRunner.Prepare(code, CodeTestSupport.ProductionRefs()));
            Assert.Equal(CodePlans.Read, plan.IntendedValues[CodePlans.ModeKey]);
        }

        [Fact]
        public void UiRequestsAreRoutedThroughTheHostShim()
        {
            var code = "var v = view;\nuidoc.RequestViewChange(v);\nuidoc\n    .ShowElements(new List<ElementId> { v.Id });\nuidoc.ShowElements(v);\nuidoc.Selection.SetElementIds(new List<ElementId>());";
            var prepared = CodeRunner.Prepare(code, CodeTestSupport.ProductionRefs());
            Assert.True(prepared.Snippet != null, prepared.Failure?.Message);
            var text = prepared.Snippet!.Wrapped.ModelCode;
            Assert.Contains("__RequestViewChange(uidoc, v)", text);
            Assert.Contains("__ShowElements(uidoc", text);
            Assert.Contains("__SetElementIds(uidoc.Selection, ", text);
            Assert.DoesNotContain(".RequestViewChange(", text);
            Assert.DoesNotContain(".SetElementIds(", text);

            // Fix round 2, N4: SetReferences and the UIView zoom calls take the same route.
            var zoom = CodeRunner.Prepare(
                "uidoc.Selection.SetReferences(new List<Reference>());\nforeach (var uv in uidoc.GetOpenUIViews())\n{\n    uv.ZoomToFit(); uv.Zoom(1.5); uv.ZoomSheetSize();\n    uv.ZoomAndCenterRectangle(XYZ.Zero, new XYZ(1, 1, 0));\n}",
                CodeTestSupport.ProductionRefs());
            Assert.True(zoom.Snippet != null, zoom.Failure?.Message);
            var z = zoom.Snippet!.Wrapped.ModelCode;
            Assert.Contains("__SetReferences(uidoc.Selection, ", z);
            Assert.Contains("__ZoomToFit(uv)", z);
            Assert.Contains("__Zoom(uv, 1.5)", z);
            Assert.Contains("__ZoomSheetSize(uv)", z);
            Assert.Contains("__ZoomAndCenterRectangle(uv, XYZ.Zero, new XYZ(1, 1, 0))", z);
            Assert.DoesNotContain(".Zoom", z);
            Assert.Equal(prepared.Analysis.Wrapped.Tree.GetText().Lines.Count, prepared.Snippet.Wrapped.Tree.GetText().Lines.Count);
        }

        [Fact]
        public void UiQueue_LastViewWins_ShownAreUnioned_GoneIdsAreDropped()
        {
            var q = new UiRequestQueue();
            q.Add(UiRequestQueue.View, new[] { 10L });
            q.Add(UiRequestQueue.Show, new[] { 1L, 2L });
            q.Add(UiRequestQueue.Select, new[] { 5L });
            q.Add(UiRequestQueue.View, new[] { 20L });
            q.Add(UiRequestQueue.Show, new[] { 2L, 3L });
            q.Add(UiRequestQueue.Select, new[] { 6L, 7L });
            var exists = new HashSet<long> { 10, 20, 1, 2, 6 };
            var plan = q.Plan(exists.Contains, id => id == 10 || id == 20);
            Assert.Equal(20L, plan.ViewId);
            Assert.Equal(new long[] { 1, 2 }, plan.Show);
            Assert.True(plan.HasSelection);
            Assert.Equal(new long[] { 6 }, plan.Select);
            Assert.Equal(new long[] { 3, 7 }, plan.Dropped);
            Assert.Equal(6, q.Count);

            var gone = new UiRequestQueue();
            gone.Add(UiRequestQueue.View, new[] { 99L });
            var p2 = gone.Plan(_ => false, _ => false);
            Assert.Null(p2.ViewId);
            Assert.Equal(new long[] { 99 }, p2.Dropped);
            Assert.False(new UiRequestQueue().Plan(_ => true, _ => true).Any);
        }

        [Fact]
        public void UiQueue_ReferencesAndZoomsDropWhatIsGone()
        {
            var q = new UiRequestQueue();
            q.AddReferences(new[] { (1L, "a:1"), (9L, "a:9") });
            q.AddReferences(new[] { (2L, "a:2"), (8L, "a:8") });            // the last selection of references wins
            q.AddZoom(100L, UiRequestQueue.ZoomToFit);
            q.AddZoom(100L, UiRequestQueue.ZoomFactor, 1.5);
            q.AddZoom(200L, UiRequestQueue.ZoomSheetSize);                  // view 200 is not open any more
            var exists = new HashSet<long> { 1, 2, 100, 200 };
            var plan = q.Plan(exists.Contains, _ => false, viewId => viewId == 100);
            Assert.True(plan.HasReferences);
            Assert.Equal(new[] { "a:2" }, plan.References.Select(r => r.Stable).ToArray());
            Assert.Equal(2, plan.Zooms.Count);
            Assert.All(plan.Zooms, zz => Assert.Equal(100L, zz.ViewId));
            Assert.Equal(1.5, plan.Zooms[1].Args[0]);
            Assert.Equal(new long[] { 8, 200 }, plan.Dropped);
        }

        [Fact]
        public void ReadModeShowElementsRunsExactlyOnce()
        {
            // One owner for read-mode UI (W3 7dd3647 also runs AfterCommit after a read-mode code call): W4
            // applies it after its own rollback and never enqueues it, so W3 running the list adds nothing.
            var q = new UiRequestQueue();
            q.Add(UiRequestQueue.Show, new[] { 1L, 2L });
            var afterCommit = new List<Action>();
            var applied = 0;
            var result = UiDispatch.Deliver(UiRequestQueue.DeliveryFor(null), q, afterCommit, () => applied++);
            Assert.Equal(UiDispatchResult.AppliedNow, result);
            foreach (var action in afterCommit) action();   // what W3 does after the call
            Assert.Equal(1, applied);
            Assert.Empty(afterCommit);
        }

        [Fact]
        public void CommitModeUiRunsOnlyAfterTheCommitAndPreviewNever()
        {
            var q = new UiRequestQueue();
            q.Add(UiRequestQueue.View, new[] { 5L });
            var afterCommit = new List<Action>();
            var applied = 0;
            Assert.Equal(UiDispatchResult.Skipped, UiDispatch.Deliver(UiRequestQueue.DeliveryFor(WritePhase.Preview), q, afterCommit, () => applied++));
            Assert.Empty(afterCommit);
            Assert.Equal(UiDispatchResult.QueuedAfterCommit, UiDispatch.Deliver(UiRequestQueue.DeliveryFor(WritePhase.Commit), q, afterCommit, () => applied++));
            Assert.Equal(0, applied);                       // nothing during the commit itself
            Assert.Single(afterCommit);
            afterCommit[0]();                               // W3 runs it after Assimilate
            Assert.Equal(1, applied);
            Assert.Equal(UiDispatchResult.NoHook, UiDispatch.Deliver(UiDelivery.AfterCommit, q, null, () => applied++));
            Assert.Equal(UiDispatchResult.NothingToDo, UiDispatch.Deliver(UiDelivery.AfterRollback, new UiRequestQueue(), afterCommit, () => applied++));
            Assert.Equal(1, applied);
        }

        [Fact]
        public void UiRequests_NeverInAPreview_AfterTheCommitInACommit()
        {
            // Fix round 2, N3 / ruling R76: a commit hands its UI work to ctx.AfterCommit (W3 runs it only
            // after Assimilate); a read applies it after its own rollback; a preview never does.
            Assert.Equal(UiDelivery.AfterRollback, UiRequestQueue.DeliveryFor(null));
            Assert.Equal(UiDelivery.Never, UiRequestQueue.DeliveryFor(WritePhase.Preview));
            Assert.Equal(UiDelivery.AfterCommit, UiRequestQueue.DeliveryFor(WritePhase.Commit));
        }

        [Fact]
        public void PlainReadsRequestNoUi()
        {
            Assert.False(Classify("Print(new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount());").RequestsUi);
            Assert.False(Classify("Print(uidoc.Selection.GetElementIds().Count);").RequestsUi);
        }

        [Theory]
        [InlineData("Unplace")]
        [InlineData("DivideCurve")]
        [InlineData("AllowJoinAtEnd")]
        [InlineData("DisallowJoinAtEnd")]
        [InlineData("PublishCoordinates")]
        [InlineData("AcquireCoordinates")]
        public void MoreWriteVerbs(string name) => Assert.True(ModeClassifier.StartsWithVerb(name, CodePolicySet.Default.WriteVerbs), name);

        [Fact]
        public void AcquireCoordinatesIsAWrite()
        {
            var d = Classify("doc.AcquireCoordinates(new ElementId(1L));");
            Assert.Equal(CodeMode.Write, d.Mode);
        }

        [Fact]
        public void ExpectedChangesForceWriteButNeverRead()
        {
            var read = Classify("Print(doc.Title);");
            Assert.Equal(CodeMode.Write, ModeClassifier.WithExpected(read, new ExpectedChanges(adds: 0, modifies: 2, deletes: 0)).Mode);
            Assert.Equal(CodeMode.Read, ModeClassifier.WithExpected(read, new ExpectedChanges(0, 0, 0)).Mode);
            Assert.Equal(CodeMode.Read, ModeClassifier.WithExpected(read, null).Mode);
            var write = Classify("doc.Regenerate();");
            Assert.Equal(CodeMode.Write, ModeClassifier.WithExpected(write, new ExpectedChanges(0, 0, 0)).Mode);
        }

        [Fact]
        public void ExpectedChangesMakeClocksAndRandomFailToo()
        {
            // A read that uses DateTime.Now is fine; the same code with expected changes is a write (R60).
            RunRevitCodeArgs.TryParse(Json("{\"code\":\"Print(DateTime.Now);\",\"intent\":\"x\",\"expected\":{\"modifies\":1}}"), out var args, out _);
            var prepared = CodeRunner.Prepare(args!.Code, CodeTestSupport.ProductionRefs());
            Assert.NotNull(prepared.Snippet);
            var failure = CodePlans.Failure(args, prepared);
            Assert.NotNull(failure);
            Assert.Equal(ErrorCodes.PolicyRejected, failure!.Code);
            Assert.Contains("DateTime.Now", failure.Message);
            var plan = CodePlans.Build(args, prepared);
            Assert.Equal(CodePlans.Read, plan.IntendedValues[CodePlans.ModeKey]);   // handed straight back, no card
            Assert.Equal(ErrorCodes.PolicyRejected, plan.IntendedValues[CodePlans.GateKey]);

            RunRevitCodeArgs.TryParse(Json("{\"code\":\"Print(DateTime.Now);\",\"intent\":\"x\"}"), out var read, out _);
            Assert.Null(CodePlans.Failure(read!, prepared));
        }

        // ---- typed arguments (ruling R14) ----------------------------------------------------

        private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

        [Fact]
        public void Args_ParseTheContractShape()
        {
            Assert.True(RunRevitCodeArgs.TryParse(Json("{\"code\":\"Print(1);\",\"intent\":\"Count walls\",\"expected\":{\"adds\":2,\"deletes\":1},\"later_key\":true}"),
                out var args, out var error), error);
            Assert.Equal("Print(1);", args!.Code);
            Assert.Equal("Count walls", args.Intent);
            Assert.Equal(2, args.Expected!.Adds);
            Assert.Null(args.Expected.Modifies);
            Assert.Equal(1, args.Expected.Deletes);
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("{\"intent\":\"x\"}")]
        [InlineData("{\"code\":\"   \",\"intent\":\"x\"}")]
        [InlineData("{\"code\":5,\"intent\":\"x\"}")]
        [InlineData("{\"code\":\"Print(1);\"}")]
        [InlineData("{\"code\":\"Print(1);\",\"intent\":\"\"}")]
        [InlineData("{\"code\":\"Print(1);\",\"intent\":\"x\",\"expected\":{\"adds\":-1}}")]
        [InlineData("{\"code\":\"Print(1);\",\"intent\":\"x\",\"expected\":{\"adds\":1.5}}")]
        [InlineData("{\"code\":\"Print(1);\",\"intent\":\"x\",\"expected\":\"many\"}")]
        public void Args_BadInputIsInvalidArgs(string json)
        {
            Assert.False(RunRevitCodeArgs.TryParse(Json(json), out _, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            var e = RunRevitCodeArgs.Invalid(error);
            Assert.Equal(ErrorCodes.InvalidArgs, e.Error!.Code);
            Assert.Empty(EnvelopeInvariants.Violations(e));
        }

        [Fact]
        public void Args_IntentOver120CharactersIsInvalid()
        {
            var json = JsonSerializer.Serialize(new { code = "Print(1);", intent = new string('x', 121) });
            Assert.False(RunRevitCodeArgs.TryParse(Json(json), out _, out _));
            json = JsonSerializer.Serialize(new { code = "Print(1);", intent = new string('x', 120) });
            Assert.True(RunRevitCodeArgs.TryParse(Json(json), out _, out _));
        }

        // ---- the plan for W3's runner (ruling R17) ---------------------------------------------

        [Fact]
        public void Plan_CarriesModeCodeAndExpected()
        {
            RunRevitCodeArgs.TryParse(Json("{\"code\":\"doc.Regenerate();\",\"intent\":\"Regenerate\",\"expected\":{\"adds\":3,\"deletes\":1}}"), out var args, out _);
            var prepared = CodeRunner.Prepare(args!.Code, CodeTestSupport.ProductionRefs());
            var plan = CodePlans.Build(args, prepared);
            Assert.Equal(ToolNames.RunRevitCode, plan.Tool);
            Assert.Equal("write", plan.IntendedValues[CodePlans.ModeKey]);
            Assert.True(plan.Previewable);
            Assert.Equal("doc.Regenerate();", plan.CodeText);
            Assert.Equal(3, plan.ExpectedAddedByCategory[CodePlans.AnyCategory]);
            Assert.Equal(1, plan.ExpectedDeletedByCategory[CodePlans.AnyCategory]);
            Assert.Empty(plan.TargetIds);
            Assert.Contains("\"code\":\"doc.Regenerate();\"", plan.CanonicalArgs);
        }

        [Fact]
        public void Plan_ReadCodeAndFailuresAreHandedStraightBack()
        {
            RunRevitCodeArgs.TryParse(Json("{\"code\":\"Print(doc.Title);\",\"intent\":\"Title\"}"), out var read, out _);
            Assert.Equal("read", CodePlans.Build(read!, CodeRunner.Prepare(read!.Code, CodeTestSupport.ProductionRefs())).IntendedValues[CodePlans.ModeKey]);

            // A rejected or broken snippet never reaches a confirm card: the runner runs it once (read)
            // and Execute returns the failure.
            RunRevitCodeArgs.TryParse(Json("{\"code\":\"System.IO.File.Delete(\\\"x\\\");\",\"intent\":\"x\"}"), out var bad, out _);
            var plan = CodePlans.Build(bad!, CodeRunner.Prepare(bad!.Code, CodeTestSupport.ProductionRefs()));
            Assert.Equal("read", plan.IntendedValues[CodePlans.ModeKey]);
            Assert.NotEqual("ok", plan.IntendedValues[CodePlans.GateKey]);
        }

        [Fact]
        public void Plan_CanonicalArgsIgnoreKeyOrderAndUnknownKeys()
        {
            RunRevitCodeArgs.TryParse(Json("{\"intent\":\"i\",\"code\":\"Print(1);\",\"expected\":{\"deletes\":1,\"adds\":2},\"x\":1}"), out var a, out _);
            RunRevitCodeArgs.TryParse(Json("{\"code\":\"Print(1);\",\"expected\":{\"adds\":2,\"deletes\":1},\"intent\":\"i\"}"), out var b, out _);
            Assert.Equal(a!.Canonical(), b!.Canonical());
        }

        // ---- envelopes ---------------------------------------------------------------------------

        [Fact]
        public void Envelopes_ReadResultsAreValid()
        {
            var output = new OutputBuffer();
            output.Print("12 walls");
            var read = CodeEnvelopes.Read("Count walls", output, new CodeRunResult(),
                new Dictionary<string, object?> { ["applied"] = true, ["shown"] = 2 },
                new[] { new WarningDto { Text = "1 element no longer exists", Severity = WarningSeverity.Warning } });
            Assert.Empty(EnvelopeInvariants.Violations(read));
            Assert.Equal(EnvelopePhase.Read, read.Phase);
            Assert.True(read.Ok);
            Assert.Equal("12 walls", read.Data!.Value.GetProperty("output").GetString());
            Assert.Equal(2, read.Data.Value.GetProperty("ui").GetProperty("shown").GetInt32());
            Assert.Single(read.Warnings!);
        }

        [Fact]
        public void Envelopes_HostedWriteReportsAddsAndDeletesAndLeavesEditsToTheRunner()
        {
            // Inside W3's transaction the tool measures added/deleted ids itself; DocumentChanged (edits,
            // side effects, noop) only exists after the runner commits, so the runner decorates those.
            var output = new OutputBuffer();
            var touched = new ChangesDto
            {
                Added = new List<ElementRef> { new ElementRef(7, null), new ElementRef(8, null) },
                Deleted = new List<ElementRef> { new ElementRef(3, null) },
            };
            var preview = CodeEnvelopes.HostedWrite(WritePhase.Preview, "Add rooms", output, new CodeRunResult(), touched,
                added: 2, deleted: 1, expected: new ExpectedChanges(2, 5, 0), verified: null, handle: null);
            Assert.Empty(EnvelopeInvariants.Violations(preview));
            Assert.Equal(EnvelopeStatus.Done, preview.Status); // never noop: the runner knows whether anything changed
            Assert.Equal(2, preview.Changes!.Touched.Added.Count);
            Assert.Null(preview.Changes.SideEffects);
            Assert.Contains(preview.Warnings!, w => w.Text.Contains("deletions"));          // expected 0 deletions, made 1
            Assert.DoesNotContain(preview.Warnings!, w => w.Text.Contains("modifications")); // unknown until commit

            var commit = CodeEnvelopes.HostedWrite(WritePhase.Commit, "Add rooms", output, new CodeRunResult(), touched,
                added: 2, deleted: 1, expected: null, verified: new VerifiedDto { Method = VerifyMethods.Exists, Checked = 3 }, handle: null);
            Assert.Empty(EnvelopeInvariants.Violations(commit));
            Assert.Equal(VerifyMethods.Exists, commit.Verified!.Method);

            var editsOnly = CodeEnvelopes.HostedWrite(WritePhase.Commit, "Renumber", output, new CodeRunResult(), new ChangesDto(),
                added: 0, deleted: 0, expected: null, verified: new VerifiedDto { Method = VerifyMethods.CountDelta, Checked = 1200 }, handle: null);
            Assert.Empty(EnvelopeInvariants.Violations(editsOnly));
            Assert.Equal(EnvelopeStatus.Done, editsOnly.Status);
        }

        [Fact]
        public void Envelopes_ACommitThatChangedNothingNeverSaysDone()
        {
            // Fix round 3, m1: W3 marks a code call noop after its commit (its DocumentChanged record), so W4's
            // commit summary must be true whether or not anything changed: never "Done: ...".
            var verified = new VerifiedDto { Method = VerifyMethods.CountDelta, Checked = 1200 };
            var nothing = CodeEnvelopes.HostedWrite(WritePhase.Commit, "Renumber doors", new OutputBuffer(), new CodeRunResult(), new ChangesDto(),
                added: 0, deleted: 0, expected: null, verified: verified, handle: null);
            Assert.False(nothing.Summary.StartsWith("Done", StringComparison.OrdinalIgnoreCase), nothing.Summary);
            Assert.StartsWith("Ran: Renumber doors", nothing.Summary);
            nothing.Status = EnvelopeStatus.Noop;               // what W3 does when nothing changed
            Assert.Empty(EnvelopeInvariants.Violations(nothing));
            Assert.DoesNotContain("added", nothing.Summary);   // no count claims a change it did not make

            var some = CodeEnvelopes.HostedWrite(WritePhase.Commit, "Add sheets", new OutputBuffer(), new CodeRunResult(),
                new ChangesDto { Added = new List<ElementRef> { new ElementRef(9, null) } }, added: 1, deleted: 0, expected: null,
                verified: new VerifiedDto { Method = VerifyMethods.Exists, Checked = 1 }, handle: null);
            Assert.False(some.Summary.StartsWith("Done", StringComparison.OrdinalIgnoreCase), some.Summary);
            Assert.Contains("added 1", some.Summary);
        }

        [Fact]
        public void Envelopes_LongChangeListsAreCappedWithAHandle()
        {
            var many = new ChangesDto { Deleted = Enumerable.Range(1, 120).Select(i => new ElementRef(i, null)).ToList() };
            var capped = CodeEnvelopes.Cap(many, out var total);
            Assert.Equal(120, total);
            Assert.Equal(EnvelopeLimits.MaxRows, capped.Deleted.Count);
        }
    }
}

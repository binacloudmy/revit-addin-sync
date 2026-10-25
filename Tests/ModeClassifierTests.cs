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
        public void RenamesAndParameterEditsAreWrites(string code, string symbol, int line)
        {
            var d = Classify(code);
            Assert.Equal(CodeMode.Write, d.Mode);
            Assert.Contains(d.Reasons, r => r.Symbol.Contains(symbol) && r.Line == line);
            RunRevitCodeArgs.TryParse(Json(JsonSerializer.Serialize(new { code, intent = "edit" })), out var args, out _);
            Assert.Equal(CodePlans.Write, CodePlans.Build(args!, CodeRunner.Prepare(code, CodeTestSupport.ProductionRefs())).IntendedValues[CodePlans.ModeKey]);
        }

        /// <summary>Navigation is not a model write: read mode, but marked so the host runs it outside the
        /// always-rolled-back group (RevitCodeHost) and the runner can do the same (IntendedValues["navigates"]).</summary>
        [Theory]
        [InlineData("var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First(v => !v.IsTemplate);\nuidoc.RequestViewChange(plan);")]
        [InlineData("var ids = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Doors).ToElementIds();\nuidoc.ShowElements(ids);")]
        public void NavigationIsReadAndMarked(string code)
        {
            var d = Classify(code);
            Assert.Equal(CodeMode.Read, d.Mode);
            Assert.True(d.Navigates);
            RunRevitCodeArgs.TryParse(Json(JsonSerializer.Serialize(new { code, intent = "open view" })), out var args, out _);
            var plan = CodePlans.Build(args!, CodeRunner.Prepare(code, CodeTestSupport.ProductionRefs()));
            Assert.Equal(CodePlans.Read, plan.IntendedValues[CodePlans.ModeKey]);
            Assert.Equal("true", plan.IntendedValues[CodePlans.NavigatesKey]);
        }

        [Fact]
        public void PlainReadsDoNotNavigate()
        {
            var d = Classify("Print(new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount());");
            Assert.False(d.Navigates);
            Assert.False(Classify("uidoc.Selection.SetElementIds(new List<ElementId>());").Navigates);
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
        public void Envelopes_ReadAndWriteResultsAreValid()
        {
            var output = new OutputBuffer();
            output.Print("12 walls");
            var run = new CodeRunResult();
            var read = CodeEnvelopes.Read("Count walls", output, run);
            Assert.Empty(EnvelopeInvariants.Violations(read));
            Assert.Equal(EnvelopePhase.Read, read.Phase);
            Assert.True(read.Ok);
            Assert.Equal("12 walls", read.Data!.Value.GetProperty("output").GetString());

            var touched = new ChangesDto { Added = new List<ElementRef> { new ElementRef(5, null) } };
            var counts = new ChangeCounts(1, 0, 0);
            var preview = CodeEnvelopes.Write(WritePhase.Preview, "Add a wall", output, run, touched, null, counts,
                new ExpectedChanges(2, null, null), verified: null, handle: null, warnings: Array.Empty<WarningDto>());
            Assert.Empty(EnvelopeInvariants.Violations(preview));
            Assert.Equal(EnvelopePhase.Preview, preview.Phase);
            Assert.Contains(preview.Warnings!, w => w.Text.Contains("expected 2"));

            var verified = new VerifiedDto { Method = VerifyMethods.Exists, Checked = 1 };
            var commit = CodeEnvelopes.Write(WritePhase.Commit, "Add a wall", output, run, touched, null, counts, null, verified, null, Array.Empty<WarningDto>());
            Assert.Empty(EnvelopeInvariants.Violations(commit));
            Assert.True(commit.Ok);
            Assert.Equal(EnvelopeStatus.Done, commit.Status);
        }

        [Fact]
        public void Envelopes_NoDocumentChangeIsANoopWithAWarning()
        {
            var e = CodeEnvelopes.Write(WritePhase.Commit, "Rename", new OutputBuffer(), new CodeRunResult(), new ChangesDto(), null,
                new ChangeCounts(0, 0, 0), null, verified: null, handle: null, warnings: Array.Empty<WarningDto>());
            Assert.Empty(EnvelopeInvariants.Violations(e));
            Assert.Equal(EnvelopeStatus.Noop, e.Status);
            Assert.True(e.Ok);
            Assert.Contains(e.Warnings!, w => w.Text.Contains("changed nothing"));
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
        public void Envelopes_LongChangeListsAreCappedWithAHandle()
        {
            var many = new ChangesDto { Deleted = Enumerable.Range(1, 120).Select(i => new ElementRef(i, null)).ToList() };
            var capped = CodeEnvelopes.Cap(many, out var total);
            Assert.Equal(120, total);
            Assert.Equal(EnvelopeLimits.MaxRows, capped.Deleted.Count);
        }
    }
}

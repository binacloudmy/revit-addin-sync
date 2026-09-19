// BuildRequest / BuildReport / BoxMm / the two build seams are the pane -> builder
// contract for CAD to BIM (spec 2026-09-08 §3 "Build", §5 "Threading"). Pure shapes:
// no XYZ, no Document, no Transaction, no ElementId is constructed or even declared
// here — the Revit API reference is metadata-only. Cad2Bim.Wall is referenced as a
// dictionary key type only.

using System;
using System.Collections.Generic;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace RevitAddinSync.Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimBuildTypesTests
    {
        [Fact]
        public void Report_IsOk_WhenErrorIsNull()
        {
            var report = new BuildReport();
            Assert.True(report.Ok);
            Assert.Null(report.Error);
            Assert.Empty(report.WallIds);          // never null: the session merges it blindly
            Assert.Equal(TimeSpan.Zero, report.Elapsed);
            Assert.Equal(0, report.Walls + report.Doors + report.Windows + report.Rooms
                            + report.SkippedOpenings + report.SkippedWalls);
        }

        [Fact]
        public void Report_IsNotOk_WhenErrorIsSet()
        {
            var report = new BuildReport { Error = "Open a project first." };
            Assert.False(report.Ok);
        }

        [Fact]
        public void Report_WallIds_AreLongs_NotRevitTypes()
        {
            // The type argument is the contract: the session and the VM store ElementId VALUES.
            Assert.Equal(typeof(Dictionary<Cad2Bim.Wall, long>), typeof(BuildReport).GetField("WallIds").FieldType);
        }

        [Fact]
        public void Request_Defaults_AreStoreyHeightAndStack()
        {
            var request = new BuildRequest();
            Assert.Equal(3000, request.HeightMm);
            Assert.Equal(StoreyMode.Stack, request.Storeys);
            Assert.Equal(BuildTarget.AddToProject, request.Target);   // enum zero = the safe target
            Assert.Null(request.LevelId);                               // null = lowest level
            Assert.Equal(typeof(long?), typeof(BuildRequest).GetProperty("LevelId").PropertyType);
            Assert.Null(request.OutputPath);
            Assert.Null(request.TemplatePath);
        }

        [Fact]
        public void BoxMm_IsValueEqual()
        {
            Assert.Equal(new BoxMm(0, 0, 1000, 500), new BoxMm(0, 0, 1000, 500));
            Assert.NotEqual(new BoxMm(0, 0, 1000, 500), new BoxMm(0, 0, 1000, 501));
        }

        [Fact]
        public void BoxMm_Normalised_AcceptsAnyTwoCorners_AndContainsIsInclusive()
        {
            var box = BoxMm.Normalised(10, 20, -5, -8);
            Assert.Equal(new BoxMm(-5, -8, 10, 20), box);
            Assert.Equal(15, box.Width);
            Assert.Equal(28, box.Height);
            Assert.True(box.Contains(10, 20));
            Assert.True(box.Contains(new Cad2Bim.Point(-5, -8)));
            Assert.False(box.Contains(10.001, 0));
        }

        private sealed class FakeSink : IBuildRequestSink
        {
            public BuildRequest Request { get; set; }
            public event Action<BuildReport> Completed;
            public void Fire(BuildReport r) => Completed?.Invoke(r);
        }

        private sealed class FakeRaiser : IBuildEventRaiser
        {
            public bool Accept = true;
            public bool Raise() => Accept;
        }

        [Fact]
        public void Seams_AreImplementableWithoutRevit()
        {
            // The view model (Task 17) is tested against exactly these two fakes.
            var sink = new FakeSink();
            BuildReport seen = null;
            sink.Completed += r => seen = r;
            sink.Request = new BuildRequest { Target = BuildTarget.NewFile };
            sink.Fire(new BuildReport { Walls = 3 });
            Assert.Equal(3, seen.Walls);
            Assert.Equal(BuildTarget.NewFile, sink.Request.Target);

            IBuildEventRaiser raiser = new FakeRaiser { Accept = false };
            Assert.False(raiser.Raise());
        }
    }
}

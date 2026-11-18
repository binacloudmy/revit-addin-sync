using System;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>
    /// "Show in model" for federation issues (14zefhy54k5): the web saves the
    /// camera in shared coordinates, metres; Revit wants internal feet.
    /// </summary>
    public class IssueCameraMathTests
    {
        private const double Ft = 1 / 0.3048;

        // A project whose internal origin sits at shared (1000, 2000, 30) ft and
        // whose internal +X points to shared +Y (rotated 90° CCW), the shape
        // bim-converter's "rvt-shared" placement takes.
        private static IssueCameraMath.Frame InternalToShared() => new IssueCameraMath.Frame
        {
            BasisX = new double[] { 0, 1, 0 },
            BasisY = new double[] { -1, 0, 0 },
            BasisZ = new double[] { 0, 0, 1 },
            Origin = new double[] { 1000, 2000, 30 },
        };

        // Its inverse, written out: shared → internal.
        private static IssueCameraMath.Frame SharedToInternal() => new IssueCameraMath.Frame
        {
            BasisX = new double[] { 0, -1, 0 },
            BasisY = new double[] { 1, 0, 0 },
            BasisZ = new double[] { 0, 0, 1 },
            Origin = new double[] { -2000, 1000, -30 },
        };

        private static readonly IssueCameraMath.Frame SharedFrame = SharedToInternal();
        private static readonly IssueCameraMath.Frame InternalFrame = InternalToShared();

        private static void Near(double[] expected, double[] actual, double tolerance = 1e-6)
        {
            Assert.Equal(3, actual.Length);
            for (int i = 0; i < 3; i++)
                Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance,
                    $"component {i}: expected {expected[i]}, got {actual[i]}");
        }

        [Fact]
        public void Design_issue_camera_only_changes_units()
        {
            var cam = IssueCameraMath.ToRevit(new BinaIssueCamera
            {
                Type = "perspective",
                ViewPoint = new double[] { 3.048, 0, 1.524 },
                Direction = new double[] { 0, 2, 0 },
                UpVector = new double[] { 0, 0, 1 },
            }, IssueCameraMath.Frame.Identity);

            Near(new double[] { 10, 0, 5 }, cam.Eye);
            Near(new double[] { 0, 1, 0 }, cam.Forward);   // normalised
            Near(new double[] { 0, 0, 1 }, cam.Up);
            Assert.Null(cam.HalfHeight);
        }

        [Fact]
        public void Federation_camera_is_moved_from_shared_to_internal()
        {
            // The eye sits 10 ft along internal +X from the internal origin, so in
            // shared coordinates it is at (1000, 2010, 30) ft, saved in metres.
            var sharedEyeMetres = new[] { 1000 / Ft, 2010 / Ft, 30 / Ft };

            var cam = IssueCameraMath.ToRevit(new BinaIssueCamera
            {
                Type = "orthogonal",
                ViewPoint = sharedEyeMetres,
                Direction = new double[] { 0, -1, 0 },   // shared -Y = internal -X
                UpVector = new double[] { 0, 0, 1 },
                ViewToWorldScale = 3.048,               // 10 ft tall
                AspectRatio = 2,
            }, SharedFrame);

            Near(new double[] { 10, 0, 0 }, cam.Eye);
            Near(new double[] { -1, 0, 0 }, cam.Forward);
            Near(new double[] { 0, 0, 1 }, cam.Up);
            Assert.Equal(5, cam.HalfHeight.Value, 6);
            Assert.Equal(10, cam.HalfWidth.Value, 6);
        }

        [Fact]
        public void Picks_the_transform_that_maps_the_base_point_correctly()
        {
            // Project base point at internal (10, 0, 0) is shared (1000, 2010, 30).
            var known = new double[] { 1000, 2010, 30 };
            var internalPoint = new double[] { 10, 0, 0 };

            Assert.Same(SharedFrame, IssueCameraMath.PickSharedToInternal(SharedFrame, InternalFrame, known, internalPoint));
            Assert.Same(SharedFrame, IssueCameraMath.PickSharedToInternal(InternalFrame, SharedFrame, known, internalPoint));
        }

        [Fact]
        public void Picks_nothing_when_neither_transform_fits()
        {
            Assert.Null(IssueCameraMath.PickSharedToInternal(
                SharedFrame, InternalFrame, new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 }));
        }

        [Fact]
        public void Incomplete_camera_gives_null()
        {
            Assert.Null(IssueCameraMath.ToRevit(new BinaIssueCamera { ViewPoint = new double[] { 1, 2 } }, null));
            Assert.Null(IssueCameraMath.ToRevit(new BinaIssueCamera
            {
                ViewPoint = new double[] { 0, 0, 0 },
                Direction = new double[] { 0, 0, 0 },
                UpVector = new double[] { 0, 0, 1 },
            }, null));
        }
    }
}

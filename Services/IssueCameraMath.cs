using System;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// The arithmetic behind "Show in model", kept free of the Revit API so it
    /// can be tested.
    ///
    /// bina-be sends an issue's camera in METRES, Z up. A design issue's camera
    /// was captured on the model alone, so it is already in the model's internal
    /// coordinates. A federation issue's camera was captured in the federated
    /// view, where every model is placed in SHARED coordinates (bim-converter's
    /// "rvt-shared" placement: the model's project location). Pointing Revit at
    /// it unconverted puts the eye where the survey point says the building is,
    /// not where Revit draws it, which for a project with any survey offset is
    /// empty space.
    /// </summary>
    public static class IssueCameraMath
    {
        public const double FeetPerMetre = 1 / 0.3048;

        /// <summary>
        /// An affine map: rotation columns plus origin, in feet. The identity
        /// for a design issue; shared-to-internal for a federation issue.
        /// </summary>
        public sealed class Frame
        {
            public double[] BasisX { get; set; } = { 1, 0, 0 };
            public double[] BasisY { get; set; } = { 0, 1, 0 };
            public double[] BasisZ { get; set; } = { 0, 0, 1 };
            public double[] Origin { get; set; } = { 0, 0, 0 };

            public static Frame Identity => new Frame();

            public double[] OfPoint(double[] p) => Add(OfVector(p), Origin);

            public double[] OfVector(double[] v) => new[]
            {
                BasisX[0] * v[0] + BasisY[0] * v[1] + BasisZ[0] * v[2],
                BasisX[1] * v[0] + BasisY[1] * v[1] + BasisZ[1] * v[2],
                BasisX[2] * v[0] + BasisY[2] * v[1] + BasisZ[2] * v[2],
            };
        }

        /// <summary>A camera ready for Revit: internal coordinates, feet.</summary>
        public sealed class RevitCamera
        {
            public double[] Eye { get; set; }
            public double[] Forward { get; set; }
            public double[] Up { get; set; }
            /// <summary>
            /// Half the visible height and width in feet for an orthographic
            /// camera; null when the camera is perspective or carries no scale.
            /// </summary>
            public double? HalfHeight { get; set; }
            public double? HalfWidth { get; set; }
        }

        /// <summary>
        /// Converts a bina-be camera to Revit's frame, or returns null when the
        /// stored viewpoint is incomplete.
        /// </summary>
        public static RevitCamera ToRevit(BinaIssueCamera camera, Frame toInternal)
        {
            if (camera == null || !Is3(camera.ViewPoint) || !Is3(camera.Direction) || !Is3(camera.UpVector))
                return null;

            var frame = toInternal ?? Frame.Identity;
            var forward = Normalise(frame.OfVector(camera.Direction));
            var up = Normalise(frame.OfVector(camera.UpVector));
            if (forward == null || up == null) return null;

            var result = new RevitCamera
            {
                Eye = frame.OfPoint(Scale(camera.ViewPoint, FeetPerMetre)),
                Forward = forward,
                Up = up,
            };

            bool orthographic = string.Equals(camera.Type, "orthogonal", StringComparison.OrdinalIgnoreCase);
            if (orthographic && camera.ViewToWorldScale is double height && height > 0)
            {
                double aspect = camera.AspectRatio is double a && a > 0 ? a : 1.0;
                result.HalfHeight = height * FeetPerMetre / 2;
                result.HalfWidth = result.HalfHeight * aspect;
            }

            return result;
        }

        /// <summary>
        /// Picks shared-to-internal out of Revit's project-location transform
        /// and its inverse, using a point whose position is known in both frames
        /// (the project base point: Position is internal, SharedPosition is
        /// shared). The right map sends the shared position onto the internal
        /// one. Asking the model beats trusting which way the API's transform is
        /// documented to go. Null when neither fits.
        /// </summary>
        public static Frame PickSharedToInternal(Frame candidate, Frame inverse,
                                                 double[] knownShared, double[] knownInternal,
                                                 double toleranceFeet = 0.01)
        {
            if (candidate == null || inverse == null || !Is3(knownShared) || !Is3(knownInternal)) return null;
            double candidateMiss = Distance(candidate.OfPoint(knownShared), knownInternal);
            double inverseMiss = Distance(inverse.OfPoint(knownShared), knownInternal);
            if (candidateMiss <= toleranceFeet && candidateMiss <= inverseMiss) return candidate;
            if (inverseMiss <= toleranceFeet) return inverse;
            return null;
        }

        public static double[] Cross(double[] a, double[] b) => new[]
        {
            a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0],
        };

        private static bool Is3(double[] v) =>
            v != null && v.Length >= 3 && !double.IsNaN(v[0] + v[1] + v[2]) && !double.IsInfinity(v[0] + v[1] + v[2]);

        private static double[] Scale(double[] v, double k) => new[] { v[0] * k, v[1] * k, v[2] * k };

        private static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };

        private static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double[] Normalise(double[] v)
        {
            double n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return n > 1e-12 ? new[] { v[0] / n, v[1] / n, v[2] / n } : null;
        }
    }
}

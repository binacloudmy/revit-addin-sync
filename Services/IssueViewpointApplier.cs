using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Puts an issue on screen (ClickUp 86d3y5jtz): selects the elements it was
    /// raised against, and restores the camera it was captured from.
    ///
    /// The elements are matched on UniqueId, which is what BINA stores in
    /// `capturedComponents` — the viewer's dbId is meaningless here. A model
    /// that has moved on since the issue was raised will be missing some of
    /// them; that is reported, never silently skipped, because "nothing
    /// happened" and "the element is gone" look identical to a user otherwise.
    ///
    /// Must be called from a Revit API context (an IExternalCommand body or an
    /// ExternalEvent handler).
    /// </summary>
    public static class IssueViewpointApplier
    {
        public sealed class Result
        {
            public int Found { get; set; }
            public int NotFound { get; set; }
            public bool CameraApplied { get; set; }
            /// <summary>An orthographic issue's zoom was applied as well as its angle.</summary>
            public bool Zoomed { get; set; }
            /// <summary>The view was zoomed to fit the elements after the camera was set.</summary>
            public bool FramedOnElements { get; set; }
            public bool SwitchedView { get; set; }
            public string ViewName { get; set; }
            /// <summary>Why the camera was not applied, when it was not.</summary>
            public string CameraNote { get; set; }
        }

        public static Result Apply(UIDocument uidoc, BinaIssueDetail issue)
        {
            if (uidoc == null) throw new ArgumentNullException(nameof(uidoc));
            if (issue == null) throw new ArgumentNullException(nameof(issue));

            var doc = uidoc.Document;
            var result = new Result();

            // A viewpoint needs somewhere to point. Switching is deliberate and
            // reported, rather than quietly changing what the user is looking at.
            var view3d = uidoc.ActiveView as View3D;
            if (view3d == null)
            {
                view3d = FindUsable3DView(doc);
                if (view3d != null)
                {
                    uidoc.ActiveView = view3d;
                    result.SwitchedView = true;
                }
            }
            result.ViewName = (view3d ?? uidoc.ActiveView as View)?.Name;

            var ids = ResolveElements(doc, issue, out int missing);
            result.Found = ids.Count;
            result.NotFound = missing;

            if (view3d == null)
            {
                result.CameraNote = "no 3D view in this model, so the viewpoint could not be restored";
            }
            else
            {
                ApplyCamera(uidoc, view3d, issue, result);
            }

            if (ids.Count > 0)
            {
                uidoc.Selection.SetElementIds(ids);

                // Framing comes AFTER the camera, deliberately. The stored
                // viewpoint gives the direction the issue was raised from, but
                // it can put the eye anywhere — inside the building, below
                // ground, or pointing at a model it was never captured on. Then
                // the right elements are selected and the user is looking at
                // nothing. ShowElements keeps the view direction and zooms to
                // fit the elements, so the issue's angle survives and its
                // subject is always on screen.
                uidoc.ShowElements(ids);
                result.FramedOnElements = true;
            }

            uidoc.RefreshActiveView();
            return result;
        }

        /// <summary>
        /// The elements the issue points at. Selection is what the user cared
        /// about; isolated elements are included because an issue captured with
        /// an isolation often has nothing in `selection`.
        /// </summary>
        private static List<ElementId> ResolveElements(Document doc, BinaIssueDetail issue, out int missing)
        {
            missing = 0;
            var ids = new List<ElementId>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var uniqueIds = (issue.CapturedComponents ?? new List<BinaIssueComponents>())
                .SelectMany(component => (component.Selection ?? new List<string>())
                    .Concat(component.Isolated ?? new List<string>()))
                .Where(id => !string.IsNullOrWhiteSpace(id));

            foreach (var uniqueId in uniqueIds)
            {
                if (!seen.Add(uniqueId)) continue;

                Element element = null;
                try
                {
                    element = doc.GetElement(uniqueId);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA Issues] lookup failed for {uniqueId}: {ex.Message}");
                }

                if (element?.Id != null) ids.Add(element.Id);
                else missing++;
            }

            return ids;
        }

        private static void ApplyCamera(UIDocument uidoc, View3D view3d, BinaIssueDetail issue, Result result)
        {
            var doc = uidoc.Document;
            var camera = issue.Camera;
            if (camera == null)
            {
                result.CameraNote = "this issue carries no viewpoint";
                return;
            }

            if (view3d.IsLocked)
            {
                result.CameraNote = $"the view \"{view3d.Name}\" is locked, so its camera was left alone";
                return;
            }

            // A federation issue was raised in the federated view, which places
            // every model in shared coordinates; a design issue was raised on the
            // model alone, in its internal ones. Only the first needs moving.
            bool federation = string.Equals(issue.Source, "coordination", StringComparison.OrdinalIgnoreCase);
            var toInternal = IssueCameraMath.Frame.Identity;
            if (federation)
            {
                toInternal = SharedToInternal(doc);
                if (toInternal == null)
                {
                    result.CameraNote = "this model's shared coordinates could not be read, so the federated viewpoint could not be placed";
                    Log(issue, camera, null, null, result);
                    return;
                }
            }

            var cam = IssueCameraMath.ToRevit(camera, toInternal);
            if (cam == null)
            {
                result.CameraNote = "the stored viewpoint is incomplete";
                Log(issue, camera, toInternal, null, result);
                return;
            }

            try
            {
                using (var t = new Transaction(doc, "BINA: restore issue viewpoint"))
                {
                    t.Start();
                    view3d.SetOrientation(new ViewOrientation3D(ToXyz(cam.Eye), ToXyz(cam.Up), ToXyz(cam.Forward)));
                    t.Commit();
                }
                result.CameraApplied = true;
            }
            catch (Exception ex)
            {
                result.CameraNote = $"Revit refused the viewpoint: {ex.Message}";
                Log(issue, camera, toInternal, cam, result);
                return;
            }

            // SetOrientation alone is not enough. Called from an ExternalEvent it
            // changes the view's camera but Revit does not redraw it, so the user
            // keeps seeing the old angle until they next zoom (seen in Revit
            // 2027.3: "viewpoint restored", screen unchanged, ZE then showed the
            // new angle). It also keeps the old zoom, which in an orthographic
            // view is most of what a viewpoint is. Re-framing fixes both.
            if (!view3d.IsPerspective)
                Frame(uidoc, view3d, cam, camera, result);

            Log(issue, camera, toInternal, cam, result);
        }

        /// <summary>
        /// Zooms an orthographic Revit view to what the issue's camera showed.
        /// An orthographic issue carries its visible height, so that is used
        /// as-is. A perspective issue does not: the model is fitted along the
        /// issue's direction, then narrowed to what the issue's field of view
        /// covers at the model's distance, which is close to the web's framing.
        /// </summary>
        private static void Frame(UIDocument uidoc, View3D view3d, IssueCameraMath.RevitCamera cam,
                                  BinaIssueCamera sent, Result result)
        {
            try
            {
                var uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view3d.Id);
                if (uiView == null) return;

                var eye = ToXyz(cam.Eye);
                var forward = ToXyz(cam.Forward);
                var up = ToXyz(cam.Up);
                var right = ToXyz(IssueCameraMath.Cross(cam.Forward, cam.Up));

                XYZ center;
                double halfHeight, halfWidth;
                if (cam.HalfHeight.HasValue && cam.HalfWidth.HasValue)
                {
                    center = eye;
                    halfHeight = cam.HalfHeight.Value;
                    halfWidth = cam.HalfWidth.Value;
                }
                else
                {
                    uiView.ZoomToFit();
                    result.Zoomed = true;

                    double fov = sent.FieldOfView ?? 0;
                    var corners = uiView.GetZoomCorners();
                    if (fov <= 0 || fov >= 180 || corners == null || corners.Count < 2) return;

                    var middle = (corners[0] + corners[1]) / 2;
                    double distance = (middle - eye).DotProduct(forward);
                    if (distance <= 0) return;   // the model is behind the camera; keep the fit

                    double aspect = sent.AspectRatio is double a && a > 0 ? a : 1.0;
                    center = eye + forward * distance;
                    halfHeight = distance * Math.Tan(fov * Math.PI / 360);
                    halfWidth = halfHeight * aspect;
                }

                var half = right * halfWidth + up * halfHeight;
                uiView.ZoomAndCenterRectangle(center - half, center + half);
                result.Zoomed = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Issues] framing failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Shared-to-internal for the active project location, checked against
        /// the project base point, whose position Revit knows in both frames.
        /// Null when the model cannot answer.
        /// </summary>
        private static IssueCameraMath.Frame SharedToInternal(Document doc)
        {
            try
            {
                var total = doc.ActiveProjectLocation?.GetTotalTransform();
                var basePoint = BasePoint.GetProjectBasePoint(doc);
                if (total == null || basePoint == null) return null;

                return IssueCameraMath.PickSharedToInternal(
                    ToFrame(total), ToFrame(total.Inverse),
                    ToArray(basePoint.SharedPosition), ToArray(basePoint.Position));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Issues] shared coordinates unreadable: {ex.Message}");
                return null;
            }
        }

        private static IssueCameraMath.Frame ToFrame(Transform t) => new IssueCameraMath.Frame
        {
            BasisX = ToArray(t.BasisX),
            BasisY = ToArray(t.BasisY),
            BasisZ = ToArray(t.BasisZ),
            Origin = ToArray(t.Origin),
        };

        private static double[] ToArray(XYZ p) => new[] { p.X, p.Y, p.Z };

        private static XYZ ToXyz(double[] v) => new XYZ(v[0], v[1], v[2]);

        /// <summary>
        /// One line per "Show in model" in %LOCALAPPDATA%\Bina\RevitSync\issues.log:
        /// what bina-be sent and what Revit was given. A viewpoint that lands in
        /// the wrong place cannot be debugged from a screenshot.
        /// </summary>
        private static void Log(BinaIssueDetail issue, BinaIssueCamera sent, IssueCameraMath.Frame frame,
                                IssueCameraMath.RevitCamera applied, Result result)
        {
            try
            {
                string Fmt(double[] v) => v == null ? "-" : string.Join(",", v.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)));
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "issues.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} show {issue.Guid} source={issue.Source} type={sent?.Type} " +
                    $"sentEye={Fmt(sent?.ViewPoint)}m sentDir={Fmt(sent?.Direction)} scale={sent?.ViewToWorldScale} " +
                    $"frameOrigin={Fmt(frame?.Origin)} eye={Fmt(applied?.Eye)}ft dir={Fmt(applied?.Forward)} " +
                    $"applied={result.CameraApplied} zoomed={result.Zoomed} note={result.CameraNote ?? "-"}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never cost the user the viewpoint.
            }
        }

        /// <summary>
        /// A 3D view the camera can be pointed at: the default {3D} by
        /// preference, otherwise any non-template one.
        /// </summary>
        private static View3D FindUsable3DView(Document doc)
        {
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(view => !view.IsTemplate && !view.IsLocked)
                .ToList();

            return views.FirstOrDefault(view => view.Name == "{3D}")
                ?? views.FirstOrDefault(view => view.Name.StartsWith("{3D", StringComparison.Ordinal))
                ?? views.FirstOrDefault();
        }
    }
}

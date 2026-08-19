using System;
using System.Collections.Generic;
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
        /// <summary>Revit works in feet internally; BINA sends BCF metres.</summary>
        private const double FeetPerMetre = 1 / 0.3048;

        public sealed class Result
        {
            public int Found { get; set; }
            public int NotFound { get; set; }
            public bool CameraApplied { get; set; }
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
                ApplyCamera(doc, view3d, issue.Camera, result);
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

        private static void ApplyCamera(Document doc, View3D view3d, BinaIssueCamera camera, Result result)
        {
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

            var eye = ToFeet(camera.ViewPoint);
            var forward = ToVector(camera.Direction);
            var up = ToVector(camera.UpVector);
            if (eye == null || forward == null || up == null)
            {
                result.CameraNote = "the stored viewpoint is incomplete";
                return;
            }

            try
            {
                using (var t = new Transaction(doc, "BINA: restore issue viewpoint"))
                {
                    t.Start();
                    view3d.SetOrientation(new ViewOrientation3D(eye, up, forward));
                    t.Commit();
                }
                result.CameraApplied = true;
            }
            catch (Exception ex)
            {
                result.CameraNote = $"Revit refused the viewpoint: {ex.Message}";
            }
        }

        /// <summary>A position, metres to Revit's internal feet.</summary>
        private static XYZ ToFeet(double[] value) =>
            value != null && value.Length >= 3
                ? new XYZ(value[0] * FeetPerMetre, value[1] * FeetPerMetre, value[2] * FeetPerMetre)
                : null;

        /// <summary>A direction. Already a unit vector, so no unit conversion.</summary>
        private static XYZ ToVector(double[] value) =>
            value != null && value.Length >= 3 ? new XYZ(value[0], value[1], value[2]) : null;

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

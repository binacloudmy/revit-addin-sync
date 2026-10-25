#nullable enable
// Revit-bound half of look (spec §7.7):
//   elements    TransactionGroup -> one transaction creates a temporary View3D
//               with a section box around the targets (+10%) (multi: also a
//               cropped plan and a section looking north) -> commit ->
//               ExportImage with no transaction open -> group.RollBack(). No
//               undo entry, the drafter's views are untouched.
//   active view ExportRange.VisibleRegionOfCurrentView, no transaction.
//   named view  ExportRange.SetOfViews, no transaction.
// PixelSize 1024, PNG, a private temp folder deleted afterwards; images are
// scaled so the long edge is <= 1024 px; multi panels are stitched side by side
// with WPF imaging. A failed export (no GPU, remote desktop) surfaces as
// NoImageException -> failed: no_gpu.
//
// Everything runs under CallbackScope Look (LookScope), so ExternalRevision
// does not count the temporary transaction.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;
using BinaVibe.Mcp.Tools;
using Wpf = System.Windows.Media;
using WpfImaging = System.Windows.Media.Imaging;

namespace BinaVibe.Copilot.Read
{
    /// <summary>Revit produced no image (no GPU, remote desktop, export error).</summary>
    public sealed class NoImageException : Exception
    {
        public NoImageException(string message, Exception? inner = null) : base(message, inner) { }
    }

    internal static class LookScope
    {
        /// <summary>Enters CallbackScope Look unless one of our scopes is already active
        /// (the read executor's, or W3's preview calling CaptureElements).</summary>
        public static IDisposable? Enter(Document doc) =>
            CallbackScope.Current(doc) == null ? CallbackScope.Enter(doc, CallbackKind.Look) : null;
    }

    internal static class LookCapture
    {
        public const int Gap = 8;
        private const double Pad = 0.05;          // per side: +10% overall
        private const double MinPadFeet = 1.0;

        // ── exports ────────────────────────────────────────────────────────

        public static byte[] Export(Document doc, View? view, int px, bool visibleRegion)
        {
            var dir = Path.Combine(Path.GetTempPath(), "bina_look", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var opts = new ImageExportOptions
                {
                    FilePath = Path.Combine(dir, "look"),
                    ExportRange = visibleRegion ? ExportRange.VisibleRegionOfCurrentView : ExportRange.SetOfViews,
                    FitDirection = FitDirectionType.Horizontal,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = px,
                };
                if (!visibleRegion && view != null) opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                try { doc.ExportImage(opts); }
                catch (Exception ex) { throw new NoImageException("Revit could not export an image: " + ex.Message, ex); }
                // Revit decorates the file name with the view name: take what it wrote.
                var file = Directory.GetFiles(dir, "*.png").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                if (file == null) throw new NoImageException("Revit reported no error but wrote no image.");
                return File.ReadAllBytes(file);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* temp folder; best effort */ }
            }
        }

        // ── imaging (WPF; Revit's UI thread is STA) ───────────────────────

        private static WpfImaging.BitmapSource Decode(byte[] png)
        {
            using var ms = new MemoryStream(png);
            var dec = new WpfImaging.PngBitmapDecoder(ms, WpfImaging.BitmapCreateOptions.PreservePixelFormat, WpfImaging.BitmapCacheOption.OnLoad);
            var frame = dec.Frames[0];
            frame.Freeze();
            return frame;
        }

        private static byte[] Encode(WpfImaging.BitmapSource bmp)
        {
            var enc = new WpfImaging.PngBitmapEncoder();
            enc.Frames.Add(WpfImaging.BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        /// <summary>A PNG with its long edge at most maxPx.</summary>
        public static (byte[] Png, int Width, int Height) Fit(byte[] png, int maxPx)
        {
            if (!Png.TryReadSize(png, out var w, out var h)) throw new NoImageException("The exported file is not a PNG.");
            var (fw, fh) = ImageLayout.FitLongEdge(w, h, maxPx);
            if (fw == w && fh == h) return (png, w, h);
            var src = Decode(png);
            var scaled = new WpfImaging.TransformedBitmap(src, new Wpf.ScaleTransform((double)fw / src.PixelWidth, (double)fh / src.PixelHeight));
            var bytes = Encode(scaled);
            return Png.TryReadSize(bytes, out var rw, out var rh) ? (bytes, rw, rh) : (bytes, fw, fh);
        }

        public static (byte[] Png, int Width, int Height) Stitch(IReadOnlyList<byte[]> panels, int maxPx)
        {
            var bitmaps = panels.Select(Decode).ToList();
            var layout = ImageLayout.SideBySide(bitmaps.Select(b => (b.PixelWidth, b.PixelHeight)).ToList(), maxPx, Gap);
            var visual = new Wpf.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Wpf.Brushes.White, null, new System.Windows.Rect(0, 0, layout.Width, layout.Height));
                for (var i = 0; i < bitmaps.Count; i++)
                {
                    var p = layout.Panels[i];
                    dc.DrawImage(bitmaps[i], new System.Windows.Rect(p.X, 0, p.Width, p.Height));
                }
            }
            var rtb = new WpfImaging.RenderTargetBitmap(layout.Width, layout.Height, 96, 96, Wpf.PixelFormats.Pbgra32);
            rtb.Render(visual);
            return (Encode(rtb), layout.Width, layout.Height);
        }

        // ── elements ───────────────────────────────────────────────────────

        private sealed class Box
        {
            public XYZ Min = XYZ.Zero;
            public XYZ Max = XYZ.Zero;
            public int Count;
            public List<ElementId> HostLevels = new();
        }

        private static Box? Bounds(Document doc, IReadOnlyList<ElementRef> targets, List<string> notes)
        {
            XYZ? min = null, max = null;
            var box = new Box();
            foreach (var r in targets)
            {
                Element? el;
                Transform? tf = null;
                if (r.Link == null) el = doc.GetElement(ElemIds.From(r.Id));
                else
                {
                    var inst = doc.GetElement(ElemIds.From(r.Link.Value)) as RevitLinkInstance;
                    Document? ld = null;
                    try { ld = inst?.GetLinkDocument(); } catch { }
                    el = ld?.GetElement(ElemIds.From(r.Id));
                    tf = inst?.GetTotalTransform();
                }
                var bb = el?.get_BoundingBox(null);
                if (el == null || bb == null) continue;
                box.Count++;
                if (r.Link == null)
                {
                    var lid = RevitValues.LevelIdOf(el);
                    if (lid != ElementId.InvalidElementId) box.HostLevels.Add(lid);
                }
                foreach (var x in new[] { bb.Min.X, bb.Max.X })
                foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                {
                    var p = new XYZ(x, y, z);
                    if (bb.Transform != null && !bb.Transform.IsIdentity) p = bb.Transform.OfPoint(p);
                    if (tf != null) p = tf.OfPoint(p);
                    min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                    max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                }
            }
            if (min == null || max == null) return null;
            if (box.Count < targets.Count) notes.Add((targets.Count - box.Count) + " target(s) have no geometry or were not found (unloaded link?).");
            box.Min = min;
            box.Max = max;
            return box;
        }

        private static ViewFamilyType? Vft(Document doc, ViewFamily family) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == family);

        /// <summary>The image of a 3D view around the targets (multi: plan, 3D and elevation).</summary>
        public static ImageDto Elements(Document doc, IReadOnlyList<ElementRef> targets, int maxPx, bool multi, List<string> notes)
        {
            if (doc.IsReadOnly) throw new InvalidOperationException("The document is read-only, so no temporary 3D view can be made; use target active_view.");
            if (doc.IsModifiable) throw new InvalidOperationException("A transaction is open; look can run only between transactions.");
            var box = Bounds(doc, targets, notes) ?? throw new ArgumentException("None of the targets has geometry to look at.");

            var size = box.Max - box.Min;
            var pad = new XYZ(Math.Max(size.X * Pad, MinPadFeet), Math.Max(size.Y * Pad, MinPadFeet), Math.Max(size.Z * Pad, MinPadFeet));
            var lo = box.Min - pad;
            var hi = box.Max + pad;
            var panels = new List<byte[]>();

            using var group = new TransactionGroup(doc, "BINA: look");
            group.Start();
            try
            {
                View3D view3d;
                ViewPlan? plan = null;
                ViewSection? section = null;
                using (var tx = new Transaction(doc, "BINA: look views"))
                {
                    TxGuard.StartSwallowing(tx);
                    var vft3d = Vft(doc, ViewFamily.ThreeDimensional) ?? throw new InvalidOperationException("The model has no 3D view type.");
                    view3d = View3D.CreateIsometric(doc, vft3d.Id);
                    view3d.SetSectionBox(new BoundingBoxXYZ { Min = lo, Max = hi });
                    try { view3d.DetailLevel = ViewDetailLevel.Fine; } catch { }
                    try { view3d.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                    if (multi)
                    {
                        plan = TryPlan(doc, box, lo, hi, notes);
                        section = TrySection(doc, lo, hi, notes);
                    }
                    TxGuard.CommitOrThrow(tx);
                }

                // No transaction is open here; the group is.
                if (plan != null) panels.Add(Export(doc, plan, EnvelopeLimits.ImageMaxPx, visibleRegion: false));
                panels.Add(Export(doc, view3d, EnvelopeLimits.ImageMaxPx, visibleRegion: false));
                if (section != null) panels.Add(Export(doc, section, EnvelopeLimits.ImageMaxPx, visibleRegion: false));
            }
            finally
            {
                if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            }

            var (png, w, h) = panels.Count > 1 ? Stitch(panels, maxPx) : Fit(panels[0], maxPx);
            var caption = LookText.ElementsCaption(box.Count, size.X * RevitValues.FeetToMetres, size.Y * RevitValues.FeetToMetres, multi && panels.Count > 1);
            return new ImageDto { Base64 = Convert.ToBase64String(png), Width = w, Height = h, Caption = caption };
        }

        private static ViewPlan? TryPlan(Document doc, Box box, XYZ lo, XYZ hi, List<string> notes)
        {
            try
            {
                var vft = Vft(doc, ViewFamily.FloorPlan);
                var levelId = box.HostLevels.GroupBy(i => i.Value).OrderByDescending(g => g.Count()).Select(g => g.First()).FirstOrDefault();
                if (levelId == null)
                {
                    var below = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .Where(l => l.ProjectElevation <= box.Min.Z + 0.5).OrderByDescending(l => l.ProjectElevation).FirstOrDefault();
                    levelId = below?.Id;
                }
                if (vft == null || levelId == null) { notes.Add("No plan panel: no floor plan type or level."); return null; }
                var plan = ViewPlan.Create(doc, vft.Id, levelId);
                var cb = plan.CropBox;
                cb.Min = new XYZ(lo.X, lo.Y, cb.Min.Z);
                cb.Max = new XYZ(hi.X, hi.Y, cb.Max.Z);
                plan.CropBox = cb;
                plan.CropBoxActive = true;
                return plan;
            }
            catch (Exception ex)
            {
                notes.Add("No plan panel: " + ex.Message);
                return null;
            }
        }

        // A section looking north from in front of the box: an elevation of the targets.
        private static ViewSection? TrySection(Document doc, XYZ lo, XYZ hi, List<string> notes)
        {
            try
            {
                var vft = Vft(doc, ViewFamily.Section);
                if (vft == null) { notes.Add("No elevation panel: no section type."); return null; }
                var center = (lo + hi) / 2;
                var t = Transform.Identity;
                t.Origin = center;
                t.BasisX = XYZ.BasisX;
                t.BasisY = XYZ.BasisZ;
                t.BasisZ = XYZ.BasisX.CrossProduct(XYZ.BasisZ);       // -Y: the viewer stands south
                var halfW = (hi.X - lo.X) / 2;
                var halfH = (hi.Z - lo.Z) / 2;
                var halfD = (hi.Y - lo.Y) / 2;
                var sb = new BoundingBoxXYZ
                {
                    Transform = t,
                    Min = new XYZ(-halfW, -halfH, -halfD),
                    Max = new XYZ(halfW, halfH, halfD + 1.0),
                };
                return ViewSection.CreateSection(doc, vft.Id, sb);
            }
            catch (Exception ex)
            {
                notes.Add("No elevation panel: " + ex.Message);
                return null;
            }
        }

        // ── views ──────────────────────────────────────────────────────────

        public static ImageDto ActiveView(UIDocument uidoc, View view, int maxPx)
        {
            var (png, w, h) = Fit(Export(uidoc.Document, view, EnvelopeLimits.ImageMaxPx, visibleRegion: true), maxPx);
            double? wm = null, hm = null;
            try
            {
                var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                var corners = ui?.GetZoomCorners();
                if (corners != null && corners.Count >= 2)
                {
                    var d = corners[1] - corners[0];
                    wm = Math.Abs(d.DotProduct(view.RightDirection)) * RevitValues.FeetToMetres;
                    hm = Math.Abs(d.DotProduct(view.UpDirection)) * RevitValues.FeetToMetres;
                }
            }
            catch { }
            return new ImageDto { Base64 = Convert.ToBase64String(png), Width = w, Height = h,
                Caption = LookText.ViewCaption("Active view", view.Name, SafeScale(view), wm, hm) };
        }

        public static ImageDto NamedView(Document doc, View view, int maxPx)
        {
            var (png, w, h) = Fit(Export(doc, view, EnvelopeLimits.ImageMaxPx, visibleRegion: false), maxPx);
            double? wm = null, hm = null;
            try
            {
                if (view.CropBoxActive)
                {
                    var cb = view.CropBox;
                    wm = (cb.Max.X - cb.Min.X) * RevitValues.FeetToMetres;
                    hm = (cb.Max.Y - cb.Min.Y) * RevitValues.FeetToMetres;
                }
            }
            catch { }
            return new ImageDto { Base64 = Convert.ToBase64String(png), Width = w, Height = h,
                Caption = LookText.ViewCaption("View", view.Name, SafeScale(view), wm, hm) };
        }

        private static int? SafeScale(View v)
        {
            try { return v.Scale > 0 ? v.Scale : (int?)null; } catch { return null; }
        }
    }
}

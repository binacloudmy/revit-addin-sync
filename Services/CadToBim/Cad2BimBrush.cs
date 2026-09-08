// Cad2BimBrush — walls from linework the drafter points at (extracted from the
// former Commands/Cad2BimSelectionCommand.cs, minus everything Revit).
//
// The automatic pass has to be careful, because everything it accepts it accepts
// without being asked: two parallel faces, a minimum length, a face several times
// longer than the wall is thick. Those guards are why it finds about a quarter of
// the linework on a busy drawing. A drafter dragging a box over a wall is telling
// us it is a wall — better evidence than any rule — so inside the box the guards
// come off, a single line becomes a wall in its own right, and if nothing at all
// pairs, the extent of what was selected becomes the wall (exploded poché).
//
// No Revit, no UI. The 40-wall "build them anyway?" question lives in the view model.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Cad2Bim.Services;
using CadPoint = Cad2Bim.Point;
using CadSegment = Cad2Bim.Segment;
using CadWall = Cad2Bim.Wall;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class BrushResult
    {
        public List<CadWall> Walls = new();

        /// <summary>One line for the status bar: "12 walls paired, 3 single-line, 0 cloud",
        /// "no linework in box", or why the box yielded nothing.</summary>
        public string Note;

        /// <summary>Paired faces + poché outlines.</summary>
        public int Paired;
        public int SingleLine;
        public int Cloud;
    }

    public static class Cad2BimBrush
    {
        /// <summary>Shortest line worth building a wall along on its own. Below this a
        /// selection box is picking up detail rather than fabric.</summary>
        public const double SingleLineMinLengthMm = 500.0;

        /// <summary>
        /// Re-reads the drawing with the settings' exclusions but NO wall-layer include
        /// list (a wall the automatic pass missed may well sit on a layer not named for
        /// walls), then runs the box.
        /// </summary>
        public static BrushResult Run(string drawingPath, BoxMm box, CadToBimSettings settings,
                                      double medianThicknessMm)
        {
            LayerFilter filter = (settings ?? new CadToBimSettings()).ToLayerFilter();
            CadModel model = ModelSource.Read(CadRenderSource.Read(drawingPath), filter);
            return RunOnModel(model, box, medianThicknessMm);
        }

        /// <summary>The box, on a model already in millimetres. Wall.SMin/SMax are read as
        /// the detect pass left them; MinFaceAspect and MinFaceLength are zeroed for the
        /// duration and restored to whatever they were.</summary>
        internal static BrushResult RunOnModel(CadModel model, BoxMm box, double medianThicknessMm)
        {
            var result = new BrushResult();

            List<CadSegment> inside = model.Segments
                .Where(segment => box.Contains(CadSegment.Midpoint(segment)))
                .ToList();

            List<List<CadPoint>> outlines = model.Outlines
                .Where(outline => outline.Count >= 4 && box.Contains(Centroid(outline)))
                .ToList();

            if (inside.Count == 0 && outlines.Count == 0)
            {
                result.Note = "no linework in box";
                return result;
            }

            List<CadSegment> faces;
            List<CadWall> walls;

            double aspect = CadWall.MinFaceAspect;
            double minFace = CadWall.MinFaceLength;
            try
            {
                // The drafter's box is the evidence, so the guards that stand in for
                // evidence are not needed inside it.
                CadWall.MinFaceAspect = 0;
                CadWall.MinFaceLength = 0;

                faces = CadClassifier.MergeCollinearSegments(inside);
                walls = CadClassifier.ClassifyWalls(faces);
                walls.AddRange(CadClassifier.WallsFromOutlines(outlines));
                walls = CadClassifier.DeduplicateWalls(walls);
            }
            finally
            {
                CadWall.MinFaceAspect = aspect;
                CadWall.MinFaceLength = minFace;
            }

            result.Paired = walls.Count;

            // What is left over becomes a wall on its own line: inside a box the drafter
            // drew, a line that pairs with nothing is a wall drawn as one line, not a stray.
            var used = new HashSet<CadSegment>(walls.SelectMany(wall => wall.Geometry).OfType<CadSegment>());
            foreach (CadSegment face in faces)
            {
                if (used.Contains(face) || face.Length < SingleLineMinLengthMm) continue;

                CadWall single = SingleLineWall(face, medianThicknessMm);
                if (single == null) continue;

                walls.Add(single);
                result.SingleLine++;
            }

            var points = new List<CadPoint>(inside.Count * 2);
            foreach (CadSegment segment in inside)
            {
                points.Add(segment.P1);
                points.Add(segment.P2);
            }
            foreach (List<CadPoint> outline in outlines) points.AddRange(outline);

            if (walls.Count == 0)
            {
                // Nothing paired and nothing ran long enough to stand alone: exploded
                // poché. The drafter has just drawn a box round it, so the extent of the
                // marks becomes the wall — narrow side thickness, long axis centreline.
                CadWall boxed = CadClassifier.WallFromCloud(points);
                if (boxed != null)
                {
                    walls.Add(boxed);
                    result.Cloud = 1;
                }
            }

            result.Walls = walls;
            result.Note = walls.Count == 0
                ? inside.Count + " lines in box, but no wall shape - their extent is " + Extent(points) +
                  ". Try a box that follows one wall rather than a room."
                : result.Paired + " walls paired, " + result.SingleLine + " single-line, " +
                  result.Cloud + " cloud";

            return result;
        }

        /// <summary>A wall from one line: the line is the centreline, given the thickness
        /// the rest of the drawing uses. null if that thickness is outside Wall.SMin..SMax
        /// or the line has no length.</summary>
        internal static CadWall SingleLineWall(CadSegment line, double thicknessMm)
        {
            double length = line.Length;
            if (length <= 0) return null;

            double half = thicknessMm / 2.0;
            double dx = (line.P2.x - line.P1.x) / length;
            double dy = (line.P2.y - line.P1.y) / length;

            CadSegment Offset(double side) => new CadSegment(
                new CadPoint(line.P1.x + (-dy * half * side), line.P1.y + (dx * half * side)),
                new CadPoint(line.P2.x + (-dy * half * side), line.P2.y + (dx * half * side)));

            try
            {
                return new CadWall(Offset(1), Offset(-1));
            }
            catch (ArgumentException)
            {
                // Outside the thickness range the model is set to accept.
                return null;
            }
        }

        private static CadPoint Centroid(List<CadPoint> outline)
        {
            double x = 0, y = 0;
            foreach (CadPoint point in outline) { x += point.x; y += point.y; }
            return new CadPoint(x / outline.Count, y / outline.Count);
        }

        private static string Extent(List<CadPoint> points)
        {
            if (points.Count == 0) return "empty";

            double minX = points.Min(p => p.x), maxX = points.Max(p => p.x);
            double minY = points.Min(p => p.y), maxY = points.Max(p => p.y);
            return (maxX - minX).ToString("0") + " by " + (maxY - minY).ToString("0") + " mm";
        }
    }
}

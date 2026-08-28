#if !REVIT2023_24
using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

using Cad2Bim;
using Cad2Bim.Services;

using CadSegment = Cad2Bim.Segment;
using CadWall = Cad2Bim.Wall;
using CadGeometry = Cad2Bim.GeometryElement;

namespace RevitWebAppSync.Commands
{
    /// <summary>
    /// Builds walls from linework the drafter points at.
    ///
    /// The automatic pass has to be careful, because everything it accepts it accepts without
    /// being asked: two parallel faces, a minimum length, and a face several times longer than
    /// the wall is thick, or a drawing comes back as a field of hatch strokes. Those guards are
    /// why it finds about a quarter of the linework on a busy drawing.
    ///
    /// A drafter dragging a box over a wall is telling us it is a wall. That is better evidence
    /// than any rule here, so inside the box the guards come off: no aspect test, no minimum
    /// length, and a single line becomes a wall in its own right rather than waiting for a
    /// partner that was never drawn.
    ///
    /// The point is not that the automatic pass gets better. It is that what it misses costs
    /// seconds instead of being traced by hand.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Cad2BimSelectionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                UIDocument uiDocument = commandData.Application.ActiveUIDocument;
                Document document = uiDocument?.Document;

                if (document == null)
                {
                    TaskDialog.Show("BINA CAD to BIM", "Open a Revit model first.");
                    return Result.Cancelled;
                }

                if (string.IsNullOrEmpty(Cad2BimConvertCommand.LastDrawingPath) ||
                    !System.IO.File.Exists(Cad2BimConvertCommand.LastDrawingPath))
                {
                    TaskDialog.Show("BINA CAD to BIM",
                        "Run CAD to BIM first.\n\nThis fills in what that pass missed, so it needs " +
                        "to know which drawing you are working from and where it was placed.");
                    return Result.Cancelled;
                }

                Level level = document.GetElement(Cad2BimConvertCommand.LastLevelId) as Level;
                WallType wallType = Cad2BimConvertCommand.DefaultWallType(document);

                if (level == null || wallType == null)
                {
                    TaskDialog.Show("BINA CAD to BIM", "The level or wall type from the last run is gone.");
                    return Result.Cancelled;
                }

                PickedBox box;
                try
                {
                    box = uiDocument.Selection.PickBox(PickBoxStyle.Crossing,
                        "Drag a box over the linework to convert");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }

                // Back into the drawing's own coordinates, undoing the placement the last run used.
                double minX = ToDrawingX(Math.Min(box.Min.X, box.Max.X));
                double maxX = ToDrawingX(Math.Max(box.Min.X, box.Max.X));
                double minY = ToDrawingY(Math.Min(box.Min.Y, box.Max.Y));
                double maxY = ToDrawingY(Math.Max(box.Min.Y, box.Max.Y));

                CadModel model = ModelSource.Read(
                    CadRenderSource.Read(Cad2BimConvertCommand.LastDrawingPath));

                List<CadSegment> inside = model.Segments
                    .Where(segment => Within(segment, minX, minY, maxX, maxY))
                    .ToList();

                if (inside.Count == 0)
                {
                    TaskDialog.Show("BINA CAD to BIM", "No drawing linework inside that box.");
                    return Result.Cancelled;
                }

                List<CadWall> walls = Detect(inside);

                if (walls.Count == 0)
                {
                    TaskDialog.Show("BINA CAD to BIM",
                        inside.Count + " lines are inside the box but none could be read as a wall.\n\n" +
                        "Try a tighter box around a single wall.");
                    return Result.Cancelled;
                }

                int created = 0;
                var madeIds = new List<ElementId>();

                using (var transaction = new Transaction(document, "Walls from selection"))
                {
                    transaction.Start();

                    FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new SilenceJoinFailures());
                    transaction.SetFailureHandlingOptions(options);

                    foreach (CadWall wall in walls)
                    {
                        try
                        {
                            Autodesk.Revit.DB.Wall made = Cad2BimConvertCommand.CreateWall(
                                document, wall, wallType, level,
                                Cad2BimConvertCommand.LastOriginX, Cad2BimConvertCommand.LastOriginY);

                            if (made != null)
                            {
                                created++;
                                madeIds.Add(made.Id);
                            }
                        }
                        catch
                        {
                            // One bad centreline should not cost the rest of the selection.
                        }
                    }

                    transaction.Commit();
                }

                if (madeIds.Count > 0)
                {
                    uiDocument.Selection.SetElementIds(madeIds);
                }

                TaskDialog.Show("BINA CAD to BIM",
                    created + " walls built from " + inside.Count + " selected lines.\n\n" +
                    "Drag another box to keep going.");

                return created > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BINA CAD to BIM - Error", ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Reads walls out of linework the drafter has vouched for.
        ///
        /// Pairing runs first, because a wall drawn with two faces should be built at its real
        /// thickness rather than a guessed one. What is left over becomes a wall on its own
        /// line: inside a box the drafter drew, a line that pairs with nothing is a wall drawn
        /// as one line, not a stray.
        /// </summary>
        private static List<CadWall> Detect(List<CadSegment> selected)
        {
            double aspect = CadWall.MinFaceAspect;
            double minFace = CadWall.MinFaceLength;

            try
            {
                // The drafter's box is the evidence, so the guards that stand in for evidence
                // are not needed inside it.
                CadWall.MinFaceAspect = 0;
                CadWall.MinFaceLength = 0;

                List<CadSegment> faces = CadClassifier.MergeCollinearSegments(selected);
                List<CadWall> walls = CadClassifier.DeduplicateWalls(CadClassifier.ClassifyWalls(faces));

                var used = new HashSet<CadSegment>();
                foreach (CadWall wall in walls)
                {
                    foreach (CadGeometry piece in wall.Geometry)
                    {
                        if (piece is CadSegment face) used.Add(face);
                    }
                }

                foreach (CadSegment face in faces)
                {
                    if (used.Contains(face)) continue;
                    if (face.Length < SingleLineMinLengthMm) continue;

                    CadWall single = SingleLineWall(face);
                    if (single != null) walls.Add(single);
                }

                return walls;
            }
            finally
            {
                CadWall.MinFaceAspect = aspect;
                CadWall.MinFaceLength = minFace;
            }
        }

        /// <summary>Shortest line worth building a wall along on its own. Below this a selection
        /// box is picking up detail rather than fabric.</summary>
        private const double SingleLineMinLengthMm = 300.0;

        /// <summary>
        /// A wall from one line: the line is the centreline, given the thickness the rest of the
        /// drawing uses. A single-line wall carries no thickness of its own, and the median of
        /// the walls already built beats any constant - it is this drawing's own wall.
        /// </summary>
        private static CadWall SingleLineWall(CadSegment line)
        {
            double half = Cad2BimConvertCommand.LastThicknessMm / 2.0;
            if (line.Length <= 0) return null;

            double dx = (line.P2.x - line.P1.x) / line.Length;
            double dy = (line.P2.y - line.P1.y) / line.Length;

            CadSegment Offset(double side) => new CadSegment(
                new Cad2Bim.Point(line.P1.x + (-dy * half * side), line.P1.y + (dx * half * side)),
                new Cad2Bim.Point(line.P2.x + (-dy * half * side), line.P2.y + (dx * half * side)));

            try
            {
                return new CadWall(Offset(1), Offset(-1));
            }
            catch
            {
                // Outside the thickness range the model is set to accept.
                return null;
            }
        }

        private static double ToDrawingX(double feet) =>
            (feet / Cad2BimConvertCommand.FromMm(1.0)) + Cad2BimConvertCommand.LastOriginX;

        private static double ToDrawingY(double feet) =>
            (feet / Cad2BimConvertCommand.FromMm(1.0)) + Cad2BimConvertCommand.LastOriginY;

        private static bool Within(CadSegment segment, double minX, double minY, double maxX, double maxY)
        {
            double x = (segment.P1.x + segment.P2.x) / 2;
            double y = (segment.P1.y + segment.P2.y) / 2;

            return x >= minX && x <= maxX && y >= minY && y <= maxY;
        }

        /// <summary>Same reason as the main command: traced walls touch constantly, and every
        /// failed join is a dialog.</summary>
        private sealed class SilenceJoinFailures : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                IList<FailureMessageAccessor> failures = accessor.GetFailureMessages();
                if (failures.Count == 0) return FailureProcessingResult.Continue;

                bool resolved = false;

                foreach (FailureMessageAccessor failure in failures)
                {
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        accessor.DeleteWarning(failure);
                        continue;
                    }

                    if (failure.HasResolutions())
                    {
                        accessor.ResolveFailure(failure);
                        resolved = true;
                    }
                }

                return resolved
                    ? FailureProcessingResult.ProceedWithCommit
                    : FailureProcessingResult.Continue;
            }
        }
    }
}
#endif

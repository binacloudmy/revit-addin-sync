#if !REVIT2023_24
using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Cad2Bim;
using Cad2Bim.Services;

// Revit has its own Segment and Wall; the classifier's are always spelled through these.
using CadSegment = Cad2Bim.Segment;
using CadWall = Cad2Bim.Wall;

namespace RevitWebAppSync.Commands
{
    /// <summary>
    /// Reads a DWG and builds native Revit walls from it.
    ///
    /// This is what "CAD to RVT" has to mean. RVT is a closed format that nothing outside
    /// Revit can write, so the only way to produce one is to create the elements inside a
    /// running Revit and let the user save. The IFC export covers the exchange case; this
    /// covers the case where the deliverable is a Revit model.
    ///
    /// The classification is the same code the standalone viewer runs - the Cad2Bim sources
    /// carry no Revit dependency, which is what makes them usable from both.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Cad2BimConvertCommand : IExternalCommand
    {
        // Millimetres. A plan carries no height, so this is an assumption until a section is
        // read; 3 m is the ordinary storey.
        private const double DefaultWallHeightMm = 3000.0;

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

                string path = PickDrawing();
                if (path == null) return Result.Cancelled;

                CadModel model = ModelSource.Read(CadRenderSource.Read(path), BuildFilter());
                List<CadWall> walls = CadClassifier.ClassifyWalls(model.Segments);

                if (walls.Count == 0)
                {
                    TaskDialog.Show("BINA CAD to BIM",
                        "No walls found in " + System.IO.Path.GetFileName(path) + ".\n\n" +
                        model.Segments.Count + " segments were read. If the drawing keeps its walls " +
                        "on a layer of their own, filtering to that layer usually finds them.");
                    return Result.Cancelled;
                }

                WallGraph graph = CadClassifier.CreateTopologicalPoints(walls);
                List<Space> spaces = CadClassifier.ClassifySpaces(graph, model.Texts);
                CadClassifier.SplitWalls(walls, spaces);

                Level level = LowestLevel(document);
                if (level == null)
                {
                    TaskDialog.Show("BINA CAD to BIM", "This model has no levels, so there is nothing to build on.");
                    return Result.Cancelled;
                }

                WallType wallType = DefaultWallType(document);
                if (wallType == null)
                {
                    TaskDialog.Show("BINA CAD to BIM", "This model has no basic wall type to use.");
                    return Result.Cancelled;
                }

                int created = 0;
                var failed = new List<string>();

                using (var transaction = new Transaction(document, "CAD to BIM"))
                {
                    transaction.Start();

                    foreach (CadWall wall in walls)
                    {
                        try
                        {
                            if (CreateWall(document, wall, wallType, level) != null) created++;
                        }
                        catch (Exception ex)
                        {
                            // One bad centreline should not cost the other thousand.
                            if (failed.Count < 20) failed.Add(ex.Message);
                        }
                    }

                    transaction.Commit();
                }

                // Counts are reported against what was found, not just what worked: a
                // conversion that quietly drops a third of the walls looks identical to one
                // that succeeded unless the shortfall is stated.
                var report = new System.Text.StringBuilder();
                report.AppendLine(created + " of " + walls.Count + " walls created.");
                report.AppendLine();
                report.AppendLine("Read from " + System.IO.Path.GetFileName(path) + ":");
                report.AppendLine("  " + model.Segments.Count + " segments, " + model.Texts.Count + " labels");
                report.AppendLine("  " + spaces.Count + " rooms found, " +
                                  spaces.Count(s => s.Name != null) + " of them named");
                report.AppendLine();
                report.AppendLine("Wall height is " + DefaultWallHeightMm + " mm throughout - a plan " +
                                  "carries no height, so it is assumed until a section is read.");

                if (failed.Count > 0)
                {
                    report.AppendLine();
                    report.AppendLine((walls.Count - created) + " could not be created, first few:");
                    foreach (string reason in failed.Take(5)) report.AppendLine("  " + reason);
                }

                TaskDialog.Show("BINA CAD to BIM", report.ToString());
                return created > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BINA CAD to BIM - Error", ex.Message);
                return Result.Failed;
            }
        }

        private static Autodesk.Revit.DB.Wall CreateWall(
            Document document, CadWall wall, WallType wallType, Level level)
        {
            CadSegment centerline = wall.Centerline;
            if (centerline.Length < 1.0) return null;   // shorter than a millimetre

            XYZ start = ToRevit(centerline.P1);
            XYZ end = ToRevit(centerline.P2);
            if (start.DistanceTo(end) < document.Application.ShortCurveTolerance) return null;

            Curve curve = Line.CreateBound(start, end);
            double height = FromMm(DefaultWallHeightMm);

            return Autodesk.Revit.DB.Wall.Create(
                document, curve, wallType.Id, level.Id, height, 0.0, false, false);
        }

        /// <summary>The classifier works in millimetres; the Revit API works in feet.</summary>
        private static double FromMm(double millimetres) =>
            UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);

        private static XYZ ToRevit(Cad2Bim.Point point) =>
            new XYZ(FromMm(point.x), FromMm(point.y), 0);

        /// <summary>
        /// Everything on a drawing that is plainly not building fabric. A starting point, not
        /// a substitute for saying which layers hold the walls: layer names vary by
        /// consultant, and this list is the one drawn from the files seen so far.
        /// </summary>
        private static LayerFilter BuildFilter()
        {
            var filter = new LayerFilter();
            filter.Exclude.AddRange(new[]
            {
                "PERABUT", "FURNITURE", "FURN*", "SANI*", "FITTING", "Toilet-fitting",
                "*-DIM*", "*TEXT*", "DEFPOINTS", "G-bubble", "GRID*",
            });
            return filter;
        }

        private static string PickDrawing()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the drawing to convert",
                Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf|All files (*.*)|*.*",
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private static Level LowestLevel(Document document) =>
            new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .FirstOrDefault();

        private static WallType DefaultWallType(Document document) =>
            new FilteredElementCollector(document)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(type => type.Kind == WallKind.Basic);
    }
}
#endif

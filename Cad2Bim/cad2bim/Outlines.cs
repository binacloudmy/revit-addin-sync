using System;
using System.Collections.Generic;
using System.Linq;

namespace Cad2Bim {
    public partial class CadClassifier {
        /// <summary>How far a wall found off the wall layers may sit from a thickness the
        /// drawing already uses, as a fraction of it.</summary>
        public const double ThicknessAgreement = 0.25;

        /// <summary>
        /// Walls from the layers that are not named for walls, kept only where they agree with
        /// the walls that are.
        ///
        /// Neither extreme works. Reading only the wall-named layers misses the internal
        /// partitions, which drafters routinely leave on layer 0 or a layer named for nothing
        /// in particular - 64 walls on the house, and the ones that make bedrooms into rooms.
        /// Reading every layer is worse: on the office block it cost 177 walls and dropped the
        /// thickness median to 81 mm, because linework from other layers pairs with wall faces
        /// first and steals the partner a real wall needed.
        ///
        /// So the wall layers are read first and believed, and the rest is admitted only where
        /// it matches a thickness those walls already established. A 115 mm partition beside
        /// 115 mm walls is a wall someone filed carelessly; an 81 mm pairing beside 114 mm walls
        /// is two bits of linework that happen to be parallel.
        /// </summary>
        public static List<Wall> ClassifyWallsElsewhere(
                IReadOnlyList<Wall> trusted, IReadOnlyList<Segment> otherSegments) {

            if (trusted.Count == 0 || otherSegments.Count == 0) return new List<Wall>();

            // The thicknesses this drawing actually builds in, commonest first.
            List<double> bands = trusted
                .GroupBy(wall => Math.Round(wall.Thickness / 10.0) * 10.0)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .Take(6)
                .ToList();

            List<Segment> faces = MergeCollinearSegments(otherSegments);

            return ClassifyWalls(faces)
                .Where(wall => bands.Any(band =>
                    Math.Abs(wall.Thickness - band) <= band * ThicknessAgreement))
                .ToList();
        }

        /// <summary>How many times longer than wide a poché outline must be to be a wall.</summary>
        public const double OutlineAspect = 2.5;

        /// <summary>
        /// Reads walls from the outlines of hatched wall poché.
        ///
        /// Pairing asks the drawing a question it often cannot answer - where are two parallel
        /// lines a wall-thickness apart - and tops out at about a quarter of the linework. A
        /// drafter hatching a wall has already drawn the answer: the hatch boundary is the
        /// wall, its short side is the thickness, its long axis is the centreline. Nothing has
        /// to be inferred.
        ///
        /// The strokes inside that hatch were what made false walls, and excluding the hatch to
        /// stop them threw away the boundary with them. Keeping the outline and dropping the
        /// strokes gets both right.
        ///
        /// An outline qualifies when its narrow side is a wall thickness and it is markedly
        /// longer than it is wide. A room outline fails the second test, a column fails it too,
        /// and a door swing's boundary fails the first.
        /// </summary>
        public static List<Wall> WallsFromOutlines(IReadOnlyList<List<Point>> outlines) {
            var walls = new List<Wall>();

            foreach (List<Point> outline in outlines) {
                if (outline.Count < 4) continue;

                Wall wall = FromOutline(outline);
                if (wall != null) walls.Add(wall);
            }

            return walls;
        }

        /// <summary>
        /// The narrowest rectangle containing the outline, found by trying each edge as the long
        /// axis. Walls are drawn square, so the answer is always one of the edges; a true
        /// minimum-area box would cost more and land in the same place.
        /// </summary>
        private static Wall FromOutline(List<Point> outline) {
            double bestWidth = double.MaxValue;
            double bestLength = 0;
            double bestAngle = 0;
            double bestAlong = 0;
            double bestAcross = 0;

            for (int i = 0; i < outline.Count; i++) {
                Point a = outline[i];
                Point b = outline[(i + 1) % outline.Count];

                double dx = b.x - a.x;
                double dy = b.y - a.y;
                double length = Math.Sqrt((dx * dx) + (dy * dy));
                if (length < 1e-6) continue;

                dx /= length;
                dy /= length;

                double minAlong = double.MaxValue, maxAlong = double.MinValue;
                double minAcross = double.MaxValue, maxAcross = double.MinValue;

                foreach (Point point in outline) {
                    double along = (point.x * dx) + (point.y * dy);
                    double across = (point.x * -dy) + (point.y * dx);

                    if (along < minAlong) minAlong = along;
                    if (along > maxAlong) maxAlong = along;
                    if (across < minAcross) minAcross = across;
                    if (across > maxAcross) maxAcross = across;
                }

                double spanAlong = maxAlong - minAlong;
                double spanAcross = maxAcross - minAcross;
                double width = Math.Min(spanAlong, spanAcross);
                if (width >= bestWidth) continue;

                bestWidth = width;
                bestLength = Math.Max(spanAlong, spanAcross);
                bestAngle = Math.Atan2(dy, dx);
                bestAlong = (minAlong + maxAlong) / 2;
                bestAcross = (minAcross + maxAcross) / 2;

                // Whichever axis turned out to be the short one decides the centreline's run.
                if (spanAlong < spanAcross) {
                    bestAngle += Math.PI / 2;
                    double swap = bestAlong;
                    bestAlong = bestAcross;
                    bestAcross = swap;
                }
            }

            if (bestWidth < Wall.SMin || bestWidth > Wall.SMax) return null;
            if (bestLength < bestWidth * OutlineAspect) return null;

            double cos = Math.Cos(bestAngle);
            double sin = Math.Sin(bestAngle);

            Point centre = new((bestAlong * cos) - (bestAcross * sin),
                               (bestAlong * sin) + (bestAcross * cos));

            double half = bestLength / 2;
            double offset = bestWidth / 2;

            Segment Face(double side) => new Segment(
                new Point(centre.x - (cos * half) + (-sin * offset * side),
                          centre.y - (sin * half) + (cos * offset * side)),
                new Point(centre.x + (cos * half) + (-sin * offset * side),
                          centre.y + (sin * half) + (cos * offset * side)));

            try {
                return new Wall(Face(1), Face(-1));
            }
            catch {
                return null;
            }
        }
    }
}

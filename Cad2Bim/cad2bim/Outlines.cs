using System;
using System.Collections.Generic;
using System.Linq;

namespace Cad2Bim {
    public partial class CadClassifier {
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

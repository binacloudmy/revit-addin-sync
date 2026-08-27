namespace Cad2Bim {
    /// <summary>Why faces were rejected, for the --faces flag. A room that fails to appear
    /// is either a loop the traversal never closed or one it closed and then discarded, and
    /// those two have completely different fixes.</summary>
    public static class Diagnostics {
        public static int Dropped;
        public static int TooSmall;
        public static List<double> Areas = new();
    }

    public partial class CadClassifier {
        /// <summary>Smallest room worth reporting. Below this the loop is a duct, a column
        /// box or a sliver left by two walls crossing.</summary>
        public const double MinSpaceAreaMm2 = 1_500_000.0; // 1.5 m²

        /// <summary>
        /// Rooms are the bounded faces of the wall graph. Walking the graph is what makes
        /// this work on real drawings: an enclosed region is a property of how walls connect,
        /// not of any one wall, so no amount of per-wall reasoning finds it.
        ///
        /// Each face is traced by always taking the sharpest available right turn at every
        /// node. That rule closes the smallest loop through each edge, which is exactly the
        /// room on that side of the wall; the one face it produces that runs the wrong way
        /// round is the outside of the building, and it is dropped.
        /// </summary>
        public static List<Space> ClassifySpaces(
                WallGraph graph, List<TextElement> texts, double minAreaMm2 = MinSpaceAreaMm2) {

            var spaces = new List<Space>();
            if (graph.Edges.Count == 0) return spaces;

            // Two half-edges per edge: 2*i runs A->B, 2*i+1 runs B->A.
            int halfEdgeCount = graph.Edges.Count * 2;
            var outgoing = new List<List<int>>(graph.Nodes.Count);
            for (int i = 0; i < graph.Nodes.Count; i++) outgoing.Add(new List<int>());

            for (int i = 0; i < graph.Edges.Count; i++) {
                outgoing[graph.Edges[i].A].Add(i * 2);
                outgoing[graph.Edges[i].B].Add((i * 2) + 1);
            }

            int From(int half) => (half & 1) == 0 ? graph.Edges[half / 2].A : graph.Edges[half / 2].B;
            int To(int half) => (half & 1) == 0 ? graph.Edges[half / 2].B : graph.Edges[half / 2].A;

            double Angle(int half) {
                Point a = graph.Nodes[From(half)].Position;
                Point b = graph.Nodes[To(half)].Position;
                return Math.Atan2(b.y - a.y, b.x - a.x);
            }

            // Around each node, half-edges in counter-clockwise order.
            foreach (List<int> around in outgoing) {
                around.Sort((x, y) => Angle(x).CompareTo(Angle(y)));
            }

            var visited = new bool[halfEdgeCount];

            for (int start = 0; start < halfEdgeCount; start++) {
                if (visited[start]) continue;

                var loop = new List<int>();
                int current = start;

                while (!visited[current]) {
                    visited[current] = true;
                    loop.Add(current);

                    // Arrive at the far node, turn around, then step to the neighbour just
                    // clockwise of where we came from.
                    int arrival = To(current);
                    int reverse = current ^ 1;

                    List<int> around = outgoing[arrival];
                    int position = around.IndexOf(reverse);
                    if (position < 0) break;

                    current = around[(position - 1 + around.Count) % around.Count];
                    if (loop.Count > halfEdgeCount) break; // never seen; cheap insurance
                }

                if (loop.Count < 3) continue;

                var boundary = new List<Point>(loop.Count);
                var walls = new List<Wall>(loop.Count);

                foreach (int half in loop) {
                    boundary.Add(graph.Nodes[From(half)].Position);
                    Wall wall = graph.Edges[half / 2].Wall;
                    if (!walls.Contains(wall)) walls.Add(wall);
                }

                // Counter-clockwise means an interior face; the outside of the building is
                // the single loop that comes back clockwise.
                double signed = SignedArea(boundary);
                if (signed <= 0) { Diagnostics.Dropped++; continue; }

                var space = new Space(boundary, walls);
                Diagnostics.Areas.Add(space.Area);
                if (space.Area < minAreaMm2) { Diagnostics.TooSmall++; continue; }

                spaces.Add(space);
            }

            AssignNames(spaces, texts);
            return spaces;
        }

        /// <summary>
        /// A wall with a room on both sides is internal. Anything the traversal touched from
        /// only one side is on the envelope — or is a stray that never closed a room, which
        /// on a real drawing looks the same from here and is worth the drafter's eye either
        /// way.
        /// </summary>
        public static void SplitWalls(List<Wall> walls, List<Space> spaces) {
            var sideCount = new Dictionary<Wall, int>();

            foreach (Space space in spaces) {
                foreach (BuildingElement element in space.SubElements) {
                    if (element is not Wall wall) continue;
                    sideCount[wall] = sideCount.GetValueOrDefault(wall) + 1;
                }
            }

            foreach (Wall wall in walls) {
                wall.IsOutdoor = sideCount.GetValueOrDefault(wall) < 2;
            }
        }

        /// <summary>Each room takes the drawing's own label, when one falls inside it. The
        /// smallest containing room wins, so a title sitting over a whole floor plan does not
        /// name every room under it.</summary>
        private static void AssignNames(List<Space> spaces, List<TextElement> texts) {
            foreach (TextElement text in texts) {
                Point anchor = new((text.P1.x + text.P2.x) / 2, (text.P1.y + text.P2.y) / 2);

                if (!IsName(text.Text)) continue;

                Space? smallest = null;
                foreach (Space space in spaces) {
                    if (space.Text.Count > 0) continue;
                    if (!Contains(space.Boundary, anchor)) continue;
                    if (smallest is not null && space.Area >= smallest.Area) continue;

                    smallest = space;
                }

                smallest?.Text.Add(text);
            }
        }

        /// <summary>
        /// Whether a label names a room rather than measures one. Plans are covered in
        /// annotations that sit inside rooms without naming them - areas, levels, door marks,
        /// grid references - and the nearest-text rule cannot tell them apart on position
        /// alone. "17.79MP" is an area written inside a room; it is not what the room is
        /// called. A name has letters in it and is not mostly digits.
        /// </summary>
        internal static bool IsName(string text) {
            if (text.Length < 2) return false;

            int letters = text.Count(char.IsLetter);
            int digits = text.Count(char.IsDigit);

            return letters >= 2 && letters > digits;
        }

        /// <summary>Shoelace with the sign kept: positive is counter-clockwise.</summary>
        internal static double SignedArea(IReadOnlyList<Point> polygon) {
            double twiceArea = 0;
            for (int i = 0; i < polygon.Count; i++) {
                Point a = polygon[i];
                Point b = polygon[(i + 1) % polygon.Count];
                twiceArea += (a.x * b.y) - (b.x * a.y);
            }
            return twiceArea / 2.0;
        }

        /// <summary>Ray casting: count crossings of a ray running out along +X.</summary>
        internal static bool Contains(IReadOnlyList<Point> polygon, Point point) {
            if (polygon.Count < 3) return false;

            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++) {
                Point a = polygon[i];
                Point b = polygon[j];

                if ((a.y > point.y) == (b.y > point.y)) continue;

                double x = a.x + ((point.y - a.y) / (b.y - a.y) * (b.x - a.x));
                if (point.x < x) inside = !inside;
            }

            return inside;
        }
    }
}

#nullable enable
// The largest circle that fits inside a room's plan boundary and clear of
// obstacles (turning_circle_ge: the OKU wheelchair turning space).
// Revit-free; coordinates in mm.
//
// Best-first search over square cells. Each cell is scored by the signed
// distance from its centre to the free area's edge (positive inside the
// room and outside every obstacle). That function changes by at most the
// distance moved, so no point in a cell can beat score + half-diagonal.
// The search keeps a proven lower bound (the best circle found) and a proven
// upper bound (nothing unexplored can beat it), and stops as soon as a
// target diameter is decided either way, so long corridors stay cheap.
// Against a target: pass only on a found circle, fail only when even the
// upper bound is short; anything in between is undecided, never a fail.

using System;
using System.Collections.Generic;

namespace BinaVibe.Copilot.Check
{
    public static class ClearCircle
    {
        /// <summary>Something the turning circle must stay clear of.</summary>
        public interface IObstacle
        {
            /// <summary>Distance from (x, y) to the obstacle's edge; <paramref name="inside"/>
            /// says whether the point is inside the obstacle.</summary>
            double Distance(double x, double y, out bool inside);
        }

        /// <summary>An axis-aligned plan box (a fixture or furniture bounding box), mm.</summary>
        public sealed class Box : IObstacle
        {
            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }

            public Box(double minX, double minY, double maxX, double maxY)
            {
                MinX = Math.Min(minX, maxX);
                MinY = Math.Min(minY, maxY);
                MaxX = Math.Max(minX, maxX);
                MaxY = Math.Max(minY, maxY);
            }

            public double Distance(double x, double y, out bool inside)
            {
                inside = x > MinX && x < MaxX && y > MinY && y < MaxY;
                if (inside) return Math.Min(Math.Min(x - MinX, MaxX - x), Math.Min(y - MinY, MaxY - y));
                var dx = Math.Max(Math.Max(MinX - x, 0), x - MaxX);
                var dy = Math.Max(Math.Max(MinY - y, 0), y - MaxY);
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        /// <summary>A quarter disc: centre C, radius R, between the perpendicular unit
        /// directions U and V, i.e. { C + a·U + b·V : a, b ≥ 0, a² + b² ≤ R² }.
        /// A door swing: C is the hinge, U the closed leaf, V the open leaf.</summary>
        public sealed class Sector : IObstacle
        {
            public double CenterX { get; }
            public double CenterY { get; }
            public double Ux { get; }
            public double Uy { get; }
            public double Vx { get; }
            public double Vy { get; }
            public double Radius { get; }

            public Sector(double centerX, double centerY, double ux, double uy, double vx, double vy, double radius)
            {
                CenterX = centerX;
                CenterY = centerY;
                Ux = ux;
                Uy = uy;
                Vx = vx;
                Vy = vy;
                Radius = radius;
            }

            public double Distance(double x, double y, out bool inside)
            {
                var px = x - CenterX;
                var py = y - CenterY;
                var a = px * Ux + py * Uy;
                var b = px * Vx + py * Vy;
                var d = Math.Sqrt(a * a + b * b);
                inside = a > 0 && b > 0 && d < Radius;
                if (inside) return Math.Min(Math.Min(a, b), Radius - d);
                if (a >= 0 && b >= 0) return d - Radius;
                // Outside the quadrant: the nearest point is on one of the straight edges.
                return Math.Min(EdgeDistance(a, b), EdgeDistance(b, a));
            }

            /// <summary>Distance from (a, b) to the edge {(t, 0) : 0 ≤ t ≤ R}.</summary>
            private double EdgeDistance(double along, double across)
            {
                var t = Math.Max(0, Math.Min(Radius, along));
                var da = along - t;
                return Math.Sqrt(da * da + across * across);
            }
        }

        public enum Verdict { Pass, Fail, Undecided }

        public sealed class Result
        {
            /// <summary>The largest circle found: a proven lower bound, mm.</summary>
            public double DiameterMm { get; }

            /// <summary>No circle can be larger than this, mm.</summary>
            public double UpperBoundMm { get; }

            /// <summary>False when the search hit its cell cap before converging or deciding.</summary>
            public bool Complete { get; }

            public Result(double diameterMm, double upperBoundMm, bool complete)
            {
                DiameterMm = diameterMm;
                UpperBoundMm = Math.Max(diameterMm, upperBoundMm);
                Complete = complete;
            }

            /// <summary>Pass only on a found circle; fail only when even the upper bound is short.</summary>
            public Verdict Against(double needMm)
            {
                if (DiameterMm + 1e-6 >= needMm) return Verdict.Pass;
                if (UpperBoundMm < needMm) return Verdict.Fail;
                return Verdict.Undecided;
            }
        }

        public const int MaxCells = 200_000;

        /// <param name="loops">Boundary loops (outer and holes, any order); inside is by even-odd.</param>
        /// <param name="obstacles">Fixture boxes and door swings. Obstacles outside the room
        /// change nothing: an outside point is never nearer than the boundary.</param>
        /// <param name="targetDiameterMm">When given, the search stops once it is decided.</param>
        /// <param name="precisionMm">Radius precision.</param>
        public static Result Find(
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> loops,
            IReadOnlyList<IObstacle> obstacles,
            double? targetDiameterMm = null,
            double precisionMm = 1.0)
        {
            var segments = new List<(double Ax, double Ay, double Bx, double By)>();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var loop in loops)
            {
                if (loop.Count < 3) continue;
                for (var i = 0; i < loop.Count; i++)
                {
                    var a = loop[i];
                    var b = loop[(i + 1) % loop.Count];
                    segments.Add((a.X, a.Y, b.X, b.Y));
                    minX = Math.Min(minX, a.X);
                    minY = Math.Min(minY, a.Y);
                    maxX = Math.Max(maxX, a.X);
                    maxY = Math.Max(maxY, a.Y);
                }
            }
            if (segments.Count == 0) return new Result(0, 0, true);
            var width = maxX - minX;
            var height = maxY - minY;
            var cellSize = Math.Min(width, height);
            if (cellSize <= 0) return new Result(0, 0, true);

            double Score(double x, double y)
            {
                var inside = false;
                var minSq = double.MaxValue;
                foreach (var s in segments)
                {
                    if ((s.Ay > y) != (s.By > y) && x < (s.Bx - s.Ax) * (y - s.Ay) / (s.By - s.Ay) + s.Ax) inside = !inside;
                    minSq = Math.Min(minSq, SegmentDistanceSq(x, y, s.Ax, s.Ay, s.Bx, s.By));
                }
                var d = Math.Sqrt(minSq);
                foreach (var o in obstacles)
                {
                    d = Math.Min(d, o.Distance(x, y, out var inObstacle));
                    if (inObstacle) inside = false;
                }
                return inside ? d : -d;
            }

            var heap = new CellHeap();
            Cell Make(double x, double y, double h) => new Cell(x, y, h, Score(x, y));

            var half = cellSize / 2;
            for (var x = minX; x < maxX; x += cellSize)
                for (var y = minY; y < maxY; y += cellSize)
                    heap.Push(Make(x + half, y + half, half));

            var target = targetDiameterMm / 2;
            var best = Make(minX + width / 2, minY + height / 2, 0);
            var evaluated = 0;
            while (heap.Count > 0)
            {
                if (target.HasValue && best.D >= target.Value - 1e-9)
                    return Done(best, heap, precisionMm, true);

                var cell = heap.Pop();
                if (cell.D > best.D) best = cell;
                // Cells pruned earlier could not beat best + precision; the popped cell
                // bounds every cell still queued.
                if (target.HasValue && Math.Max(cell.Max, best.D + precisionMm) < target.Value && best.D < target.Value)
                    return new Result(2 * best.D, 2 * Math.Max(cell.Max, best.D + precisionMm), true);
                if (cell.Max - best.D <= precisionMm) continue;
                if (++evaluated > MaxCells)
                {
                    heap.Push(cell);
                    return Done(best, heap, precisionMm, false);
                }
                var h = cell.H / 2;
                heap.Push(Make(cell.X - h, cell.Y - h, h));
                heap.Push(Make(cell.X + h, cell.Y - h, h));
                heap.Push(Make(cell.X - h, cell.Y + h, h));
                heap.Push(Make(cell.X + h, cell.Y + h, h));
            }
            return Done(best, heap, precisionMm, true);
        }

        private static Result Done(Cell best, CellHeap heap, double precisionMm, bool complete)
        {
            var upper = best.D + precisionMm;
            if (heap.Count > 0) upper = Math.Max(upper, heap.PeekMax);
            return new Result(Math.Max(0, 2 * best.D), Math.Max(0, 2 * upper), complete);
        }

        private static double SegmentDistanceSq(double px, double py, double ax, double ay, double bx, double by)
        {
            var dx = bx - ax;
            var dy = by - ay;
            var lenSq = dx * dx + dy * dy;
            var t = lenSq > 0 ? Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq)) : 0;
            var cx = ax + t * dx - px;
            var cy = ay + t * dy - py;
            return cx * cx + cy * cy;
        }

        private readonly struct Cell
        {
            public double X { get; }
            public double Y { get; }
            public double H { get; }
            public double D { get; }
            public double Max => D + H * Math.Sqrt(2);

            public Cell(double x, double y, double h, double d)
            {
                X = x;
                Y = y;
                H = h;
                D = d;
            }
        }

        /// <summary>Max-heap on Cell.Max (net48 has no PriorityQueue).</summary>
        private sealed class CellHeap
        {
            private readonly List<Cell> _items = new List<Cell>();

            public int Count => _items.Count;

            public double PeekMax => _items[0].Max;

            public void Push(Cell cell)
            {
                _items.Add(cell);
                var i = _items.Count - 1;
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (_items[parent].Max >= _items[i].Max) break;
                    (_items[parent], _items[i]) = (_items[i], _items[parent]);
                    i = parent;
                }
            }

            public Cell Pop()
            {
                var top = _items[0];
                var last = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                if (_items.Count == 0) return top;
                _items[0] = last;
                var i = 0;
                while (true)
                {
                    var l = 2 * i + 1;
                    var r = l + 1;
                    var largest = i;
                    if (l < _items.Count && _items[l].Max > _items[largest].Max) largest = l;
                    if (r < _items.Count && _items[r].Max > _items[largest].Max) largest = r;
                    if (largest == i) break;
                    (_items[largest], _items[i]) = (_items[i], _items[largest]);
                    i = largest;
                }
                return top;
            }
        }
    }
}

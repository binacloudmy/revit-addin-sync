#nullable enable
// The largest circle that fits inside a room's plan boundary and clear of
// obstacle boxes (turning_circle_ge: the OKU wheelchair turning space).
// Revit-free; coordinates in mm.
//
// Best-first search over square cells. Each cell is scored by the signed
// distance from its centre to the free area's edge (positive inside the
// room and outside every obstacle). That function changes by at most the
// distance moved, so no point in a cell can beat score + half-diagonal:
// cells that cannot beat the best found so far by the precision are not
// split further. The search is capped; a capped result is a lower bound,
// reported as incomplete so a fail is never claimed from it.

using System;
using System.Collections.Generic;

namespace BinaVibe.Copilot.Check
{
    public static class ClearCircle
    {
        /// <summary>An axis-aligned obstacle in plan, mm.</summary>
        public readonly struct Box
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
        }

        public sealed class Result
        {
            public double DiameterMm { get; }

            /// <summary>False when the search hit its cap: DiameterMm is then only a lower bound.</summary>
            public bool Complete { get; }

            public Result(double diameterMm, bool complete)
            {
                DiameterMm = diameterMm;
                Complete = complete;
            }
        }

        public const int MaxCells = 200_000;

        /// <param name="loops">Boundary loops (outer and holes, any order); inside is by even-odd.</param>
        /// <param name="obstacles">Plan boxes of fixtures and furniture inside the room.</param>
        /// <param name="precisionMm">Radius precision.</param>
        public static Result Find(IReadOnlyList<IReadOnlyList<(double X, double Y)>> loops, IReadOnlyList<Box> obstacles, double precisionMm = 1.0)
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
            if (segments.Count == 0) return new Result(0, true);
            var width = maxX - minX;
            var height = maxY - minY;
            var cellSize = Math.Min(width, height);
            if (cellSize <= 0) return new Result(0, true);

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
                    if (x > o.MinX && x < o.MaxX && y > o.MinY && y < o.MaxY)
                    {
                        inside = false;
                        d = Math.Min(d, Math.Min(Math.Min(x - o.MinX, o.MaxX - x), Math.Min(y - o.MinY, o.MaxY - y)));
                    }
                    else
                    {
                        var dx = Math.Max(Math.Max(o.MinX - x, 0), x - o.MaxX);
                        var dy = Math.Max(Math.Max(o.MinY - y, 0), y - o.MaxY);
                        d = Math.Min(d, Math.Sqrt(dx * dx + dy * dy));
                    }
                }
                return inside ? d : -d;
            }

            var heap = new CellHeap();
            Cell Make(double x, double y, double h) => new Cell(x, y, h, Score(x, y));

            var half = cellSize / 2;
            for (var x = minX; x < maxX; x += cellSize)
                for (var y = minY; y < maxY; y += cellSize)
                    heap.Push(Make(x + half, y + half, half));

            var best = Make(minX + width / 2, minY + height / 2, 0);
            var evaluated = 0;
            var complete = true;
            while (heap.Count > 0)
            {
                var cell = heap.Pop();
                if (cell.D > best.D) best = cell;
                if (cell.Max - best.D <= precisionMm) continue;
                if (++evaluated > MaxCells)
                {
                    complete = false;
                    break;
                }
                var h = cell.H / 2;
                heap.Push(Make(cell.X - h, cell.Y - h, h));
                heap.Push(Make(cell.X + h, cell.Y - h, h));
                heap.Push(Make(cell.X - h, cell.Y + h, h));
                heap.Push(Make(cell.X + h, cell.Y + h, h));
            }

            return new Result(Math.Max(0, 2 * best.D), complete);
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

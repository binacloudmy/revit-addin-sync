using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>
    /// Clear Sheet's segmented control: equal segments side by side ("Open 99 | Ignored 0 |
    /// Resolved 0 | Manual 17"). When one label is wider than an equal share (a 320 px pane
    /// in BM: "Diabaikan 0"), each segment gets its own width plus an equal share of the room
    /// left, so no label is cut; only if even that does not fit are the segments equal and
    /// their content may wrap.
    /// </summary>
    public sealed class SegmentPanel : System.Windows.Controls.Panel
    {
        private double[] _widths = new double[0];

        protected override Size MeasureOverride(Size available)
        {
            var kids = InternalChildren.Cast<UIElement>().ToList();
            var n = kids.Count;
            if (n == 0) return new Size(0, 0);
            var natural = kids.Select(k => { k.Measure(new Size(double.PositiveInfinity, available.Height)); return k.DesiredSize.Width; }).ToArray();
            var width = available.Width;
            if (double.IsInfinity(width))
            {
                var cellMax = natural.Max();
                _widths = natural.Select(_ => cellMax).ToArray();
            }
            else
            {
                var cell = width / n;
                var sum = natural.Sum();
                if (natural.All(w => w <= cell)) _widths = natural.Select(_ => cell).ToArray();
                else if (sum <= width) _widths = natural.Select(w => w + (width - sum) / n).ToArray();
                else _widths = natural.Select(_ => cell).ToArray();
            }
            var h = 0.0;
            for (var i = 0; i < n; i++)
            {
                kids[i].Measure(new Size(_widths[i], available.Height));
                h = Math.Max(h, kids[i].DesiredSize.Height);
            }
            return new Size(_widths.Sum(), h);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var x = 0.0;
            var kids = InternalChildren.Cast<UIElement>().ToList();
            for (var i = 0; i < kids.Count && i < _widths.Length; i++)
            {
                kids[i].Arrange(new Rect(x, 0, _widths[i], final.Height));
                x += _widths[i];
            }
            return final;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp;

using Cad2Bim.Services;

namespace Cad2Bim {
    /// <summary>
    /// Which layers the classifier is allowed to look at. Wildcards only, because a drafter
    /// naming layers knows `A-FURN*` and does not know regular expressions.
    ///
    /// This is the cheapest accuracy control there is: on a furnished plan, excluding the
    /// furniture layer removes more false walls than any amount of threshold tuning, and it
    /// removes them for a reason the drafter can state out loud.
    /// </summary>
    public class LayerFilter {
        /// <summary>Empty means every layer. Otherwise only layers matching a pattern.</summary>
        public List<string> Include { get; } = new();

        /// <summary>Always wins over Include.</summary>
        public List<string> Exclude { get; } = new();

        /// <summary>Hatch boundaries and dimension leaders draw, but they are not fabric.</summary>
        public bool IncludeHatch { get; set; }
        public bool IncludeDimensions { get; set; }

        /// <summary>
        /// Whether a label may be read. Exclusions still apply - a dimension's text is not a
        /// room name - but the include list does not, because it says where the walls are, not
        /// where the words are.
        /// </summary>
        public bool AllowsText(string layer, CadSource source) {
            if (source == CadSource.Hatch && !IncludeHatch) return false;
            if (source == CadSource.Dimension && !IncludeDimensions) return false;

            foreach (string pattern in Exclude) {
                if (Matches(layer, pattern)) return false;
            }

            return true;
        }

        public bool Allows(string layer, CadSource source) {
            if (source == CadSource.Hatch && !IncludeHatch) return false;
            if (source == CadSource.Dimension && !IncludeDimensions) return false;

            foreach (string pattern in Exclude) {
                if (Matches(layer, pattern)) return false;
            }

            if (Include.Count == 0) return true;

            foreach (string pattern in Include) {
                if (Matches(layer, pattern)) return true;
            }

            return false;
        }

        /// <summary>Case-insensitive glob: `*` stands for any run of characters.</summary>
        public static bool Matches(string value, string pattern) {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern == "*") return true;

            string[] parts = pattern.Split('*');
            int cursor = 0;

            for (int i = 0; i < parts.Length; i++) {
                string part = parts[i];
                if (part.Length == 0) continue;

                int found = value.IndexOf(part, cursor, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return false;

                // A pattern not opening with * must match from the very start.
                if (i == 0 && found != 0) return false;

                cursor = found + part.Length;
            }

            // A pattern not ending in * must reach the end of the value.
            return pattern.EndsWith('*') || cursor == value.Length;
        }
    }

    /// <summary>Everything the classifier gets from one drawing, in millimetres.</summary>
    public class CadModel {
        public List<Segment> Segments { get; } = new();
        public List<Arc> Arcs { get; } = new();
        public List<TextElement> Texts { get; } = new();

        /// <summary>Segments per layer before filtering — the answer to "what is in this
        /// drawing, and what am I throwing away".</summary>
        public Dictionary<string, int> LayerCensus { get; } = new();

        public double Scale { get; set; } = 1.0;
        public int DroppedByFilter { get; set; }
    }

    /// <summary>
    /// Builds the classifier's model from the same traversal the viewport draws.
    ///
    /// The loader this replaces read only top-level Line, Arc and TextEntity, so on a real
    /// drawing it never saw polyline walls or anything inside a block: 4,076 segments against
    /// the 25,048 outlines actually in the file. Door blocks in particular were invisible,
    /// which is why openings and rooms came back almost empty.
    /// </summary>
    public static class ModelSource {
        public static CadModel Read(CadDocument document, LayerFilter? filter = null) {
            var sink = new ModelSink(filter ?? new LayerFilter());
            CadRenderSource.Walk(document, sink);

            CadModel model = sink.Model;

            // Scale is resolved from the geometry that survived the filter, then applied to it.
            model.Scale = Units.Resolve(document, model.Segments);
            if (model.Scale != 1.0) Rescale(model);

            return model;
        }

        private static void Rescale(CadModel model) {
            double scale = model.Scale;
            Point At(Point p) => new(p.x * scale, p.y * scale);

            var segments = model.Segments
                .Select(s => new Segment(At(s.P1), At(s.P2)) { Layer = s.Layer })
                .ToList();

            var arcs = model.Arcs.Select(a => new Arc {
                Center = At(a.Center),
                Radius = a.Radius * scale,
                StartAngle = a.StartAngle,
                EndAngle = a.EndAngle,
                Layer = a.Layer,
            }).ToList();

            var texts = model.Texts.Select(t => new TextElement {
                P1 = At(t.P1), P2 = At(t.P2), Text = t.Text, Layer = t.Layer,
            }).ToList();

            model.Segments.Clear();
            model.Segments.AddRange(segments);
            model.Arcs.Clear();
            model.Arcs.AddRange(arcs);
            model.Texts.Clear();
            model.Texts.AddRange(texts);
        }

        private sealed class ModelSink : ICadSink {
            private const double MinSegmentLengthSquared = 1e-6;

            private readonly LayerFilter _filter;

            public CadModel Model { get; } = new();

            public ModelSink(LayerFilter filter) => _filter = filter;

            public void Polyline(IReadOnlyList<(double X, double Y)> points, bool isClosed,
                                 string layer, CadSource source) {
                Count(layer, points.Count - 1);
                if (!_filter.Allows(layer, source)) {
                    Model.DroppedByFilter += Math.Max(points.Count - 1, 0);
                    return;
                }

                int last = isClosed ? points.Count : points.Count - 1;
                for (int i = 0; i < last; i++) {
                    AddSegment(points[i], points[(i + 1) % points.Count], layer);
                }
            }

            public void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters,
                            string layer, CadSource source) {
                Count(layer, 1);
                if (!_filter.Allows(layer, source)) {
                    Model.DroppedByFilter++;
                    return;
                }

                // A door swing is only recognisable while it is still an arc, so keep the
                // parameters when the transform preserved them and fall back to chords when it
                // did not - a squashed arc is an ellipse, and pretending otherwise misplaces it.
                if (parameters is ArcParams arc) {
                    Model.Arcs.Add(new Arc {
                        Center = new Point(arc.CenterX, arc.CenterY),
                        Radius = arc.Radius,
                        StartAngle = arc.StartAngle,
                        EndAngle = arc.EndAngle,
                        Layer = layer,
                    });
                    return;
                }

                for (int i = 0; i + 1 < points.Count; i++) {
                    AddSegment(points[i], points[i + 1], layer);
                }
            }

            public void Text(double x, double y, double height, string value,
                             string layer, CadSource source) {
                // Text is judged by the exclude list only. Include names which layers hold
                // *walls*, and room labels never live on a wall layer - they sit on a text
                // layer of their own. Filtering them the same way loses every room name the
                // drawing carries, which is the one thing a plan states outright.
                if (!_filter.AllowsText(layer, source)) return;

                Model.Texts.Add(new TextElement {
                    P1 = new Point(x, y),
                    // Rough box: the DWG stores an insertion point, not an extent.
                    P2 = new Point(x + (height * value.Length * 0.6), y + height),
                    Text = CleanText(value),
                    Layer = layer,
                });
            }

            /// <summary>
            /// Strips MText formatting so a room name reads the way it looks on the drawing.
            /// MText stores its styling inline: \P is a paragraph break, \A1; an alignment,
            /// {\f...} a font switch. Left in, they arrive as part of the name and a room
            /// comes back called "BILIK \PPERBINCANGAN".
            /// </summary>
            internal static string CleanText(string value) {
                var text = new System.Text.StringBuilder(value.Length);

                for (int i = 0; i < value.Length; i++) {
                    char c = value[i];

                    if (c == '\\' && i + 1 < value.Length) {
                        char code = value[i + 1];

                        // \P and \p are line breaks; the rest run to a semicolon.
                        if (code is 'P' or 'p' or '~') { text.Append(' '); i++; continue; }
                        if (code is '\\' or '{' or '}') { text.Append(code); i++; continue; }

                        int end = value.IndexOf(';', i);
                        i = end < 0 ? value.Length : end;
                        continue;
                    }

                    if (c is '{' or '}') continue;
                    text.Append(c);
                }

                // A paragraph break becomes a space, so "BILIK\PPERBINCANGAN" would otherwise
                // come back double-spaced where the drawing shows one word per line.
                return string.Join(' ', text.ToString()
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }

            private void AddSegment((double X, double Y) a, (double X, double Y) b, string layer) {
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                if ((dx * dx) + (dy * dy) < MinSegmentLengthSquared) return;

                Model.Segments.Add(
                    new Segment(new Point(a.X, a.Y), new Point(b.X, b.Y)) { Layer = layer });
            }

            private void Count(string layer, int segments) {
                if (segments <= 0) return;
                Model.LayerCensus[layer] = Model.LayerCensus.GetValueOrDefault(layer) + segments;
            }
        }
    }
}

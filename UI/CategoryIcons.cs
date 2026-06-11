using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Vector icons for Revit categories. All drawn with WPF Geometry paths.
    /// Each icon is a 16x16 viewbox-scaled vector.
    /// </summary>
    public static class CategoryIcons
    {
        // Category → (GeometryData, Color)
        private static readonly Dictionary<string, (string Path, Color Color)> Icons =
            new Dictionary<string, (string, Color)>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Structure ──
            { "Walls",              ("M2,14 L2,3 L4,2 L4,14 Z M6,14 L6,3 L8,2 L8,14 Z M10,14 L10,3 L12,2 L12,14 Z M0,14 L14,14", Color.FromRgb(120, 85, 72)) },
            { "Stacked Walls",      ("M2,14 L2,3 L4,2 L4,14 Z M6,14 L6,3 L8,2 L8,14 Z M10,14 L10,3 L12,2 L12,14 Z M0,14 L14,14", Color.FromRgb(141, 110, 99)) },
            { "Wall Sweeps",        ("M1,8 L13,8 M1,6 L13,6 M3,4 L3,10 M7,4 L7,10 M11,4 L11,10", Color.FromRgb(161, 136, 127)) },
            { "Floors",             ("M1,10 L7,6 L13,10 L7,14 Z M1,8 L7,4 L13,8", Color.FromRgb(33, 150, 243)) },
            { "Ceilings",           ("M1,6 L7,2 L13,6 L7,10 Z M3,6 L7,4 L11,6 M5,6 L7,5 L9,6", Color.FromRgb(156, 39, 176)) },
            { "Roofs",              ("M7,1 L1,7 L3,7 L3,13 L11,13 L11,7 L13,7 Z M7,1 L7,5", Color.FromRgb(233, 30, 99)) },
            { "Roof Soffits",       ("M7,2 L1,8 L13,8 Z M3,8 L3,12 M11,8 L11,12", Color.FromRgb(240, 98, 146)) },

            // ── Openings ──
            { "Doors",              ("M3,2 L3,13 L11,13 L11,2 Z M9,8 A0.5,0.5 0 1,1 9.01,8 M3,13 A8,8 0 0,1 11,13", Color.FromRgb(255, 152, 0)) },
            { "Windows",            ("M2,3 L12,3 L12,11 L2,11 Z M2,7 L12,7 M7,3 L7,11", Color.FromRgb(0, 188, 212)) },

            // ── Circulation ──
            { "Stairs",             ("M1,13 L1,10 L4,10 L4,7 L7,7 L7,4 L10,4 L10,1 L13,1", Color.FromRgb(76, 175, 80)) },
            { "Railings",           ("M2,4 L2,12 M2,4 L12,4 M12,4 L12,12 M5,4 L5,12 M9,4 L9,12", Color.FromRgb(129, 199, 132)) },
            { "Ramps",              ("M1,12 L13,4 L13,12 Z", Color.FromRgb(100, 181, 246)) },

            // ── Fixtures & Equipment ──
            { "Casework",           ("M2,4 L12,4 L12,13 L2,13 Z M2,8 L12,8 M7,4 L7,8 M6,10 L8,10", Color.FromRgb(121, 85, 72)) },
            { "Furniture",          ("M3,6 L11,6 L11,10 L3,10 Z M4,10 L4,13 M10,10 L10,13 M2,6 L12,6", Color.FromRgb(255, 193, 7)) },
            { "Plumbing Fixtures",  ("M5,2 L5,5 L3,7 L3,12 L11,12 L11,7 L9,5 L9,2 M5,2 L9,2 M3,12 L4,14 M11,12 L10,14", Color.FromRgb(3, 169, 244)) },
            { "Mechanical Equipment", ("M7,1 A6,6 0 1,1 7.01,1 M7,4 L7,10 M4,7 L10,7 M5,4 L9,10 M5,10 L9,4", Color.FromRgb(255, 87, 34)) },
            { "Electrical Fixtures", ("M8,1 L5,7 L9,7 L6,13 M9,5 L11,3", Color.FromRgb(255, 193, 7)) },
            { "Electrical Equipment", ("M8,1 L5,7 L9,7 L6,13 M9,5 L11,3", Color.FromRgb(230, 170, 0)) },
            { "Lighting Fixtures",  ("M7,1 L7,4 M4,5 L10,5 L9,11 L5,11 Z M5,11 L4,13 L10,13 L9,11 M7,1 L5,3 M7,1 L9,3", Color.FromRgb(255, 235, 59)) },
            { "Generic Models",     ("M2,2 L12,2 L12,12 L2,12 Z M2,2 L7,7 L12,2 M2,12 L7,7 L12,12", Color.FromRgb(158, 158, 158)) },
            { "Specialty Equipment", ("M3,3 L11,3 L11,11 L3,11 Z M7,3 L7,11 M3,7 L11,7", Color.FromRgb(255, 112, 67)) },

            // ── Detail / Annotation ──
            { "Fascias",            ("M1,6 L13,6 L13,10 L1,10 Z M1,8 L13,8", Color.FromRgb(188, 170, 164)) },
            { "Gutters",            ("M2,4 L2,10 L4,12 L10,12 L12,10 L12,4", Color.FromRgb(144, 164, 174)) },
            { "Reveals",            ("M4,2 L4,12 M6,2 L6,12 M8,4 L8,10 M10,5 L10,9", Color.FromRgb(176, 190, 197)) },
            { "Legend Components",  ("M2,2 L6,2 L6,6 L2,6 Z M8,3 L13,3 M8,5 L12,5 M2,8 L6,8 L6,12 L2,12 Z M8,9 L13,9 M8,11 L12,11", Color.FromRgb(158, 158, 158)) },
            { "Materials",          ("M2,2 L12,2 L12,12 L2,12 Z M2,2 L12,12 M12,2 L2,12", Color.FromRgb(189, 189, 189)) },
            { "Work Plane Grid",    ("M1,4 L13,4 M1,7 L13,7 M1,10 L13,10 M4,1 L4,13 M7,1 L7,13 M10,1 L10,13", Color.FromRgb(200, 200, 200)) },
        };

        // Default icon for unknown categories
        private static readonly (string Path, Color Color) DefaultIcon =
            ("M2,2 L12,2 L12,12 L2,12 Z M7,5 L7,9 M7,10 L7,11", Color.FromRgb(158, 158, 158));

        /// <summary>
        /// Get category color
        /// </summary>
        public static Color GetColor(string category)
        {
            if (string.IsNullOrEmpty(category)) return DefaultIcon.Color;
            return Icons.TryGetValue(category, out var icon) ? icon.Color : DefaultIcon.Color;
        }

        /// <summary>
        /// Create a vector icon for the given category.
        /// Returns a Viewbox containing the icon, sized to fit.
        /// </summary>
        public static Viewbox CreateIcon(string category, double size = 20)
        {
            var (pathData, color) = GetIconData(category);

            var viewbox = new Viewbox
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform
            };

            var canvas = new Canvas { Width = 14, Height = 14 };

            // Parse path data into segments and draw
            try
            {
                var geometry = Geometry.Parse(pathData);
                var path = new Path
                {
                    Data = geometry,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 1.2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                canvas.Children.Add(path);
            }
            catch
            {
                // Fallback: just draw a square with letter
                canvas.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 12, Height = 12,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = 1,
                    RadiusX = 2, RadiusY = 2,
                    Margin = new Thickness(1)
                });
            }

            viewbox.Child = canvas;
            return viewbox;
        }

        /// <summary>
        /// Create an icon inside a colored background square (for list rows)
        /// </summary>
        public static Border CreateIconBadge(string category, double size = 28)
        {
            var color = GetColor(category);
            var bgColor = Color.FromArgb(25, color.R, color.G, color.B);

            var border = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(bgColor),
                VerticalAlignment = VerticalAlignment.Center
            };

            var icon = CreateIcon(category, size * 0.6);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            border.Child = icon;

            return border;
        }

        /// <summary>
        /// Create a level icon (numbered circle)
        /// </summary>
        public static Border CreateLevelIcon(string levelName, int index, double size = 28)
        {
            var colors = new[]
            {
                Color.FromRgb(0, 120, 215),   // Blue
                Color.FromRgb(16, 124, 16),    // Green
                Color.FromRgb(255, 140, 0),    // Orange
                Color.FromRgb(156, 39, 176),   // Purple
                Color.FromRgb(233, 30, 99),    // Pink
                Color.FromRgb(0, 150, 136),    // Teal
                Color.FromRgb(255, 87, 34),    // Deep Orange
                Color.FromRgb(63, 81, 181),    // Indigo
            };

            var color = colors[index % colors.Length];
            var bgColor = Color.FromArgb(25, color.R, color.G, color.B);

            // Extract short label from level name
            string label = GetLevelLabel(levelName);

            var border = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2), // Circle
                Background = new SolidColorBrush(bgColor),
                VerticalAlignment = VerticalAlignment.Center
            };

            border.Child = new TextBlock
            {
                Text = label,
                FontSize = size * 0.35,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            return border;
        }

        private static (string Path, Color Color) GetIconData(string category)
        {
            if (string.IsNullOrEmpty(category)) return DefaultIcon;
            return Icons.TryGetValue(category, out var icon) ? icon : DefaultIcon;
        }

        private static string GetLevelLabel(string levelName)
        {
            if (string.IsNullOrEmpty(levelName)) return "?";

            // Try to extract number: "Aras 01" → "01", "Level 2" → "2", "Ground Floor" → "G"
            var match = System.Text.RegularExpressions.Regex.Match(levelName, @"\d+");
            if (match.Success) return match.Value;

            // Common abbreviations
            string lower = levelName.ToLower();
            if (lower.Contains("ground") || lower.Contains("tanah")) return "G";
            if (lower.Contains("roof") || lower.Contains("bumbung")) return "R";
            if (lower.Contains("basement")) return "B";
            if (lower.Contains("mezzanine")) return "M";

            return levelName.Substring(0, Math.Min(2, levelName.Length)).ToUpper();
        }
    }
}

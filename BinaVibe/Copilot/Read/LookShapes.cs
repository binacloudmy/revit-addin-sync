#nullable enable
// Revit-free half of look (spec §7.7, §11 #6): argument parsing, image layout
// math (long edge <= 1024 px; multi panels side by side), the PNG header
// reader, captions with the real size in metres, and the envelope. LookCapture
// (Revit-bound) exports and stitches.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public enum LookTarget { ActiveView, View, Elements }

    public sealed class LookArgs
    {
        public LookTarget Target;
        public string? View;
        public string? Handle;
        public List<long> Ids = new();
        public bool Multi;

        public static bool TryParse(JsonElement args, out LookArgs a, out string error)
        {
            a = new LookArgs();
            error = "";
            switch (ReadArgs.String(args, "target")?.ToLowerInvariant())
            {
                case "active_view": a.Target = LookTarget.ActiveView; break;
                case "view": a.Target = LookTarget.View; break;
                case "elements": a.Target = LookTarget.Elements; break;
                default:
                    error = "target must be active_view, view or elements.";
                    return false;
            }
            a.View = ReadArgs.String(args, "view");
            a.Handle = ReadArgs.String(args, "handle")?.ToLowerInvariant();
            a.Ids = (ReadArgs.Longs(args, "ids") ?? new List<long>()).Distinct().ToList();
            var mode = ReadArgs.String(args, "mode")?.ToLowerInvariant() ?? "single";
            if (mode != "single" && mode != "multi")
            {
                error = "mode must be single or multi.";
                return false;
            }
            a.Multi = mode == "multi";
            if (a.Target == LookTarget.View && a.View == null)
            {
                error = "target view needs view (the view name).";
                return false;
            }
            if (a.Target == LookTarget.Elements && a.Handle == null && a.Ids.Count == 0)
            {
                error = "target elements needs handle or ids.";
                return false;
            }
            if (a.Handle != null && !HandleStore.IsHandle(a.Handle))
            {
                error = "handle must look like h_1a2b3c4d (from an earlier find_elements result).";
                return false;
            }
            return true;
        }
    }

    public readonly struct PanelRect
    {
        public PanelRect(int x, int width, int height)
        {
            X = x;
            Width = width;
            Height = height;
        }

        public int X { get; }
        public int Width { get; }
        public int Height { get; }
    }

    public sealed class SideBySideLayout
    {
        public int Width;
        public int Height;
        public List<PanelRect> Panels = new();
    }

    public static class ImageLayout
    {
        /// <summary>Scales (w, h) down so the long edge is at most max; never up.</summary>
        public static (int Width, int Height) FitLongEdge(int w, int h, int max)
        {
            if (w <= 0 || h <= 0) return (Math.Max(1, w), Math.Max(1, h));
            var longEdge = Math.Max(w, h);
            if (longEdge <= max) return (w, h);
            var s = (double)max / longEdge;
            return (Math.Max(1, (int)Math.Round(w * s)), Math.Max(1, (int)Math.Round(h * s)));
        }

        /// <summary>Panels scaled to one height, left to right with a gap, all within max px.</summary>
        public static SideBySideLayout SideBySide(IReadOnlyList<(int Width, int Height)> panels, int max, int gap)
        {
            var layout = new SideBySideLayout();
            if (panels.Count == 0) return layout;
            var aspects = panels.Select(p => (double)Math.Max(1, p.Width) / Math.Max(1, p.Height)).ToList();
            var usable = max - gap * (panels.Count - 1);
            var h = Math.Min(Math.Min(panels.Min(p => p.Height), max), (int)Math.Floor(usable / aspects.Sum()));
            h = Math.Max(1, h);
            var x = 0;
            for (var i = 0; i < panels.Count; i++)
            {
                var w = Math.Max(1, (int)Math.Floor(h * aspects[i]));
                layout.Panels.Add(new PanelRect(x, w, h));
                x += w + (i < panels.Count - 1 ? gap : 0);
            }
            layout.Width = x;
            layout.Height = h;
            return layout;
        }
    }

    public static class Png
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>Width and height from the IHDR chunk, without decoding.</summary>
        public static bool TryReadSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png == null || png.Length < 24) return false;
            for (var i = 0; i < Signature.Length; i++) if (png[i] != Signature[i]) return false;
            if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R') return false;
            width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return width > 0 && height > 0;
        }
    }

    public static class LookText
    {
        private static string M(double v) =>
            (v >= 10 ? Math.Round(v) : Math.Round(v, 1)).ToString("0.#", CultureInfo.InvariantCulture) + " m";

        public static string ElementsCaption(int count, double widthM, double depthM, bool multi) =>
            (multi ? "Plan, 3D and elevation" : "3D view") + " around " + count + " element" + (count == 1 ? "" : "s")
            + ", about " + M(widthM) + " × " + M(depthM) + (multi ? "." : " (section box +10%).");

        public static string ViewCaption(string prefix, string viewName, int? scale, double? widthM, double? heightM)
        {
            var s = prefix + " '" + viewName + "' (" + (scale.HasValue && scale > 0 ? "1:" + scale : "scale n/a") + ")";
            if (widthM.HasValue && heightM.HasValue) s += ", about " + M(widthM.Value) + " × " + M(heightM.Value);
            return s + ".";
        }

        public static Envelope Build(ImageDto image, string? handle, IReadOnlyList<string> notes)
        {
            var next = "Use the image to orient and check placement, never to count or measure: use find_elements and inspect for numbers."
                       + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
            object? data = notes.Count > 0 ? new Dictionary<string, object?> { ["notes"] = notes.ToList() } : null;
            return ReadEnvelope.Done(image.Caption, data, handle: handle, next: next, image: image);
        }
    }
}

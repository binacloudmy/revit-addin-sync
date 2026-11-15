// LookImageStash: the view images the `look` tool exported during the
// CURRENT copilot turn (row G-03, "look at this view, ada apa-apa masalah?").
//
// The `look` handler (BinaVibe/Mcp/Tools/LookTool.cs) adds the PNG here and
// also returns it to the backend, which runs the vision model and scrubs the
// bytes from everything it stores. The pane needs its own copy to show the
// image under the reply, so the add-in never has to get the bytes back.
//
// Privacy: memory only, never written to disk. RevitChatRouter clears it when
// a new turn starts and TAKES it (read + clear) when the turn ends, so an
// image never outlives its turn here. After that it lives only on the reply's
// in-memory ChatMessage; the persisted history (CopilotStateStore) never
// carries it.
//
// Revit-free on purpose so Tests/LookImageStashTests.cs can link it.
using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Services
{
    public static class LookImageStash
    {
        /// <summary>One exported view image, ready for the pane.</summary>
        public sealed class LookImage
        {
            public string ViewName { get; set; } = "";
            public string Base64 { get; set; } = "";
            public int Width { get; set; }
            public int Height { get; set; }
        }

        /// <summary>More than this per turn is a loop, not a look; extra
        /// images are dropped from the pane (the backend still reads them).</summary>
        public const int MaxPerTurn = 3;

        private static readonly object _lock = new object();
        private static readonly List<LookImage> _images = new List<LookImage>();

        public static int Count { get { lock (_lock) return _images.Count; } }

        /// <summary>Keep an exported PNG for the end of this turn. Returns the
        /// stored entry, or null when the bytes are empty or the turn is full.</summary>
        public static LookImage Add(string viewName, byte[] png)
        {
            if (png == null || png.Length == 0) return null;
            TryPngSize(png, out var w, out var h);
            var img = new LookImage
            {
                ViewName = viewName ?? "",
                Base64 = Convert.ToBase64String(png),
                Width = w,
                Height = h,
            };
            lock (_lock)
            {
                if (_images.Count >= MaxPerTurn) return null;
                _images.Add(img);
            }
            return img;
        }

        /// <summary>End of turn: hand the images to the reply and forget them.
        /// Null when the turn looked at nothing.</summary>
        public static List<LookImage> TakeAll()
        {
            lock (_lock)
            {
                if (_images.Count == 0) return null;
                var all = new List<LookImage>(_images);
                _images.Clear();
                return all;
            }
        }

        /// <summary>Start of a turn (or an aborted one): drop anything left over.</summary>
        public static void Clear()
        {
            lock (_lock) _images.Clear();
        }

        /// <summary>Width/height from a PNG's IHDR chunk. False when the bytes
        /// are not a PNG.</summary>
        public static bool TryPngSize(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png == null || png.Length < 24) return false;
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (int i = 0; i < sig.Length; i++) if (png[i] != sig[i]) return false;
            if (png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R') return false;
            width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            return width > 0 && height > 0;
        }
    }
}

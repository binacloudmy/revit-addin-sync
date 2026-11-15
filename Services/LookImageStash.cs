#nullable enable
// LookImageStash: the view images the `look` tool returned during the turn
// that is RUNNING now, so the pane can show them while the reply that talks
// about them is still streaming (row G-03, "look at this view, ada apa-apa
// masalah?").
//
// Fed by PaneToolRunner.RunReadsAsync with every read envelope that carries an
// image. Once the turn ends the reply's TurnEvidence.Images carries the same
// pictures under the answer (TurnEvidenceView), so the stash is cleared at
// turn end (PaneToolRunner.EndTurn) and whenever a new turn id starts adding.
//
// Privacy: memory only, never written to disk; the persisted history
// (CopilotStateStore) never carries it.
//
// Revit-free on purpose so Tests can link it.
using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.Services
{
    public static class LookImageStash
    {
        /// <summary>More than this per turn is a loop, not a look; extra
        /// images are dropped from the live view (the reply's evidence still caps its own).</summary>
        public const int MaxPerTurn = 3;

        private static readonly object _lock = new object();
        private static readonly List<ImageDto> _images = new List<ImageDto>();
        private static string _turnId = "";

        public static int Count { get { lock (_lock) return _images.Count; } }

        /// <summary>Keep the images of one read batch. A different turn id than
        /// the last one starts a fresh list. The same bytes are kept once.</summary>
        public static void AddFrom(string? turnId, IEnumerable<Envelope?>? envelopes)
        {
            if (envelopes == null) return;
            lock (_lock)
            {
                var id = turnId ?? "";
                if (!string.Equals(id, _turnId, StringComparison.Ordinal)) { _images.Clear(); _turnId = id; }
                foreach (var env in envelopes)
                {
                    var img = env?.Image;
                    if (img == null || string.IsNullOrEmpty(img.Base64)) continue;
                    if (_images.Count >= MaxPerTurn) break;
                    if (_images.Exists(i => i.Base64 == img.Base64)) continue;
                    _images.Add(img);
                }
            }
        }

        /// <summary>The running turn's images, in arrival order (a copy).</summary>
        public static List<ImageDto> Current()
        {
            lock (_lock) return new List<ImageDto>(_images);
        }

        /// <summary>Turn end (or an aborted one): forget everything.</summary>
        public static void Clear()
        {
            lock (_lock) { _images.Clear(); _turnId = ""; }
        }
    }
}

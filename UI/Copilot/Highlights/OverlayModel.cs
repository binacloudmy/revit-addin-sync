#nullable enable
using System;
using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>One docked callout card on the overlay: a group of touched elements.</summary>
    public sealed class OverlayCallout
    {
        public string Title = "";
        public int Count;
        public string Color = HighlightSet.Green;
        public bool Dashed;
        public List<(string Param, string Before, string After)> Rows = new List<(string, string, string)>();
        public string? Note;
        /// <summary>Where the leader points (percent of the view), or null for no leader.</summary>
        public (double X, double Y)? Anchor;
        public Action? OpenTypeProperties;
    }

    /// <summary>What the overlay draws after a turn: up to two callouts.</summary>
    public sealed class OverlayModel
    {
        public List<OverlayCallout> Callouts = new List<OverlayCallout>();
        public Action<bool>? SetAfter;
        public bool ShowBeforeAfter;
        public bool After = true;
    }
}

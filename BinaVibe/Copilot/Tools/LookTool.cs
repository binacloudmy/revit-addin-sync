#nullable enable
// Stub created by W0; body owned by W2
// look (spec §11 #6, §7.7): an image of the active view, a named view, or a 3D
// view around elements, at most 1024 px on the long edge.

using System;
using System.Collections.Generic;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Tools
{
    public sealed class LookTool : ICopilotTool
    {
        public string Name => ToolNames.Look;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args) => EnvelopeFactory.NotImplemented(Name);

        /// <summary>Image of a 3D view around the targets (used by W3's preview thumbnail).</summary>
        public static ImageDto? CaptureElements(ToolContext ctx, IReadOnlyList<ElementRef> targets, int maxPx = 1024) =>
            throw new NotImplementedException("W2");
    }
}

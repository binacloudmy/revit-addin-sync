#nullable enable
// look (spec §11 #6, §7.7): an image of the active view, a named view, or a 3D
// view around elements, at most 1024 px on the long edge.
// LookArgs/LookText (Revit-free) parse and shape; LookCapture exports. All of
// it runs under CallbackScope Look, so the temporary view's transaction never
// counts as a drafter change. Export failure -> failed: no_gpu.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;

namespace BinaVibe.Copilot.Tools
{
    public sealed class LookTool : ICopilotTool
    {
        public string Name => ToolNames.Look;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args)
        {
            if (!LookArgs.TryParse(args, out var a, out var error)) return ReadEnvelope.InvalidArgs(error);
            var notes = new List<string>();
            try
            {
                using (LookScope.Enter(ctx.Doc))
                {
                    switch (a.Target)
                    {
                        case LookTarget.ActiveView:
                        {
                            var view = ctx.UiDoc.ActiveView;
                            if (view == null) return ReadEnvelope.Fail(ErrorCodes.NotFound, "There is no active view.", Array.Empty<string>());
                            if (view is ViewSchedule) return NotAnImage(view);
                            if (a.Multi) notes.Add("mode multi works with target elements; this is the single active view.");
                            return LookText.Build(LookCapture.ActiveView(ctx.UiDoc, view, EnvelopeLimits.ImageMaxPx), null, notes);
                        }
                        case LookTarget.View:
                        {
                            var views = new FilteredElementCollector(ctx.Doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
                            var view = views.FirstOrDefault(v => string.Equals(v.Name, a.View!.Trim(), StringComparison.OrdinalIgnoreCase));
                            if (view == null)
                            {
                                var folded = Fuzzy.Fold(a.View);
                                var contains = views.Where(v => Fuzzy.Fold(v.Name).Contains(folded)).ToList();
                                if (contains.Count == 1) view = contains[0];
                                else
                                {
                                    var near = Fuzzy.Rank(a.View, views.Select(v => v.Name), 5);
                                    return ReadEnvelope.Fail(ErrorCodes.NotFound, "No view '" + a.View + "' in this model.",
                                        near.Count > 0 ? near : contains.Select(v => v.Name).Take(5));
                                }
                            }
                            if (view is ViewSchedule) return NotAnImage(view);
                            if (a.Multi) notes.Add("mode multi works with target elements; this is the single named view.");
                            return LookText.Build(LookCapture.NamedView(ctx.Doc, view, EnvelopeLimits.ImageMaxPx), null, notes);
                        }
                        default:
                        {
                            var targets = new List<ElementRef>();
                            if (a.Handle != null)
                            {
                                var held = ctx.Handles.Get(a.Handle);
                                if (held == null)
                                    return ReadEnvelope.Fail(ErrorCodes.NotFound,
                                        "Handle " + a.Handle + " is unknown or expired in this session. Run find_elements again.", Array.Empty<string>());
                                targets.AddRange(held);
                            }
                            targets.AddRange(a.Ids.Select(id => new ElementRef(id, null)));
                            targets = targets.Distinct().ToList();
                            var image = LookCapture.Elements(ctx.Doc, targets, EnvelopeLimits.ImageMaxPx, a.Multi, notes);
                            return LookText.Build(image, a.Handle, notes);
                        }
                    }
                }
            }
            catch (NoImageException ex)
            {
                return ReadEnvelope.Fail(ErrorCodes.NoGpu, ex.Message + " The machine may have no usable GPU (for example over remote desktop).");
            }
            catch (ArgumentException ex)
            {
                return ReadEnvelope.Fail(ErrorCodes.NotFound, ex.Message, Array.Empty<string>());
            }
            catch (InvalidOperationException ex)
            {
                return ReadEnvelope.Fail(ErrorCodes.NotAllowed, ex.Message);
            }
        }

        private static Envelope NotAnImage(View view) =>
            ReadEnvelope.Fail(ErrorCodes.NotAllowed,
                "'" + view.Name + "' is a schedule, which has no picture; read its rows with inspect(schedule: '" + view.Name + "').");

        /// <summary>Image of a 3D view around the targets (used by W3's preview thumbnail).
        /// Null when no image can be made; never throws. Runs inside the caller's
        /// CallbackScope when one is active, else under its own Look scope.</summary>
        public static ImageDto? CaptureElements(ToolContext ctx, IReadOnlyList<ElementRef> targets, int maxPx = 1024)
        {
            if (ctx?.Doc == null || targets == null || targets.Count == 0) return null;
            try
            {
                using (LookScope.Enter(ctx.Doc))
                    return LookCapture.Elements(ctx.Doc, targets, Math.Max(16, Math.Min(maxPx, EnvelopeLimits.ImageMaxPx)), multi: false, new List<string>());
            }
            catch
            {
                return null;
            }
        }
    }
}

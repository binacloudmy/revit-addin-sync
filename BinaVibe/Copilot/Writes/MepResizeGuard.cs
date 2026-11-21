#nullable enable
// r23 (0.0.169), live laptop bfa7d410 on merged54: the model's own run_revit_code set a duct's Width and Height and committed with no
// card (set_parameters on a duct size always gets the ripple card; the backend's text checks of the code can be dodged). The structural
// rule, in the add-in's preview: a run_revit_code call WITHOUT a stored playbook whose preview changed a size parameter of a duct, pipe,
// flex duct, flex pipe, conduit or cable tray is never committed. Its envelope is refused (ok false, status rejected, phase preview,
// policy_rejected) with the backend's OWN_RESIZE text (copilot_unitask.py on fix/copilot-r23-units4 a1fbd320) and data.mep_resize, one
// row per changed value. Stored playbook runs and set_parameters are untouched. Revit-free (the engine reads the values) so it is tested.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public static class MepResizeGuard
    {
        /// <summary>The backend's OWN_RESIZE message, byte for byte.</summary>
        public const string OwnResize =
            "Not run: nothing was changed. The size of a duct, pipe, conduit or cable tray is never changed by your own code: Revit resizes the "
            + "connected run, so the drafter confirms it on a card first. Send set_parameters instead, on the same elements: Width and Height "
            + "together (rectangular: the first number Width, the second Height) or Diameter (round), each with the drafter's value and unit as "
            + "typed. The backend asks the unit if it is open and shows the confirm card.";

        /// <summary>The guarded categories (BuiltInCategory names) and size parameters (BuiltInParameter names).</summary>
        public static readonly string[] Categories =
            { "OST_DuctCurves", "OST_PipeCurves", "OST_FlexDuctCurves", "OST_FlexPipeCurves", "OST_Conduit", "OST_CableTray" };

        public static readonly string[] Parameters =
        {
            "RBS_CURVE_WIDTH_PARAM", "RBS_CURVE_HEIGHT_PARAM", "RBS_CURVE_DIAMETER_PARAM", "RBS_PIPE_DIAMETER_PARAM",
            "RBS_CONDUIT_DIAMETER_PARAM", "RBS_CABLETRAY_WIDTH_PARAM", "RBS_CABLETRAY_HEIGHT_PARAM",
        };

        /// <summary>Internal-unit tolerance (feet): a value that moved by less is Revit's rounding, not a resize.</summary>
        public const double Tolerance = 1e-9;

        /// <summary>The call is the model's own code: run_revit_code with no stored playbook.</summary>
        public static bool Applies(ToolCallDto? call)
        {
            if (call == null || !string.Equals(call.Tool, ToolNames.RunRevitCode, StringComparison.Ordinal)) return false;
            var a = call.Args;
            if (a.ValueKind != JsonValueKind.Object) return true;
            return !(a.TryGetProperty("playbook", out var pb) && pb.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pb.GetString()));
        }

        /// <summary>One size value of one element: internal value (null: no value) and its display text.</summary>
        public sealed class SizeValue
        {
            public long Id;
            public string Category = "";
            public string Param = "";
            public double? Value;
            public string? Display;
        }

        /// <summary>The rows that changed: each value read after the code ran in the preview, against the same value once the preview rolled
        /// back (the value before). A value that appeared or disappeared counts as changed.</summary>
        public static List<Dictionary<string, object?>> Changed(IEnumerable<SizeValue> after, IEnumerable<SizeValue> before)
        {
            var was = (before ?? Enumerable.Empty<SizeValue>()).GroupBy(v => (v.Id, v.Param)).ToDictionary(g => g.Key, g => g.First());
            var rows = new List<Dictionary<string, object?>>();
            foreach (var now in after ?? Enumerable.Empty<SizeValue>())
            {
                was.TryGetValue((now.Id, now.Param), out var old);
                var a = now.Value; var b = old?.Value;
                var same = a.HasValue == b.HasValue && (!a.HasValue || Math.Abs(a.Value - b!.Value) <= Tolerance);
                if (same) continue;
                rows.Add(new Dictionary<string, object?>
                {
                    ["id"] = now.Id, ["category"] = now.Category, ["param"] = now.Param,
                    ["before"] = old?.Display, ["after"] = now.Display,
                });
            }
            return rows;
        }

        /// <summary>The refusal that replaces the preview's envelope: nothing is committed.</summary>
        public static Envelope Refusal(IReadOnlyList<Dictionary<string, object?>> rows)
        {
            var e = EnvelopeFactory.Failed(ErrorCodes.PolicyRejected, OwnResize);
            e.Status = EnvelopeStatus.Rejected;
            e.Phase = EnvelopePhase.Preview;
            e.Data = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["mep_resize"] = rows });
            return e;
        }
    }
}

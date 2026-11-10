// When a write turn earns before/after view images (AI-217, P2.2).
//
// Each image is a full ExportImage of the active view (1-5 s on the Revit
// thread). The receipt card shows them as 200 px thumbnails, so they only
// help when the change is something you can SEE: geometry, placement, type.
// A batch that only writes data (Comments, Fire Rating, sheet issue dates,
// JKR codes) looks the same before and after, so both exports are skipped.
// Revit-free so the rule is unit-tested.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RevitWebAppSync.Services
{
    public static class ReceiptCapture
    {
        /// <summary>Write tools that only change data, never what a view shows.</summary>
        private static readonly HashSet<string> DataOnlyTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "set_revision_on_sheets", "create_revision", "create_project_parameter", "store_data",
        };

        /// <summary>Tools that set one named parameter; data-only when that
        /// parameter is a data field (see IsDataParameter).</summary>
        private static readonly HashSet<string> ParameterTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "set_parameter", "set_parameter_bulk", "set_parameter_by_filter",
            "set_parameter_where", "fill_missing_parameter", "propagate_parameter_by_name",
        };

        /// <summary>Text / identity fields that never move geometry. Mark,
        /// Number and Keynote are left out on purpose: tags show them in the view.
        /// Workset edits are left out too: views can colour or hide by workset.</summary>
        private static readonly HashSet<string> DataParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Comments", "Type Comments", "Description", "Fire Rating",
            "Assembly Code", "Manufacturer", "Model", "URL", "Cost",
            "Sheet Issue Date", "Checked By", "Drawn By", "Approved By", "Designed By", "Designer",
            "Ulasan", "Kadar Api",
        };

        public static bool IsDataParameter(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Trim();
            if (DataParameters.Contains(n)) return true;
            // JKR shared parameters (Kod_Komponen_Sumber_jkr_stt, …) are data fields.
            return n.StartsWith("Kod_", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("_jkr", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True when one write call can't change what the view shows.</summary>
        public static bool IsDataOnly(string tool, JsonElement args)
        {
            if (string.IsNullOrEmpty(tool)) return false;
            if (DataOnlyTools.Contains(tool)) return true;
            if (!ParameterTools.Contains(tool)) return false;
            return IsDataParameter(ParamName(args));
        }

        /// <summary>Should this approved batch take before/after images? True
        /// when any write call might change what the view shows (or can't be
        /// classified). Reads are ignored.</summary>
        public static bool NeedsImages(IReadOnlyList<PendingToolCall> pending)
        {
            if (pending == null) return false;
            foreach (var c in pending)
                if (c != null && c.Mutate && !IsDataOnly(c.Tool, c.Args)) return true;
            return false;   // no writes, or data-only writes: nothing to see
        }

        /// <summary>(ms, count) of image exports a receipt job measured itself
        /// (its result's export_ms / exports), or null when it didn't.</summary>
        public static (double ms, int count)? ReportedExport(IDictionary<string, object> result)
        {
            if (result == null || !result.TryGetValue("export_ms", out var ms) || ms == null) return null;
            try
            {
                int n = result.TryGetValue("exports", out var c) && c != null
                    ? Convert.ToInt32(c, System.Globalization.CultureInfo.InvariantCulture) : 0;
                return (Convert.ToDouble(ms, System.Globalization.CultureInfo.InvariantCulture), n);
            }
            catch { return null; }
        }

        private static string ParamName(JsonElement args)
        {
            try
            {
                if (args.ValueKind != JsonValueKind.Object) return null;
                foreach (var key in new[] { "parameter", "param" })
                    if (args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
            }
            catch { /* unreadable args = unknown = take the images */ }
            return null;
        }
    }
}

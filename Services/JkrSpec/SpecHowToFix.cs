using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RevitWebAppSync.Services.JkrSpec
{
    /// <summary>The "How to fix manually" text of one of the 61 checks.</summary>
    public sealed class HowToFixEntry
    {
        public string CheckId = "";
        public List<string> Steps = new List<string>();
        public string Why = "";
        public string Source = "";
        /// <summary>The Researcher marked the check as one the Copilot can fix. The steps still show (the manual path).</summary>
        public bool CopilotFixable;

        /// <summary>"Why: … Source: …" for the line under the steps; "" when both are empty.</summary>
        public string Note
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Why)) parts.Add("Why: " + Why.Trim());
                if (!string.IsNullOrWhiteSpace(Source)) parts.Add("Source: " + Source.Trim());
                return string.Join(" ", parts);
            }
        }
    }

    /// <summary>
    /// Per-check manual fix steps, as data (columns: check_id, steps separated by " | ", why, source, copilot_fixable).
    /// Source: Researcher file how-to-fix-steps-2026-11-23.csv, saved without the UTF-8 BOM (the loader also strips one).
    /// Refresh: replace <c>Services/JkrSpec/data/jkr_howto_fix_steps.csv</c>; SpecHowToFixTests checks every spec id has steps.
    /// </summary>
    public static class SpecHowToFix
    {
        public const string ResourceName = "jkr_howto_fix_steps.csv";
        public const string StepSeparator = " | ";

        /// <summary>Added by the card (not in the csv) after the check's steps when the row is eligible for "Fix via Copilot".</summary>
        public const string CopilotSentence = "Or use Fix via Copilot.";

        private static readonly Lazy<IReadOnlyDictionary<string, HowToFixEntry>> _all =
            new Lazy<IReadOnlyDictionary<string, HowToFixEntry>>(LoadEmbedded);

        public static IReadOnlyDictionary<string, HowToFixEntry> All => _all.Value;

        /// <summary>The entry of a check id ("A1.1"), or null.</summary>
        public static HowToFixEntry Find(string checkId) =>
            !string.IsNullOrWhiteSpace(checkId) && All.TryGetValue(checkId.Trim(), out var e) ? e : null;

        /// <summary>Parse the csv (header: check_id,steps,why,source,copilot_fixable). Quote-aware; a leading BOM is dropped.</summary>
        public static Dictionary<string, HowToFixEntry> Parse(string csv)
        {
            var map = new Dictionary<string, HowToFixEntry>(StringComparer.Ordinal);
            var text = (csv ?? "").TrimStart('﻿');
            var first = true;
            foreach (var f in SpecTemplateViews.Records(text))
            {
                if (first) { first = false; continue; }
                if (f.Count < 5) throw new FormatException(ResourceName + ": expected 5 columns, got " + f.Count + ": " + string.Join("|", f));
                var e = new HowToFixEntry
                {
                    CheckId = f[0].Trim(),
                    Steps = f[1].Split(new[] { StepSeparator }, StringSplitOptions.None).Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
                    Why = f[2].Trim(),
                    Source = f[3].Trim(),
                    CopilotFixable = string.Equals(f[4].Trim(), "yes", StringComparison.OrdinalIgnoreCase),
                };
                if (e.CheckId.Length > 0) map[e.CheckId] = e;
            }
            return map;
        }

        private static IReadOnlyDictionary<string, HowToFixEntry> LoadEmbedded()
        {
            var asm = typeof(SpecHowToFix).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ResourceName, StringComparison.Ordinal));
            if (name == null) throw new InvalidOperationException(ResourceName + " is not embedded in " + asm.GetName().Name);
            using (var s = asm.GetManifestResourceStream(name))
            using (var r = new StreamReader(s, Encoding.UTF8))
                return Parse(r.ReadToEnd());
        }
    }
}

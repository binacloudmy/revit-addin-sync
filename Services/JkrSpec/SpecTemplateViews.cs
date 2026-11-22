using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RevitWebAppSync.Services.JkrSpec
{
    /// <summary>One row of the official JKR AR Revit template's view list.</summary>
    public sealed class TemplateViewRow
    {
        public string Name = "";
        /// <summary>Revit ViewType name ("FloorPlan", "Schedule" …).</summary>
        public string ViewType = "";
        /// <summary>True = a view template (what ViewTemplates in the snapshot holds); false = an ordinary view.</summary>
        public bool IsViewTemplate;
    }

    /// <summary>
    /// The view list of the official JKR AR template, as data (Linear AI-482, C1.1.1 / C1.1.2).
    /// Source: <c>jkrAR27_TEMFLEX TERKINI 2025_251015.rte</c>, 216 views, exported in Revit on 22 Nov 2026
    /// (name, view_type, is_view_template). Only the 80 rows with <c>is_view_template = true</c> are judged; the
    /// other 136 are the template's own plans, schedules, legends and sheets (project-specific, names carry level
    /// and set) and are not judged.
    /// <para>Refresh for a new JKR edition: open the new .rte in Revit, export every view (Document.GetElements of
    /// View, including templates) as name, view_type, is_view_template, and replace
    /// <c>Services/JkrSpec/data/jkr_ar_template_views.csv</c> (same three quoted columns). Update the counts in
    /// SpecTemplateViewsTests.</para>
    /// </summary>
    public static class SpecTemplateViews
    {
        public const string ResourceName = "jkr_ar_template_views.csv";

        /// <summary>The JKR-made view templates are named with this prefix ("jkrAR_01w_WIP_(Pelan Siling)"); the
        /// rest of the 80 are Revit's own default-template names ("Architectural Plan", "Site Plan" …).</summary>
        public const string JkrPrefix = "jkrAR_";

        private static readonly Lazy<IReadOnlyList<TemplateViewRow>> _all = new Lazy<IReadOnlyList<TemplateViewRow>>(LoadEmbedded);

        public static IReadOnlyList<TemplateViewRow> All => _all.Value;

        /// <summary>All view templates of the JKR file (JKR-made + Revit default names).</summary>
        public static IEnumerable<TemplateViewRow> ViewTemplates => All.Where(r => r.IsViewTemplate);

        /// <summary>The view templates C1.1.1 / C1.1.2 judge: JKR-made ("jkrAR_…"). The Revit default-template
        /// names are not judged: the data does not say JKR requires them kept (AI-504 policy: never invent).</summary>
        public static List<string> Judged => ViewTemplates.Where(r => IsJkr(r.Name)).Select(r => r.Name).ToList();

        /// <summary>Revit default-template names in the JKR file, not judged.</summary>
        public static List<string> NotJudged => ViewTemplates.Where(r => !IsJkr(r.Name)).Select(r => r.Name).ToList();

        public static bool IsJkr(string name) => (name ?? "").Trim().StartsWith(JkrPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>Name compared without case, spaces, underscores and punctuation ("jkrAR_03p_Persembahan (Pelan)" ==
        /// "JKRAR 03P Persembahan Pelan"). Equal keys with different text = a cosmetic rename.</summary>
        public static string LooseKey(string name) =>
            new string((name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        /// <summary>Parse the quoted CSV (header: name,view_type,is_view_template). Quote-aware: names hold commas.</summary>
        public static List<TemplateViewRow> Parse(string csv)
        {
            var rows = new List<TemplateViewRow>();
            var first = true;
            foreach (var f in Records(csv ?? ""))
            {
                if (first) { first = false; continue; }
                if (f.Count < 3) throw new FormatException(ResourceName + ": expected 3 columns, got " + f.Count + ": " + string.Join("|", f));
                rows.Add(new TemplateViewRow { Name = f[0], ViewType = f[1], IsViewTemplate = string.Equals(f[2].Trim(), "true", StringComparison.OrdinalIgnoreCase) });
            }
            return rows;
        }

        internal static IEnumerable<List<string>> Records(string text)
        {
            var rec = new List<string>();
            var sb = new StringBuilder();
            var quoted = false; var any = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; }
                    else sb.Append(c);
                }
                else if (c == '"') { quoted = true; any = true; }
                else if (c == ',') { rec.Add(sb.ToString()); sb.Clear(); any = true; }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    if (any || sb.Length > 0) { rec.Add(sb.ToString()); yield return rec; }
                    rec = new List<string>(); sb.Clear(); any = false;
                }
                else { sb.Append(c); any = true; }
            }
            if (any || sb.Length > 0) { rec.Add(sb.ToString()); yield return rec; }
        }

        private static IReadOnlyList<TemplateViewRow> LoadEmbedded()
        {
            var asm = typeof(SpecTemplateViews).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ResourceName, StringComparison.Ordinal));
            if (name == null) throw new InvalidOperationException(ResourceName + " is not embedded in " + asm.GetName().Name);
            using (var s = asm.GetManifestResourceStream(name))
            using (var r = new StreamReader(s, Encoding.UTF8))
                return Parse(r.ReadToEnd());
        }
    }
}

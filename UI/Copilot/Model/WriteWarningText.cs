#nullable enable
// The "Revit warned about N things" row of a result card (AI-332): words and counts, pure.
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class WriteWarningText
    {
        /// <summary>How many warnings Revit raised: a folded finding counts every warning it stands for.</summary>
        public static int Count(IEnumerable<Finding> warnings) => warnings.Sum(w => System.Math.Max(1, w.Repeats));

        /// <summary>"Revit warned about 3 things" / "... 1 thing"; BM "Revit memberi amaran tentang 3 perkara".</summary>
        public static string Title(int count, ReceiptLanguage lang)
        {
            var n = count.ToString(CultureInfo.InvariantCulture);
            return lang == ReceiptLanguage.Ms
                ? "Revit memberi amaran tentang " + n + " perkara"
                : "Revit warned about " + n + (count == 1 ? " thing" : " things");
        }

        /// <summary>The line under the title: the write went through, and this is what Revit flagged.</summary>
        public static string Subtitle(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms
            ? "Perubahan telah dibuat. Semak elemen yang ditandakan."
            : "The change was made. Look at what Revit flagged.";

        /// <summary>Rows shown before "+ N more".</summary>
        public const int MaxRows = 5;
    }
}

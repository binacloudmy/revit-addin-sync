using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitWebAppSync.Services.JkrSpec
{
    /// <summary>
    /// One row of a Buku Parameter AR category table (PB p.36–102): a parameter, whether the book
    /// lists it under Type or Instance, and the contiguous run of LOi columns ticked.
    /// Source of truth: <c>Services/JkrSpec/data/jkr_pb_ar_parameters.csv</c>, generated from the book by
    /// <c>tools/pb-extract/extract.py</c> (never hand-edited).
    /// </summary>
    public sealed class PbParam
    {
        public string Category = "";
        public int Page;
        /// <summary>"Type" or "Instance", from the name's suffix (…_jkr_s<b>t</b>t / s<b>i</b>t) for shared parameters.</summary>
        public string Kind = "";
        public string Name = "";
        /// <summary>0 = plain; 1 = "*" ("Parameter yang perlu diisi jika berkaitan" = fill if relevant, the book's
        /// legend p.39, 44 … 102); 2 = "**" (3 AR rows, no AR legend). A starred row is LISTED, never failed.</summary>
        public int Star;
        public int FromLoi;
        public int ToLoi;
        /// <summary>"_jkr_sit" … ; empty for a plain Revit built-in (Name, Base Level, Description …).</summary>
        public string Suffix = "";

        /// <summary>A JKR shared parameter, read by name. False = a Revit built-in, which needs a map to Revit's own
        /// parameters and is not judged yet.</summary>
        public bool IsShared => Suffix.Length > 0;
        public bool IsInstance => string.Equals(Kind, "Instance", StringComparison.OrdinalIgnoreCase);
        public bool Starred => Star > 0;

        /// <summary>Value letter of the name (…_jkr_sit → 't' text, 'x' multiline, 'y' yes/no, 'm' material, 'c' currency,
        /// 'l' length, 'i' integer, 'a' area); '\0' for a built-in.</summary>
        public char ValueLetter => Suffix.Length > 0 ? Suffix[Suffix.Length - 1] : '\0';

        /// <summary>Only a text-valued parameter has a value in the snapshot (SpecType.Texts) to judge as empty.</summary>
        public bool IsTextValued => ValueLetter == 't' || ValueLetter == 'x';

        public bool RequiredAt(int loi) => FromLoi > 0 && loi >= FromLoi && loi <= ToLoi;

        public override string ToString() => Name + " (PB p." + Page + ", LOi " + FromLoi + "–" + ToLoi + (Star > 0 ? ", " + new string('*', Star) : "") + ")";
    }

    /// <summary>The per-category × LOi parameter tables of the Buku Parameter, as data (Linear AI-503).</summary>
    public static class SpecPbParameters
    {
        public const string ResourceName = "jkr_pb_ar_parameters.csv";

        private static readonly Lazy<IReadOnlyList<PbParam>> _all = new Lazy<IReadOnlyList<PbParam>>(LoadEmbedded);

        public static IReadOnlyList<PbParam> All => _all.Value;

        /// <summary>The rows of a book category ("Door", "Curtain System").</summary>
        public static IEnumerable<PbParam> ForBookCategory(string bookCategory) =>
            All.Where(p => string.Equals(p.Category, bookCategory, StringComparison.OrdinalIgnoreCase));

        /// <summary>The book category a Revit category name belongs to ("Doors" → "Door", "Structural Columns" → "Column");
        /// null when the Buku Parameter has no AR page for it.</summary>
        public static string BookCategoryOf(string revitCategory)
        {
            var rc = (revitCategory ?? "").Trim();
            if (string.Equals(rc, "Structural Columns", StringComparison.OrdinalIgnoreCase)) return "Column";   // PB p.45 covers both column categories
            return SpecPbData.ArCategories.FirstOrDefault(c => c.Matches(rc))?.Name;
        }

        /// <summary>Rows of <paramref name="bookCategory"/> ticked at <paramref name="loi"/>, in book order.</summary>
        public static List<PbParam> At(string bookCategory, int loi) =>
            ForBookCategory(bookCategory).Where(p => p.RequiredAt(loi)).ToList();

        /// <summary>Parse the CSV (header: category,pb_page,param_kind,parameter,star,loi_from,loi_to,suffix_code,name_kind).
        /// No field holds a comma or a quote, so a split is enough.</summary>
        public static List<PbParam> Parse(string csv)
        {
            var rows = new List<PbParam>();
            var first = true;
            foreach (var raw in (csv ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (first) { first = false; continue; }
                var c = line.Split(',');
                if (c.Length < 9) throw new FormatException("jkr_pb_ar_parameters.csv: expected 9 columns: " + line);
                rows.Add(new PbParam
                {
                    Category = c[0], Page = Int(c[1]), Kind = c[2], Name = c[3], Star = Int(c[4]),
                    FromLoi = Int(c[5]), ToLoi = Int(c[6]), Suffix = c[7],
                });
            }
            return rows;
        }

        private static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static IReadOnlyList<PbParam> LoadEmbedded()
        {
            var asm = typeof(SpecPbParameters).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ResourceName, StringComparison.Ordinal));
            if (name == null) throw new InvalidOperationException(ResourceName + " is not embedded in " + asm.GetName().Name);
            using (var s = asm.GetManifestResourceStream(name))
            using (var r = new StreamReader(s))
                return Parse(r.ReadToEnd());
        }
    }
}

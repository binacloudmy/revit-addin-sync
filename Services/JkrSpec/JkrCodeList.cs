using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services.JkrSpec
{
    /// <summary>
    /// AI-535 (Da.1): the JKR code list the add-in proves a type name against, and the one place a "real JKR code" is decided. Revit-free.
    /// Source: the Researcher's list <c>jkr-ar-code-list-2026-11-23.csv</c> (333 rows, Piawaian JKR Lampiran A3 p.123-133 + 12 Buku Parameter samples),
    /// embedded as <c>Services/JkrSpec/data/jkr_ar_code_list.csv</c>.
    /// <list type="bullet">
    /// <item><b>Format</b>: Kod_Komponen = Kod_Jenis (two capitals + one small letter) + three digits + an optional small letter, <c>^[A-Z]{2}[a-z][0-9]{3}[a-z]?$</c>,
    /// written in brackets: "(PTr001)". Case-sensitive: "TGC002" is not "TGc002". Wrong format (two digits, no type letter) is a FAIL.</item>
    /// <item><b>Categories WITH a JKR list</b> (Door PT, Window TK, Casework PB, Furniture PA, Furniture System PM/PK, Plumbing LS, Specialty AL, Wall DG/DB/DF,
    /// Floor LT/LF, Roof BG/BF, Ceiling SL/SF): the Kod_Jenis must be in that category's list, else FAIL.</item>
    /// <item><b>Categories WITHOUT a list</b> (Generic Model, Railing, Stair, Ramp, Planting, Column and the rest): format only. A listed sample (GMa, TGc, LAk, PKa, Co)
    /// is coded; a well-formed code that is not a sample is a PERSON row ("confirm"), never a fail, until JKR gives the full list.</item>
    /// <item>In a "jkrXX_" system-family name the lowercase material / finish code in brackets ("(bb02)", "(kt)") of the list counts as a code.</item>
    /// <item>The "jkrAR" prefix alone, a generic name, or no bracketed code is NO code: FAIL.</item>
    /// </list>
    /// The three-digit number is chosen by the project and is in no list: a pass never proves the number exists.
    /// </summary>
    public static class JkrCodeList
    {
        private const string ResourceName = "jkr_ar_code_list.csv";

        // Revit category -> the CSV category it has a list for. A category not here has no list.
        private static readonly Dictionary<string, string> ListCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Doors"] = "Door (dor)", ["Windows"] = "Window (wdw)", ["Casework"] = "Casework (cwk)", ["Furniture"] = "Furniture (frn)",
            ["Furniture Systems"] = "Furniture System (frn-sy)", ["Plumbing Fixtures"] = "Plumbing Fixture (plb-fx)",
            ["Specialty Equipment"] = "Specialty Equipment (spc-eq)", ["Walls"] = "Wall (wll)", ["Floors"] = "Floor (flr)",
            ["Roofs"] = "Roof (rof)", ["Ceilings"] = "Ceiling (clg)",
        };

        private sealed class Data
        {
            public readonly Dictionary<string, HashSet<string>> ByCategory = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            public readonly HashSet<string> Samples = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Materials = new HashSet<string>(StringComparer.Ordinal);
        }

        private static Data _data;
        private static Data Load() => _data ?? (_data = Parse(ReadEmbedded()));

        private static string ReadEmbedded()
        {
            var asm = typeof(JkrCodeList).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(ResourceName, StringComparison.Ordinal));
            if (name == null) throw new InvalidOperationException(ResourceName + " is not embedded in " + asm.GetName().Name);
            using (var s = asm.GetManifestResourceStream(name))
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        private static Data Parse(string csv)
        {
            var d = new Data();
            var first = true;
            foreach (var line in (csv ?? "").Replace("\r", "").TrimStart('﻿').Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (first) { first = false; continue; }
                var c = Cells(line);
                if (c.Count < 3) continue;
                var cat = c[0].Trim(); var field = c[1].Trim(); var code = c[2].Trim();
                if (code.Length == 0) continue;
                if (field.StartsWith("pb_sample_only", StringComparison.Ordinal)) d.Samples.Add(code);
                else if (field.StartsWith("kod_bahan", StringComparison.Ordinal)) d.Materials.Add(code);
                else if (field.StartsWith("kod_jenis", StringComparison.Ordinal) || field.StartsWith("kod_sistem", StringComparison.Ordinal))
                {
                    if (!d.ByCategory.TryGetValue(cat, out var set)) d.ByCategory[cat] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(code);
                }
            }
            return d;
        }

        private static List<string> Cells(string line)
        {
            var cells = new List<string>(); var sb = new StringBuilder(); var q = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (q) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(ch); }
                else if (ch == '"') q = true;
                else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            cells.Add(sb.ToString());
            return cells;
        }

        private static readonly Regex Parens = new Regex(@"\(([^()]*)\)", RegexOptions.CultureInvariant);
        private static readonly Regex Component = new Regex(@"^(?<k>[A-Z]{2}[a-z])(?<n>[0-9]{3})(?<v>[a-z]?)$", RegexOptions.CultureInvariant);
        // The Buku Parameter column sample "Co100" (no type letter): a Column code, which has no list.
        private static readonly Regex ColumnCode = new Regex(@"^Co[0-9]{3}[a-z]?$", RegexOptions.CultureInvariant);
        // A token that is trying to be a component code (a capital, up to two more letters, digits, an optional letter): wrong format is a fail.
        private static readonly Regex CodeShaped = new Regex(@"^[A-Z][A-Za-z]{1,2}[0-9]{1,4}[A-Za-z]?$", RegexOptions.CultureInvariant);
        private static readonly Regex Material = new Regex(@"^(?<m>[a-z]{1,3})[0-9]{0,2}$", RegexOptions.CultureInvariant);
        private static readonly Regex JkrPrefix = new Regex(@"^jkr[A-Z]{2}", RegexOptions.CultureInvariant);

        /// <summary>True when the category has a JKR list (Door, Window, Casework, Furniture, Furniture System, Plumbing, Specialty, Wall, Floor, Roof, Ceiling).</summary>
        public static bool HasList(string category) => ListCategory.ContainsKey((category ?? "").Trim());

        /// <summary>The Kod_Jenis of the category's list (empty when it has none), sorted.</summary>
        public static List<string> KodJenisFor(string category)
        {
            if (!ListCategory.TryGetValue((category ?? "").Trim(), out var csvCat)) return new List<string>();
            return Load().ByCategory.TryGetValue(csvCat, out var set) ? set.OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>();
        }

        /// <summary>The documented samples of the categories that have no list (TGc, GMa, LAk, PKa, Co ...), sorted.</summary>
        public static List<string> Samples() => Load().Samples.OrderBy(x => x, StringComparer.Ordinal).ToList();

        /// <summary>True when the string (no brackets) has the Kod_Komponen format.</summary>
        public static bool HasFormat(string code) => Component.IsMatch((code ?? "").Trim()) || ColumnCode.IsMatch((code ?? "").Trim());

        /// <summary>Why a name is or is not coded.</summary>
        public enum Verdict
        {
            /// <summary>A code of the list (or a documented sample of a category with no list, or a material code in a jkrXX name) is in the name: pass.</summary>
            Coded,
            /// <summary>No bracketed code at all: the "jkrAR" prefix alone, a generic name: FAIL.</summary>
            NoCode,
            /// <summary>A code-shaped token of the wrong format (two digits, no type letter, wrong case): FAIL.</summary>
            BadFormat,
            /// <summary>Well-formed, the category has a list, the Kod_Jenis is not in it: FAIL.</summary>
            NotInList,
            /// <summary>Well-formed, the category has NO list and the code is not a documented sample: a PERSON confirms; never a fail.</summary>
            UnknownCode,
        }

        public sealed class NameJudgement
        {
            public Verdict Verdict;
            /// <summary>The code found (Coded) or the offending token (BadFormat, NotInList, UnknownCode), or "".</summary>
            public string Code = "";
            /// <summary>True when the name opens with the JKR discipline prefix ("jkrAR…").</summary>
            public bool HasPrefix;
        }

        /// <summary>The verdict for one bracketed token in a category.</summary>
        private static Verdict Token(string t, string category, bool prefixed)
        {
            var m = Component.Match(t);
            if (m.Success)
            {
                var k = m.Groups["k"].Value;
                if (ListCategory.TryGetValue((category ?? "").Trim(), out var csvCat))
                    return Load().ByCategory.TryGetValue(csvCat, out var set) && set.Contains(k) ? Verdict.Coded : Verdict.NotInList;
                return Load().Samples.Contains(k) ? Verdict.Coded : Verdict.UnknownCode;
            }
            if (ColumnCode.IsMatch(t))
                return HasList(category) ? Verdict.BadFormat : Verdict.Coded;
            if (prefixed)
            {
                var mm = Material.Match(t);
                if (mm.Success && Load().Materials.Contains(mm.Groups["m"].Value)) return Verdict.Coded;
            }
            return CodeShaped.IsMatch(t) ? Verdict.BadFormat : Verdict.NoCode;
        }

        /// <summary>Judges one name (a type name or a family name) in a Revit category. Order of precedence: Coded, UnknownCode (a person), NotInList, BadFormat, NoCode.</summary>
        public static NameJudgement Judge(string name, string category = null)
        {
            var s = Clean(name);
            var prefixed = JkrPrefix.IsMatch(s);
            var found = Tokens(s).Select(t => (T: t, V: Token(t, category, prefixed))).ToList();
            foreach (var v in new[] { Verdict.Coded, Verdict.UnknownCode, Verdict.NotInList, Verdict.BadFormat })
            {
                var hit = found.FirstOrDefault(x => x.V == v);
                if (hit.T != null) return new NameJudgement { Verdict = v, Code = hit.T, HasPrefix = prefixed };
            }
            return new NameJudgement { Verdict = Verdict.NoCode, HasPrefix = prefixed };
        }

        /// <summary>True when the code (no brackets) is well-formed and, for a category with a list, in it; for a category without one, a documented sample. Null category: any list.</summary>
        public static bool IsComponentCode(string code, string category = null)
        {
            var c = (code ?? "").Trim();
            if (category != null) return Token(c, category, false) == Verdict.Coded && HasFormat(c);
            var m = Component.Match(c);
            if (m.Success) return Load().Samples.Contains(m.Groups["k"].Value) || Load().ByCategory.Values.Any(s => s.Contains(m.Groups["k"].Value));
            return ColumnCode.IsMatch(c);
        }

        /// <summary>Every "(...)" token of a name, without the parentheses, in order.</summary>
        public static List<string> Tokens(string name) =>
            Parens.Matches(Clean(name)).Cast<Match>().Select(m => m.Groups[1].Value.Trim()).Where(t => t.Length > 0).ToList();

        /// <summary>The codes of a name that are real for the category, without brackets.</summary>
        public static List<string> ComponentCodesIn(string name, string category = null) =>
            Tokens(name).Where(t => HasFormat(t) && IsComponentCode(t, category)).ToList();

        private static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format) sb.Append(ch);
            return sb.ToString().Trim();
        }
    }
}

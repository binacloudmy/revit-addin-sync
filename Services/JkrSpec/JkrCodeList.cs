using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services.JkrSpec
{
    /// <summary>
    /// AI-535 (Da.1): the JKR code list the add-in can prove a type name against, and the one place a "real JKR code" is decided.
    /// Revit-free and WPF-free (unit-tested on the Mac harness).
    /// <list type="bullet">
    /// <item><b>Component code</b> "(TGc002)", "(TKh281a)", "(PTr001a)": kod jenis (two capitals and one small letter) + two or three digits + an optional variant
    /// letter. The kod jenis must be in <see cref="KodJenis"/>: the codes of Piawaian JKR Lampiran A3 (Kod Jenis of every Arkitek family and system family)
    /// plus the ones the Buku Parameter examples add (TGc stair, LBk floor, LAk ramp, GMa generic, PKa planting). The three-digit number is chosen by the
    /// project, so the list cannot prove it: a pass means "pattern and kod jenis are real", never "the number is in the project's list".</item>
    /// <item><b>Material / finish code</b> "(bb02)", "(kt)", "(jh02)" of a "jkrAR_…" system-family name (Lampiran A3 Kod Bahan / Kod Kemasan and the PB
    /// examples), accepted only inside a name that opens with the JKR discipline prefix.</item>
    /// </list>
    /// Three outcomes (review round 1): <see cref="Verdict.Coded"/> = in the list = pass; <see cref="Verdict.NoCode"/> (no parenthesised code, the
    /// "jkrAR" prefix alone, a generic name, a listed code in the wrong case) = fail; <see cref="Verdict.UnknownCode"/> (code-SHAPED token not in the list,
    /// "(TG002a)", "(MKq001a)") = NOT a fail: a person confirms it, so an incomplete list can never lower the % or rename a correct name.
    /// The list is case-sensitive: "TGC002" is not "TGc002".
    /// The prefix "jkrAR" alone is never a code (AI-475 gap, AI-535: Copilot wrote "jkrAR_System-Panel_100" and it passed).
    /// </summary>
    public static class JkrCodeList
    {
        private static readonly string[] KodJenisRaw =
        {
            "ALa", "ALb", "ALc", "ALd", "ALe", "ALf", "ALi", "ALk", "ALm", "ALn", "ALq", "BFa",
            "BFe", "BFf", "BFg", "BFk", "BFl", "BFp", "BFr", "BFt", "BGc", "BGe", "BGk", "BGm",
            "BGq", "BGt", "BGu", "DBa", "DBb", "DBc", "DBd", "DBg", "DBi", "DBj", "DBk", "DBm",
            "DBn", "DBo", "DFa", "DFb", "DFd", "DFk", "DFl", "DFn", "DFp", "DFq", "DFs", "DGa",
            "DGb", "DGc", "DGd", "DGg", "DGi", "DGj", "DGk", "DGm", "DGn", "DGo", "DGp", "DGq",
            "DGr", "DGt", "DGu", "GMa", "LAk", "LBk", "LFa", "LFb", "LFh", "LFk", "LFl", "LFp",
            "LFq", "LFs", "LFv", "LSa", "LSb", "LSc", "LSd", "LSe", "LSf", "LSg", "LSh", "LSi",
            "LSj", "LSl", "LSm", "LSn", "LSo", "LSp", "LSq", "LSr", "LSs", "LSt", "LSu", "LSw",
            "LTd", "LTg", "LTi", "LTk", "LTm", "LTq", "LTr", "LTy", "PAa", "PAb", "PAc", "PAd",
            "PAk", "PAm", "PAq", "PAr", "PAt", "PBa", "PBb", "PBc", "PBd", "PBe", "PBg", "PBk",
            "PBm", "PBq", "PBr", "PBt", "PKa", "PKj", "PKk", "PKq", "PKr", "PKs", "PKu", "PMa",
            "PMb", "PMc", "PMk", "PMl", "PMm", "PMq", "PMr", "PMt", "PTa", "PTb", "PTc", "PTd",
            "PTe", "PTf", "PTg", "PTh", "PTi", "PTj", "PTk", "PTl", "PTm", "PTn", "PTo", "PTp",
            "PTq", "PTr", "PTs", "PTt", "PTu", "PTv", "PTy", "PTz", "SFb", "SFc", "SFf", "SFg",
            "SFj", "SFm", "SFp", "SFq", "SFs", "SLf", "SLg", "SLk", "SLt", "TGc", "TKa", "TKb",
            "TKc", "TKd", "TKe", "TKf", "TKg", "TKh", "TKj", "TKk", "TKm", "TKn", "TKo", "TKp",
            "TKq", "TKr", "TKt", "TKu", "TKy", "TKz",
        };

        private static readonly string[] MaterialRaw =
        {
            "ab", "ac", "al", "as", "aw", "ba", "bb", "bc", "bk", "bm", "bp", "br",
            "bu", "bv", "ca", "cb", "cd", "ce", "cf", "cl", "cp", "ctc", "dl", "dw",
            "emp", "en", "ep", "fb", "fh", "fo", "fp", "fr", "gbp", "gf", "gk", "gp",
            "gr", "gt", "hp", "jh", "jhh", "jhn", "jk", "jkh", "jng", "jns", "js", "jsg",
            "jt", "ka", "kb", "kcp", "kcs", "kd", "kf", "kh", "kl", "kp", "kr", "ks",
            "ksm", "kt", "ktd", "ky", "kyp", "la", "lc", "le", "lg", "lh", "li", "lm",
            "ls", "lv", "mf", "mr", "ms", "mz", "mzk", "ni", "of", "or", "pa", "pb",
            "pc", "pg", "pgc", "ph", "pj", "pk", "pks", "pls", "po", "pr", "ps", "ptc",
            "pu", "pv", "pw", "px", "py", "q", "rm", "rs", "sc", "sf", "sl", "ss",
            "st", "tk", "tp", "vh", "vy", "yh", "zc",
        };

        /// <summary>The kod jenis (three letters) of the JKR code list, case-sensitive ("TGc", not "TGC").</summary>
        public static readonly IReadOnlyCollection<string> KodJenis = new HashSet<string>(KodJenisRaw, StringComparer.Ordinal);

        /// <summary>The material / finish codes (letters only; a name may add two digits: "bb02").</summary>
        public static readonly IReadOnlyCollection<string> MaterialCodes = new HashSet<string>(MaterialRaw, StringComparer.Ordinal);

        private static readonly Regex Parens = new Regex(@"\(([^()]*)\)", RegexOptions.CultureInvariant);
        private static readonly Regex Component = new Regex(@"^(?<k>[A-Z]{2}[a-z])(?<n>\d{2,3})(?<v>[a-z]?)$", RegexOptions.CultureInvariant);
        // The older shape the repo's own examples use ("(PT2p600a) 900 x 2325 a"): two capitals, a digit, a small letter, three digits, an optional letter.
        // Accepted only when the two-letter family exists in the list, so a correct older name is never failed.
        private static readonly Regex Legacy = new Regex(@"^(?<f>[A-Z]{2})\d[a-z]\d{3}[a-z]?$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Families = new HashSet<string>(KodJenisRaw.Select(k => k.Substring(0, 2)), StringComparer.Ordinal);
        // The Buku Parameter column example "Co100": a structural column code with no third letter.
        private static readonly Regex ColumnCode = new Regex(@"^Co\d{3}[a-z]?$", RegexOptions.CultureInvariant);
        private static readonly Regex Material = new Regex(@"^(?<m>[a-z]{1,3})\d{0,2}$", RegexOptions.CultureInvariant);
        // A token that is trying to be a component code (so a wrong one is "not in the list", not "no code").
        private static readonly Regex CodeShaped = new Regex(@"^[A-Z]{2}[a-z]?\d{2,3}[a-z]?$", RegexOptions.CultureInvariant);
        private static readonly Regex JkrPrefix = new Regex(@"^jkr[A-Z]{2}", RegexOptions.CultureInvariant);

        /// <summary>True when <paramref name="code"/> (without parentheses) is a component code of the list: known kod jenis, three digits, optional letter.</summary>
        public static bool IsComponentCode(string code)
        {
            var c = (code ?? "").Trim();
            var m = Component.Match(c);
            if (m.Success) return KodJenis.Contains(m.Groups["k"].Value);
            var l = Legacy.Match(c);
            if (l.Success) return Families.Contains(l.Groups["f"].Value);
            return ColumnCode.IsMatch(c);
        }

        /// <summary>True for a material / finish code of the list ("bb02", "kt").</summary>
        public static bool IsMaterialCode(string code)
        {
            var m = Material.Match((code ?? "").Trim());
            return m.Success && MaterialCodes.Contains(m.Groups["m"].Value);
        }

        /// <summary>Every "(...)" token of a name, without the parentheses, in order.</summary>
        public static List<string> Tokens(string name) =>
            Parens.Matches(Clean(name)).Cast<Match>().Select(m => m.Groups[1].Value.Trim()).Where(t => t.Length > 0).ToList();

        /// <summary>Why a name is or is not coded.</summary>
        public enum Verdict
        {
            /// <summary>A real component code (or, in a jkrXX name, a real material code) is in the name.</summary>
            Coded,
            /// <summary>No code at all in the name (a "jkrAR" prefix alone lands here).</summary>
            NoCode,
            /// <summary>A component-code-shaped token whose kod jenis is not in the list.</summary>
            UnknownCode,
        }

        public sealed class NameJudgement
        {
            public Verdict Verdict;
            /// <summary>The real code found (Coded), the unknown token (UnknownCode), or "".</summary>
            public string Code = "";
            /// <summary>True when the name opens with the JKR discipline prefix ("jkrAR…").</summary>
            public bool HasPrefix;
            /// <summary>Set when a token is a listed code written in the wrong letter case ("TGC002" for "TGc002"): the list is case-sensitive.</summary>
            public string CaseMismatch = "";
        }

        /// <summary>Judges one name (a type name or a family name) against the code list.</summary>
        public static NameJudgement Judge(string name)
        {
            var s = Clean(name);
            var prefixed = JkrPrefix.IsMatch(s);
            var tokens = Tokens(s);
            foreach (var t in tokens)
                if (IsComponentCode(t)) return new NameJudgement { Verdict = Verdict.Coded, Code = t, HasPrefix = prefixed };
            if (prefixed)
                foreach (var t in tokens)
                    if (IsMaterialCode(t)) return new NameJudgement { Verdict = Verdict.Coded, Code = t, HasPrefix = true };
            var unknown = tokens.FirstOrDefault(t => CodeShaped.IsMatch(t));
            if (unknown != null) return new NameJudgement { Verdict = Verdict.UnknownCode, Code = unknown, HasPrefix = prefixed };
            var wrongCase = tokens.FirstOrDefault(t => KodJenis.Any(k => Regex.IsMatch(t, "^" + k + @"\d{2,3}[A-Za-z]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
            return new NameJudgement { Verdict = Verdict.NoCode, HasPrefix = prefixed, CaseMismatch = wrongCase ?? "" };
        }

        /// <summary>True when a real code is in the name.</summary>
        public static bool IsCoded(string name) => Judge(name).Verdict == Verdict.Coded;

        /// <summary>The component codes of the list found in a name, "(TGc002)" form without the parentheses, in order.</summary>
        public static List<string> ComponentCodesIn(string name) => Tokens(name).Where(IsComponentCode).ToList();

        /// <summary>Format of the answer the prompt accepts for a kod jenis: "TGc" + three digits + an optional letter (text for the prompt).</summary>
        public static string ShapeText(string kodJenis) => kodJenis + "nnn (three digits, optionally one small letter)";

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

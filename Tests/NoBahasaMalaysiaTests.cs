using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-513: the JKR compliance pane is English only. This scans the JKR source files and fails when a string
    /// literal (C#) or a text attribute (XAML) holds a Bahasa Malaysia word. WPF-free: it reads files only, so it
    /// runs on the Mac harness.
    ///
    /// What is scanned: the string literals of every .cs file of the JKR pane and its content (UI/Jkr/**, the
    /// dashboard host / panel, Services/JkrSpec/**, Services/Jkr*, Handlers/Jkr*, Commands/Jkr*) and of the legacy
    /// BIM010 checker whose remarks reach the pane (BinaVibe/Mcp/Tools/Audit/*); the text attributes of the XAML
    /// of UI/Jkr/** and the dashboard panel. Comments are not scanned (a comment may quote a Malay term of the
    /// spec); neither are test files.
    ///
    /// How the allow-list is defined (all of it is in this file, each entry says why):
    ///   1. <see cref="ExemptFiles"/> — whole files that are Malay on purpose: the Borang BIM010 export (JKR's
    ///      official Malay form; Ashraf's decision, 22 Nov 2026) and its form definition.
    ///   2. <see cref="ProperNames"/> — JKR proper names removed from a literal before it is checked
    ///      ("Borang", "Buku Parameter", "Kod_Jenis…", shared-parameter names, view-set names).
    ///   3. <see cref="ExemptLiterals"/> — a literal that holds Malay on purpose because the add-in MATCHES it
    ///      against names written by people or by JKR (regexes and keyword lists that find "pintu", "tangga",
    ///      "Butiran Pintu" … in a model), or parses a value ("ya"/"tidak"). Keyed by file + a substring of the
    ///      literal, so a new Malay sentence in the same file is still caught.
    ///   4. <see cref="PartAPending"/> — files of AI-513 part A (pane UI strings) that still hold Malay in this
    ///      branch. It must be empty once part A is merged; <see cref="Part_A_pending_files_are_only_listed_while_they_hold_Malay"/>
    ///      fails when a listed file is already clean, so the list can only shrink.
    /// </summary>
    public class NoBahasaMalaysiaTests
    {
        // ── the word list ──
        // Malay words that are not English words and do not occur in the JKR parameter names. Matched as whole words,
        // case-insensitive (so Terbuka / Selesai / Abaikan / Baiki / Semak / Tukar / Tetapkan / Tidak / Ya also hit).
        // Words that are also English or that appear inside names ("model", "fail", "manual", "grid", "di", "nama" as
        // part of Dimodel_Nama …) are left out: a whole-word match never fires inside an underscored name.
        private static readonly string[] MalayWords =
        {
            // the UI terms the issue names
            "terbuka", "selesai", "abaikan", "baiki", "semak", "senarai", "senaraikan", "tukar", "tetapkan", "tidak", "tiada", "ya",
            "sila", "dan", "atau", "dengan", "untuk", "yang", "adalah", "dalam", "daripada", "dari", "pada", "kepada", "ini", "itu",
            // sentence words the pane texts and remarks used
            "dijumpai", "tersebut", "supaya", "mengikut", "ikut", "kosong", "belum", "sudah", "telah", "boleh", "perlu", "wajib",
            "setiap", "semua", "hanya", "juga", "lagi", "bukan", "ialah", "oleh", "serta", "sahaja", "berkenaan", "berbanding",
            "betulkan", "kenapa", "lulus", "gagal", "patuh", "amaran", "ralat", "manusia", "putuskan", "disemak", "dikira",
            "kemaskini", "dikemaskini", "dibaca", "dikenal", "dikesan", "diletakkan", "diperlukan", "sebenar", "menunggu",
            "jalankan", "hantar", "ubah", "pilih", "tambah", "isi", "cth", "bila", "mana", "jika", "kerana", "tak", "dapat",
            // model words
            "lantai", "bilik", "pintu", "tingkap", "dinding", "siling", "bumbung", "tangga", "ruang", "aras", "jenis", "nama",
            "projek", "laluan", "jadual", "helaian", "bahan", "keluarga", "kategori", "keputusan", "semakan", "peraturan",
            "masuk", "keluar", "buka", "tutup", "simpan", "batal", "mula", "habis", "selesaikan", "tunggu", "berjaya",
        };

        private static readonly Regex MalayRx = new Regex(
            @"(?<![\w])(" + string.Join("|", MalayWords.OrderByDescending(w => w.Length)) + @")(?![\w])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // ── allow-list 1: files that are Malay on purpose (Borang BIM010 export) ──
        private static readonly (string File, string Why)[] ExemptFiles =
        {
            ("UI/Jkr/Export/BorangPdf.cs", "Borang BIM010 export: JKR's official Malay form (Ashraf, 22 Nov 2026)"),
            ("UI/Jkr/Export/BorangDoc.cs", "Borang BIM010 export: JKR's official Malay form"),
            ("UI/Jkr/Panel/Core/SpecBorang.cs", "Borang BIM010 rows and marks (the form's own wording, BorangKey data keys)"),
            ("BinaVibe/Mcp/Tools/Audit/Bim010Form.cs", "the Borang BIM010 form definition (row descriptions as printed)"),
            ("BinaVibe/Mcp/Tools/Audit/AuditFormParser.cs", "reads the Malay Borang form file (its headings)"),
            ("BinaVibe/Mcp/Tools/Audit/AuditMatching.cs", "keyword lists that match the Malay Borang row descriptions"),
            ("BinaVibe/Mcp/Tools/Audit/AuditModels.cs", "form-row model: the Borang column names"),
        };

        // ── allow-list 2: JKR proper names, removed from a literal before checking ──
        private static readonly Regex ProperNames = new Regex(
            @"Borang(?:\s+BIM010)?|Buku\s+Parameter|Piawaian|Dokumen|Need\s+Statement|Kod_\w+|\w+_jkr_\w+|jkr\w*_[\w\-\(\)\.]+|"
            + @"Dimodel\w*|Tajuk_\w+|Maklumat_\w+|Bahan_\w+|Luas_\w+|Aras\s+[\w\-]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // ── allow-list 3: literals that MATCH or PARSE Malay on purpose (file + substring of the literal) ──
        private static readonly (string File, string Contains, string Why)[] ExemptLiterals =
        {
            ("Services/JkrSpec/SpecChecks.A.cs", "tangga", "regex / space-kind word: finds stairs by their Malay or English names in the model"),
            ("Services/JkrSpec/SpecChecks.A.cs", "Aras ", "level names a model writes as 'Aras 01' keep their own name"),
            ("Services/JkrSpec/SpecChecks.A.cs", "|masuk|", "regex: finds a main entrance door by its Malay or English name"),
            ("Services/JkrSpec/SpecChecks.A.cs", "utama", "regex: finds a main door by its Malay or English name"),
            ("Services/JkrSpec/SpecChecks.A.cs", "susur", "regex: finds a handrail / balcony by its Malay or English name"),
            ("Services/JkrSpec/SpecChecks.BC.cs", "lantai", "level-word list: matches Malay level names in a model"),
            ("Services/JkrSpec/SpecChecks.BC.cs", "aras", "level-word list: matches Malay level names in a model"),
            ("Services/JkrSpec/SpecChecks.BC.cs", "pintu", "sheet / view name matching: 'Butiran Pintu', 'detail pintu' in a model"),
            ("Services/JkrSpec/SpecChecks.BC.cs", "tingkap", "sheet / view name matching: 'Butiran Tingkap' in a model"),
            ("Services/JkrSpec/SpecChecks.BC.cs", "Tajuk", "title-block parameter names: Tajuk_Projek, Tajuk Lukisan"),
            ("Services/JkrSpec/SpecChecks.BC.cs", "Oleh", "title-block parameter names: Dimodel_Oleh, Dilukis Oleh"),
            ("Services/JkrSpec/SpecChecks.DE.cs", "pintu", "type-name keyword table: Malay and English words that name a Revit category"),
            ("Services/JkrSpec/SpecChecks.DE.cs", "tingkap", "type-name keyword table"),
            ("Services/JkrSpec/SpecChecks.DE.cs", "dinding", "type-name keyword table"),
            ("Services/JkrSpec/SpecChecks.DE.cs", "lantai", "type-name keyword table"),
            ("Services/JkrSpec/SpecChecks.DE.cs", "siling", "type-name keyword table"),
            ("Services/JkrSpec/SpecChecks.DE.cs", "bumbung", "type-name keyword table"),
            ("Services/JkrSpec/SpecChecks.DE.cs", "tangga", "type-name keyword table"),
            ("Services/JkrSpec/SpecChecks.DE.cs", "generic|default", "generic-name regex: also catches Malay generic names"),
            ("UI/Jkr/ViewModels/JkrFixerRow.cs", "Pintu Kayu", "the JKR example material name (a Malay material description in JKR's own coding)"),
            ("Services/JkrFixApplicator.cs", "ya", "parses a Yes/No value the backend may send as Ya/Tidak"),
            ("Services/JkrFixApplicator.cs", "tidak", "parses a Yes/No value the backend may send as Ya/Tidak"),
            ("BinaVibe/Mcp/Tools/Audit/AuditCheckers.cs", "aras", "keyword that matches a Malay Borang row description"),
            ("BinaVibe/Mcp/Tools/Audit/AuditCheckers.cs", "ralat", "keyword that matches a Malay Borang row description"),
            ("BinaVibe/Mcp/Tools/Audit/AuditCheckers.cs", "amaran", "keyword that matches a Malay Borang row description"),
            ("BinaVibe/Mcp/Tools/Audit/AuditCheckers.cs", "keluarga", "keyword that matches a Malay Borang row description"),
            ("BinaVibe/Mcp/Tools/Audit/AuditCheckers.cs", "helaian", "keyword that matches a Malay Borang row description"),
            ("BinaVibe/Mcp/Tools/Audit/AuditCheckers.cs", "jadual", "keyword that matches a Malay Borang row description"),
            ("BinaVibe/Mcp/Tools/Audit/AuditTools.cs", "Semak manual", "the Borang docx/pdf remark fallback (Borang export, Malay form)"),
        };

        // ── allow-list 4: part A files still holding Malay in this branch (must shrink to empty) ──
        private static readonly string[] PartAPending = { };

        // ── what is scanned ──
        private static readonly string[] ScanRoots = { "UI/Jkr", "Services/JkrSpec", "BinaVibe/Mcp/Tools/Audit" };
        private static readonly string[] ScanFilePatterns =
        {
            "UI/JkrComplianceDashboard*.cs", "UI/JkrComplianceDashboardPanel.xaml", "Services/Jkr*.cs", "Handlers/Jkr*.cs", "Commands/Jkr*.cs",
        };

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.True(dir != null, "repo root not found from " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        private static string Rel(string root, string path) => path.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');

        private static List<string> ScannedFiles()
        {
            var root = Root();
            var files = new List<string>();
            foreach (var r in ScanRoots)
            {
                var dir = Path.Combine(root, r.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir)) continue;
                files.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)));
            }
            foreach (var p in ScanFilePatterns)
            {
                var slash = p.LastIndexOf('/');
                var dir = Path.Combine(root, p.Substring(0, slash).Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, p.Substring(slash + 1)));
            }
            // BinaVibe/Mcp/Tools/Audit also holds the Borang form parser etc.; only the files that write user text matter,
            // the rest is exempt by name above.
            return files.Select(f => Rel(root, f)).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        // ── extracting the text ──

        /// <summary>The string literals of a C# source (comments skipped); interpolation holes are cut out.</summary>
        internal static List<(int Line, string Text)> CSharpLiterals(string src)
        {
            var list = new List<(int, string)>();
            var line = 1;
            for (var i = 0; i < src.Length;)
            {
                var c = src[i];
                if (c == '\n') { line++; i++; continue; }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/') { while (i < src.Length && src[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) { if (src[i] == '\n') line++; i++; }
                    i += 2; continue;
                }
                if (c == '\'')
                {
                    // char literal ('"', '\'', '\\'): skip it whole so a quote inside is not a string start
                    i++;
                    if (i < src.Length && src[i] == '\\') i++;
                    i++;
                    if (i < src.Length && src[i] == '\'') i++;
                    continue;
                }
                var interp = false; var verbatim = false; var j = i;
                while (j < src.Length && (src[j] == '$' || src[j] == '@') && j - i < 3) { if (src[j] == '$') interp = true; else verbatim = true; j++; }
                if (j < src.Length && src[j] == '"' && (j == i || (j > i)) && (j == i || src[j - 1] == '$' || src[j - 1] == '@'))
                {
                    var startLine = line;
                    j++;
                    var sb = new StringBuilder();
                    while (j < src.Length)
                    {
                        var d = src[j];
                        if (d == '\n') line++;
                        if (verbatim)
                        {
                            if (d == '"') { if (j + 1 < src.Length && src[j + 1] == '"') { sb.Append('"'); j += 2; continue; } break; }
                        }
                        else
                        {
                            if (d == '\\' && j + 1 < src.Length) { sb.Append(src[j + 1]); j += 2; continue; }
                            if (d == '"') break;
                        }
                        if (interp && d == '{')
                        {
                            if (j + 1 < src.Length && src[j + 1] == '{') { sb.Append('{'); j += 2; continue; }
                            // skip the hole, including nested strings
                            var depth = 1; j++;
                            while (j < src.Length && depth > 0)
                            {
                                var e = src[j];
                                if (e == '\n') line++;
                                if (e == '"') { j++; while (j < src.Length && src[j] != '"') { if (src[j] == '\\') j++; j++; } }
                                else if (e == '{') depth++;
                                else if (e == '}') depth--;
                                j++;
                            }
                            sb.Append(' ');
                            continue;
                        }
                        sb.Append(d); j++;
                    }
                    list.Add((startLine, sb.ToString()));
                    i = j + 1; continue;
                }
                i++;
            }
            return list;
        }

        /// <summary>The text of a XAML file: attribute values that are shown to the user.</summary>
        internal static List<(int Line, string Text)> XamlTexts(string src)
        {
            var list = new List<(int, string)>();
            var shown = new Regex(@"\b(Text|Content|Header|ToolTip|Title|Watermark|PlaceholderText|AutomationProperties\.Name|Label|Caption)\s*=\s*""([^""{][^""]*)""");
            var lines = src.Replace("\r\n", "\n").Split('\n');
            for (var n = 0; n < lines.Length; n++)
                foreach (Match m in shown.Matches(lines[n])) list.Add((n + 1, System.Net.WebUtility.HtmlDecode(m.Groups[2].Value)));
            // element text:  <TextBlock>text</TextBlock>
            var inner = new Regex(@">\s*([^<>{\s][^<>{]*?)\s*</");
            for (var n = 0; n < lines.Length; n++)
                foreach (Match m in inner.Matches(lines[n])) list.Add((n + 1, System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)));
            return list;
        }

        /// <summary>The Malay words of a text, after the JKR proper names are taken out.</summary>
        internal static List<string> MalayIn(string text)
        {
            var t = ProperNames.Replace(text ?? "", " ");
            return MalayRx.Matches(t).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
        }

        private static bool Exempt(string file, string text) =>
            ExemptFiles.Any(e => e.File == file)
            || ExemptLiterals.Any(e => e.File == file && text.IndexOf(e.Contains, StringComparison.OrdinalIgnoreCase) >= 0);

        private static List<string> Findings(string file)
        {
            var root = Root();
            var src = File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            var texts = file.EndsWith(".xaml") ? XamlTexts(src) : CSharpLiterals(src);
            var found = new List<string>();
            foreach (var (line, text) in texts)
            {
                if (text.Length < 2 || Exempt(file, text)) continue;
                var words = MalayIn(text);
                if (words.Count > 0) found.Add($"{file}:{line}: [{string.Join(", ", words)}] \"{(text.Length > 90 ? text.Substring(0, 90) + "…" : text.Replace("\n", "\\n"))}\"");
            }
            return found;
        }

        // ── the tests ──

        [Fact]
        public void No_Malay_text_in_the_JKR_pane_sources()
        {
            var bad = ScannedFiles().Where(f => !PartAPending.Contains(f)).SelectMany(Findings).ToList();
            Assert.True(bad.Count == 0, "Bahasa Malaysia text in the JKR pane (AI-513: English only; Borang export and JKR names are allow-listed in this test):\n"
                                         + string.Join("\n", bad.Take(400)));
        }

        [Fact]
        public void Part_A_pending_files_are_only_listed_while_they_hold_Malay()
        {
            foreach (var f in PartAPending)
                Assert.True(Findings(f).Count > 0, f + " holds no Malay any more: remove it from PartAPending.");
        }

        [Fact]
        public void The_scan_reaches_the_files_it_is_meant_to_and_the_allow_list_names_real_files()
        {
            var files = ScannedFiles();
            foreach (var must in new[]
            {
                "Services/JkrSpec/SpecChecks.A.cs", "Services/JkrSpec/SpecChecks.BC.cs", "Services/JkrSpec/SpecChecks.DE.cs",
                "UI/Jkr/Panel/Core/SpecCatalog.cs", "UI/Jkr/Panel/Core/SpecReport.cs", "UI/Jkr/ViewModels/IssueMapper.cs",
                "UI/Jkr/ViewModels/JkrCopilotPrompt.cs", "BinaVibe/Mcp/Tools/Audit/AuditCheckers.cs",
            })
                Assert.Contains(must, files);
            var root = Root();
            foreach (var e in ExemptFiles) Assert.True(File.Exists(Path.Combine(root, e.File)), e.File + " does not exist");
            foreach (var e in ExemptLiterals) Assert.True(File.Exists(Path.Combine(root, e.File)), e.File + " does not exist");
            // every exempt literal is still needed: removing it makes the scan fire, so a stale entry cannot hide a new one
            var stale = new List<string>();
            foreach (var e in ExemptLiterals)
            {
                var src = File.ReadAllText(Path.Combine(root, e.File));
                var needed = CSharpLiterals(src).Any(l => l.Text.IndexOf(e.Contains, StringComparison.OrdinalIgnoreCase) >= 0 && MalayIn(l.Text).Count > 0);
                if (!needed) stale.Add(e.File + " \"" + e.Contains + "\"");
            }
            Assert.True(stale.Count == 0, "exemptions no literal needs any more (remove them):\n" + string.Join("\n", stale));
        }

        [Theory]
        [InlineData("Terbuka")]
        [InlineData("Selesai")]
        [InlineData("Abaikan semua")]
        [InlineData("Baiki semua 3 perubahan")]
        [InlineData("tak dapat disemak")]
        [InlineData("Semak manual")]
        [InlineData("Tukar nama jenis")]
        [InlineData("Tetapkan Material")]
        [InlineData("Ya")]
        [InlineData("Tidak")]
        [InlineData("Tiada siling dimodelkan")]
        [InlineData("Sila tunggu")]
        [InlineData("A dan B")]
        [InlineData("satu atau dua")]
        public void The_word_list_catches_the_known_Malay_terms(string text) =>
            Assert.NotEmpty(MalayIn(text));

        [Theory]
        [InlineData("Open")]
        [InlineData("Done")]
        [InlineData("Ignore all")]
        [InlineData("Fix all 3 changes")]
        [InlineData("not judged")]
        [InlineData("Yes")]
        [InlineData("No")]
        [InlineData("Kod_Jenis_jkr_stt (from LOi 300)")]
        [InlineData("Borang BIM010 · Buku Parameter p.36")]
        [InlineData("Dokumen set, BOMBA, PBT")]
        [InlineData("Level Aras 01: ceiling 2700 mm < 3000 mm")]
        [InlineData("Model code found: W00 (single model)")]
        [InlineData("No manual fix: the grid has a name")]
        public void The_word_list_does_not_hit_English_or_JKR_names(string text)
        {
            // "Aras" in a model's own level name is data, not a Malay sentence: only a Malay WORD list hit counts, and
            // "Level Aras 01" is checked here because the add-in prints level names as the model has them.
            var words = MalayIn(text);
            if (text.Contains("Aras 01")) words.Remove("aras");
            Assert.Empty(words);
        }

        [Fact]
        public void The_literal_reader_skips_comments_and_reads_interpolated_and_verbatim_strings()
        {
            var lits = CSharpLiterals("// \"tiada\"\nvar a = \"Open\"; /* \"semak\" */ var b = $\"x {(c ? \"tiada\" : \"y\")} done\"; var d = @\"a \"\"dan\"\" b\"; var e = '\"'; var f = \"ok\";");
            Assert.Equal(new[] { "Open", "x   done", "a \"dan\" b", "ok" }, lits.Select(l => l.Text).ToArray());
        }
    }
}

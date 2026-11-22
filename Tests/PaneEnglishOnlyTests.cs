// AI-513: the JKR compliance pane is English only. The BM / EN toggle and its
// stored setting are gone; no screen string and no PaneText string is Malay.
// (The content strings, the catalog, the remarks and the Borang export are
// scanned by their own tests.)

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class PaneEnglishOnlyTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        // Words that are Malay and not English or a JKR name.
        private static readonly Regex Bm = new Regex(
            @"\b(Terbuka|Selesai|Abaikan|Diabaikan|Baiki|Dibaiki|Semak|semak|Tukar|Tetapkan|Tidak|Tiada|tiada|Ya|isu|Isu|gagal|Gagal|dalam|pada|dan|atau|untuk|yang|dengan|belum|Belum|sahaja|Pilih|Projek|Imbas|Tutup|Batal|Eksport|Pembaikan|Mengaudit|Menunggu|Cari|Guna|Keperluan|Elemen|Cadangan|Tahap|Disiplin|Seksyen|Skor|Tarikh|patuh|Patuh|ditulis|Perubahan|Ralat|sudah|tetapi|boleh|anda|Anda|Sila|Halaman|Papan|Lulus|lulus|Simpan|Jalankan|daripada|Aras)\b");

        [Fact]
        public void There_is_no_language_switch_in_the_pane()
        {
            var root = RepoRoot();
            foreach (var rel in new[] { @"UI\Jkr\Panel\JkrPaneShell.xaml", @"UI\Jkr\Export\BorangPreviewWindow.xaml" })
            {
                var xaml = File.ReadAllText(Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar)));
                Assert.DoesNotContain("Content=\"BM\"", xaml);
                Assert.DoesNotContain("Content=\"EN\"", xaml);
                Assert.DoesNotContain("IsBm", xaml);
            }
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "UI"), "*.cs", SearchOption.AllDirectories)
                         .Where(f => f.IndexOf(Path.DirectorySeparatorChar + "Copilot" + Path.DirectorySeparatorChar, StringComparison.Ordinal) < 0))
            {
                // comments may still name the old switch; code may not
                var text = string.Join("\n", File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                Assert.False(text.Contains("PaneLanguage"), file + " still reads PaneLanguage");
                Assert.False(Regex.IsMatch(text, @"\bSetLang\("), file + " still has SetLang");
            }
        }

        [Fact]
        public void The_screen_strings_are_English()
        {
            foreach (var key in PanelStrings.Keys)
            {
                var text = PanelStrings.Get(key);
                Assert.False(string.IsNullOrEmpty(text), key);
                Assert.False(Bm.IsMatch(text), key + ": " + text);
            }
        }

        [Fact]
        public void The_PaneText_strings_are_English()
        {
            // Call every public static string method with plain arguments and scan what it returns.
            var checkedAny = 0;
            foreach (var m in typeof(PaneText).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(x => x.ReturnType == typeof(string)))
            {
                var args = m.GetParameters().Select(p =>
                    p.ParameterType == typeof(string) ? (object)"x"
                    : p.ParameterType == typeof(int) ? 3
                    : p.ParameterType == typeof(long) ? 3L
                    : p.ParameterType == typeof(bool) ? false
                    : null).ToArray();
                if (args.Any(a => a == null)) continue;
                var text = (string)m.Invoke(null, args);
                Assert.False(Bm.IsMatch(text ?? ""), m.Name + ": " + text);
                checkedAny++;
            }
            Assert.True(checkedAny > 40);
            foreach (var f in typeof(PaneText).GetFields(BindingFlags.Public | BindingFlags.Static).Where(x => x.IsLiteral && x.FieldType == typeof(string)))
                Assert.False(Bm.IsMatch((string)f.GetRawConstantValue()), f.Name);
        }
    }
}

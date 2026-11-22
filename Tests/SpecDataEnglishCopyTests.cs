using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-513: the repo copies of the spec sheet (jkr_spec_rules / jkr_spec_items / jkr_spec_ns_clauses .tsv,
    /// spec_rows.json) are hand-translated English; SpecTallyTests prove the code matches THAT copy. The Malay
    /// exports of the workbook are kept beside them as *_ms.tsv / spec_rows_ms.json. This test proves the English
    /// copy still says the same numbers as the Malay one: same ids, same numbers, units, clause and page references
    /// per row and cell. The next real export from the workbook replaces the *_ms files; a drift fails here, loudly.
    /// </summary>
    public class SpecDataEnglishCopyTests
    {
        private static string Path_(string n) => Path.Combine(AppContext.BaseDirectory, "data", n);

        private static List<string[]> Tsv(string n) =>
            File.ReadAllLines(Path_(n)).Where(l => !l.StartsWith("#")).Skip(1).Select(l => l.Split('\t')).ToList();

        // numbers (850, 2.5, 3000), clause / page references (§5.8.5, p.36, T 21:19) are the same in both languages
        private static List<string> Facts(string cell) =>
            Regex.Matches(cell ?? "", @"§?\d+(?:[.,:–/\-]\d+)*").Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal).ToList();

        private static void SameFacts(string where, string ms, string en) =>
            Assert.True(Facts(ms).SequenceEqual(Facts(en)), where + ": numbers differ — ms [" + string.Join(" ", Facts(ms)) + "] en [" + string.Join(" ", Facts(en)) + "]");

        [Fact]
        public void The_English_rules_sheet_has_the_ids_and_numbers_of_the_Malay_one()
        {
            var ms = Tsv("jkr_spec_rules_ms.tsv"); var en = Tsv("jkr_spec_rules.tsv");
            Assert.Equal(ms.Count, en.Count);
            for (var i = 0; i < ms.Count; i++)
            {
                Assert.Equal(ms[i][2], en[i][2]);                      // check id
                if (ms[i][2].Length == 0) { Assert.Equal("JUMLAH", ms[i][0]); Assert.Equal("TOTAL", en[i][0]); SameFacts("totals", string.Join(" ", ms[i]), string.Join(" ", en[i])); continue; }
                Assert.Equal(ms[i][0], en[i][0]);                      // section
                Assert.Equal(ms[i][1], en[i][1]);                      // item
                for (var c = 3; c <= 8 && c < ms[i].Length && c < en[i].Length; c++)
                    if (c != 7) SameFacts(ms[i][2] + " col " + c, ms[i][c], en[i][c]);
            }
        }

        [Fact]
        public void The_English_items_sheet_has_the_rows_and_numbers_of_the_Malay_one()
        {
            var ms = Tsv("jkr_spec_items_ms.tsv"); var en = Tsv("jkr_spec_items.tsv");
            Assert.Equal(ms.Count, en.Count);
            for (var i = 0; i < ms.Count; i++)
            {
                Assert.Equal(ms[i][1], en[i][1]);
                if (ms[i].Length > 2 && en[i].Length > 2) SameFacts("item " + ms[i][1], ms[i][2], en[i][2]);
            }
        }

        [Fact]
        public void The_English_NS_clauses_sheet_has_the_clauses_and_numbers_of_the_Malay_one()
        {
            var ms = Tsv("jkr_spec_ns_clauses_ms.tsv"); var en = Tsv("jkr_spec_ns_clauses.tsv");
            Assert.Equal(ms.Count, en.Count);
            for (var i = 0; i < ms.Count; i++)
                for (var c = 0; c < 4 && c < ms[i].Length && c < en[i].Length; c++)
                    if (c == 0 && !Regex.IsMatch(ms[i][0], @"\d")) continue;   // "lain-lain" / "other"
                    else SameFacts("clause " + ms[i][0] + " col " + c, ms[i][c], en[i][c]);
        }

        [Fact]
        public void The_English_spec_rows_json_has_the_ids_classes_and_numbers_of_the_Malay_one()
        {
            var ms = JArray.Parse(File.ReadAllText(Path_("spec_rows_ms.json")));
            var en = JArray.Parse(File.ReadAllText(Path_("spec_rows.json")));
            Assert.Equal(ms.Count, en.Count);
            for (var i = 0; i < ms.Count; i++)
            {
                Assert.Equal((string)ms[i]["id"], (string)en[i]["id"]);
                Assert.Equal((string)ms[i]["class"], (string)en[i]["class"]);
                SameFacts((string)ms[i]["id"] + " comply", (string)ms[i]["comply_text"], (string)en[i]["comply_text"]);
                SameFacts((string)ms[i]["id"] + " not comply", (string)ms[i]["not_comply_text"], (string)en[i]["not_comply_text"]);
            }
        }
    }
}

#nullable enable
// The "Needs your OK" card for a write that is wider than the prompt reads (board Confirm). Revit-free.
//
// The server side (a gate that asks before a wide write) is built elsewhere and its payload is not fixed yet.
// Everything the card shows is therefore derived from plain values handed to ONE factory, WideWriteModel.From:
// when the real payload lands, map it to these arguments there and nowhere else. The view
// (WideWriteCard) only draws what this model holds.
//
// Content of the board: the eyebrow, one headline sentence, an optional second sentence, "The write would reach
// 235 fixtures . 10 types" with a tile per group (the narrow group the prompt meant, the wide group it did not)
// and per-type rows (the first 5, then "and N more types"), the parameters it will set as chips, and three
// answers: the narrow write (primary), the whole write (secondary), cancel (ghost).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum WideChoice { Narrow, All, Cancel }

    public enum WideRole { Primary, Secondary, Ghost }

    public sealed class WideTypeRow
    {
        public string Name = "";
        public int Count;
    }

    /// <summary>The plain input for one group of types (e.g. "Downlights", "Other lighting").</summary>
    public sealed class WideGroupInput
    {
        public string Label = "";
        /// <summary>True for the group the prompt did NOT mean (drawn with the amber warning look).</summary>
        public bool IsWide;
        public List<WideTypeRow> Types = new List<WideTypeRow>();
    }

    public sealed class WideGroupModel
    {
        public string Label = "";
        public bool IsWide;
        public int TypeCount;
        public int FixtureCount;
        /// <summary>The rows shown: the first <see cref="WideWriteModel.MaxRows"/> types.</summary>
        public List<WideTypeRow> Rows = new List<WideTypeRow>();
        public int MoreTypes;
        public int MoreFixtures;
        /// <summary>"Downlights . 2 types".</summary>
        public string Title = "";
        /// <summary>"52 fixtures".</summary>
        public string CountText = "";
        /// <summary>"and 4 more types", empty when every type is shown.</summary>
        public string MoreText = "";
    }

    public sealed class WideButtonModel
    {
        public WideRole Role;
        public WideChoice Choice;
        /// <summary>The option id the answer is sent under.</summary>
        public string Id = "";
        public string Label = "";
        /// <summary>The grey second line ("52 fixtures . 16 values . matches the brochure"); empty for the ghost.</summary>
        public string Detail = "";
    }

    public sealed class WideWriteModel
    {
        /// <summary>Type rows listed per group before "and N more types".</summary>
        public const int MaxRows = 5;

        // The option ids of the server's gate, which arrives as an ordinary ask event (multi=false, purpose clarify).
        public const string WriteAllId = "write_all";
        public const string WriteCancelId = "write_cancel";
        /// <summary>Reserved: the server never sends it today; if it ever does, it becomes the primary button.</summary>
        public const string WriteNarrowId = "write_narrow";

        public ReceiptLanguage Language;
        public string Eyebrow = "";
        public string Headline = "";
        public string Detail = "";
        public string ReachTitle = "";
        /// <summary>"235 fixtures . 10 types".</summary>
        public string ReachText = "";
        public int TotalFixtures;
        public int TotalTypes;
        public List<WideGroupModel> Groups = new List<WideGroupModel>();
        /// <summary>"Will set 8 parameters per type".</summary>
        public string ParamsTitle = "";
        public List<string> Params = new List<string>();
        public List<WideButtonModel> Buttons = new List<WideButtonModel>();
        public string Footnote = "";

        // ---- the words shared with the ask card's "Needs your OK" look -------------------------------------------

        public static string EyebrowText(bool ms) => ms ? "Perlu persetujuan anda" : "Needs your OK";

        public static string FootnoteText(bool ms) => ms
            ? "Copilot hanya bertanya apabila penulisan lebih luas daripada yang arahan nyatakan, memadam elemen, atau menjalankan kod. Pengisian biasa terus dilaksanakan dengan Buat asal."
            : "Copilot asks only when a write is wider than the prompt reads, deletes elements, or runs code. Ordinary fills apply straight away with Undo.";

        public static string FixturesText(int n, bool ms) =>
            N(n) + (ms ? " fixtur" : n == 1 ? " fixture" : " fixtures");

        public static string TypesText(int n, bool ms) =>
            N(n) + (ms ? " jenis" : n == 1 ? " type" : " types");

        public static string ValuesText(int n, bool ms) =>
            N(n) + (ms ? " nilai" : n == 1 ? " value" : " values");

        private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>The single place the real payload is mapped to the card. All arguments are plain values.</summary>
        /// <param name="headline">The one sentence ("Before I write: this reaches 10 lighting types, and 8 of them are not downlights.").</param>
        /// <param name="detail">An optional second, quieter sentence; empty for none.</param>
        /// <param name="groups">The types the write would reach, grouped; at most one group should be the wide one.</param>
        /// <param name="parameters">The parameter names set on every type, shown as chips.</param>
        /// <param name="narrowNoun">Singular noun for the narrow button ("downlight" gives "Write to the 2 downlight types only").</param>
        /// <param name="narrowReason">Why the narrow write is the suggested one ("matches the brochure"); empty for none.</param>
        public static WideWriteModel From(ReceiptLanguage lang, string headline, string? detail,
            IEnumerable<WideGroupInput> groups, IEnumerable<string>? parameters, string narrowNoun, string? narrowReason = null,
            string narrowId = "narrow", string allId = "all", string cancelId = "cancel")
        {
            var ms = lang == ReceiptLanguage.Ms;
            var pars = (parameters ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            var m = new WideWriteModel
            {
                Language = lang,
                Eyebrow = EyebrowText(ms),
                Headline = headline ?? "",
                Detail = detail ?? "",
                ReachTitle = ms ? "Penulisan ini akan sampai ke" : "The write would reach",
                Params = pars,
                Footnote = FootnoteText(ms),
            };

            foreach (var g in groups ?? Array.Empty<WideGroupInput>())
            {
                if (g == null || g.Types == null || g.Types.Count == 0) continue;
                var rows = g.Types.Where(t => t != null && !string.IsNullOrWhiteSpace(t.Name)).ToList();
                if (rows.Count == 0) continue;
                var gm = new WideGroupModel
                {
                    Label = g.Label ?? "",
                    IsWide = g.IsWide,
                    TypeCount = rows.Count,
                    FixtureCount = rows.Sum(t => t.Count),
                    Rows = rows.Take(MaxRows).ToList(),
                    MoreTypes = Math.Max(0, rows.Count - MaxRows),
                    MoreFixtures = rows.Skip(MaxRows).Sum(t => t.Count),
                };
                gm.Title = gm.Label + " · " + TypesText(gm.TypeCount, ms);
                gm.CountText = FixturesText(gm.FixtureCount, ms);
                gm.MoreText = gm.MoreTypes == 0 ? ""
                    : ms ? "dan " + N(gm.MoreTypes) + " jenis lagi" : "and " + N(gm.MoreTypes) + " more type" + (gm.MoreTypes == 1 ? "" : "s");
                m.Groups.Add(gm);
            }

            m.TotalFixtures = m.Groups.Sum(g => g.FixtureCount);
            m.TotalTypes = m.Groups.Sum(g => g.TypeCount);
            m.ReachText = FixturesText(m.TotalFixtures, ms) + " · " + TypesText(m.TotalTypes, ms);
            m.ParamsTitle = pars.Count == 0 ? ""
                : ms ? "Akan tetapkan " + N(pars.Count) + " parameter bagi setiap jenis"
                : "Will set " + N(pars.Count) + (pars.Count == 1 ? " parameter" : " parameters") + " per type";

            // The narrow write: the types of the groups that are not wide. Offered only when it really is narrower.
            var narrowTypes = m.Groups.Where(g => !g.IsWide).Sum(g => g.TypeCount);
            var narrowFixtures = m.Groups.Where(g => !g.IsWide).Sum(g => g.FixtureCount);
            var hasNarrow = narrowTypes > 0 && narrowTypes < m.TotalTypes;

            if (hasNarrow)
            {
                var noun = (narrowNoun ?? "").Trim();
                var label = ms
                    ? "Tulis ke " + N(narrowTypes) + " jenis " + noun + " sahaja"
                    : "Write to the " + N(narrowTypes) + " " + noun + (narrowTypes == 1 ? " type" : " types") + " only";
                m.Buttons.Add(new WideButtonModel
                {
                    Role = WideRole.Primary, Choice = WideChoice.Narrow, Id = narrowId, Label = label.Replace("  ", " "),
                    Detail = DetailLine(narrowFixtures, narrowTypes * pars.Count, narrowReason, ms),
                });
            }
            m.Buttons.Add(new WideButtonModel
            {
                Role = hasNarrow ? WideRole.Secondary : WideRole.Primary, Choice = WideChoice.All, Id = allId,
                Label = ms ? "Tulis ke semua " + N(m.TotalTypes) + " jenis"
                    : m.TotalTypes == 1 ? "Write to the 1 type" : "Write to all " + N(m.TotalTypes) + " types",
                Detail = DetailLine(m.TotalFixtures, m.TotalTypes * pars.Count, null, ms),
            });
            m.Buttons.Add(new WideButtonModel
            {
                Role = WideRole.Ghost, Choice = WideChoice.Cancel, Id = cancelId,
                Label = ms ? "Batal, jangan tulis apa-apa" : "Cancel, write nothing",
            });
            return m;
        }

        private static string DetailLine(int fixtures, int values, string? reason, bool ms)
        {
            var parts = new List<string> { FixturesText(fixtures, ms) };
            if (values > 0) parts.Add(ValuesText(values, ms));
            if (!string.IsNullOrWhiteSpace(reason)) parts.Add(reason!.Trim());
            return string.Join(" · ", parts);
        }

        private static readonly Regex BulletLine = new Regex(@"^\s*[•\-\*]\s*(?<name>.+?)\s*:\s*(?<n>[\d,]+)\s*$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex MoreLine = new Regex(@"^\s*(?:and\s+(?<k1>\d+)\s+more|dan\s+(?<k2>\d+)\s+lagi)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>True when an ask's options are the gate's: both write_all and write_cancel are present.</summary>
        public static bool IsGateOptions(IEnumerable<AskOptionModel>? options) =>
            options != null && options.Any(o => o.Id == WriteAllId) && options.Any(o => o.Id == WriteCancelId);

        /// <summary>Maps the gate's ask event to the card. The question is "headline", then up to 5 lines "bullet name: count",
        /// then "and K more" ("dan K lagi"); the bullet lines appear only when the server knows the groups. The buttons are the
        /// options (write_all primary, write_cancel ghost); a write_narrow option, if one ever arrives, comes first as the primary
        /// and write_all becomes the secondary. Null when the options are not the gate's.</summary>
        public static WideWriteModel? FromAsk(string? question, IEnumerable<AskOptionModel>? options, ReceiptLanguage lang)
        {
            var opts = (options ?? Array.Empty<AskOptionModel>()).Where(o => o != null).ToList();
            if (!IsGateOptions(opts)) return null;
            var ms = lang == ReceiptLanguage.Ms;
            var lines = (question ?? "").Replace("\r", "").Split('\n');
            var m = new WideWriteModel
            {
                Language = lang,
                Eyebrow = EyebrowText(ms),
                Headline = lines.Length > 0 ? lines[0].Trim() : "",
                ReachTitle = ms ? "Penulisan ini akan sampai ke" : "The write would reach",
                Footnote = FootnoteText(ms),
            };

            var group = new WideGroupModel();
            var extra = new List<string>();
            foreach (var raw in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var more = MoreLine.Match(raw);
                if (more.Success)
                {
                    group.MoreTypes = int.Parse(more.Groups["k1"].Success ? more.Groups["k1"].Value : more.Groups["k2"].Value, CultureInfo.InvariantCulture);
                    group.MoreFixtures = -1;                                // the server does not say how many elements they hold
                    group.MoreText = raw.Trim();
                    continue;
                }
                var bullet = BulletLine.Match(raw);
                if (bullet.Success && group.Rows.Count < MaxRows)
                {
                    int.TryParse(bullet.Groups["n"].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n);
                    group.Rows.Add(new WideTypeRow { Name = bullet.Groups["name"].Value.Trim(), Count = n });
                    continue;
                }
                extra.Add(raw.Trim());
            }
            m.Detail = string.Join(" ", extra);
            if (group.Rows.Count > 0)
            {
                group.TypeCount = group.Rows.Count + group.MoreTypes;
                m.Groups.Add(group);              // one unlabeled group: the view draws the plain list, no bar or tiles
            }

            string Label(string id) => opts.First(o => o.Id == id).Label;
            var hasNarrow = opts.Any(o => o.Id == WriteNarrowId);
            if (hasNarrow)
                m.Buttons.Add(new WideButtonModel { Role = WideRole.Primary, Choice = WideChoice.Narrow, Id = WriteNarrowId, Label = Label(WriteNarrowId) });
            m.Buttons.Add(new WideButtonModel
            {
                Role = hasNarrow ? WideRole.Secondary : WideRole.Primary, Choice = WideChoice.All, Id = WriteAllId, Label = Label(WriteAllId),
            });
            m.Buttons.Add(new WideButtonModel { Role = WideRole.Ghost, Choice = WideChoice.Cancel, Id = WriteCancelId, Label = Label(WriteCancelId) });
            return m;
        }

        /// <summary>The answer for a button: its option id as true, the same shape an ask option answers with.</summary>
        public AnswerDto AnswerFor(WideChoice choice)
        {
            var answer = new AnswerDto();
            var b = Buttons.FirstOrDefault(x => x.Choice == choice);
            if (b != null) answer.Values[b.Id] = JsonSerializer.SerializeToElement(true);
            return answer;
        }
    }
}

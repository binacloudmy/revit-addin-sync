#nullable enable
// Phase 0 summary of an electrical census: the three assumptions the spec (§10, §11) has to
// check on real drawings before the later phases are built, answered YES / PARTLY / NO.
//
//   A1  Electrical symbols are named block inserts (whatever layer they sit on).
//   A2  The drawing carries the architectural background (walls, plans).
//   A3  Conduit is drawn as lines/polylines on its own layer.
//
// The thresholds are starting values for measuring, kept here in code so they can be tested
// and argued about. Like ElectricalCensus it must compile on net48.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal sealed class CensusAnswer
    {
        public string Verdict = "NO";
        public List<string> Lines = new List<string>();
    }

    internal sealed class CensusAnswers
    {
        public CensusAnswer A1 = new CensusAnswer();
        public CensusAnswer A2 = new CensusAnswer();
        public CensusAnswer A3 = new CensusAnswer();
    }

    internal static class ElectricalCensusSummary
    {
        /// <summary>Discipline prefixes, matched as a whole token among the first three ("A-LV-
        /// ..."). "E" alone is too common a tail ("A-WALL-E") so it only counts first.</summary>
        private static readonly string[] ElectricalTokens = { "EL", "ELEC", "ELEK", "ELEKTRIK", "ELECTRICAL", "LV", "ELV" };

        /// <summary>Electrical words, English and Malay. Each must be a whole token, optionally
        /// inflected (LIGHTS, SWITCHES, LIGHTING), so PERIMETER is not a METER, SATELLITE not a
        /// LITE, PORTRAYAL not a TRAY and a FANLIGHT (a window) not a FAN.</summary>
        private static readonly string[] ElectricalWords =
        {
            "LIGHT", "LITE", "LTG", "LAMPU", "POWER", "POWR", "PWR", "KUASA", "SOCKET", "SOKET", "SSO",
            "SWITCH", "SUIS", "FAN", "KIPAS", "METER", "CONDUIT", "KONDUIT", "TRUNK", "TRUNKING", "TRAY",
            "KABEL", "CABLE", "WIRING", "LIGHTING",
        };

        private static readonly string[] Inflections = { "", "S", "ES", "ING" };

        /// <summary>A word this long is specific enough to be found inside a run-together token
        /// (ETRUNKING, MAINCONDUIT); shorter ones are not (PERIMETER).</summary>
        private const int CompoundableLength = 7;

        /// <summary>
        /// Whether a layer name looks electrical. A phase 0 measuring aid, not a mapping rule:
        /// it only lets the summary say "on electrical-looking layers". Deciding what a layer
        /// holds is the AI's job, from the census table, which never carries this guess.
        ///
        /// The name is split into tokens at every non-alphanumeric character, between letters
        /// and digits (POWER1 is POWER, 1) and at a lower-to-upper case change (DeskFan), and
        /// matched token by token, never as a substring of the whole name.
        /// </summary>
        public static bool LooksElectrical(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return false;

            List<string> tokens = Tokens(layer);
            if (tokens.Count > 0 && tokens[0] == "E") return true;

            for (int i = 0; i < tokens.Count && i < 3; i++)
            {
                if (ElectricalTokens.Contains(tokens[i], StringComparer.Ordinal)) return true;
            }

            foreach (string token in tokens)
            {
                foreach (string word in ElectricalWords)
                {
                    if (IsWord(token, word)) return true;
                    if (word.Length >= CompoundableLength && token.IndexOf(word, StringComparison.Ordinal) >= 0) return true;
                }
            }

            return false;
        }

        private static bool IsWord(string token, string word)
        {
            if (!token.StartsWith(word, StringComparison.Ordinal)) return false;
            string rest = token.Substring(word.Length);
            return Inflections.Contains(rest, StringComparer.Ordinal);
        }

        /// <summary>Upper-cased tokens of a layer name (see LooksElectrical).</summary>
        internal static List<string> Tokens(string name)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            char previous = '\0';

            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c))
                {
                    Flush();
                    continue;
                }

                if (current.Length > 0 &&
                    (char.IsDigit(c) != char.IsDigit(previous) || (char.IsUpper(c) && char.IsLower(previous))))
                {
                    Flush();
                }

                current.Append(c);
                previous = c;
            }
            Flush();
            return tokens;

            void Flush()
            {
                if (current.Length > 0) tokens.Add(current.ToString().ToUpperInvariant());
                current.Clear();
            }
        }

        /// <summary>Words that name an electrical symbol, in a block name or in the words written
        /// with it. The layer words, and the short ones a drafter names a symbol by but nobody
        /// names a layer by alone (PLUG, DB, TV, TEL).</summary>
        private static readonly string[] SymbolWords = ElectricalWords.Concat(new[]
        {
            "LAMP", "PLUG", "DB", "MSB", "SSB", "ISOLATOR", "TV", "TEL", "DATA", "LED", "LUMINAIRE",
            "DOWNLIGHT", "SPOTLIGHT", "FLOODLIGHT", "OUTLET", "JUNCTION",
        }).ToArray();

        /// <summary>Words that name a piece of the building or its furniture, never a symbol of
        /// the electrical services.</summary>
        private static readonly string[] ArchitecturalWords =
        {
            "DOOR", "DR", "PINTU", "WIN", "WINDOW", "TINGKAP", "JAMB", "COL", "COLUMN", "TIANG", "WALL", "DINDING",
            "STAIR", "TANGGA", "CHAIR", "TABLE", "BED", "SOFA", "FURNITURE", "FURN", "SINK", "BASIN", "WC", "TOILET",
        };

        /// <summary>Whether a whole token of the text is one of the words (inflected forms
        /// count; see IsWord). Long words also match inside a run-together token.</summary>
        private static bool HasWord(string text, string[] words)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string token in Tokens(text))
            {
                foreach (string word in words)
                {
                    if (IsWord(token, word)) return true;
                    if (word.Length >= CompoundableLength && token.IndexOf(word, StringComparison.Ordinal) >= 0) return true;
                }
            }
            return false;
        }

        /// <summary>A block name that names an electrical symbol: an electrical word, or a
        /// layer-style electrical name ("E-LIGHT", "ELEC-1").</summary>
        internal static bool NameLooksElectrical(string name) => HasWord(name, SymbolWords) || LooksElectrical(name);

        /// <summary>Text written with a symbol (an attribute, a legend row, a nearby label)
        /// that describes an electrical one. Words only: a bare "E" or "EL" in a text is an
        /// earth mark or a level, not a discipline.</summary>
        internal static bool TextLooksElectrical(string text) => HasWord(text, SymbolWords);

        internal static bool LooksArchitectural(string name) => HasWord(name, ArchitecturalWords);

        /// <summary>The fewest electrical-looking instances for A1 to answer YES.</summary>
        internal const int MinElectricalInstances = 20;

        /// <summary>The share of electrical-looking instances that must be named blocks for YES.</summary>
        internal const double NamedShareForYes = 0.7;

        /// <summary>
        /// Why a block class looks like an electrical symbol, or null when it does not. The
        /// evidence is tried in this order and the first that holds is returned: "name" (the
        /// block's own name), "layer" (the layer most of it sits on), "attribute" (an attribute
        /// tag or value), "legend" (a legend row drawn with it), "near text" (a word written
        /// beside at least a fifth of its instances).
        ///
        /// An electrical name wins over everything else: a "WALL LIGHT" is a light, wherever it
        /// sits. Without one, a class whose name or main layer is architectural (door, window,
        /// jamb, column, wall...) or whose main layer draws walls is the building, and nothing
        /// written near it makes it a symbol.
        /// </summary>
        internal static string? ElectricalEvidence(CensusBlockClass block, ISet<string> wallLikeLayers,
                                                   IReadOnlyDictionary<string, List<string>> legend)
        {
            string key = block.Anonymous ? string.Empty : block.Key;
            if (NameLooksElectrical(key)) return "name";

            List<CensusCount> layers = block.AllLayers ?? block.Layers;
            string main = layers.Count > 0 ? layers[0].Name : string.Empty;
            if (LooksArchitectural(key) || LooksArchitectural(main) || wallLikeLayers.Contains(main)) return null;

            if (LooksElectrical(main)) return "layer";

            if (block.Attributes.Any(attribute => TextLooksElectrical(attribute.Tag) ||
                                                  attribute.Samples.Any(TextLooksElectrical)))
            {
                return "attribute";
            }

            if (legend.TryGetValue(block.Key, out List<string>? rows) && rows.Any(TextLooksElectrical)) return "legend";

            double needed = Math.Max(2, 0.2 * block.Count);
            if (block.NearTexts.Any(text => text.Count >= needed && TextLooksElectrical(text.Name))) return "near text";

            return null;
        }

        /// <summary>A class drawn at the size of a symbol (50 mm to 2 m), not a container.</summary>
        private static bool SymbolSized(CensusBlockClass block)
        {
            if (block.IsContainer) return false;
            double extent = Math.Max(block.WidthMm, block.HeightMm);
            return extent >= 50 && extent <= 2000;
        }

        private static string F(double value, string format = "0.#") => value.ToString(format, CultureInfo.InvariantCulture);

        private static string Percent(double part, double whole) => whole > 0 ? F(100.0 * part / whole, "0") + "%" : "0%";

        /// <summary>
        /// A1 from the counts: YES when at least MinElectricalInstances instances look
        /// electrical and NamedShareForYes of them are named (not anonymous) blocks; NO when the
        /// symbols look exploded, or under five instances look electrical (what
        /// electrical content there is, is loose linework); PARTLY otherwise - some electrical
        /// blocks, too few, or too many of them anonymous.
        /// </summary>
        internal static string A1Verdict(int electricalSymbols, int namedElectrical, bool exploded)
        {
            if (exploded || electricalSymbols < 5) return "NO";
            if (electricalSymbols >= MinElectricalInstances && namedElectrical >= NamedShareForYes * electricalSymbols) return "YES";
            return "PARTLY";
        }

        public static CensusAnswers Answer(ElectricalCensusResult census)
        {
            var answers = new CensusAnswers();

            // The answers count everything; the caps only shorten the tables (see AllBlocks).
            List<CensusBlockClass> allBlocks = census.AllBlocks ?? census.Blocks;
            List<CensusLayer> allLayers = census.AllLayers ?? census.Layers;
            List<CensusCount> LayersOf(CensusBlockClass block) => block.AllLayers ?? block.Layers;

            // ------------------------------------------------------------ A1
            List<CensusBlockClass> symbolSized = allBlocks.Where(SymbolSized).ToList();
            int symbolInstances = symbolSized.Sum(block => block.Count);
            int electricalInstances = symbolSized.Sum(block => LayersOf(block).Where(l => LooksElectrical(l.Name)).Sum(l => l.Count));
            int electricalClasses = symbolSized.Count(block => LayersOf(block).Count > 0 && LooksElectrical(LayersOf(block)[0].Name));
            double looseElectricalM = allLayers.Where(layer => LooksElectrical(layer.Name)).Sum(layer => layer.LooseLengthM);
            int anonymousGroups = allBlocks.Count(block => block.Anonymous);
            CensusTotals totals = census.Totals;

            // Which symbol-sized classes look electrical, and why: the question is whether the
            // electrical symbols are named blocks, wherever the drafter layered them.
            var wallLikeLayers = new HashSet<string>(allLayers.Where(layer => layer.PairsLikeWalls).Select(layer => layer.Name),
                                                     StringComparer.Ordinal);
            var legendTexts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (CensusLegendRow row in census.AllLegend ?? census.Legend)
            {
                if (string.IsNullOrEmpty(row.Text)) continue;
                if (!legendTexts.TryGetValue(row.Block, out List<string>? texts))
                {
                    texts = new List<string>();
                    legendTexts[row.Block] = texts;
                }
                texts.Add(row.Text!);
            }

            List<(CensusBlockClass Block, string Evidence)> electrical = symbolSized
                .Select(block => (Block: block, Evidence: ElectricalEvidence(block, wallLikeLayers, legendTexts)))
                .Where(pair => pair.Evidence != null)
                .Select(pair => (pair.Block, pair.Evidence!))
                .ToList();
            int electricalSymbols = electrical.Sum(pair => pair.Block.Count);
            int namedElectrical = electrical.Where(pair => !pair.Block.Anonymous).Sum(pair => pair.Block.Count);

            CensusAnswer a1 = answers.A1;
            a1.Verdict = A1Verdict(electricalSymbols, namedElectrical, census.SymbolsAreExploded);

            a1.Lines.Add(totals.Inserts + " inserts, " + Percent(totals.Named, totals.Inserts) + " named, " +
                         Percent(totals.Anonymous, totals.Inserts) + " anonymous in " + anonymousGroups + " shape groups");
            a1.Lines.Add("symbol-sized: " + symbolSized.Count + " classes, " + symbolInstances + " instances");
            a1.Lines.Add("electrical-looking: " + electrical.Count + " classes, " + electricalSymbols + " instances (" +
                         Percent(electricalSymbols, symbolInstances) + " of symbol-sized), " +
                         Percent(namedElectrical, electricalSymbols) + " of them named blocks");
            a1.Lines.Add("evidence: " + (electrical.Count == 0 ? "none" : string.Join(", ", electrical
                .GroupBy(pair => pair.Evidence)
                .OrderByDescending(group => group.Sum(pair => pair.Block.Count)).ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key + " " + group.Count() + " classes / " + group.Sum(pair => pair.Block.Count)))));
            a1.Lines.Add("on electrical-looking layers: " + electricalClasses + " classes, " + electricalInstances +
                         " instances (" + Percent(electricalInstances, symbolInstances) + " of symbol-sized)");
            a1.Lines.Add("loose linework on electrical-looking layers: " + F(looseElectricalM, "0") + " m" +
                         (census.SymbolsAreExploded ? " (symbols look exploded)" : ""));
            a1.Lines.Add("top electrical classes: " + (electrical.Count == 0 ? "none" : string.Join(", ", electrical
                .OrderByDescending(pair => pair.Block.Count).ThenBy(pair => pair.Block.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(pair => pair.Block.Key + " x" + pair.Block.Count +
                                (LayersOf(pair.Block).Count > 0 ? " on " + LayersOf(pair.Block)[0].Name : "") +
                                " (" + pair.Evidence + ")"))));
            a1.Lines.Add("top classes: " + string.Join(", ", allBlocks.Take(10).Select(block =>
                block.Key + " x" + block.Count + (block.Layers.Count > 0 ? " on " + block.Layers[0].Name : ""))));

            // ------------------------------------------------------------ A2
            List<CensusLayer> wallLike = allLayers.Where(layer => layer.PairsLikeWalls).ToList();
            int nearWall = symbolSized.Sum(block => block.NearWall);
            int titled = census.Plans.Count(plan => plan.Titles.Count > 0);

            CensusAnswer a2 = answers.A2;
            if (wallLike.Count > 0 && census.Plans.Count > 0 && symbolInstances > 0 && nearWall >= 0.5 * symbolInstances) a2.Verdict = "YES";
            else if (wallLike.Count == 0) a2.Verdict = "NO";
            else a2.Verdict = "PARTLY";

            a2.Lines.Add("wall-like layers: " + (wallLike.Count == 0 ? "none" : string.Join(", ", wallLike.Take(8).Select(layer =>
                layer.Name + " (" + layer.WallPairs + " pairs, share " + F(layer.PairedLengthShare, "0.00") + ")"))));
            a2.Lines.Add("plans on sheet: " + census.Plans.Count + (census.PlansTruncated ? "+" : "") + ", " + titled + " titled");
            a2.Lines.Add("symbols within 400 mm of a wall: " + nearWall + " of " + symbolInstances +
                         " (" + Percent(nearWall, symbolInstances) + ")");
            a2.Lines.Add("inserts inside a plan: " + totals.InsidePlans + " of " + totals.Inserts +
                         " (" + Percent(totals.InsidePlans, totals.Inserts) + ")");
            a2.Lines.Add("unresolved xrefs: " + (totals.UnresolvedXrefs.Count == 0 ? "none" : string.Join(", ", totals.UnresolvedXrefs)) +
                         "; bound-xref layers: " + totals.BoundXrefLayers);

            // ------------------------------------------------------------ A3
            List<CensusLayer> candidates = allLayers.Where(layer =>
            {
                if (!LooksElectrical(layer.Name) || layer.PairsLikeWalls || layer.LooseLengthM < 20) return false;
                int primitives = layer.Lines + layer.OpenPolylines + layer.ClosedPolylines + layer.Arcs + layer.Texts + layer.Inserts;
                return primitives > 0 && layer.Inserts < 0.1 * primitives;
            }).ToList();

            bool ArcHeavy(CensusLayer layer) => layer.Arcs > layer.Lines + layer.OpenPolylines;

            CensusAnswer a3 = answers.A3;
            if (a1.Verdict == "NO") a3.Verdict = "CANNOT TELL";
            else if (candidates.Count == 0) a3.Verdict = "NO";
            else if (candidates.Any(layer => layer.AxisAlignedShare >= 0.7 && layer.OpenPolylinesOver1m >= 10)) a3.Verdict = "YES";
            else if (candidates.All(layer => layer.AxisAlignedShare < 0.4 || ArcHeavy(layer))) a3.Verdict = "PARTLY (reads as wiring arcs)";
            else a3.Verdict = "PARTLY";

            if (candidates.Count == 0)
            {
                a3.Lines.Add("candidate layers: none (electrical-looking, not wall-like, 20 m+ loose, inserts under 10%)");
            }
            foreach (CensusLayer layer in candidates.Take(8))
            {
                a3.Lines.Add(layer.Name + ": " + F(layer.LooseLengthM, "0") + " m, " + layer.OpenPolylinesOver1m +
                             " open runs > 1 m, " + Percent(layer.AxisAlignedShare, 1) + " axis-aligned, " +
                             layer.Inserts + " inserts" +
                             (layer.AxisAlignedShare < 0.4 || ArcHeavy(layer) ? " (reads as wiring arcs)" : ""));
            }

            return answers;
        }

        public static string Format(ElectricalCensusResult census, string fileName)
        {
            CensusAnswers answers = Answer(census);
            CensusDrawing drawing = census.Drawing;
            var text = new StringBuilder();

            double seconds = census.TimingsSeconds.Values.Sum();
            string units = "1 unit = " + F(drawing.MmPerUnit, "0.###") + " mm (" + drawing.UnitsSource +
                           (drawing.UnitsSource == "inferred" ? "; header says " + drawing.HeaderUnits : "") + ")";

            text.Append("file     ").Append(fileName).Append("     units  ").Append(units)
                .Append("     read+census ").Append(F(seconds, "0.0")).Append(" s\n");
            text.Append("sheet    ").Append(F(drawing.WidthM, "0")).Append(" x ").Append(F(drawing.HeightM, "0"))
                .Append(" m, ").Append(drawing.Entities).Append(" entities\n");

            string aec = drawing.AecRefused ? "REFUSED: " + (drawing.AecWarning ?? "nothing readable")
                       : drawing.AecWarning ?? "none";
            text.Append("AEC      ").Append(aec).Append('\n');

            AppendAnswer(text, "A1", "Electrical symbols are named block inserts", answers.A1);
            AppendAnswer(text, "A2", "The drawing carries the architectural background", answers.A2);
            AppendAnswer(text, "A3", "Conduit is drawn as lines/polylines on its own layer", answers.A3);

            text.Append("legend   ").Append(census.Legend.Count).Append(" rows read (legend titles found: ")
                .Append(census.Totals.LegendTitleTexts).Append(")\n");

            var truncated = new List<string>();
            if (census.BlocksTruncated) truncated.Add("blocks");
            if (census.LayersTruncated) truncated.Add("layers");
            if (census.PlansTruncated) truncated.Add("plans");
            if (census.LegendTruncated) truncated.Add("legend");
            text.Append("truncated  ").Append(truncated.Count == 0 ? "none" : string.Join(", ", truncated)).Append('\n');

            return text.ToString();
        }

        private static void AppendAnswer(StringBuilder text, string id, string question, CensusAnswer answer)
        {
            text.Append(id).Append("  ").Append(question).Append(":  ").Append(answer.Verdict).Append('\n');
            foreach (string line in answer.Lines) text.Append("    ").Append(line).Append('\n');
        }
    }
}

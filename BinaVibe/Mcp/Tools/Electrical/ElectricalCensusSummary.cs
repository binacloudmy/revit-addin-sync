#nullable enable
// Phase 0 summary of an electrical census: the three assumptions the spec (§10, §11) has to
// check on real drawings before the later phases are built, answered YES / PARTLY / NO.
//
//   A1  Electrical symbols are named block inserts on electrical layers.
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
        private static readonly char[] NameSeparators = { '-', '_', ' ', '.', '$', '|' };

        private static readonly string[] ElectricalTokens = { "E", "EL", "ELEC", "ELEK", "ELEKTRIK", "ELECTRICAL", "LV", "ELV" };

        private static readonly string[] ElectricalWords =
        {
            "light", "lite", "ltg", "lampu", "power", "powr", "pwr", "kuasa", "socket", "soket",
            "switch", "suis", "fan", "kipas", "meter", "conduit", "konduit", "trunk", "tray",
            "kabel", "cable", "wiring",
        };

        /// <summary>
        /// Whether a layer name looks electrical. A phase 0 measuring aid, not a mapping rule:
        /// it only lets the summary say "on electrical-looking layers". Deciding what a layer
        /// holds is the AI's job, from the census table, which never carries this guess.
        /// </summary>
        public static bool LooksElectrical(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return false;

            string[] tokens = layer.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length && i < 3; i++)
            {
                foreach (string token in ElectricalTokens)
                {
                    if (string.Equals(tokens[i], token, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }

            foreach (string word in ElectricalWords)
            {
                if (layer.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
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

            CensusAnswer a1 = answers.A1;
            if (electricalInstances >= 0.7 * symbolInstances && electricalInstances >= 20) a1.Verdict = "YES";
            else if (census.SymbolsAreExploded || (electricalInstances < 5 && looseElectricalM > 50)) a1.Verdict = "NO";
            else a1.Verdict = "PARTLY";

            a1.Lines.Add(totals.Inserts + " inserts, " + Percent(totals.Named, totals.Inserts) + " named, " +
                         Percent(totals.Anonymous, totals.Inserts) + " anonymous in " + anonymousGroups + " shape groups");
            a1.Lines.Add("symbol-sized: " + symbolSized.Count + " classes, " + symbolInstances + " instances");
            a1.Lines.Add("on electrical-looking layers: " + electricalClasses + " classes, " + electricalInstances +
                         " instances (" + Percent(electricalInstances, symbolInstances) + " of symbol-sized)");
            a1.Lines.Add("loose linework on electrical-looking layers: " + F(looseElectricalM, "0") + " m" +
                         (census.SymbolsAreExploded ? " (symbols look exploded)" : ""));
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

            AppendAnswer(text, "A1", "Electrical symbols are named block inserts on electrical layers", answers.A1);
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

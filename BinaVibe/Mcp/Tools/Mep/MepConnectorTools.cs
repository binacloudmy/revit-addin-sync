// find_compatible_connector — Layer 0, discipline-agnostic.
//
// Answers "which connector on A can join which connector on B", so the agent
// never has to reason about domain/shape/size compatibility from a connector
// list. It returns RANKED pairs with a reason per pair, because the useful
// answer to "these don't fit" is WHY: wrong domain is a modelling error, a
// size mismatch is a transition fitting, and a fully-occupied element needs a
// disconnect first.
//
// Matching is deliberately tiered rather than boolean. Revit will happily
// ConnectTo two connectors of different sizes and quietly resize one; refusing
// those outright would block legitimate work, so they come back ranked lower
// with size_match:false rather than filtered out.
//
// UNITS: mm out. Distances between connector origins are reported so the
// caller can see whether a join means moving something.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal static class MepConnectorTools
    {
        /// <summary>Sizes within this fraction of each other count as matching.
        /// Not a code rule — a tolerance so a 99.9mm and a 100mm connector do
        /// not read as a mismatch.</summary>
        private const double SizeTolerance = 0.02;

        public static Dictionary<string, object?> FindCompatibleConnector(Document doc, JsonElement args)
        {
            var idA = ArgsHelp.GetLong(args, "element_id_a")
                ?? throw new ArgumentException("missing element_id_a");
            var idB = ArgsHelp.GetLong(args, "element_id_b")
                ?? throw new ArgumentException("missing element_id_b");

            var a = doc.GetElement(ElemIds.From(idA))
                ?? throw new ArgumentException($"element {idA} not found");
            var b = doc.GetElement(ElemIds.From(idB))
                ?? throw new ArgumentException($"element {idB} not found");

            var freeOnly = ArgsHelp.GetBool(args, "free_only") ?? true;
            var requireSize = ArgsHelp.GetBool(args, "require_size_match") ?? false;
            var domainWord = ArgsHelp.GetString(args, "domain");
            var domainFilter = MepDomains.Parse(domainWord);
            if (domainWord != null && domainFilter == MepDomainKind.Unknown)
                return MepTx.Failure(
                    $"unknown domain '{domainWord}' — use one of: {string.Join(", ", MepDomains.AcceptedWords)}");

            var consA = MepConnectors.TryConnectorsOf(a);
            var consB = MepConnectors.TryConnectorsOf(b);

            if (consA.Count == 0 || consB.Count == 0)
                return MepTx.Blocked("no_connectors",
                    $"element {(consA.Count == 0 ? idA : idB)} has no MEP connectors — "
                    + "it cannot be joined to anything (use get_element_mep_info to confirm)");

            var pairs = new List<Dictionary<string, object?>>();
            for (int i = 0; i < consA.Count; i++)
            {
                for (int j = 0; j < consB.Count; j++)
                {
                    var ca = consA[i];
                    var cb = consB[j];

                    if (freeOnly && (ca.IsConnected || cb.IsConnected)) continue;
                    if (domainFilter != MepDomainKind.Unknown
                        && MepDomains.FromConnectorDomain(ca.Domain) != domainFilter) continue;

                    var domainMatch = ca.Domain == cb.Domain;
                    var shapeMatch = SameShape(ca, cb);
                    var sizeMatch = SameSize(ca, cb);
                    if (requireSize && !sizeMatch) continue;
                    if (!domainMatch) continue;   // a duct connector will never join a pipe one

                    pairs.Add(new Dictionary<string, object?>
                    {
                        ["connector_index_a"] = i,
                        ["connector_index_b"] = j,
                        ["domain"] = MepDomains.ConnectorDomainLabel(ca.Domain),
                        ["shape_match"] = shapeMatch,
                        ["size_match"] = sizeMatch,
                        ["a_free"] = !ca.IsConnected,
                        ["b_free"] = !cb.IsConnected,
                        ["distance_mm"] = DistanceMm(ca, cb),
                        ["origin_a_mm"] = MepConnectors.OriginMm(ca),
                        ["origin_b_mm"] = MepConnectors.OriginMm(cb),
                        ["rank"] = Rank(shapeMatch, sizeMatch, ca, cb),
                        ["note"] = Note(shapeMatch, sizeMatch),
                    });
                }
            }

            if (pairs.Count == 0)
                return MepTx.Blocked("no_compatible_pair",
                    $"no connector on element {idA} can join element {idB}"
                    + (freeOnly ? " with free_only:true — pass free_only:false to see occupied ones" : "")
                    + " (domains must match; use get_element_mep_info on both to see why)");

            // Best first: shape+size, then nearest. Distance breaks ties because
            // the closest compatible pair is almost always the intended join.
            var ordered = pairs
                .OrderBy(p => (int)p["rank"]!)
                .ThenBy(p => (double?)p["distance_mm"] ?? double.MaxValue)
                .ToList();

            var best = ordered[0];
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["element_id_a"] = idA,
                ["element_id_b"] = idB,
                ["count"] = ordered.Count,
                ["best"] = best,
                ["pairs"] = ordered,
            };
        }

        // ─── helpers ────────────────────────────────────────────────────

        private static bool SameShape(Connector a, Connector b)
        {
            try { return a.Shape == b.Shape; }
            catch { return false; }
        }

        private static bool SameSize(Connector a, Connector b)
        {
            try
            {
                if (a.Shape != b.Shape) return false;
                switch (a.Shape)
                {
                    case ConnectorProfileType.Round:
                        return Close(a.Radius, b.Radius);
                    case ConnectorProfileType.Rectangular:
                    case ConnectorProfileType.Oval:
                        return Close(a.Width, b.Width) && Close(a.Height, b.Height);
                    default:
                        // An electrical connector has no profile — there is no
                        // size to disagree about, so it is not a mismatch.
                        return true;
                }
            }
            catch { return true; }
        }

        private static bool Close(double x, double y)
        {
            if (x <= 0 && y <= 0) return true;
            var bigger = Math.Max(Math.Abs(x), Math.Abs(y));
            if (bigger <= 0) return true;
            return Math.Abs(x - y) / bigger <= SizeTolerance;
        }

        private static double? DistanceMm(Connector a, Connector b)
        {
            try { return a.Origin.DistanceTo(b.Origin) * MepConnectors.MmPerFoot; }
            catch { return null; }
        }

        /// <summary>0 is best. Occupied connectors rank last so a caller that
        /// asked for free_only:false still gets the free pairs first.</summary>
        private static int Rank(bool shapeMatch, bool sizeMatch, Connector a, Connector b)
        {
            var occupied = a.IsConnected || b.IsConnected;
            var baseRank = (shapeMatch, sizeMatch) switch
            {
                (true, true) => 0,
                (true, false) => 1,
                _ => 2,
            };
            return occupied ? baseRank + 3 : baseRank;
        }

        private static string? Note(bool shapeMatch, bool sizeMatch)
        {
            if (shapeMatch && sizeMatch) return null;
            if (!shapeMatch) return "different connector profiles — Revit needs a transition fitting";
            return "sizes differ — connecting will resize one end; add a transition if that is not wanted";
        }
    }
}

// create_circuit_routes: the conduit run and its joint fittings.
//
// A station's ARITY decides the fitting — two ends elbow, three tee — and
// the branch drop must be the tee's third argument. Everything here runs
// inside CommitOne's transaction.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static partial class RouteCommit
    {
        /// <summary>One Conduit per leg, then a fitting at every station where
        /// two or more ends meet. Runs INSIDE CommitOne's transaction and opens
        /// none of its own.
        ///
        /// A joint that cannot be fitted costs that joint, never the run: each
        /// is tried separately and the failures are reported per station.</summary>
        private static ConduitOutcome BuildConduits(
            Document doc, PlannedRoute r, ConduitType conduitType,
            ElementId levelId, bool connectConduits)
        {
            var outcome = new ConduitOutcome();
            var conduits = new List<Conduit>();

            foreach (var leg in r.Legs)
            {
                var a = new XYZ(leg.FromXMm / MmPerFoot, leg.FromYMm / MmPerFoot, leg.FromZMm / MmPerFoot);
                var b = new XYZ(leg.ToXMm / MmPerFoot, leg.ToYMm / MmPerFoot, leg.ToZMm / MmPerFoot);
                // Type first, level LAST — Conduit.Create's arg order
                // differs from Duct/Pipe (MutatorsMep precedent).
                var conduit = Conduit.Create(doc, conduitType.Id, a, b, levelId);
                if (r.ConduitDiameterMm.HasValue)
                    conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)
                        ?.Set(r.ConduitDiameterMm.Value / MmPerFoot);
                conduits.Add(conduit);
                outcome.ConduitIds.Add(conduit.Id.Value);
            }

            if (!connectConduits) return outcome;

            // Joints are found by STATION, not leg adjacency: a device station
            // has THREE conduits meeting (run in, run out, branch drop), so
            // pairing by index would elbow two and leave the third hanging.
            // Arity is what decides elbow vs tee.
            foreach (var station in JointStations(r.Legs, conduits))
            {
                try
                {
                    outcome.FittingIds.Add(Join(doc, station));
                }
                catch (Exception ex)
                {
                    outcome.UnconnectedJoints.Add(new Dictionary<string, object?>
                    {
                        ["at_mm"] = station.AtMm.Select(v => (object)Math.Round(v)).ToList(),
                        ["conduits_meeting"] = station.Connectors.Count,
                        ["reason"] = ex.Message,
                    });
                }
            }
            // 0 means "joined without a fitting", which is a success.
            outcome.FittingIds.RemoveAll(id => id == 0);
            return outcome;
        }

        /// <summary>One point where two or more conduit ends meet.</summary>
        private sealed class JointStation
        {
            public double[] AtMm = new double[3];
            public List<Connector> Connectors = new();
            /// <summary>Connector belonging to the branch drop, when this is a
            /// trunk station a device hangs off. NewTeeFitting wants the branch
            /// as its THIRD argument, so it cannot be found by position later.</summary>
            public Connector? Branch;
        }

        /// <summary>Group conduit endpoints by shared position. Endpoints that
        /// are ends of the whole run (the panel connector, each device) are
        /// left out — one conduit at a station is nothing to join.</summary>
        private static List<JointStation> JointStations(
            IReadOnlyList<RouteLeg> legs, IReadOnlyList<Conduit> conduits)
        {
            var stations = new List<JointStation>();

            void Add(double[] at, Conduit conduit, bool isBranch)
            {
                var st = stations.FirstOrDefault(s =>
                    Math.Abs(s.AtMm[0] - at[0]) <= JointTolMm &&
                    Math.Abs(s.AtMm[1] - at[1]) <= JointTolMm &&
                    Math.Abs(s.AtMm[2] - at[2]) <= JointTolMm);
                if (st == null)
                {
                    st = new JointStation { AtMm = at };
                    stations.Add(st);
                }
                var conn = ConnectorNear(conduit, at);
                if (conn == null) return;
                st.Connectors.Add(conn);
                if (isBranch) st.Branch = conn;
            }

            for (int i = 0; i < legs.Count && i < conduits.Count; i++)
            {
                var leg = legs[i];
                bool branch = leg.DropsToDeviceId != 0;
                // Only a branch drop's TOP end is a joint; its bottom lands on
                // the device.
                Add(new[] { leg.FromXMm, leg.FromYMm, leg.FromZMm }, conduits[i], branch);
                if (!branch)
                    Add(new[] { leg.ToXMm, leg.ToYMm, leg.ToZMm }, conduits[i], false);
            }

            return stations.Where(s => s.Connectors.Count >= 2).ToList();
        }

        /// <summary>Fit one station. Returns the fitting's id, or 0 when the
        /// ends were connected directly (collinear, or an elbow the conduit
        /// type cannot serve) — connected without a fitting is a real outcome,
        /// and reporting it as a failed joint is what made a working run look
        /// broken.</summary>
        private static long Join(Document doc, JointStation station)
        {
            var conns = station.Connectors;

            if (conns.Count >= 3)
            {
                // Trunk station with a branch drop. The branch MUST be the
                // third argument; Revit reads it as the tee's leg.
                var branch = station.Branch
                    ?? throw new InvalidOperationException(
                        conns.Count + " conduits meet here but none is a branch drop");
                var run = conns.Where(c => !ReferenceEquals(c, branch)).Take(2).ToList();
                if (run.Count < 2)
                    throw new InvalidOperationException("tee needs two run ends plus the branch");
                if (conns.Count > 3)
                    throw new InvalidOperationException(
                        conns.Count + " conduits meet at one point — Revit fits at most a tee; " +
                        "review this station by hand");

                // A tee is straight-through plus a branch. Two runs that TURN
                // here need a fitting that both turns and branches, and Revit
                // has none — no conduit type will supply one, so saying
                // "routing preferences lack a tee" would be a wrong diagnosis.
                // RouteAssembly keeps the trunk on one axis through a device
                // station precisely to prevent this; reaching it means an
                // obstruction probe overrode that choice.
                if (!IsCollinear(run[0], run[1]))
                {
                    // No fitting both turns and branches, but the two run ends
                    // DO turn — that is an ordinary elbow. Fitting them keeps
                    // the trunk continuous and leaves only the branch open,
                    // instead of abandoning all three ends. Previously this
                    // threw before touching anything, so one corner severed the
                    // whole run (UAT 2026-08-05).
                    string salvage;
                    try
                    {
                        var corner = doc.Create.NewElbowFitting(run[0], run[1]);
                        salvage = corner != null
                            ? "the two run ends were elbowed together, so the trunk is continuous"
                            : "the two run ends were joined, so the trunk is continuous";
                    }
                    catch
                    {
                        try
                        {
                            run[0].ConnectTo(run[1]);
                            salvage = "the two run ends were joined directly, so the trunk is continuous";
                        }
                        catch { salvage = "the two run ends could not be joined either"; }
                    }

                    throw new InvalidOperationException(
                        "the trunk TURNS at this branch station, so no single fitting can serve " +
                        "it (a tee runs straight through) — " + salvage + ", and only the BRANCH " +
                        "drop is left open. The corner was forced here by the obstruction probe; " +
                        "re-run suggest_circuit_routes with probe_obstacles off, or have the " +
                        "drafter place a junction box at this point");
                }

                try
                {
                    var tee = doc.Create.NewTeeFitting(run[0], run[1], branch);
                    return tee?.Id.Value ?? 0L;
                }
                catch (Exception ex)
                {
                    // The elbow path below has always fallen back to a direct
                    // ConnectTo when the conduit type carries no fitting; the
                    // tee path had no such fallback, so ONE missing tee left the
                    // trunk itself severed (UAT 2026-08-05, Revit's bare
                    // "failed to insert tee." at a station whose two run ends
                    // were collinear — a genuinely absent tee in the type's
                    // routing preferences, not the turn-and-branch case above).
                    //
                    // Salvage what a fitting-less run can still be: join the two
                    // run ends so the trunk stays continuous. The BRANCH cannot
                    // be joined — a connector takes one partner — so this is
                    // still reported as an open joint, now saying exactly what
                    // is open and what is not.
                    try { run[0].ConnectTo(run[1]); }
                    catch
                    {
                        throw new InvalidOperationException(
                            "no tee and the run ends would not connect either: " + ex.Message);
                    }
                    throw new InvalidOperationException(
                        "no tee fitting for this conduit type, so the trunk was joined " +
                        "through and the BRANCH drop is left open at this point. Add a tee " +
                        "to the conduit type's routing preferences (or pass conduit_type_name " +
                        "for a type that has one), then re-run. Revit said: " + ex.Message);
                }
            }

            var a = conns[0];
            var b = conns[1];

            // NewElbowFitting refuses anything outside roughly 2-95 degrees, so
            // a straight continuation has to be connected, not elbowed. That
            // rejection is what produced 8 "failed fittings" on a 9-device
            // circuit in UAT 2026-08-04 — one per device junction, when the
            // route still dropped onto a device and rose straight back off it.
            if (IsCollinear(a, b))
            {
                a.ConnectTo(b);
                return 0L;
            }

            try
            {
                var elbow = doc.Create.NewElbowFitting(a, b);
                return elbow?.Id.Value ?? 0L;
            }
            catch (Exception ex)
            {
                // A conduit type whose routing preferences carry no elbow of
                // this size still leaves a physically continuous run if the
                // ends are simply joined. Better a connected run with a noted
                // missing fitting than an open one.
                try
                {
                    a.ConnectTo(b);
                    return 0L;
                }
                catch
                {
                    throw new InvalidOperationException(
                        "no elbow and no direct connection: " + ex.Message);
                }
            }
        }

        /// <summary>Two conduit ends pointing along the same line. Connector
        /// basis Z is the direction the connector faces, so two ends that meet
        /// head-on face opposite ways — hence the absolute value.</summary>
        private static bool IsCollinear(Connector a, Connector b)
        {
            try
            {
                var da = a.CoordinateSystem.BasisZ.Normalize();
                var db = b.CoordinateSystem.BasisZ.Normalize();
                return Math.Abs(da.DotProduct(db)) > 0.999;   // within ~2.5 degrees
            }
            catch { return false; }
        }

        private static Connector? ConnectorNear(Conduit conduit, double[] jointMm)
        {
            Connector? best = null;
            double bestDist = double.MaxValue;
            foreach (Connector c in conduit.ConnectorManager.Connectors)
            {
                double dx = c.Origin.X * MmPerFoot - jointMm[0];
                double dy = c.Origin.Y * MmPerFoot - jointMm[1];
                double dz = c.Origin.Z * MmPerFoot - jointMm[2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return bestDist <= JointTolMm ? best : null;
        }
    }
}

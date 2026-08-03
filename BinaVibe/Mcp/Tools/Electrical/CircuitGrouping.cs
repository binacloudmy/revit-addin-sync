// Circuit grouping math — pure, Revit-free, MILLIMETRES ONLY.
//
// Split out of CircuitCandidates.cs (which needs a live Revit Document)
// precisely so the grouping, capacity and chain-order rules are testable in
// Tests/Tests.csproj (explicit <Compile Include>, no globs). Same reason
// SocketLayout.cs was split out of SocketCandidates.cs.
//
// UNITS: every number in this file is mm (coordinates) or VA (loads). The
// ft<->mm boundary is CircuitCandidates.cs; nothing here ever sees a foot.
//
// No regulatory value is baked in: max devices, max VA and the grouping span
// all arrive from the caller (ultimately the backend recipe), constructor-
// required so a missing value cannot silently default.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One placed device eligible for circuiting, already resolved to
    /// project-internal mm coordinates with a load value.</summary>
    public sealed class ElecDevice
    {
        public long Id;
        public double XMm;
        public double YMm;
        public double ZMm;
        /// <summary>Apparent load in VA.</summary>
        public double Va;
        /// <summary>"parameter" when read from the element, "default_arg" when
        /// the per-class fallback arg supplied it. Reporting only.</summary>
        public string LoadSource = "default_arg";
        /// <summary>"lighting" | "receptacle" — circuits never mix classes.</summary>
        public string LoadClass = "receptacle";
        public string LevelName = "";
    }

    /// <summary>Caller-supplied circuit limits. Constructor-required on purpose
    /// — the numbers live in the backend recipe, never here.</summary>
    public sealed class GroupingOptions
    {
        public int MaxDevices { get; }
        public double MaxVa { get; }
        /// <summary>Cap on how far (mm) a device may sit from the nearest
        /// device already in the group. null = no proximity cap.</summary>
        public double? MaxGroupSpanMm { get; }

        public GroupingOptions(int maxDevices, double maxVa, double? maxGroupSpanMm)
        {
            if (maxDevices < 1) throw new ArgumentException("max_devices_per_circuit must be >= 1");
            if (!(maxVa > 0)) throw new ArgumentException("max_va_per_circuit must be > 0");
            if (maxGroupSpanMm.HasValue && !(maxGroupSpanMm.Value > 0))
                throw new ArgumentException("max_group_span_mm must be > 0 when supplied");
            MaxDevices = maxDevices;
            MaxVa = maxVa;
            MaxGroupSpanMm = maxGroupSpanMm;
        }
    }

    /// <summary>One proposed circuit: devices in daisy-chain order, single
    /// load class, within the caller's device/VA caps.</summary>
    public sealed class CircuitGroup
    {
        public int Index;
        public string LoadClass = "";
        /// <summary>Chain order: element 0 is where the home run lands (the
        /// device nearest the panel), then nearest-neighbor onward.</summary>
        public List<ElecDevice> DevicesInChainOrder = new();
        public double TotalVa;
        public List<string> Notes = new();

        public IEnumerable<long> DeviceIds => DevicesInChainOrder.Select(d => d.Id);
    }

    public static class CircuitGrouping
    {
        /// <summary>Group devices into circuits. Deterministic: every choice
        /// breaks ties by ascending element id, so the same model always
        /// yields the same plan.
        ///
        /// Per load class: seed a group with the unassigned device nearest the
        /// panel, then grow by the device nearest to any current member
        /// (single-linkage), stopping at MaxDevices, MaxVa, or the span cap.
        /// Chain order within the group is a nearest-neighbor walk starting
        /// from the device nearest the panel.</summary>
        public static List<CircuitGroup> Group(
            IReadOnlyList<ElecDevice> devices,
            double panelXMm, double panelYMm,
            GroupingOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var groups = new List<CircuitGroup>();
            if (devices == null || devices.Count == 0) return groups;

            int index = 0;
            foreach (var byClass in devices
                         .GroupBy(d => d.LoadClass ?? "")
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var pool = byClass
                    .OrderBy(d => DistMm(d.XMm, d.YMm, panelXMm, panelYMm))
                    .ThenBy(d => d.Id)
                    .ToList();

                while (pool.Count > 0)
                {
                    var group = new CircuitGroup { Index = index, LoadClass = byClass.Key };
                    var members = new List<ElecDevice>();

                    var seed = pool[0];
                    pool.RemoveAt(0);
                    members.Add(seed);
                    double totalVa = seed.Va;
                    if (seed.Va > options.MaxVa)
                        group.Notes.Add("device " + seed.Id + " alone exceeds max_va_per_circuit");

                    while (members.Count < options.MaxDevices && pool.Count > 0)
                    {
                        ElecDevice? best = null;
                        double bestDist = double.MaxValue;
                        foreach (var cand in pool)
                        {
                            double d = MinDistToMembers(cand, members);
                            if (d < bestDist - 1e-9 ||
                                (Math.Abs(d - bestDist) <= 1e-9 && (best == null || cand.Id < best.Id)))
                            {
                                bestDist = d;
                                best = cand;
                            }
                        }
                        if (best == null) break;
                        if (options.MaxGroupSpanMm.HasValue && bestDist > options.MaxGroupSpanMm.Value)
                            break;                       // nothing near enough — close the group
                        if (totalVa + best.Va > options.MaxVa) break;

                        pool.Remove(best);
                        members.Add(best);
                        totalVa += best.Va;
                    }

                    group.DevicesInChainOrder = ChainOrder(members, panelXMm, panelYMm);
                    group.TotalVa = totalVa;
                    group.Index = index++;
                    groups.Add(group);
                }
            }
            return groups;
        }

        /// <summary>Nearest-neighbor walk from the device nearest the panel —
        /// the order routing later wires the daisy-chain in.</summary>
        public static List<ElecDevice> ChainOrder(
            IReadOnlyList<ElecDevice> members, double panelXMm, double panelYMm)
        {
            var remaining = members.ToList();
            var chain = new List<ElecDevice>(remaining.Count);
            double cx = panelXMm, cy = panelYMm;
            while (remaining.Count > 0)
            {
                ElecDevice next = remaining[0];
                double bestDist = double.MaxValue;
                foreach (var cand in remaining)
                {
                    double d = DistMm(cand.XMm, cand.YMm, cx, cy);
                    if (d < bestDist - 1e-9 ||
                        (Math.Abs(d - bestDist) <= 1e-9 && cand.Id < next.Id))
                    {
                        bestDist = d;
                        next = cand;
                    }
                }
                remaining.Remove(next);
                chain.Add(next);
                cx = next.XMm; cy = next.YMm;
            }
            return chain;
        }

        private static double MinDistToMembers(ElecDevice cand, IReadOnlyList<ElecDevice> members)
        {
            double best = double.MaxValue;
            foreach (var m in members)
            {
                double d = DistMm(cand.XMm, cand.YMm, m.XMm, m.YMm);
                if (d < best) best = d;
            }
            return best;
        }

        private static double DistMm(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2, dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}

#nullable enable
// The elements connected to a write's targets, for the force path (ShapeComparer.JudgeConnected). Revit-bound; read on
// Revit's thread inside the commit callback: once BEFORE the calls run (a fitting Revit removes cannot be read afterwards)
// and once after (a fitting Revit added is connected then).
//
// Connected means: an MEP connector neighbour of a target (physical connections only: a logical connection or a system is
// not a neighbour), one more connector hop from those (the fitting beyond a transition), elements joined to a target
// (JoinGeometryUtils), a target's dependents (Element.GetDependentElements: insulation, lining, hosted inserts, tags) and (r22) the
// MEP systems the targets and those connected elements belong to (MEPCurve.MEPSystem, Connector.MEPSystem), never any other system.
// Every read is guarded: an element that cannot be read adds nothing. At most ConnectedDrift.MaxConnected ids; no set at
// all above ConnectedDrift.MaxTargets targets (no force offered then).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Copilot.Writes
{
    public static class ConnectedSet
    {
        /// <summary>Longest a set may take to read; past it the set is incomplete.</summary>
        public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

        public static Dictionary<long, string> Of(Document doc, IReadOnlyCollection<long> targets) => Of(doc, targets, out _);

        /// <summary>id -> category name of what is connected to <paramref name="targets"/> now (the targets themselves excluded).
        /// <paramref name="complete"/> is false when nothing was read (no target, more than MaxTargets) or the read stopped early (the cap,
        /// the time budget): then no drift may be called connected-only.</summary>
        public static Dictionary<long, string> Of(Document doc, IReadOnlyCollection<long> targets, out bool complete)
        {
            var found = new Dictionary<long, string>();
            complete = false;
            if (doc == null || targets == null || targets.Count == 0 || targets.Count > ConnectedDrift.MaxTargets) return found;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var targetSet = new HashSet<long>(targets);
            var hop1 = new List<long>();
            foreach (var id in targetSet)
            {
                var e = Get(doc, id);
                if (e == null) continue;
                foreach (var n in ConnectorNeighbours(e)) if (Add(doc, found, targetSet, n)) hop1.Add(n);
                foreach (var n in Joined(doc, e)) Add(doc, found, targetSet, n);
                foreach (var n in Dependents(e)) Add(doc, found, targetSet, n);
                if (found.Count >= ConnectedDrift.MaxConnected || clock.Elapsed > Budget) return found;
            }
            foreach (var id in hop1)
            {
                var e = Get(doc, id);
                if (e == null) continue;
                foreach (var n in ConnectorNeighbours(e)) Add(doc, found, targetSet, n);
                if (found.Count >= ConnectedDrift.MaxConnected || clock.Elapsed > Budget) return found;
            }
            // r22 (live K3 4e3aa38e): the MEP system a target or a connected element belongs to changes with every resize (duct 1382112's Duct
            // System 1365689), so it is part of that change. Only the systems of those elements: MEPCurve.MEPSystem for a duct or pipe, the
            // connectors' MEPSystem for a fitting or equipment. No other system is ever counted.
            foreach (var id in targetSet.Concat(found.Keys.ToList()).ToList())
            {
                var e = Get(doc, id);
                if (e == null) continue;
                foreach (var s in SystemsOf(e)) Add(doc, found, targetSet, s);
                if (found.Count >= ConnectedDrift.MaxConnected || clock.Elapsed > Budget) return found;
            }
            complete = true;
            return found;
        }

        private static bool Add(Document doc, Dictionary<long, string> found, HashSet<long> targets, long id)
        {
            if (targets.Contains(id) || found.ContainsKey(id) || found.Count >= ConnectedDrift.MaxConnected) return false;
            var e = Get(doc, id);
            if (e == null) return false;
            found[id] = CategoryName(e);
            return true;
        }

        private static Element? Get(Document doc, long id)
        {
            try { return doc.GetElement(ElemIds.From(id)); }
            catch (Exception) { return null; }
        }

        private static string CategoryName(Element e)
        {
            try
            {
                return e.Category?.Name is string name && name.Length > 0 ? name
                    : e is ElementType ? "Types"
                    : ChangeClassifier.ClassKey(e.GetType().Name);
            }
            catch (Exception) { return "Other"; }
        }

        private static IEnumerable<long> ConnectorNeighbours(Element e)
        {
            var ids = new List<long>();
            try
            {
                ConnectorManager? cm = e is MEPCurve curve ? curve.ConnectorManager
                    : e is FamilyInstance fi ? fi.MEPModel?.ConnectorManager
                    : null;
                if (cm == null) return ids;
                foreach (Connector c in cm.Connectors)
                {
                    if (c == null || c.ConnectorType == ConnectorType.Logical || !c.IsConnected) continue;
                    foreach (Connector r in c.AllRefs)
                    {
                        if (r == null || r.ConnectorType == ConnectorType.Logical) continue;
                        var owner = r.Owner;
                        if (owner == null || owner is MEPSystem || owner.Id == e.Id) continue;
                        ids.Add((long)owner.Id.Value);
                    }
                }
            }
            catch (Exception)
            {
                // An element whose connectors cannot be read adds no neighbours.
            }
            return ids;
        }

        /// <summary>The MEP systems <paramref name="e"/> itself belongs to (MEPCurve.MEPSystem; Connector.MEPSystem of a family instance's
        /// connectors); none for anything else or when unreadable.</summary>
        private static IEnumerable<long> SystemsOf(Element e)
        {
            var ids = new List<long>();
            try
            {
                if (e is MEPCurve curve)
                {
                    if (curve.MEPSystem != null) ids.Add((long)curve.MEPSystem.Id.Value);
                }
                else if (e is FamilyInstance fi && fi.MEPModel?.ConnectorManager is ConnectorManager cm)
                {
                    foreach (Connector c in cm.Connectors)
                        if (c?.MEPSystem != null) ids.Add((long)c.MEPSystem.Id.Value);
                }
            }
            catch (Exception)
            {
                // A system that cannot be read adds nothing (the forced commit is then refused, the safe side).
            }
            return ids.Distinct();
        }

        private static IEnumerable<long> Joined(Document doc, Element e)
        {
            try { return JoinGeometryUtils.GetJoinedElements(doc, e).Select(id => (long)id.Value).ToList(); }
            catch (Exception) { return Enumerable.Empty<long>(); }
        }

        /// <summary>Dependents of an instance only (a duct, pipe, fitting, wall, floor): a type's or a level's dependents are the whole model.</summary>
        private static IEnumerable<long> Dependents(Element e)
        {
            if (!(e is FamilyInstance || e is MEPCurve || e is HostObject)) return Enumerable.Empty<long>();
            try { return e.GetDependentElements(null).Take(ConnectedDrift.MaxConnected).Select(id => (long)id.Value).ToList(); }
            catch (Exception) { return Enumerable.Empty<long>(); }
        }
    }
}

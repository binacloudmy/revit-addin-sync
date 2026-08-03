// Link geometry context — the one place that resolves RevitLinkInstances and
// their transforms for obstruction queries (QueryGeometry clashes, and
// check_corridor). Read-only, no Transaction.
//
// On a typical MEP model the architectural and structural obstructions live in
// LINKS, not the host document. A host-only obstruction query is blind to most
// of the model on exactly the projects this addin targets, so the link arm is
// not optional — but an UNLOADED link cannot be searched at all, and that gap
// must be reported (links_unloaded), never silently read as "clear".
//
// Transform precedent: check_grid_alignment (Coordination.cs) — GetTotalTransform
// maps link space into host space; its Inverse maps back. A rotated link's
// bbox must be re-AABBed from all 8 corners after either mapping
// (GeomMm.AabbOfCorners); transforming Min/Max alone is wrong and banned.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal sealed class LinkCtx
    {
        public long LinkId;
        public string Name = "";
        public Document Doc = null!;
        public Transform ToHost = null!;
        public Transform ToLink = null!;
        /// <summary>Host-space bbox of the whole link instance — cheap cull.</summary>
        public BoundingBoxXYZ? HostBox;
    }

    internal static class LinkGeom
    {
        public const double MmPerFoot = 304.8;

        /// <summary>Resolve every loaded link once per tool call. Unloaded link
        /// names come back separately so the caller can report the blind spot.</summary>
        public static List<LinkCtx> Build(Document doc, out List<string> unloaded)
        {
            var ctxs = new List<LinkCtx>();
            unloaded = new List<string>();
            foreach (var li in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document? ldoc = null;
                try { ldoc = li.GetLinkDocument(); } catch { }
                if (ldoc == null) { unloaded.Add(li.Name); continue; }

                Transform toHost;
                try { toHost = li.GetTotalTransform(); } catch { continue; }
                ctxs.Add(new LinkCtx
                {
                    LinkId = li.Id.Value,
                    Name = li.Name,
                    Doc = ldoc,
                    ToHost = toHost,
                    ToLink = toHost.Inverse,
                    HostBox = li.get_BoundingBox(null),
                });
            }
            return ctxs;
        }

        /// <summary>Revit bbox (feet) to a mm AABB, optionally re-mapped through
        /// a transform — via all 8 corners, never Min/Max alone.</summary>
        public static BoxMm ToMmBox(BoundingBoxXYZ bb, Transform? t = null)
        {
            var corners = new List<Pt3Mm>(8);
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
                foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                    {
                        var p = new XYZ(x, y, z);
                        if (t != null) p = t.OfPoint(p);
                        corners.Add(new Pt3Mm(p.X * MmPerFoot, p.Y * MmPerFoot, p.Z * MmPerFoot));
                    }
            return GeomMm.AabbOfCorners(corners);
        }

        /// <summary>Host-space mm AABB to a link-space Revit Outline (feet) for
        /// a BoundingBoxIntersectsFilter inside the link document.</summary>
        public static Outline ToLinkOutline(BoxMm hostMm, Transform toLink)
        {
            var corners = new List<Pt3Mm>(8);
            foreach (var c in GeomMm.Corners(hostMm))
            {
                var p = toLink.OfPoint(new XYZ(c.X / MmPerFoot, c.Y / MmPerFoot, c.Z / MmPerFoot));
                corners.Add(new Pt3Mm(p.X, p.Y, p.Z));   // feet, link space
            }
            var aabb = GeomMm.AabbOfCorners(corners);
            return new Outline(new XYZ(aabb.Min.X, aabb.Min.Y, aabb.Min.Z),
                               new XYZ(aabb.Max.X, aabb.Max.Y, aabb.Max.Z));
        }
    }
}

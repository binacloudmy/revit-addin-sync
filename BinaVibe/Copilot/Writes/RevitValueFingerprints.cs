#nullable enable
// Captures R75 value fingerprints from live elements (Revit half of
// ValueFingerprint). Called on Revit's thread, inside the open group: after
// the preview's inner commits and before its rollback, and after the commit's
// inner commits and before the shape decision. Uncategorised (class:*) ids
// take no part (R43), like the rest of shape matching.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Copilot.Writes
{
    internal static class RevitValueFingerprints
    {
        public static void Fill(Document doc, BatchShape shape, IReadOnlyCollection<long> addedIds)
        {
            var eligible = shape.Modified.Where(id => !ChangeClassifier.IsClassKey(shape.CategoryOfId(id))).ToList();
            shape.ValuesTotal = eligible.Count;
            shape.Values.Clear();
            var added = new HashSet<long>(addedIds ?? Array.Empty<long>());
            foreach (var id in ValueFingerprint.Select(eligible))
            {
                try
                {
                    var e = doc.GetElement(ElemIds.From(id));
                    if (e != null) shape.Values[id] = Of(doc, e, added);
                }
                catch (Exception)
                {
                    // An element we cannot read is compared by id only.
                }
            }
        }

        private static string Of(Document doc, Element e, HashSet<long> added)
        {
            var parts = new List<KeyValuePair<string, string>>();
            foreach (Parameter p in e.Parameters)
            {
                if (p == null) continue;
                string value;
                try
                {
                    value = p.StorageType switch
                    {
                        StorageType.String => "s:" + (p.AsString() ?? ""),
                        StorageType.Integer => "i:" + p.AsInteger().ToString(CultureInfo.InvariantCulture),
                        StorageType.Double => "d:" + ValueFingerprint.Number(p.AsDouble()),
                        StorageType.ElementId => "e:" + Ref(doc, p.AsElementId(), added),
                        _ => "n:",
                    };
                }
                catch (Exception)
                {
                    value = "?";
                }
                parts.Add(new KeyValuePair<string, string>((p.Definition?.Name ?? "") + "#" + ((long)p.Id.Value).ToString(CultureInfo.InvariantCulture), value));
            }
            return ValueFingerprint.Compute(parts, Ref(doc, e.GetTypeId(), added), Location(e));
        }

        /// <summary>An element reference; one to an element this batch added is "new:&lt;kind&gt;",
        /// because its id differs between preview and commit.</summary>
        private static string Ref(Document doc, ElementId? id, HashSet<long> added)
        {
            if (id == null || id == ElementId.InvalidElementId) return "-1";
            var value = (long)id.Value;
            if (!added.Contains(value)) return value.ToString(CultureInfo.InvariantCulture);
            var target = doc.GetElement(id);
            return "new:" + (target?.Category?.Name ?? target?.GetType().Name ?? "element");
        }

        private static string Location(Element e)
        {
            var parts = new List<string>();
            try
            {
                switch (e.Location)
                {
                    case LocationPoint lp:
                        parts.Add("pt:" + Xyz(lp.Point));
                        try { parts.Add("rot:" + ValueFingerprint.Number(lp.Rotation)); } catch (Exception) { /* no rotation */ }
                        break;
                    case LocationCurve lc:
                        parts.Add("crv:" + Xyz(lc.Curve.GetEndPoint(0)) + ">" + Xyz(lc.Curve.GetEndPoint(1)));
                        break;
                }
                var bb = e.get_BoundingBox(null);
                if (bb != null) parts.Add("bb:" + Xyz(bb.Min) + "|" + Xyz(bb.Max));
            }
            catch (Exception)
            {
                parts.Add("loc:?");
            }
            return string.Join(";", parts);
        }

        private static string Xyz(XYZ p) =>
            ValueFingerprint.Coordinate(p.X) + "," + ValueFingerprint.Coordinate(p.Y) + "," + ValueFingerprint.Coordinate(p.Z);
    }
}

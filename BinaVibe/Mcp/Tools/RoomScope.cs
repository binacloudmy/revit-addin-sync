// Room resolution + room membership, shared by any tool that answers "the
// <things> in this room". Extracted from SocketCandidates (2026-08): the
// membership logic there was private and only used to BLOCK candidate points,
// while plan_panel_assignment needs the same answer for existing sockets —
// one implementation, or the two disagree on the same room.
//
// The membership rule, promoted verbatim:
//   * FamilyInstance.Room is authoritative when present.
//   * IsPointInRoom is the fallback, and it is Z-SENSITIVE — a fixture's
//     LocationPoint sits at floor level and fails a naive test, so the probe
//     is raised into the room volume (floor Z + half the room height).
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace BinaVibe.Mcp.Tools
{
    internal static class RoomScope
    {
        private const double MmPerFoot = 304.8;

        /// <summary>Room by id, else by name or number (case-insensitive).
        /// Null + a reason naming the next action on a miss — never a silent
        /// empty result the agent reads as "no sockets here".</summary>
        public static Room? Resolve(Document doc, long? roomId, string? roomName, out string? whyNot)
        {
            whyNot = null;
            if (roomId != null)
            {
                if (doc.GetElement(ElemIds.From(roomId.Value)) is Room byId) return byId;
                whyNot = $"no room with id {roomId} — resolve the id with list_rooms first";
                return null;
            }

            if (string.IsNullOrWhiteSpace(roomName))
            {
                whyNot = "pass room_id or room_name — names come from list_rooms";
                return null;
            }

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .ToList();
            var hit = rooms.FirstOrDefault(r =>
                          string.Equals(SafeName(r), roomName, StringComparison.OrdinalIgnoreCase))
                   ?? rooms.FirstOrDefault(r =>
                          string.Equals(SafeNumber(r), roomName, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
                whyNot = $"no room named or numbered '{roomName}' — list_rooms shows the "
                       + $"{rooms.Count} room(s) this model has";
            return hit;
        }

        /// <summary>Floor Z of the room in mm: level elevation + lower offset.
        /// (Moved from SocketCandidates, which now delegates here.)</summary>
        public static double RoomFloorZMm(Document doc, Room room)
        {
            double ft = 0.0;
            var level = room.Level ?? doc.GetElement(room.LevelId) as Level;
            if (level != null) ft += level.Elevation;
            var lower = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET);
            if (lower != null && lower.StorageType == StorageType.Double) ft += lower.AsDouble();
            return ft * MmPerFoot;
        }

        /// <summary>Probe height (feet, internal units) for IsPointInRoom —
        /// mid-room, so floor-level points do not fail the naive test.</summary>
        public static double RaisedProbeZ(Document doc, Room room) =>
            RoomFloorZMm(doc, room) / MmPerFoot
            + (room.UnboundedHeight > 0 ? room.UnboundedHeight / 2.0 : 3.0);

        /// <summary>Point membership with the raised probe. Pass the probeZ
        /// from RaisedProbeZ once when testing many points.</summary>
        public static bool IsPointInRoomRaised(Room room, XYZ point, double probeZ)
        {
            try { return room.IsPointInRoom(new XYZ(point.X, point.Y, probeZ)); }
            catch { return false; }
        }

        /// <summary>Element membership: FamilyInstance.Room authoritative,
        /// raised-probe point test as fallback. Elements with neither a room
        /// association nor a LocationPoint are NOT members — unknowable, and
        /// claiming them would put another room's sockets on this panel.</summary>
        public static bool IsInRoom(Document doc, Room room, Element el) =>
            IsInRoom(room, el, RaisedProbeZ(doc, room));

        /// <summary>Overload for many-element sweeps: compute probeZ once.</summary>
        public static bool IsInRoom(Room room, Element el, double probeZ)
        {
            ElementId? roomId = null;
            try { roomId = (el as FamilyInstance)?.Room?.Id; } catch { roomId = null; }
            if (roomId != null) return roomId == room.Id;

            if (el.Location is LocationPoint lp)
                return IsPointInRoomRaised(room, lp.Point, probeZ);
            return false;
        }

        /// <summary>Placed instances of a category inside the room.</summary>
        public static List<Element> ElementsIn(Document doc, Room room, BuiltInCategory bic)
        {
            var probeZ = RaisedProbeZ(doc, room);
            return new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .Where(el => IsInRoom(room, el, probeZ))
                .ToList();
        }

        private static string SafeName(Element e) { try { return e.Name ?? ""; } catch { return ""; } }

        private static string SafeNumber(Room r)
        {
            try
            {
                var p = r.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                return p != null && p.HasValue ? p.AsString() ?? "" : "";
            }
            catch { return ""; }
        }
    }
}

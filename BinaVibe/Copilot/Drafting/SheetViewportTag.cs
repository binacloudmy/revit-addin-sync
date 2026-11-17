#nullable enable
// Sheet, viewport, title block and tag routines (AI-304).
//
// Moved out of the MCP-era Mutators.cs and TypedJobs.cs so those can be
// deleted (AI-303); sheet generation, sheets from Excel and smart tags build
// on these. Behaviour is unchanged from where each routine was lifted.
//
// None of these open a transaction. The caller owns the Transaction (and
// TxGuard), so one tool call stays one undo entry.
//
// What each routine assumes:
//   Title block   - looked up among loaded OST_TitleBlocks symbols by Name or
//                   "Family : Name" (case-insensitive). No name means the
//                   caller's choice (usually the first loaded). A sheet made
//                   with ElementId.InvalidElementId has no title block. A
//                   symbol must be Activate()d before use if it is inactive.
//   Viewport      - placed at the XYZ given, in feet, on the sheet. Revit
//                   refuses a view already on another sheet, an empty view
//                   and a view that cannot be placed (CanAddViewToSheet).
//   Centring      - ViewSheet.Outline is only valid after the title block is
//                   regenerated (doc.Regenerate() inside the open
//                   transaction). A zero-size outline falls back to
//                   (1.0, 0.7) ft, roughly the middle of an A1 sheet.
//   Tags          - IndependentTag uses TM_ADDBY_CATEGORY, so a tag family
//                   for the category must be loaded or Revit throws. The tag
//                   sits at the middle of the element's bounding box in the
//                   view (z = 0). Rooms use NewRoomTag at their location point.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Copilot.Drafting
{
    internal static class SheetViewportTag
    {
        // ─── title block ────────────────────────────────────────────────
        public static List<FamilySymbol> LoadedTitleblocks(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>().ToList();

        /// <summary>Match by type name or "Family : Type"; null when none matches.</summary>
        public static FamilySymbol? FindTitleblock(IEnumerable<FamilySymbol> titleblocks, string name) =>
            titleblocks.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals($"{t.FamilyName} : {t.Name}", name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The title block type used on an existing sheet, or InvalidElementId.</summary>
        public static ElementId TitleblockTypeOnSheet(Document doc, ViewSheet sheet)
        {
            var tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().FirstOrDefault();
            return tb?.Symbol?.Id ?? ElementId.InvalidElementId;
        }

        // ─── sheet ──────────────────────────────────────────────────────
        public static ViewSheet CreateSheet(Document doc, ElementId titleblockId, string number, string name)
        {
            var sheet = ViewSheet.Create(doc, titleblockId);
            sheet.SheetNumber = number;
            sheet.Name = name;
            return sheet;
        }

        /// <summary>Middle of the sheet outline; call after doc.Regenerate().</summary>
        public static XYZ SheetCentre(ViewSheet sheet)
        {
            var ol = sheet.Outline;
            return ol != null && ol.Max.U > ol.Min.U
                ? new XYZ((ol.Min.U + ol.Max.U) / 2, (ol.Min.V + ol.Max.V) / 2, 0)
                : new XYZ(1.0, 0.7, 0);
        }

        // ─── viewport ───────────────────────────────────────────────────
        public static Viewport PlaceView(Document doc, ElementId sheetId, ElementId viewId, XYZ point) =>
            Viewport.Create(doc, sheetId, viewId, point);

        /// <summary>Place a view like an existing viewport: same centre, same viewport type (type is best-effort).</summary>
        public static Viewport PlaceLikeViewport(Document doc, ElementId sheetId, ElementId viewId, Viewport like)
        {
            var nvp = Viewport.Create(doc, sheetId, viewId, like.GetBoxCenter());
            try { if (nvp != null && like.GetTypeId() != ElementId.InvalidElementId) nvp.ChangeTypeId(like.GetTypeId()); }
            catch { /* viewport type best-effort */ }
            return nvp!;
        }

        // ─── tags ───────────────────────────────────────────────────────
        /// <summary>True when a tag family for this category is loaded (unknown categories do not block).</summary>
        public static bool TagFamilyLoaded(Document doc, BuiltInCategory bic)
        {
            if (bic == BuiltInCategory.OST_Rooms)
                return new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_RoomTags).Any();
            var tagCat = bic switch
            {
                BuiltInCategory.OST_Doors => BuiltInCategory.OST_DoorTags,
                BuiltInCategory.OST_Windows => BuiltInCategory.OST_WindowTags,
                BuiltInCategory.OST_Walls => BuiltInCategory.OST_WallTags,
                _ => (BuiltInCategory?)null,
            };
            if (tagCat == null) return true;   // unknown pairing: don't block, let Revit decide
            return new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(tagCat.Value).Any();
        }

        /// <summary>Tag a room/space at its location point. False when it has no point location.</summary>
        public static bool TryTagRoom(Document doc, View view, Element el)
        {
            if (!(el is SpatialElement sp) || !(sp.Location is LocationPoint lp)) return false;
            doc.Create.NewRoomTag(new LinkElementId(el.Id), new UV(lp.Point.X, lp.Point.Y), view.Id);
            return true;
        }

        /// <summary>Tag an element by category at its bounding-box middle in the view. False when it has no box there.</summary>
        public static bool TryTagByCategory(Document doc, View view, Element el)
        {
            var bb = el.get_BoundingBox(view);
            if (bb == null) return false;
            var mid = (bb.Min + bb.Max) / 2.0;
            IndependentTag.Create(doc, view.Id, new Reference(el), false,
                TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, new XYZ(mid.X, mid.Y, 0));
            return true;
        }
    }
}

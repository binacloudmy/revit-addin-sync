// RoomColorScheme — colour-fill the placed rooms so a space plan reads as one.
//
// ISOLATED ON PURPOSE. Everything else in the space-planning Build (rooms,
// separation lines, tags, walls) is written against Revit API calls that already
// have working call sites in this repo. The colour-fill API does NOT — it is the
// one part written from documentation rather than from precedent. Keeping it in
// its own file means that if it fails to compile against a given Revit year, this
// file can be deleted and the Build still works, minus the colours.
//
// Nothing here is required for a correct model: colour fill is a VIEW setting.
// The rooms, their names, numbers and areas are all already correct without it.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class RoomColorScheme
    {
        /// <summary>The pane's palette, so the plan in Revit and the preview in the
        /// pane are the same colours. Keep in step with MassingPalette in
        /// UI/SpacePlanning/Model/MassingPlan.cs — a drafter comparing the two
        /// side by side should not have to translate.</summary>
        private static readonly (string room, byte r, byte g, byte b)[] Palette =
        {
            ("Bilik Darjah",      0xDF, 0xE4, 0xFD),
            ("Sokongan",          0xE2, 0xF5, 0xC9),
            ("Tandas",            0xD5, 0xED, 0xFB),
            ("Dewan Perhimpunan", 0xFD, 0xEE, 0xC2),
            ("Kantin",            0xFB, 0xE0, 0xD3),
            ("Padang",            0xE8, 0xF2, 0xDD),
            ("Selasar",           0xF1, 0xF2, 0xF4),
            ("Tangga",            0xF1, 0xF2, 0xF4),
        };

        /// <summary>
        /// Colour the rooms in <paramref name="view"/> by Name.
        ///
        /// TRANSACTION-FREE — the caller must already be inside a Transaction.
        /// Returns the number of colour entries applied, or 0 if anything went
        /// wrong. NEVER throws: a missing colour is cosmetic, and losing a correct
        /// 40-room build over it would be absurd.
        /// </summary>
        internal static int Apply(Document doc, View view)
        {
            try
            {
                var roomsCategoryId = new ElementId(BuiltInCategory.OST_Rooms);

                // Revit ships one or more room colour schemes in most templates.
                // Take one that is already keyed to a string parameter; otherwise
                // there is nothing safe to repoint and we bail rather than mangle a
                // scheme the user may be relying on elsewhere.
                var scheme = new FilteredElementCollector(doc)
                    .OfClass(typeof(ColorFillScheme))
                    .Cast<ColorFillScheme>()
                    .FirstOrDefault(s => s.CategoryId == roomsCategoryId && !s.IsByRange);
                if (scheme == null) return 0;

                // Duplicate rather than edit in place: the shipped scheme may be
                // applied to the user's own views, and silently recolouring those
                // would be a side effect nobody asked for.
                ColorFillScheme mine;
                var existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(ColorFillScheme))
                    .Cast<ColorFillScheme>()
                    .FirstOrDefault(s => s.Name == SchemeName);
                if (existing != null) mine = existing;
                else
                {
                    var dupId = scheme.Duplicate(SchemeName);
                    mine = doc.GetElement(dupId) as ColorFillScheme;
                    if (mine == null) return 0;
                }

                // Drive the colours from the room NAME — the same field the pane's
                // legend uses.
                try { mine.ParameterDefinition = new ElementId(BuiltInParameter.ROOM_NAME); }
                catch { /* already by name, or not repointable on this year */ }

                var entries = mine.GetEntries().ToList();
                var byValue = new Dictionary<string, ColorFillSchemeEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                    if (!string.IsNullOrEmpty(e.GetStringValue()))
                        byValue[e.GetStringValue()] = e;

                int applied = 0;
                foreach (var (room, r, g, b) in Palette)
                {
                    if (!byValue.TryGetValue(room, out var entry)) continue;
                    entry.Color = new Color(r, g, b);
                    applied++;
                }
                if (applied > 0) mine.SetEntries(entries);

                view.SetColorFillSchemeId(roomsCategoryId, mine.Id);
                return applied;
            }
            catch
            {
                // Any API drift between Revit years lands here. Colour is a nicety;
                // the model is already correct without it.
                return 0;
            }
        }

        private const string SchemeName = "BINA Space Planning";
    }
}

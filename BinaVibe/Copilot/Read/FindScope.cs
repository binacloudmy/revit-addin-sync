#nullable enable
// Revit-free scope decisions for find_elements (review C1, I3), so the
// executor never walks the whole model:
//   - categories: the query's (given or inferred); a free-text query with no
//     category searches a fixed set of common model categories (one Revit-side
//     ElementMulticategoryFilter), never every element; a handle bounds the
//     search by itself;
//   - room fallback (matching text against the element's room): only when the
//     query has a category or a room, so "tandas" does not return every
//     fixture, floor and door inside toilets;
//   - the scan budget: at most ScanBudget elements are looked at per call;
//     past it the result is partial and asks for a category or level;
//   - the links a handle points into are searched even under source host.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public sealed class ScopeDecision
    {
        /// <summary>OST_ names to filter on; empty means "resolve CategoryText" or "no
        /// category filter" (see the flags).</summary>
        public List<string> Categories = new();
        /// <summary>The category is a name the synonyms do not know; the executor resolves it.</summary>
        public bool ResolveCategoryText;
        /// <summary>The default common-categories set was chosen (free text, no category).</summary>
        public bool DefaultSet;
        /// <summary>No category filter: a handle bounds the search.</summary>
        public bool HandleOnly;
        /// <summary>Text may also match the element's room name/number.</summary>
        public bool RoomFallback;
    }

    public static class FindScope
    {
        /// <summary>Elements looked at per find_elements call, over all queries and sources.</summary>
        public const int ScanBudget = 20000;

        /// <summary>Common model categories a categoryless text query searches. Pipes,
        /// ducts, conduits, fittings and rebar are left out (very many elements,
        /// rarely searched by name): they need an explicit category.</summary>
        public static IReadOnlyList<string> DefaultCategories { get; } = new[]
        {
            "OST_Rooms", "OST_Areas", "OST_Doors", "OST_Windows", "OST_Walls", "OST_Floors", "OST_Ceilings",
            "OST_Roofs", "OST_Stairs", "OST_StairsRailing", "OST_Ramps", "OST_Columns", "OST_StructuralColumns",
            "OST_StructuralFraming", "OST_StructuralFoundation", "OST_Furniture", "OST_FurnitureSystems",
            "OST_Casework", "OST_PlumbingFixtures", "OST_LightingFixtures", "OST_LightingDevices",
            "OST_ElectricalFixtures", "OST_ElectricalEquipment", "OST_MechanicalEquipment",
            "OST_SpecialityEquipment", "OST_GenericModel", "OST_CurtainWallPanels", "OST_Parking",
            "OST_Planting", "OST_Site", "OST_Sprinklers", "OST_FireAlarmDevices", "OST_DuctTerminal",
            "OST_DataDevices", "OST_CommunicationDevices", "OST_SecurityDevices", "OST_NurseCallDevices",
            "OST_Entourage", "OST_Mass",
        };

        public static ScopeDecision Decide(FindQuery q, bool hasHandle)
        {
            var d = new ScopeDecision();
            if (q.CategoryOsts.Count > 0) d.Categories.AddRange(q.CategoryOsts);
            else if (q.CategoryText != null) d.ResolveCategoryText = true;
            else if (hasHandle) d.HandleOnly = true;
            else
            {
                d.DefaultSet = true;
                d.Categories.AddRange(DefaultCategories);
            }
            d.RoomFallback = q.HasCategory || q.Room != null;
            return d;
        }

        /// <summary>Link instance ids a handle's refs live in, in first-seen order.</summary>
        public static IReadOnlyList<long> HandleLinks(IEnumerable<ElementRef>? handleRefs) =>
            handleRefs == null
                ? Array.Empty<long>()
                : handleRefs.Where(r => r.Link.HasValue).Select(r => r.Link!.Value).Distinct().ToList();

        /// <summary>The not_searched line for a scan stopped by the budget.</summary>
        public static string BudgetNote(int scanned) =>
            "the rest of the model (scan stopped at the " + ScanBudget + "-element budget after " + scanned + " elements)";
    }
}

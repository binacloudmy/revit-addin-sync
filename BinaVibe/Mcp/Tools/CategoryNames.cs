// The Revit-free half of CategoryResolve: turning what a caller typed into the
// BuiltInCategory MEMBER NAMES worth trying, in order.
//
// It is split from CategoryResolve.cs (which returns a BuiltInCategory and so
// drags Autodesk.Revit.DB in) because the Tests project has the Revit API as a
// REFERENCE-ONLY package — no runtime assembly — and a single test type whose
// signature mentions a Revit type makes xUnit skip the entire assembly, not
// just that test. Names are strings, so all of the actual decisions below are
// testable; only the final Enum.TryParse is not.
//
// SINGULAR FALLBACK (UAT 2026-08-04). "Electrical Circuits" — the name Revit's
// own UI shows — compacts to OST_ElectricalCircuits, but the enum member is the
// SINGULAR OST_ElectricalCircuit, so filter_elements threw "unknown category"
// and the agent had no way left to reach a circuit's element id. The fallback
// strips one trailing "s", and it is LAST in the candidate order: every name
// that resolved before still resolves at an earlier candidate, so the only
// inputs whose behaviour can change are the ones that used to throw.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools
{
    internal static class CategoryNames
    {
        /// <summary>BuiltInCategory member names to try, in order. First one
        /// that parses wins; an empty list means "unknown category", which the
        /// callers turn into a loud failure rather than an unfiltered
        /// collector.</summary>
        public static List<string> Candidates(string nameOrFriendly)
        {
            var outNames = new List<string>();
            var category = nameOrFriendly;
            if (string.IsNullOrWhiteSpace(category)) return outNames;

            // 1. The enum literal, spelled out ("OST_Walls").
            if (category.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
                outNames.Add(category);

            // 2. The friendly name, generically ("Plumbing Fixtures" ->
            //    OST_PlumbingFixtures). This is what keeps the resolver from
            //    needing an entry per category.
            var compact = "OST_" + category.Replace(" ", "").Replace("-", "");
            outNames.Add(compact);

            // 3. Names the generic rule cannot reach.
            var alias = Alias(category);
            if (alias != null) outNames.Add(alias);

            // 4. Plural friendly name, singular enum member. See file header.
            if (compact.Length > 5 && compact.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                outNames.Add(compact.Substring(0, compact.Length - 1));

            return outNames;
        }

        private static string? Alias(string category) => category.ToLowerInvariant() switch
        {
            "walls" => "OST_Walls",
            "doors" => "OST_Doors",
            "windows" => "OST_Windows",
            "floors" => "OST_Floors",
            "rooms" => "OST_Rooms",
            "levels" => "OST_Levels",
            "grids" => "OST_Grids",

            // Power circuits. Listed as well as caught by the singular fallback
            // because these are the words a drafter actually uses, and "litar"
            // is the Malay one. NOTE for whoever reads a result: filter_elements
            // can now FIND circuits, but its row shape carries no panel, no
            // members and no rating — list_circuits is the tool that answers
            // questions about them.
            "electrical circuits" => "OST_ElectricalCircuit",
            "electrical circuit" => "OST_ElectricalCircuit",
            "circuits" => "OST_ElectricalCircuit",
            "circuit" => "OST_ElectricalCircuit",
            "power circuits" => "OST_ElectricalCircuit",
            "litar" => "OST_ElectricalCircuit",

            _ => null,
        };
    }
}

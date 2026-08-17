using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// The one place the add-in translates between what a user sees and what the
    /// backend accepts.
    ///
    /// bina-be's DisciplineType enum is
    ///   Architecture | Structure | Mechanical | Electrical | MainFile | Civil
    /// (src/constants/index.ts). The add-in has always sent "HVAC", which is not
    /// a member, so every discipline sync it attempted was rejected. "HVAC" is
    /// the label Malaysian BIM teams use, so it stays on screen and maps to
    /// Mechanical on the wire.
    ///
    /// Civil is a valid backend discipline that the add-in never offered.
    /// </summary>
    public static class DisciplineTypes
    {
        public const string Architecture = "Architecture";
        public const string Structure = "Structure";
        public const string Mechanical = "Mechanical";
        public const string Electrical = "Electrical";
        public const string Civil = "Civil";
        public const string MainFile = "MainFile";

        /// <summary>Label shown to the user for a given backend value.</summary>
        public const string MechanicalLabel = "HVAC";

        /// <summary>Order the disciplines are offered in the sync dialog.</summary>
        public static readonly IReadOnlyList<(string ApiValue, string Label, string Description)> Selectable =
            new List<(string, string, string)>
            {
                (Architecture, "Architecture", "Walls, doors, windows, finishes."),
                (Structure, "Structure", "Beams, columns, foundations, framing."),
                (Mechanical, MechanicalLabel, "Heating, ventilation and air conditioning."),
                (Electrical, "Electrical", "Power, lighting, containment."),
                (Civil, "Civil", "Earthworks, drainage, external works."),
                (MainFile, "Main model", "The federated or general model for the project.")
            };

        /// <summary>
        /// Convert anything the UI or a filename produced into a value bina-be
        /// accepts. Unknown values pass through so a backend that gains a new
        /// discipline does not need an add-in release.
        /// </summary>
        public static string ToApiValue(string uiOrLegacyValue)
        {
            if (string.IsNullOrWhiteSpace(uiOrLegacyValue)) return MainFile;
            return uiOrLegacyValue.Trim().Equals(MechanicalLabel, StringComparison.OrdinalIgnoreCase)
                ? Mechanical
                : uiOrLegacyValue.Trim();
        }

        /// <summary>Label for a backend value, for display only.</summary>
        public static string ToLabel(string apiValue)
        {
            if (string.IsNullOrWhiteSpace(apiValue)) return apiValue;
            return apiValue.Equals(Mechanical, StringComparison.OrdinalIgnoreCase)
                ? MechanicalLabel
                : apiValue;
        }

        /// <summary>
        /// Best-guess discipline from a filename prefix, used to tag linked
        /// models. Returns a backend value, not a label.
        /// </summary>
        public static string FromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return MainFile;
            string upper = fileName.ToUpperInvariant();

            if (upper.StartsWith("ARCHITECTURE") || upper.StartsWith("ARCH")) return Architecture;
            if (upper.StartsWith("STRUCTURE") || upper.StartsWith("STRUCT")) return Structure;
            if (upper.StartsWith("HVAC") || upper.StartsWith("MECHANICAL")) return Mechanical;
            if (upper.StartsWith("ELECTRICAL") || upper.StartsWith("ELEC")) return Electrical;
            if (upper.StartsWith("CIVIL")) return Civil;
            return MainFile;
        }
    }
}

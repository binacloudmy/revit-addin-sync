using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Category-scoped name classifier for fire-system detection (phase-2
    /// design §A.5). Fire-system families are category-sloppy in real
    /// Malaysian models (hose reels turn up as Mechanical Equipment,
    /// Plumbing Fixtures, even Generic Models), so detection is name
    /// classification scoped by candidate category — never category
    /// identity alone (except Sprinklers, where the category suffices).
    ///
    /// Pure static function over strings: unit-tests on macOS with zero
    /// Revit API (ScheduleToolsTests precedent). Returns a neutral
    /// detection key (engine translates to jurisdiction prose via legend
    /// aliases) or null for "not a fire-system element".
    /// </summary>
    public static class BombaSystemClassifier
    {
        // Canonical category keys — the extractor maps OST_* onto these so
        // the classifier stays Revit-free.
        public const string CatSprinklers = "sprinklers";
        public const string CatFireAlarm = "fire_alarm";
        public const string CatMechanical = "mechanical";
        public const string CatPlumbing = "plumbing";
        public const string CatSpecialty = "specialty";
        public const string CatCommunication = "communication";
        public const string CatElectricalFixtures = "electrical_fixtures";
        public const string CatGeneric = "generic";
        public const string CatPipeAccessory = "pipe_accessory";

        public static string Classify(string categoryKey, string familyName, string typeName)
        {
            if (string.IsNullOrEmpty(categoryKey)) return null;
            var text = ((familyName ?? "") + " " + (typeName ?? "")).ToLowerInvariant();

            // Name evidence first — it beats category defaults everywhere
            // except the sprinkler category itself.
            if (Has(text, "hose reel") || Has(text, "hosereel") || HasWord(text, "hr"))
                return "hose_reels";
            if (Has(text, "hydrant") || Has(text, "pili bomba"))
                return "hydrants";
            if (Has(text, "fm200") || Has(text, "fm-200") || Has(text, "fm 200")
                || HasWord(text, "co2") || Has(text, "suppression") || Has(text, "foam")
                || Has(text, "clean agent"))
                return "other_suppression";
            if (Has(text, "breeching") || Has(text, "dry riser"))
                return "dry_riser_inlets";
            if (Has(text, "landing valve") || Has(text, "wet riser"))
                return "wet_riser_outlets";

            if (categoryKey == CatSprinklers) return "sprinkler_heads";

            if (categoryKey == CatFireAlarm)
            {
                if (Has(text, "call point") || Has(text, "break glass") || Has(text, "breakglass")
                    || Has(text, "bell") || Has(text, "sounder") || HasWord(text, "mcp")
                    || Has(text, "penggera"))
                    return "manual_call_points";
                if (Has(text, "annunciator") || Has(text, "monitoring") || HasWord(text, "fcc")
                    || Has(text, "command centre") || Has(text, "command center")
                    || Has(text, "main panel"))
                    return "fire_monitoring_panels";
                // Everything else in Fire Alarm Devices counts as a detector —
                // the conservative direction: over-counting presence can only
                // soften a "missing" into "present", never fabricate a
                // "missing" (§A.5).
                return "detectors";
            }

            if (categoryKey == CatCommunication || categoryKey == CatElectricalFixtures)
            {
                if (HasWord(text, "pa") || Has(text, "public address") || Has(text, "speaker")
                    || Has(text, "intercom"))
                    return "pa_speakers";
            }

            // Detectors by explicit name in the sloppy categories.
            if (Has(text, "smoke detector") || Has(text, "heat detector")
                || Has(text, "beam detector") || Has(text, "pengesan asap")
                || Has(text, "pengesan haba"))
                return "detectors";

            return null;
        }

        private static bool Has(string text, string needle)
        {
            return text.Contains(needle);
        }

        private static bool HasWord(string text, string word)
        {
            return Regex.IsMatch(text, "\\b" + Regex.Escape(word) + "\\b");
        }
    }
}

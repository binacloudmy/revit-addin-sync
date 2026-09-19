using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>
    /// Whether a drawing's AutoCAD-vertical (Civil 3D / AutoCAD Architecture / AutoCAD MEP)
    /// CLASSES records mean it must be refused, warned about, or ignored — decided from plain
    /// data (no ACadSharp types) so it is testable without a DWG on disk. Class NAMES alone
    /// (AECC_*/AEC_*/AECB_*) never refuse: those records persist in the CLASSES table long after
    /// the AEC objects that created them are gone from a drawing that is otherwise plain
    /// linework, and refusing on names before reading a single entity was the original bug
    /// (readable plain-linework drawings were refused with no layers, no geometry). Refusal
    /// happens only when AEC classes both HAVE instances in this drawing (InstanceCount &gt; 0,
    /// IsAnEntity) and the survey pass found nothing readable either.
    /// </summary>
    internal static class CadSourceVerdict
    {
        /// <summary>
        /// Counts entity instances of AutoCAD-vertical classes (AECC_* Civil 3D, AEC_*
        /// Architecture, AECB_* MEP — case-insensitive), then decides:
        /// - no AEC classes at all -&gt; not refused, no warning.
        /// - AEC entity instances found but the drawing still has readable plain geometry
        ///   (readableCount &gt; 0) -&gt; not refused; <paramref name="warning"/> is set to a string
        ///   naming the skipped count, for the caller to append to its normal detect status.
        /// - AEC entity instances found and nothing readable -&gt; refused (returns true), caller
        ///   should throw with <paramref name="unsupportedMessage"/>.
        /// Classes named AECC_*/AEC_*/AECB_* with InstanceCount == 0 count for nothing — a class
        /// record can outlive every object it ever described.
        /// </summary>
        public static bool IsRefused(
            IEnumerable<(string dxfName, bool isEntity, int instanceCount)> classes,
            int readableCount,
            out string warning)
        {
            warning = null;
            int aecEntities = CountAecEntities(classes);
            if (aecEntities <= 0) return false;

            if (readableCount <= 0) return true;

            warning = aecEntities + " AutoCAD Architecture/Civil 3D/MEP objects were skipped — " +
                      "if walls are missing, run EXPORTTOAUTOCAD in AutoCAD and open the exported file.";
            return false;
        }

        private static int CountAecEntities(IEnumerable<(string dxfName, bool isEntity, int instanceCount)> classes)
        {
            if (classes == null) return 0;
            int total = 0;
            foreach (var c in classes)
            {
                if (!c.isEntity) continue;
                if (c.instanceCount <= 0) continue;
                if (string.IsNullOrEmpty(c.dxfName)) continue;
                if (IsAecClassName(c.dxfName)) total += c.instanceCount;
            }
            return total;
        }

        private static bool IsAecClassName(string dxfName)
        {
            return dxfName.StartsWith("AECC_", StringComparison.OrdinalIgnoreCase) ||
                   dxfName.StartsWith("AEC_", StringComparison.OrdinalIgnoreCase) ||
                   dxfName.StartsWith("AECB_", StringComparison.OrdinalIgnoreCase);
        }
    }
}

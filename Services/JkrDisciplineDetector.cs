namespace RevitWebAppSync.Services
{
    /// <summary>
    /// A model's discipline from its file name (JKR naming: jkrAR…, jkrST…).
    /// Shared by the scan extraction and the Start screen, which pre-fills the
    /// Discipline box with it. Revit-free.
    /// </summary>
    public static class JkrDisciplineDetector
    {
        /// <summary>The disciplines the Start screen offers. AR only for now: the
        /// scan can only read the architectural model. Rule data for the others
        /// stays; add a code here to bring one back.</summary>
        public static readonly string[] Selectable = { "AR" };

        /// <summary>AR / ST / ME / EL / CD / LD from the file name; AR when unknown.</summary>
        public static string Detect(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "AR";
            var upper = fileName.ToUpperInvariant();
            if (upper.Contains("JKRAR") || upper.StartsWith("AR")) return "AR";
            if (upper.Contains("JKRST") || upper.StartsWith("ST")) return "ST";
            if (upper.Contains("JKRME") || upper.StartsWith("ME")) return "ME";
            if (upper.Contains("JKREL") || upper.StartsWith("EL")) return "EL";
            if (upper.Contains("JKRCD") || upper.StartsWith("CD")) return "CD";
            if (upper.Contains("JKRLD") || upper.StartsWith("LD")) return "LD";
            return "AR"; // default
        }

        /// <summary>The detected discipline as one of <see cref="Selectable"/>:
        /// ST / ME / EL / CD / LD and unknown fall back to AR (the only one offered).</summary>
        public static string DetectSelectable(string fileName)
        {
            var d = Detect(fileName);
            return System.Array.IndexOf(Selectable, d) >= 0 ? d : "AR";
        }
    }
}

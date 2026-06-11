using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Parses JKR Schedule of Rates codes from Revit element/family/type names.
    /// JKR BIM naming convention embeds codes in parentheses, e.g.:
    ///   - (DBb300a) — wall type
    ///   - (PTa001a) — door type
    ///   - (LFh301a) — floor finish
    ///   - (TKk400a) — window type
    ///   - (LSw952a) — plumbing fixture
    /// </summary>
    public static class JkrCodeParser
    {
        // Match codes in parentheses: (DBb300a), (PTa001a), etc.
        // Pattern: opening paren, 2-3 uppercase letters, then alphanumeric, closing paren
        private static readonly Regex CodeInParens = new Regex(
            @"\(([A-Z]{2,3}[a-z]?\d{2,3}[a-z]?)\)",
            RegexOptions.Compiled);

        // Match codes after _( in family names: _(DBb300a)
        private static readonly Regex CodeAfterUnderscore = new Regex(
            @"_\(([A-Z]{2,3}[a-z]?\d{2,3}[a-z]?)\)",
            RegexOptions.Compiled);

        // Broader fallback: any code-like pattern in parentheses
        private static readonly Regex BroadCodePattern = new Regex(
            @"\(([A-Za-z]{2,4}\d{2,4}[a-z]?)\)",
            RegexOptions.Compiled);

        /// <summary>
        /// Try to extract a JKR code from element name, family name, or type name.
        /// Tries multiple sources in priority order.
        /// </summary>
        public static string Parse(string elementName, string familyName = null, string typeName = null)
        {
            // Try type name first (most specific)
            if (!string.IsNullOrEmpty(typeName))
            {
                var code = ExtractCode(typeName);
                if (code != null) return code;
            }

            // Then family name
            if (!string.IsNullOrEmpty(familyName))
            {
                var code = ExtractCode(familyName);
                if (code != null) return code;
            }

            // Then element name
            if (!string.IsNullOrEmpty(elementName))
            {
                var code = ExtractCode(elementName);
                if (code != null) return code;
            }

            return null;
        }

        private static string ExtractCode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            // Try strict JKR pattern first
            var match = CodeInParens.Match(text);
            if (match.Success) return match.Groups[1].Value;

            // Try underscore pattern
            match = CodeAfterUnderscore.Match(text);
            if (match.Success) return match.Groups[1].Value;

            // Broad fallback
            match = BroadCodePattern.Match(text);
            if (match.Success) return match.Groups[1].Value;

            return null;
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// The stable 12-hex issue id: the same (category, rule, elementId) always gives the
    /// same id, so a re-scan re-uses persisted Ignore / Approve decisions. Kept apart from
    /// JkrComplianceService (HTTP client) so IssueMapper can be unit-tested.
    /// </summary>
    internal static class JkrIssueId
    {
        public static string Compute(string category, string rule, long elementId)
        {
            var key = $"{category ?? ""}|{rule ?? ""}|{elementId}";
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}

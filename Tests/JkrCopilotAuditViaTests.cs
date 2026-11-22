using System;
using System.IO;
using System.Linq;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-495: "fixed via Copilot" survives an audit save and reload (needs IssueVm, so it runs on Windows only).</summary>
    public class JkrCopilotAuditViaTests
    {
        // ── audit: "fixed via Copilot" survives a save and a reload ──

        [Fact]
        public void Audit_record_keeps_fixed_via_Copilot()
        {
            var dir = Path.Combine(Path.GetTempPath(), "jkr-495-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var rvt = Path.Combine(dir, "m.rvt");
                var i = new IssueVm { Id = "r1", CheckId = "SPEC-Da.1", SpecCheckId = "Da.1", Title = "Door name", RevitCategory = "Doors", Verification = IssueVerification.Ai, RevitElementId = 100 }; i.Status = IssueStatus.Fixed; i.FixedVia = "Copilot";
                JkrAuditStore.Save(rvt, i);
                Assert.Contains("\"via\": \"Copilot\"", File.ReadAllText(JkrAuditStore.AuditPath(rvt)));
                Assert.Equal("Copilot", JkrAuditStore.LoadFixedFor(rvt).Single().FixedVia);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}

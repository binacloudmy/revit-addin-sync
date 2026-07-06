using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Source of the Copilot subscription / usage signal. Everything the panel
    /// shows about quota (meter colour, warning notes, the blocked state, the
    /// plan sheet) derives from <see cref="UsagePct"/> (0–100). Role fields drive
    /// the admin-vs-member branch on the upgrade CTAs.
    ///
    /// Today this is mocked (see <see cref="MockUsageService"/>); a real backend
    /// (extend AIService.CreditInfo with a plan/quota field) can implement this
    /// later without touching the view-model.
    /// </summary>
    public interface IUsageService : INotifyPropertyChanged
    {
        /// <summary>Percent of the monthly quota consumed, 0–100.</summary>
        int UsagePct { get; }

        /// <summary>Display name of the active plan, e.g. "Free".</summary>
        string PlanName { get; }

        /// <summary>True when this user can change the plan (billing owner / admin).
        /// Members see the "managed by your admin" branch instead of Upgrade CTAs.</summary>
        bool IsAdmin { get; }

        /// <summary>Who to contact to upgrade when the user is a member (email or
        /// name). Null/empty when unknown.</summary>
        string AdminContact { get; }
    }

    /// <summary>
    /// In-memory <see cref="IUsageService"/> for development and the UiHarness
    /// debug slider. All fields are settable and raise change notifications so the
    /// panel re-derives every usage state live.
    /// </summary>
    public class MockUsageService : IUsageService
    {
        private int _usagePct = 88;      // matches the design screenshots
        private string _planName = "Free";
        private bool _isAdmin = true;    // default to the admin/upgrade flow
        private string _adminContact = "your workspace admin";

        public int UsagePct
        {
            get => _usagePct;
            set { var v = Clamp(value); if (_usagePct == v) return; _usagePct = v; Raise(); }
        }

        public string PlanName
        {
            get => _planName;
            set { if (_planName == value) return; _planName = value; Raise(); }
        }

        public bool IsAdmin
        {
            get => _isAdmin;
            set { if (_isAdmin == value) return; _isAdmin = value; Raise(); }
        }

        public string AdminContact
        {
            get => _adminContact;
            set { if (_adminContact == value) return; _adminContact = value; Raise(); }
        }

        private static int Clamp(int v) => v < 0 ? 0 : (v > 100 ? 100 : v);

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

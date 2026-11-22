using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;
using HL = RevitWebAppSync.UI.Copilot.Highlights;


namespace RevitWebAppSync.UI.Copilot
{
    // Credits badge and usage meter. Split mechanically out of CopilotViewModel.cs (AI-310).
    public partial class CopilotViewModel
    {
        /// <summary>
        /// Fetch the signed-in user's monthly AI credit balance and seed the usage
        /// meter (footer) + header badge. Called right after login. No chat message —
        /// the quota lives in the meter, not the thread (ClickUp 86eybn4vz).
        /// Best-effort: silently no-ops when not signed in or the backend is unavailable.
        /// </summary>
        public async System.Threading.Tasks.Task ShowCreditsAsync()
        {
            try
            {
                var credits = await FetchCreditsAsync();
                if (credits == null) return;
                SetUsage(UsageState.FromCredits(credits.Unlimited, credits.Used, credits.Limit, credits.Plan, credits.ResetsAt));
            }
            catch { /* best-effort — never block login on the credits read */ }
        }

        /// <summary>Re-fetch the balance and update ONLY the header badge (no chat message).
        /// Called after each prompt so the badge ticks down live.</summary>
        public async System.Threading.Tasks.Task RefreshCreditBadgeAsync()
        {
            try
            {
                var cfg = _pane.LoadConfig();
                if (cfg == null || !cfg.IsLoggedIn()) { SetCreditBadge(""); return; }   // signed out → hide
                var credits = await new AIService().GetCreditsAsync(cfg.AccessToken);
                if (credits != null) SetCreditBadge(BadgeText(credits));                // keep last value on transient failure
            }
            catch { /* best-effort */ }
        }

        private async System.Threading.Tasks.Task<AIService.CreditInfo> FetchCreditsAsync()
        {
            var cfg = _pane.LoadConfig();
            if (cfg == null || !cfg.IsLoggedIn()) return null;
            return await new AIService().GetCreditsAsync(cfg.AccessToken);
        }

        // NOTE: BadgeText describes the QUOTA, not the plan — "Unlimited"
        // here means an uncapped wallet (an internal/admin override; pricing v2 has no
        // unlimited tier), which is what UsageState reports as "Unlimited (internal)".
        private static string BadgeText(AIService.CreditInfo c)
        {
            if (c.Unlimited) return "Unlimited";
            if (c.Limit <= 0) return "";
            int remaining = c.Remaining ?? System.Math.Max(0, c.Limit - c.Used);
            return $"{remaining:N0} / {c.Limit:N0} credits";
        }

        // UI-thread-safe badge setter (RefreshCreditBadgeAsync may resume off the UI thread).
        private void SetCreditBadge(string text)
        {
            _pane.RunOnUi(() => CreditBadge = text);
        }

        // ── Usage / plan (footer meter, popover, upgrade, blocked state) ─────
        /// <summary>Override for the credits-backed default — harness/tests inject a
        /// StubUsageService; a real billing adapter can replace it later.</summary>
        public Services.IUsageService UsageService { get; set; }

        private UsageState _usage = new UsageState();
        public UsageState Usage
        {
            get => _usage;
            private set { _usage = value ?? new UsageState(); Raise(); UsageChanged?.Invoke(); }
        }
        public event System.Action UsageChanged;

        /// <summary>Refresh the usage snapshot — from UsageService when injected,
        /// else from the credits balance. Best-effort; the meter never blocks chat.</summary>
        public async System.Threading.Tasks.Task RefreshUsageAsync()
        {
            long seq = NextUsageSeq();
            try
            {
                if (UsageService != null) { SetUsage(await UsageService.GetAsync(), seq); return; }
                var credits = await FetchCreditsAsync();
                if (credits != null)
                    SetUsage(UsageState.FromCredits(credits.Unlimited, credits.Used, credits.Limit, credits.Plan, credits.ResetsAt), seq);
            }
            catch { /* best-effort */ }
        }

        /// <summary>ONE balance read feeding both the usage snapshot and the credit
        /// badge. The poll previously called RefreshUsageAsync + RefreshCreditBadgeAsync
        /// back to back, and each fetched independently — two byte-identical
        /// GET /credits/balance per tick (~24/min in the fast tier). Use this wherever
        /// both are wanted.</summary>
        public async System.Threading.Tasks.Task RefreshUsageAndBadgeAsync()
        {
            long seq = NextUsageSeq();
            try
            {
                if (UsageService != null) { SetUsage(await UsageService.GetAsync(), seq); return; }
                var credits = await FetchCreditsAsync();
                if (credits == null) return;
                SetUsage(UsageState.FromCredits(credits.Unlimited, credits.Used, credits.Limit, credits.Plan, credits.ResetsAt), seq);
                SetCreditBadge(BadgeText(credits));
            }
            catch { /* best-effort */ }
        }

        // Monotonic stamp per usage read. Polling can leave two reads in flight at a
        // 5s tick and they may land out of order — a slow PRE-upgrade response
        // arriving after a fast POST-upgrade one would re-raise the blocked wall over
        // a working composer. Only ever publish a read newer than the last applied.
        private long _usageSeq;
        private long _usageApplied;

        /// <summary>Take a ticket for an in-flight usage read; hand it to SetUsage.</summary>
        private long NextUsageSeq() => System.Threading.Interlocked.Increment(ref _usageSeq);

        private void SetUsage(UsageState u, long seq = 0)
        {
            if (u == null) return;

            if (!_pane.IsOnUi) { _pane.RunOnUi(() => SetUsage(u, seq)); return; }

            // Ordering and de-duping are both decided on the UI thread, so two
            // concurrent refreshes can never both pass these checks.
            if (seq != 0)
            {
                if (seq <= _usageApplied) return;      // stale read, already overtaken
                _usageApplied = seq;
            }

            // While the quota is exhausted the pane re-polls the SAME snapshot every
            // few seconds waiting for an upgrade to land. Publishing an identical
            // value would still raise UsageChanged, and consumers rebuild on that —
            // the blocked wall would be torn down and re-created on every tick
            // (visible flicker, and it would reset a button mid-click).
            //
            // SameAs covers EVERY rendered field. Comparing only a subset was a real
            // bug: a snapshot differing solely in the reset date could never reach the
            // screen, so a fresh 0%-usage account kept the seeded default forever and
            // was shown an "Upgrade plan" CTA instead of "Resets 1 Aug".
            if (_usage != null && _usage.SameAs(u)) return;

            Usage = u;
        }

    }
}

using System;
using Newtonsoft.Json;

namespace RevitWebAppSync.Services
{
    /// <summary>What a forced update allows right now.</summary>
    public enum UpdateGateState
    {
        /// <summary>No forced update pending: BINA Sync works.</summary>
        Open,
        /// <summary>A forced update is pending and not downloaded yet.</summary>
        Blocked,
        /// <summary>The forced update is on disk; it applies after a Revit restart.</summary>
        RestartRequired,
    }

    /// <summary>
    /// The one forced-update gate every entry point reads: ribbon commands
    /// (UpdateService.EnsureUpToDate), every docked pane (UpdateGateOverlay),
    /// the AI tool handlers, the local tool server, the cloud tunnel and the
    /// engine. UpdateService is the only writer.
    ///
    /// Revit-free on purpose so the policy is unit-testable off Windows.
    /// </summary>
    public static class UpdateGate
    {
        private static readonly object Sync = new object();
        private static UpdateGateState _state = UpdateGateState.Open;
        private static string _version;

        /// <summary>Raised on a real state change, on whatever thread called
        /// Set — UI listeners must marshal to their dispatcher.</summary>
        public static event Action<UpdateGateState> Changed;

        public static UpdateGateState State { get { lock (Sync) return _state; } }

        /// <summary>Version the gate is waiting for (null when open).</summary>
        public static string PendingVersion { get { lock (Sync) return _version; } }

        /// <summary>True while BINA Sync must not be used (blocked or waiting for a restart).</summary>
        public static bool IsBlocked => State != UpdateGateState.Open;

        public static void Set(UpdateGateState state, string version = null)
        {
            Action<UpdateGateState> handler;
            lock (Sync)
            {
                var versionChanged = state != UpdateGateState.Open && version != null && version != _version;
                if (state == _state && !versionChanged) return;
                _state = state;
                _version = state == UpdateGateState.Open ? null : (version ?? _version);
                handler = Changed;
            }
            try { handler?.Invoke(state); } catch { /* a listener must never break the gate */ }
        }

        /// <summary>User-facing reason for a refused action, naming the way out.</summary>
        public static string RefusalMessage
        {
            get
            {
                var v = PendingVersion;
                var name = string.IsNullOrEmpty(v) ? "a new version of BINA Sync" : $"BINA Sync {v}";
                return State == UpdateGateState.RestartRequired
                    ? $"{name} is downloaded. Restart Revit to finish the update before using BINA Sync."
                    : $"{name} must be installed before BINA Sync can be used. Click Update now in the update window.";
            }
        }

        internal static void ResetForTest()
        {
            lock (Sync) { _state = UpdateGateState.Open; _version = null; Changed = null; }
        }
    }

    /// <summary>The last forced-update answer, remembered on disk so the next
    /// Revit start is locked immediately instead of open until the feed answers.</summary>
    public sealed class UpdateGateMemory
    {
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("mandatory")] public bool Mandatory { get; set; }

        public UpdateGateMemory() { }

        public UpdateGateMemory(string version, bool mandatory)
        {
            Version = version;
            Mandatory = mandatory;
        }

        public string ToJson() => JsonConvert.SerializeObject(this);

        /// <summary>Null for missing or corrupt content — never throws.</summary>
        public static UpdateGateMemory Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<UpdateGateMemory>(json); }
            catch { return null; }
        }
    }

    /// <summary>Pure decisions behind the gate.</summary>
    public static class UpdateGatePolicy
    {
        /// <summary>From a feed answer. Only a NEWER forced build blocks: a pin to
        /// an older build cannot downgrade a machine (BinaLoader runs the newest on disk).
        /// <para>A feed version this machine has marked BAD (the loader skipped it
        /// as a crashing build) never gates: forcing it would loop forever —
        /// restart, loader skips it again, still "restart required".</para></summary>
        public static UpdateGateState FromFeed(Version installed, Version feed, bool mandatory, bool stagedOnDisk,
            bool markedBadLocally = false)
        {
            if (installed == null || feed == null || !mandatory || feed <= installed || markedBadLocally)
                return UpdateGateState.Open;
            return stagedOnDisk ? UpdateGateState.RestartRequired : UpdateGateState.Blocked;
        }

        /// <summary>From the remembered answer, before the feed has answered.</summary>
        public static UpdateGateState FromMemory(Version installed, UpdateGateMemory memory, bool stagedOnDisk,
            bool markedBadLocally = false)
        {
            if (memory == null || !memory.Mandatory) return UpdateGateState.Open;
            Version remembered;
            if (!Version.TryParse(memory.Version ?? "", out remembered)) return UpdateGateState.Open;
            return FromFeed(installed, remembered, mandatory: true, stagedOnDisk: stagedOnDisk,
                markedBadLocally: markedBadLocally);
        }

        /// <summary>No feed answer (offline, DNS, 5xx): never lock the machine —
        /// it could not download the update anyway.</summary>
        public static UpdateGateState WhenFeedUnreachable() => UpdateGateState.Open;
    }
}

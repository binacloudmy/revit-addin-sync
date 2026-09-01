using System;

namespace RevitWebAppSync.Services
{
    /// <summary>Why the running build is refused, if it is.</summary>
    public enum GateReason
    {
        None,
        /// <summary>An update payload is known and can be staged now.</summary>
        UpdateAvailable,
        /// <summary>Already downloaded — only a Revit restart is left.</summary>
        Staged,
        /// <summary>Running outside versions\, so the updater cannot apply anything.</summary>
        ManualInstall,
        /// <summary>Floor is known but no payload is (offline, or a 426 arrived
        /// before the feed did) — re-check the feed before offering to stage.</summary>
        NoPayload,
    }

    /// <summary>Immutable snapshot of the version gate.</summary>
    public struct UpdateGate
    {
        public bool Blocked;
        public Version Current;
        public Version Required;
        public GateReason Reason;
    }

    /// <summary>
    /// The version-gate decision, pure and side-effect free. Split out of
    /// <see cref="UpdateService"/> — which needs Autodesk.Revit.UI and WPF —
    /// precisely so the truth table is testable without a feed, a Revit host,
    /// or the filesystem (same rationale as ToolLoopDtos / ConfirmGate).
    /// </summary>
    public static class UpdateGateRules
    {
        /// <summary>
        /// Decide whether the running build is refused.
        ///
        /// Deliberately fails OPEN at every ambiguity. This is a remote kill
        /// switch: a malformed feed, an unparseable floor, or a floor demanding
        /// a build that was never published must never brick a paying user
        /// mid-project. The cost of wrongly locking the fleet is far higher than
        /// the cost of one stale client reaching the backend, which rejects it
        /// with a 426 anyway.
        /// </summary>
        /// <param name="current">Running version (versions\&lt;ver&gt;\ folder, else assembly).</param>
        /// <param name="floor">minAddinVersion in force, or null for none.</param>
        /// <param name="feedVersion">Newest published build, for the sanity check; null when unknown.</param>
        /// <param name="hasPayload">An update payload is known and stageable.</param>
        /// <param name="runningFromVersionsStore">BinaLoader booted us — a staged update can actually apply.</param>
        /// <param name="staged">The update is already on disk; only a restart is left.</param>
        public static UpdateGate Evaluate(Version current, Version floor, Version feedVersion,
                                          bool hasPayload, bool runningFromVersionsStore, bool staged)
        {
            var gate = new UpdateGate { Current = current, Required = floor, Reason = GateReason.None };

            if (floor == null || current == null || current >= floor)
                return gate;

            // Floor above the newest published build = misconfigured release. The
            // update it demands does not exist, so honouring it would lock every
            // client out with no way back.
            if (feedVersion != null && floor > feedVersion)
                return gate;

            gate.Blocked = true;
            gate.Reason = !runningFromVersionsStore ? GateReason.ManualInstall
                        : staged ? GateReason.Staged
                        : hasPayload ? GateReason.UpdateAvailable
                        : GateReason.NoPayload;
            return gate;
        }
    }
}

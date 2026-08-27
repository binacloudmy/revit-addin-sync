// EnginePreflight — the pure planner behind ToolLoopService.EnsureEngineReadyAsync.
//
// The preflight's job is to MAKE the engine healthy before a turn, not to ask
// whether it is. Given what the probe found, Next() names the one step to take;
// the caller performs it and asks again. Kept free of Process/HttpClient so
// the decision table is unit-tested (Tests/EnginePreflightTests.cs) — the
// previous preflight had an untested `if (eng == null) return null` branch, and
// that single branch is how a raw WinSock string reached drafters.
//
// Invariant the tests pin: MessageFor() returns null ONLY for "healthy". Every
// other status — including ones this file has never heard of — becomes a
// sentence a drafter can act on, never the socket text.

using System;

namespace RevitWebAppSync.Services
{
    public enum PreflightStep
    {
        /// <summary>Engine answers /health with our shape. Run the turn.</summary>
        Ready,
        /// <summary>No EngineManager in this session — build one (App.cs path).</summary>
        ConstructManager,
        /// <summary>No engine\&lt;ver&gt;\ bundle on disk — stage one from the feed.</summary>
        FetchBundle,
        /// <summary>Bundle present, engine not answering — spawn and await health.</summary>
        Spawn,
        /// <summary>Gateway configured, user signed in, no device token — mint one
        /// (BrowserLoginCommand.MintDeviceTokenAndRestartEngineAsync) and restart
        /// the engine with it. Taken BEFORE the health check: a healthy engine
        /// that was spawned tokenless still dies 401 at the gateway.</summary>
        MintToken,
        /// <summary>Gateway configured, no sign-in at all — nothing the preflight
        /// can do; tell the drafter which button, and never dial.</summary>
        LoginRequired,
    }

    public static class EnginePreflight
    {
        /// <summary>The one thing to do next. Order matters: a healthy engine
        /// is attached to no matter how it got there (hand-started dev rig);
        /// otherwise a manager must exist before a bundle is useful, and a
        /// bundle must exist before a spawn can succeed. A terminal manager
        /// status (crash-loop, start-timeout) is an earlier attempt's verdict —
        /// this turn still gets one fresh spawn; the caller bounds the loop.</summary>
        public static PreflightStep Next(bool healthy, bool managerExists, bool bundleOnDisk, string status,
                                         bool gatewayConfigured, bool hasAccessToken, bool hasDeviceToken)
        {
            // Token first, health second. v0.0.60 on a fresh box got all the way
            // to a running engine and a 4-step turn, then died 401 "login
            // required" - surfaced as "Unknown model error" - because the device
            // token is only minted by a ribbon Login click, and a box signed in
            // BEFORE the gateway URL arrived (every OTA'd box) never clicked
            // again. A healthy-but-tokenless engine is not ready.
            if (gatewayConfigured && !hasDeviceToken)
                return hasAccessToken ? PreflightStep.MintToken : PreflightStep.LoginRequired;
            if (healthy) return PreflightStep.Ready;
            if (!managerExists) return PreflightStep.ConstructManager;
            if (!bundleOnDisk) return PreflightStep.FetchBundle;
            return PreflightStep.Spawn;
        }

        /// <summary>Drafter-facing sentence for an EngineManager.Status. Null
        /// means healthy and is the ONLY null this returns.</summary>
        public static string MessageFor(string status)
        {
            switch (status ?? "")
            {
                case "healthy":
                    return null;
                case "error:not-installed":
                    return "BINA Engine is not installed on this machine yet and could not be downloaded — check the network and try again.";
                case "error:addin-too-old":
                    return "BINA Engine needs a newer add-in than this one — update the add-in.";
                case "error:crash-loop":
                    return "BINA Engine keeps crashing on start — restart Revit; if it persists, send the engine log to support.";
                case "error:start-timeout":
                    return "BINA Engine did not come up in time — it may still be warming up. Try again in a minute.";
                case "error:spawn-failed":
                    return "BINA Engine failed to launch — restart Revit; if it persists, send the engine log to support.";
                case "starting":
                    return "BINA Engine is still starting — try again in a few seconds.";
                case "":
                    return "BINA Engine is not running — try again in a few seconds.";
                default:
                    return "BINA Engine is not ready (" + status + ") — try again shortly.";
            }
        }

        /// <summary>Sentence for the step that could not be completed.</summary>
        public static string FailureMessage(PreflightStep failedAt, string status, string detail)
        {
            var why = string.IsNullOrWhiteSpace(detail) ? "" : " (" + detail + ")";
            switch (failedAt)
            {
                case PreflightStep.ConstructManager:
                    return "BINA Engine could not be set up from the current config" + why + " — check config.json or reinstall.";
                case PreflightStep.FetchBundle:
                    return "BINA Engine download failed" + why + " — check the network and try again.";
                case PreflightStep.Spawn:
                    return MessageFor(status) ?? "BINA Engine is not ready — try again shortly.";
                case PreflightStep.LoginRequired:
                    // Name the RIGHT button. The "BINA Cloud Account" dialog is a
                    // different sign-in (CDE) and never mints the engine token.
                    return "Please sign in to use the Copilot: Bina tab -> Login.";
                case PreflightStep.MintToken:
                    return "BINA Engine could not get its sign-in token" + why + " - open Bina tab -> Login to sign in again.";
                default:
                    return MessageFor(status) ?? "BINA Engine is not ready — try again shortly.";
            }
        }

        /// <summary>BinaConfig.ApplyHeals rule: engine mode on earns auto-spawn.
        /// The old rule also demanded a bundle on disk; the preflight can now
        /// fetch one, so the flag must not wait for it. Cloud mode untouched.</summary>
        public static bool ShouldEnableAutoSpawn(bool engineMode, bool autoSpawn)
            => engineMode && !autoSpawn;
    }
}

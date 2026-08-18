using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitWebAppSync.Events;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.UI;
using RevitWebAppSync.UI.Copilot;
using BinaVibe.Indexer;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class App : IExternalApplication
    {
        // Static properties for ExternalEvent access from commands
        public static ExternalEvent AIExternalEvent { get; private set; }
        public static CodeExecutionHandler AIHandler { get; private set; }

        // JKR rename handler
        public static ExternalEvent JkrRenameEvent { get; private set; }
        public static RevitWebAppSync.Services.BombaPickWriteHandler BombaPickHandler { get; private set; }
        public static ExternalEvent BombaPickEvent { get; private set; }
        public static RevitWebAppSync.Services.BombaScopeWriteHandler BombaScopeHandler { get; private set; }
        public static ExternalEvent BombaScopeEvent { get; private set; }
        public static RevitWebAppSync.Services.BombaAutoFixHandler BombaAutoFixHandler { get; private set; }
        public static ExternalEvent BombaAutoFixEvent { get; private set; }
        public static JkrRenameHandler JkrRenameHandler { get; private set; }

        // Cost Dashboard dockable pane host
        public static CostDashboardHost CostDashboardHost { get; private set; }

        // Fire Compliance dockable pane host
        public static ComplianceDashboardHost ComplianceDashboardHost { get; private set; }

        // JKR BIM Compliance dockable pane host
        public static JkrComplianceDashboardHost JkrComplianceDashboardHost { get; private set; }

        public static BombaComplianceDashboardHost BombaComplianceDashboardHost { get; private set; }

        // Revit Copilot dockable pane host (right-docked side panel)
        public static CopilotPaneHost CopilotPaneHost { get; private set; }

        // Live UIApplication captured on Idling (a valid Revit API context).
        // Fallback for the dockable Copilot pane: its _uiApp is only pushed by
        // OpenCopilotCommand, so it stays NULL when Revit auto-restores the
        // docked pane on startup (no ribbon click). Without this,
        // BuildEnvContext sees a null ActiveUIDocument and ships a blank env
        // header (no project name / Revit version) — and MCP tool execution
        // has no UIApplication to run against.
        public static UIApplication UiApp { get; private set; }

        // Live cost update handler
        public static CostUpdateHandler CostUpdateHandler { get; private set; }

        // BinaVibe v2 embedded MCP server (PRD §12 Step 1).
        // Exposes /mcp/tools/{name} on localhost:8080 so the bina-ai
        // backend can call back into the live Revit session for the
        // Inspector preflight (and Step 3+ MUTATE tools).
        public static BinaVibe.Mcp.McpServer VibeMcpServer { get; private set; }
        // Phase 4: owns the local engine process when EngineAutoSpawn is on.
        public static RevitWebAppSync.Services.EngineManager VibeEngine { get; private set; }

        // BinaVibe v2 outbound WSS tunnel client (PRD §6.5 / production
        // transport). When BINA_VIBE_MCP_TRANSPORT is "wss" or "both",
        // the addin dials out to bina-ai at /revit-copilot/mcp/tunnel and the
        // cloud orchestrator pushes tool-call frames down the socket.
        // No inbound port on the customer machine. Firewall-friendly.
        public static BinaVibe.Mcp.McpTunnelClient VibeMcpTunnel { get; private set; }

        // Tool-calling execution handler + its ExternalEvent — ALWAYS created
        // (not gated with the tunnel). The HTTP tool-loop enqueues an McpJob and
        // raises McpToolEvent to run the tool on Revit's UI thread.
        public static BinaVibe.Mcp.McpExternalEventHandler McpToolHandler { get; private set; }
        public static Autodesk.Revit.UI.ExternalEvent McpToolEvent { get; private set; }

        // DocumentChangedIndexer — subscribes to Revit changes and ships
        // deltas to the backend snapshot endpoint. Also fires a one-time
        // BulkIndexAsync on connect to seed the mirror with a full baseline.
        public static DocumentChangedIndexer VibeIndexer { get; private set; }

        // Warm-up handler/event — runs the first (expensive) regen of a newly
        // opened model at load so the user's first build is not frozen by it.
        public static BinaVibe.WarmupHandler VibeWarmup { get; private set; }
        public static ExternalEvent VibeWarmupEvent { get; private set; }

        // True once the active model has been warmed (first regen paid). The
        // Copilot pane gates the first send until this is set, so the user's
        // first build runs warm (~60ms) instead of paying the ~70s cold regen.
        // Reset to false on each DocumentOpened, set in the warm-up after-hook.
        public static bool VibeModelWarm { get; set; }

        // True once the warm-up edit has actually begun on the UI thread — lets
        // the pane switch its "Preparing model" overlay from "Loading model…"
        // to "Warming up…". Reset on DocumentOpened, set in WarmupHandler.
        public static bool VibeWarmupStarted { get; set; }

        // Deferred warm-up: the project doc waiting to be warmed, and when it
        // opened. The throwaway-wall warm-up must run AFTER the model fully
        // loads (the big idle block clears) — at first-idle the model is still
        // light and the wall logged ~8-17ms, warming nothing while the real
        // build later paid ~32-70s. Set on DocumentOpened, consumed in Idling.
        private static Autodesk.Revit.DB.Document _pendingWarmDoc;
        private static long _warmOpenedTs;

        /// <summary>
        /// Reconnects the WSS tunnel with a freshly-acquired access token.
        /// Called by LoginCommand after a successful login so the backend can
        /// bind the tunnel to the authenticated user (and allocate AI credits
        /// correctly). No-op when the WSS transport is not active.
        /// </summary>
        public static void RestartVibeTunnel(string newToken)
        {
            if (VibeMcpTunnel == null) return;
            try
            {
                VibeMcpTunnel.Dispose();
                var cfg = BinaConfig.Load();
                var flags = BinaVibe.Policy.VibeFlags.Load();
                var sessionId = Guid.NewGuid().ToString();
                VibeMcpTunnel = new BinaVibe.Mcp.McpTunnelClient(
                    cfg.ResolvedAIBaseUrl,
                    flags.TenantId,
                    sessionId,
                    newToken,
                    flags.UserId);
                VibeMcpTunnel.Start();
                System.Diagnostics.Debug.WriteLine($"[BINA] Vibe MCP tunnel restarted with authenticated token (tenant={flags.TenantId})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Vibe MCP tunnel restart failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Restarts the locally-spawned engine process with a freshly-minted
        /// device token (colocate deployment pipeline Task 4). Called by
        /// BrowserLoginCommand after it mints + persists BinaConfig.DeviceToken
        /// so the engine's next spawn picks it up via BINA_ENGINE_TOKEN
        /// (EngineManager reads BinaConfig fresh at spawn time).
        ///
        /// Disposing an EngineManager also disposes its single-flight gate
        /// semaphore (see EngineManager.Dispose), so the disposed instance
        /// cannot be reused — this constructs a brand-new one exactly as
        /// OnStartup's auto-spawn block does, rather than calling
        /// EnsureRunningAsync again on the old (now-disposed) instance.
        ///
        /// No-op when engine auto-spawn isn't configured (VibeEngine is null
        /// in that mode, so there is nothing to restart). Best-effort/
        /// fire-and-forget — never blocks the caller (login UX).
        /// </summary>
        public static void RestartVibeEngineForNewToken()
        {
            try
            {
                var cfg = BinaConfig.Load();
                // Same gate as OnStartup's spawn path: EngineMode + AutoSpawn,
                // AND a non-blank secret — OnStartup refuses to start the local
                // tool server (and therefore the engine) when EngineSecret is
                // missing, so a restart must never spawn what startup refused.
                if (!cfg.EngineMode || !cfg.EngineAutoSpawn ||
                    string.IsNullOrWhiteSpace(cfg.EngineSecret))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[BINA] engine auto-spawn not enabled (or EngineSecret missing) — skipping engine restart after login.");
                    return;
                }

                VibeEngine?.Dispose();
                VibeEngine = new RevitWebAppSync.Services.EngineManager(
                    cfg.EngineHostPort, cfg.EngineSecret ?? "");
                _ = VibeEngine.EnsureRunningAsync();   // fire-and-forget; health-gated
                System.Diagnostics.Debug.WriteLine(
                    "[BINA] engine restart requested with fresh device token");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] engine restart after login failed: {ex.Message}");
            }
        }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Fleet heartbeat: 'started' now, 'ready' at the end of this
                // method — the gap between the two is how the backend detects a
                // release that dies mid-startup on some Revit year.
                Services.TelemetryService.Init(application.ControlledApplication.VersionNumber);
                Services.TelemetryService.Track("startup", "started");

                // Which backends THIS build resolved. The channel .env is baked
                // in at compile time and config.json can still pin individual
                // hosts, so "which backend is this install talking to" had no
                // answer short of disassembling the DLL — and a stale host is
                // the usual cause of a sign-in that succeeds but leaves every
                // feature 404ing. Best-effort: diagnostics must never be what
                // stops the add-in loading.
                try
                {
                    var endpoints = BinaConfig.Load();
                    System.Diagnostics.Debug.WriteLine(
                        "[BINA] resolved endpoints\n" + endpoints.DescribeEndpoints());
                    Services.TelemetryService.Track("startup", "endpoints", new
                    {
                        channel = BinaConfig.Channel,
                        ai_base = endpoints.ResolvedAIBaseUrl,
                        api_base = endpoints.ResolvedApiBaseUrl,
                        login_web = endpoints.ResolvedLoginWebUrl,
                        cloud_web = endpoints.ResolvedCloudWebUrl
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA] endpoint diagnostics failed: {ex.Message}");
                }

                // Self-heal legacy direct-load installs (stale RevitWebAppSync
                // manifest + DLL in Addins collide with the loader path — the
                // "assembly with same name is already loaded" dialog). The
                // telemetry event counts affected machines fleet-wide.
                int cleaned = Services.DirectLoadCleanup.Run();
                if (cleaned > 0)
                    Services.TelemetryService.Track("startup", "directload_cleaned",
                        new { files_removed = cleaned });

                // Idle-gap heartbeat (diagnostic): Revit raises Idling whenever
                // its UI thread is free. If two consecutive Idling events are
                // >2s apart, the UI thread was BLOCKED that long ("Not
                // Responding"). Logs the block duration so freezes show up in
                // the timeline with nothing-of-ours running = Revit-internal
                // (e.g. the large model still finishing its open).
                long lastIdleTs = 0;
                application.Idling += (s, e) =>
                {
                    // Capture a live UIApplication for the Copilot pane fallback
                    // (sender of Idling IS the UIApplication, in valid context).
                    if (UiApp == null && s is UIApplication __ua) UiApp = __ua;
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    double freq = System.Diagnostics.Stopwatch.Frequency;
                    double gapMs = lastIdleTs != 0 ? (now - lastIdleTs) * 1000.0 / freq : 0;
                    if (gapMs > 2000)
                        System.Diagnostics.Debug.WriteLine(
                            $"[BinaVibe][idle] UI thread blocked {gapMs:F0}ms before idle resumed");
                    lastIdleTs = now;

                    // Deferred warm-up trigger: once the model has SETTLED, warm
                    // it (throwaway wall pays the cold regen on the fully-loaded
                    // model). "Settled" = an idle resumed after a big load block
                    // (>5s) — Revit just finished heavy work — or a fallback
                    // window elapsed (light model that never blocked). Until then
                    // VibeModelWarm stays false so the pane holds the send gate.
                    if (_pendingWarmDoc != null)
                    {
                        double sinceOpenMs = (now - _warmOpenedTs) * 1000.0 / freq;
                        bool settledAfterLoad = gapMs > 5000;
                        bool fallback = sinceOpenMs > 15000;
                        if (settledAfterLoad || fallback)
                        {
                            var d = _pendingWarmDoc;
                            _pendingWarmDoc = null;
                            System.Diagnostics.Debug.WriteLine(
                                $"[BinaVibe] warm-up trigger ({(settledAfterLoad ? "settled" : "fallback")}) " +
                                $"after {sinceOpenMs:F0}ms — warming {d?.Title}");
                            if (d != null && d.IsValidObject && VibeWarmup != null)
                            {
                                VibeWarmup.Enqueue(d);
                                VibeWarmupEvent?.Raise();
                            }
                        }
                    }
                };

                // Create external event handler for AI code execution
                AIHandler = new CodeExecutionHandler();
                AIExternalEvent = ExternalEvent.Create(AIHandler);

                JkrRenameHandler = new JkrRenameHandler();
                JkrRenameEvent = ExternalEvent.Create(JkrRenameHandler);

                BombaPickHandler = new RevitWebAppSync.Services.BombaPickWriteHandler();
                BombaPickEvent = ExternalEvent.Create(BombaPickHandler);

                BombaScopeHandler = new RevitWebAppSync.Services.BombaScopeWriteHandler();
                BombaScopeEvent = ExternalEvent.Create(BombaScopeHandler);

                BombaAutoFixHandler = new RevitWebAppSync.Services.BombaAutoFixHandler();
                BombaAutoFixEvent = ExternalEvent.Create(BombaAutoFixHandler);

                // Tool-calling execution handler — ALWAYS created (independent of
                // the gated tunnel/MCP transport). The HTTP tool-loop (ToolLoopRunner)
                // enqueues an McpJob via McpJobPump, which drains it on the Revit UI
                // thread from the Idling event (forcing continuous idling so it runs
                // promptly even from the modeless pane), and fast-fails if Revit is
                // busy / has a modal dialog open. This is the addin half of Step 4
                // (/tool/generate ↔ /tool/resume) — no tunnel needed.
                McpToolHandler = new BinaVibe.Mcp.McpExternalEventHandler();
                McpToolEvent = ExternalEvent.Create(McpToolHandler);
                // Idling-driven pump (kills the "Revit not idle → 600s freeze" bug,
                // see docs/superpowers/specs/2026-06-30-revit-tool-execution-reliability-design.md).
                BinaVibe.Mcp.McpJobPump.Init(application, McpToolHandler, McpToolEvent);
                // A Revit modal dialog blocks idling entirely — note it so a
                // fast-fail tells the user to close the dialog.
                application.DialogBoxShowing += (s, e) => BinaVibe.Mcp.McpJobPump.NoteDialogShown();

                // Warm-up: pays the one-time first-regen at doc-open so the
                // user's first build doesn't freeze (see WarmupHandler).
                VibeWarmup = new BinaVibe.WarmupHandler();
                VibeWarmupEvent = ExternalEvent.Create(VibeWarmup);

                // Register Cost Dashboard dockable pane
                try
                {
                    CostDashboardHost = new CostDashboardHost();
                    application.RegisterDockablePane(
                        CostDashboardHost.PaneId,
                        "BINA Cost Tracker",
                        CostDashboardHost);
                }
                catch (Exception dockEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Cost dockable pane registration failed: {dockEx.Message}");
                    Services.TelemetryService.Track("subsystem", "failed",
                        new { name = "cost_pane", error_class = dockEx.GetType().Name });
                }

                // Register Fire Compliance dockable pane
                try
                {
                    ComplianceDashboardHost = new ComplianceDashboardHost();
                    application.RegisterDockablePane(
                        ComplianceDashboardHost.PaneId,
                        "BINA Fire Compliance",
                        ComplianceDashboardHost);
                }
                catch (Exception compEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Compliance dockable pane registration failed: {compEx.Message}");
                    Services.TelemetryService.Track("subsystem", "failed",
                        new { name = "compliance_pane", error_class = compEx.GetType().Name });
                }

                // Register JKR BIM Compliance dockable pane
                try
                {
                    JkrComplianceDashboardHost = new JkrComplianceDashboardHost();
                    application.RegisterDockablePane(
                        JkrComplianceDashboardHost.PaneId,
                        "BINA JKR Compliance",
                        JkrComplianceDashboardHost);
                }
                catch (Exception jkrEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] JKR Compliance dockable pane registration failed: {jkrEx.Message}");
                    Services.TelemetryService.Track("subsystem", "failed",
                        new { name = "jkr_pane", error_class = jkrEx.GetType().Name });
                }

                // Register Bomba Compliance dockable pane
                try
                {
                    BombaComplianceDashboardHost = new BombaComplianceDashboardHost();
                    application.RegisterDockablePane(
                        BombaComplianceDashboardHost.PaneId,
                        "BINA Bomba Compliance",
                        BombaComplianceDashboardHost);
                }
                catch (Exception bombaEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Bomba Compliance dockable pane registration failed: {bombaEx.Message}");
                    Services.TelemetryService.Track("subsystem", "failed",
                        new { name = "bomba_pane", error_class = bombaEx.GetType().Name });
                }

                // Register Revit Copilot dockable pane
                try
                {
                    CopilotPaneHost = new CopilotPaneHost();
                    application.RegisterDockablePane(
                        CopilotPaneHost.PaneId,
                        "BINA AI Copilot",
                        CopilotPaneHost);
                }
                catch (Exception copilotEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Copilot dockable pane registration failed: {copilotEx.Message}");
                    Services.TelemetryService.Track("subsystem", "failed",
                        new { name = "copilot_pane", error_class = copilotEx.GetType().Name });
                }

                // Subscribe to document changes for live cost updates
                try
                {
                    CostUpdateHandler = new CostUpdateHandler(application);
                    CostUpdateHandler.Subscribe();
                }
                catch (Exception evtEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Cost update handler failed: {evtEx.Message}");
                    Services.TelemetryService.Track("subsystem", "failed",
                        new { name = "cost_update_handler", error_class = evtEx.GetType().Name });
                }

                // Decide which MCP transport(s) to boot. Default is
                // `http` (HttpListener on :8080) for dev compatibility.
                //   http  — inbound HTTP only (dev, needs tunnel for cloud)
                //   wss   — outbound WSS only (production, firewall-safe)
                //   both  — start both (handy during migration)
                var transport = (Environment.GetEnvironmentVariable("BINA_VIBE_MCP_TRANSPORT") ?? "http").ToLowerInvariant();
                var cfg = BinaConfig.Load();

                if (cfg.EngineMode)
                {
                    // BINA Copilot Engine mode: the agent loop runs as a local
                    // process next to Revit and calls this add-in's local tool
                    // server (McpServer) over 127.0.0.1. Start the HTTP tool
                    // server ONLY — the cloud WSS tunnel is not used in engine
                    // mode. The old BINA_VIBE_TOOLPATH env gate is bypassed.
                    if (string.IsNullOrWhiteSpace(cfg.EngineSecret))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[BINA] EngineMode on but EngineSecret missing — local tool server NOT started.");
                        transport = "off";
                    }
                    else
                    {
                        transport = "http";
                    }
                }
                // HARD GATE (cloud mode only) — the MCP tool path is disabled.
                // The backend is codegen-only and rejects the tunnel (403), and
                // the mirror the DocumentChangedIndexer feeds is unread. Running
                // this block is pure cost: ~15 tunnel reconnect attempts PER
                // PROMPT plus a UI-thread element walk on every edit (mirror
                // delta) that nothing consumes. Off by default; set
                // BINA_VIBE_TOOLPATH=1 to revive once the backend's tool-calling
                // path is back.
                else if ((Environment.GetEnvironmentVariable("BINA_VIBE_TOOLPATH") ?? "0") != "1")
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[BINA] MCP tool path gated OFF (codegen-only) — no tunnel, no mirror indexer. Set BINA_VIBE_TOOLPATH=1 to enable.");
                    transport = "off";
                }

                if (transport == "http" || transport == "both")
                {
                    try
                    {
                        var port = cfg.EngineMode
                            ? cfg.EnginePort
                            : (int.TryParse(Environment.GetEnvironmentVariable("BINA_VIBE_MCP_PORT"), out var p) ? p : 8080);
                        VibeMcpServer = new BinaVibe.Mcp.McpServer(port, cfg.EngineSecret ?? "");
                        VibeMcpServer.Start();
                        System.Diagnostics.Debug.WriteLine($"[BINA] Vibe MCP server listening on {VibeMcpServer.Prefix}");

                        // Phase 4 (opt-in): auto-spawn the packaged engine and
                        // hand it the SAME secret the tool server validates.
                        // Off by default — Phases 1-3 start the engine manually.
                        if (cfg.EngineMode && cfg.EngineAutoSpawn)
                        {
                            try
                            {
                                VibeEngine = new RevitWebAppSync.Services.EngineManager(
                                    cfg.EngineHostPort, cfg.EngineSecret ?? "");
                                _ = VibeEngine.EnsureRunningAsync();   // fire-and-forget; health-gated
                                System.Diagnostics.Debug.WriteLine(
                                    $"[BINA] engine auto-spawn requested on port {cfg.EngineHostPort}");
                            }
                            catch (Exception engEx)
                            {
                                System.Diagnostics.Debug.WriteLine("[BINA] engine auto-spawn failed: " + engEx.Message);
                            }
                        }
                    }
                    catch (Exception mcpEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BINA] Vibe MCP server failed to start: {mcpEx.Message}");
                    }
                }

                // Cloud WSS tunnel — never started in engine mode.
                if ((transport == "wss" || transport == "both") && !cfg.EngineMode)
                {
                    try
                    {
                        var flags = BinaVibe.Policy.VibeFlags.Load();
                        // Send the logged-in user's BINA Cloud JWT so the backend can
                        // validate the tunnel against bina-be and bind the tenant to the
                        // verified identity. Falls back to BINA_VIBE_TOKEN / dev-token only
                        // when no session exists (dev / not-yet-logged-in).
                        var token = !string.IsNullOrWhiteSpace(cfg?.AccessToken)
                            ? cfg.AccessToken
                            : (Environment.GetEnvironmentVariable("BINA_VIBE_TOKEN") ?? "dev-token");
                        var sessionId = Guid.NewGuid().ToString();
                        VibeMcpTunnel = new BinaVibe.Mcp.McpTunnelClient(
                            cfg.ResolvedAIBaseUrl,
                            flags.TenantId,
                            sessionId,
                            token,
                            flags.UserId);
                        VibeMcpTunnel.Start();
                        System.Diagnostics.Debug.WriteLine($"[BINA] Vibe MCP tunnel dialing out to {cfg.ResolvedAIBaseUrl}/revit-copilot/mcp/tunnel (tenant={flags.TenantId})");

                        // Wire the DocumentChanged indexer so delta changes
                        // are shipped on every edit. The DocumentOpened event
                        // triggers a one-time bulk walk for each document that
                        // opens, seeding the backend mirror with a full baseline.
                        // Uses a shared HttpClient instance (not per-request).
                        var indexerHttp = new System.Net.Http.HttpClient();
                        VibeIndexer = new DocumentChangedIndexer(
                            application.ControlledApplication,
                            indexerHttp,
                            cfg.ResolvedAIBaseUrl,
                            flags.TenantId,
                            cfg.ProjectId.ToString());

                        // Capture for the lambda so it doesn't close over a
                        // loop variable or mutable field.
                        var indexer = VibeIndexer;
                        // Bulk index runs inside the warm-up's UI-thread context:
                        // the Revit element walk (CollectBulkDocs) runs ON the UI
                        // thread (fast, no idle contention — the old off-thread
                        // Task.Run walk starved Revit idle ~30s and froze the
                        // first build); only the network POST goes to a background
                        // thread.
                        VibeWarmup.AfterWarm = warmedDoc =>
                        {
                            // The first-regen has now been paid (warm-up edit) —
                            // mark the model warm so the pane releases its gate
                            // and the user's first build runs warm. Always set,
                            // even if the warm-up edit was best-effort-skipped, so
                            // the gate never traps the user.
                            VibeModelWarm = true;

                            // DISABLED (freeze fix, 2026-06-04): CollectBulkDocs
                            // walks the ENTIRE model on the UI thread to seed the
                            // cloud mirror — measured ~178s per document, firing
                            // once per opened doc (main + every linked model) =
                            // the multi-minute "[BinaVibe][idle] UI thread blocked"
                            // freezes. The mirror it feeds is currently UNUSED
                            // (VIBE_MIRROR_READS=0, grounding inert), so the walk
                            // is pure cost. Re-enable only once the mirror walk is
                            // moved off the UI thread / chunked across idle cycles.
                            //
                            // var _sw = System.Diagnostics.Stopwatch.StartNew();
                            // var snapshot = indexer.CollectBulkDocs(warmedDoc);
                            // _sw.Stop();
                            // System.Diagnostics.Debug.WriteLine(
                            //     $"[BinaVibe][timing] CollectBulkDocs (UI walk)={_sw.ElapsedMilliseconds}ms " +
                            //     $"docs={snapshot.Count} doc={warmedDoc.Title}");
                            // _ = System.Threading.Tasks.Task.Run(
                            //     () => indexer.PostBulkAsync(snapshot, version: 1));
                        };
                        // On open: warm the first regen on the UI thread (pays
                        // the one-time ~58s here, not on the user's first build),
                        // then AfterWarm kicks the bulk index.
                        application.ControlledApplication.DocumentOpened +=
                            (s, e) =>
                            {
                                var d = e.Document;
                                // Only index the real project model. Revit raises
                                // DocumentOpened for EVERY linked model and family
                                // too — each would queue a full bulk walk on the UI
                                // thread and stack into a multi-minute freeze.
                                if (d == null || d.IsLinked || d.IsFamilyDocument)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[BinaVibe] skip bulk-index for non-project doc: {d?.Title}");
                                    return;
                                }
                                // A freshly opened project model is cold again —
                                // gate sends until warm. DON'T warm now: at this
                                // first idle the model is still light (warm-up
                                // logged ~8-17ms and warmed nothing). Defer to the
                                // Idling handler, which warms once the model has
                                // SETTLED (after its big load block) so the
                                // throwaway wall actually pays the cold regen.
                                VibeModelWarm = false;
                                VibeWarmupStarted = false;
                                _pendingWarmDoc = d;
                                _warmOpenedTs = System.Diagnostics.Stopwatch.GetTimestamp();
                            };
                    }
                    catch (Exception tunEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BINA] Vibe MCP tunnel failed to start: {tunEx.Message}");
                    }
                }

                CreateRibbonTab(application);

                // Delete manifests of old parallel installs (pre-loader
                // direct-load .addin in APPDATA/ProgramData) so the next
                // start runs a single copy. See LegacyInstallCleaner.
                try { Services.LegacyInstallCleaner.Purge(application); } catch { }

                // OTA: stage any newer build in the background; BinaLoader
                // applies it on the next Revit start. No-op if no feed is
                // configured (see BinaConfig.UpdateFeedUrl).
                Services.UpdateService.Start(application);

                Services.TelemetryService.Track("startup", "ready");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Services.TelemetryService.Track("startup", "failed",
                    new { error_class = ex.GetType().Name });
                // Revit may be about to unload us — drain synchronously (2s cap).
                Services.TelemetryService.FlushBlocking(TimeSpan.FromSeconds(2));
                TaskDialog.Show("Error", $"Failed to initialize add-in: {ex.Message}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            // Unsubscribe from document change events
            CostUpdateHandler?.Unsubscribe();

            // Stop the embedded MCP server cleanly so the port is free
            // on next Revit start.
            try { VibeMcpServer?.Dispose(); } catch { }
            try { VibeEngine?.Dispose(); } catch { }
            try { VibeMcpTunnel?.Dispose(); } catch { }
            try { VibeIndexer?.Dispose(); } catch { }

            // Scratch documents opened to read attached DWGs — left open they
            // hold a file lock and show up in Revit's window list. Attached-PDF
            // text goes with them (whole spec documents held as strings).
            try { BinaVibe.Mcp.Tools.DwgScratchCache.CloseAll(); } catch { }
            try { BinaVibe.Mcp.Tools.PdfAttachmentCache.CloseAll(); } catch { }
            try { BinaVibe.Mcp.Tools.Electrical.SocketPlanCache.CloseAll(); } catch { }

            // 'clean' shutdown marker: a session with 'started' but neither
            // 'ready' nor 'clean' reads as a dirty exit in the scorecard.
            try
            {
                Services.TelemetryService.Track("shutdown", "clean");
                Services.TelemetryService.FlushBlocking(TimeSpan.FromSeconds(2));
            }
            catch { }

            return Result.Succeeded;
        }

        private void CreateRibbonTab(UIControlledApplication application)
        {
            string tabName = "Bina"; // Renamed from "Sync" as requested
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Tab already exists — a stale parallel install created it
                // first. Panels attach to the existing tab; without this the
                // whole OnStartup died and the addin vanished from the ribbon.
            }

            // One panel per BACKEND, because there is one sign-in per backend.
            // bina-be (CDE) issues the token for /api/cloud-docs/* — sync and
            // discipline downloads; bina-ai issues the one Copilot and JKR use.
            // A bina-ai token is rejected by bina-be, so grouping each login with
            // the buttons it actually unlocks is what makes the two sign-ins read
            // as deliberate rather than as one of them being redundant.
            RibbonPanel cdePanel = application.CreateRibbonPanel(tabName, "BINA CDE");
            RibbonPanel aiPanel = application.CreateRibbonPanel(tabName, "BINA AI");
            RibbonPanel compliancePanel = application.CreateRibbonPanel(tabName, "Compliance");

            PushButtonData buttonData = new PushButtonData(
                "SyncToWebApp",
                "Sync",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.SyncCommand")
            {
                ToolTip = "Open BINA Cloud",
                LongDescription = "Opens BINA Cloud in your default browser.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            // Login opens the BINA Cloud web page (login OR register) in the
            // browser; identity is bina-ai (OAuth + PKCE). Tokens land in the
            // Windows Credential Manager. No password is typed into Revit.
            PushButtonData loginButtonData = new PushButtonData(
                "Login",
                "Login to\nAI",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.BrowserLoginCommand")
            {
                ToolTip = "Sign in to BINA AI (Copilot, JKR compliance, space planning)",
                LongDescription = "Opens the BINA AI sign-in page in your browser to log in or create an account. Separate from Login to CDE — that one signs you in to Cloud Docs for model sync, and the two backends issue their own tokens.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            // Second sign-in, against bina-be. Cloud Docs / BIM sync live on a
            // different service from Copilot/JKR and it issues its own tokens, so
            // a bina-ai session cannot authorise /api/cloud-docs/* calls. Both
            // sessions are stored separately; merging the two buttons into one
            // sign-in that mints both tokens is a follow-up.
            PushButtonData cloudLoginButtonData = new PushButtonData(
                "BinaCloudLogin",
                "Login to\nCDE",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.BinaCloudLoginCommand")
            {
                ToolTip = "Sign in to the BINA CDE / Cloud Docs (projects, documents, model sync)",
                LongDescription = "Signs in to BINA Cloud Docs in your browser. Required by Sync and Shared Download — the other buttons on this panel. Separate from Login to AI, which Copilot, JKR and space planning use.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            PushButtonData bimDisciplineButtonData = new PushButtonData(
                "BimDiscipline",
                "Shared\nDownload",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.BimDisciplineCommand")
            {
                ToolTip = "Download the latest shared discipline files",
                LongDescription = "Download the latest Architecture, Structure, HVAC, and Electrical discipline files from BINA cloud.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            // Parameters entered in the BINA viewer live in BINA's database, not
            // in the .rvt — so a downloaded model opens without them. This writes
            // them back onto the elements (ClickUp 86d3y5jxx).
            PushButtonData syncParametersButtonData = new PushButtonData(
                "SyncParameters",
                "Sync\nParameters",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.SyncParametersCommand")
            {
                ToolTip = "Write BINA element parameters into this model",
                LongDescription = "Pulls the parameters added to elements in BINA Cloud and writes them onto the matching elements here, creating shared parameters where the model does not have them yet.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            // Issues raised in the BINA viewer, shown against the open model
            // (ClickUp 86d3y5jtz). Read-only in this release.
            PushButtonData syncIssuesButtonData = new PushButtonData(
                "SyncIssues",
                "Issues",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.SyncIssuesCommand")
            {
                ToolTip = "Show BINA issues against this model",
                LongDescription = "Pulls the issues raised on this model in BINA Cloud, selects the elements an issue points at, and restores the viewpoint it was captured from.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            // LEGACY — Federate Disciplines is retired; the command itself is
            // excluded from the build (FederateDisciplinesCommand.cs). This button
            // was already never added to a panel; kept commented for reference.
            // PushButtonData federateButtonData = new PushButtonData(
            // "FederateDisciplines",
            // "Federate Disciplines",
            // Assembly.GetExecutingAssembly().Location,
            // "RevitWebAppSync.FederateDisciplinesCommand")
            // {
            // ToolTip = "Link Downloaded Discipline Files",
            // LongDescription = "Link previously downloaded discipline files to the current Revit document for coordination and clash detection.",
            // Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
            // LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            // };

            PushButtonData askAiButtonData = new PushButtonData(
                "AskAI",
                "AI Assistant",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.OpenCopilotCommand")
            {
                ToolTip = "Open BINA AI Copilot",
                LongDescription = "Open the BINA AI Copilot side panel to run vetted tools or AI commands with natural language. Examples: Count doors by level, Rename levels, Find walls missing fire rating.",
                Image = LoadImage("RevitWebAppSync.Resources.aiAssistant.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.aiAssistant.png", 32)
            };

            // Cost Tracker buttons
            PushButtonData costExportButtonData = new PushButtonData(
                "CostExport",
                "Export\nCost Items",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.CostExportCommand")
            {
                ToolTip = "Export Cost Items to Excel",
                LongDescription = "Extract all model elements with quantities, JKR codes, and levels. Export to Excel for QS to fill in prices.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            PushButtonData costImportButtonData = new PushButtonData(
                "CostImport",
                "Import\nPrices",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.CostImportCommand")
            {
                ToolTip = "Import Prices from Excel",
                LongDescription = "Import unit prices from a filled Excel file. Prices are saved locally and applied to the cost tracker.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            PushButtonData costDashboardButtonData = new PushButtonData(
                "CostDashboard",
                "Cost\nTracker",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.CostDashboardCommand")
            {
                ToolTip = "Open Cost Tracker Dashboard",
                LongDescription = "Show the cost tracker panel with total cost breakdown by level and category.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            PushButtonData complianceButtonData = new PushButtonData(
                "FireCompliance",
                "Fire\nCompliance",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.ComplianceDashboardCommand")
            {
                ToolTip = "Check Fire Compliance (UKBS 1984)",
                LongDescription = "Check building elements against Malaysian UKBS 1984 fire safety requirements (Jadual 5-11). Shows non-compliant elements and allows querying the by-laws.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            PushButtonData jkrComplianceButtonData = new PushButtonData(
                "JkrCompliance",
                "JKR\nCompliance",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.JkrComplianceDashboardCommand")
            {
                ToolTip = "Check JKR BIM Compliance (Document 09)",
                LongDescription = "Check model elements against JKR BIM Spesifikasi Parameter — naming, required parameters per LOD level, JKR codes. 53 categories, 5,478 parameter rules.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            PushButtonData bombaComplianceButtonData = new PushButtonData(
                "BombaCompliance",
                "Bomba\nCompliance",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.BombaComplianceDashboardCommand")
            {
                ToolTip = "Check Bomba fire-safety compliance (UBBL)",
                LongDescription = "Check the model against UBBL fire-safety requirements — exit width, travel distance, fire systems. Reads the model; changes nothing.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            // BINA CDE: the bina-be sign-in, then the two buttons that need it.
            // Login leads the panel because both of the others fail without it.
            cdePanel.AddItem(cloudLoginButtonData);
            cdePanel.AddItem(buttonData);
            cdePanel.AddItem(bimDisciplineButtonData);
            cdePanel.AddItem(syncParametersButtonData);
            cdePanel.AddItem(syncIssuesButtonData);

            // BINA AI: the bina-ai sign-in, then the copilot it unlocks.
            aiPanel.AddItem(loginButtonData);
            aiPanel.AddItem(askAiButtonData);

            // Compliance: JKR (Cost/Fire hidden below)
            compliancePanel.AddItem(jkrComplianceButtonData);
            compliancePanel.AddItem(bombaComplianceButtonData);

            // Stack: Export Cost Items / Import Prices
            // cdePanel.AddStackedItems(costExportButtonData, costImportButtonData); // Hidden as requested

            // Stack: Cost Tracker / Fire Compliance
            // compliancePanel.AddStackedItems(costDashboardButtonData, complianceButtonData); // Hidden as requested
            // cdePanel.AddItem(federateButtonData); // Hidden as requested
        }

        private BitmapImage LoadImage(string resourceName, int size = 32)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                Stream stream = assembly.GetManifestResourceStream(resourceName);

                if (stream == null)
                    return null;

                BitmapImage bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = stream;
                bitmapImage.DecodePixelWidth = size;
                bitmapImage.DecodePixelHeight = size;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
            catch
            {
                return null;
            }
        }
    }
}

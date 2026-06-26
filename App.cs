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
        public static JkrRenameHandler JkrRenameHandler { get; private set; }

        // Cost Dashboard dockable pane host
        public static CostDashboardHost CostDashboardHost { get; private set; }

        // Fire Compliance dockable pane host
        public static ComplianceDashboardHost ComplianceDashboardHost { get; private set; }

        // JKR BIM Compliance dockable pane host
        public static JkrComplianceDashboardHost JkrComplianceDashboardHost { get; private set; }

        // Revit Copilot dockable pane host (right-docked side panel)
        public static CopilotPaneHost CopilotPaneHost { get; private set; }

        // Live UIApplication captured on Idling (a valid Revit API context).
        // Fallback for the dockable Copilot pane: its _uiApp is only pushed by
        // OpenCopilotCommand, so it stays NULL when Revit auto-restores the
        // docked pane on startup (no ribbon click). Without this, BuildContext
        // sees a null ActiveUIDocument and ships a BLANK context (no selection,
        // levels, or views) to the agent.
        public static UIApplication UiApp { get; private set; }

        // Live cost update handler
        public static CostUpdateHandler CostUpdateHandler { get; private set; }

        // BinaVibe v2 embedded MCP server (PRD §12 Step 1).
        // Exposes /mcp/tools/{name} on localhost:8080 so the bina-ai
        // backend can call back into the live Revit session for the
        // Inspector preflight (and Step 3+ MUTATE tools).
        public static BinaVibe.Mcp.McpServer VibeMcpServer { get; private set; }

        // BinaVibe v2 outbound WSS tunnel client (PRD §6.5 / production
        // transport). When BINA_VIBE_MCP_TRANSPORT is "wss" or "both",
        // the addin dials out to bina-ai at /vibe/mcp/tunnel and the
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

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
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

                // Tool-calling execution handler — ALWAYS created (independent of
                // the gated tunnel/MCP transport). The HTTP tool-loop (ToolLoopRunner)
                // enqueues an McpJob here, raises the event, and waits; the handler
                // runs the tool via ToolRegistry.Invoke on the Revit UI thread. This
                // is the addin half of Step 4 (/tool/generate ↔ /tool/resume) — no
                // tunnel needed, the backend just hands us pending tool calls.
                McpToolHandler = new BinaVibe.Mcp.McpExternalEventHandler();
                McpToolEvent = ExternalEvent.Create(McpToolHandler);

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
                }

                // Register Revit Copilot dockable pane
                try
                {
                    CopilotPaneHost = new CopilotPaneHost();
                    application.RegisterDockablePane(
                        CopilotPaneHost.PaneId,
                        "BINA Revit Copilot",
                        CopilotPaneHost);
                }
                catch (Exception copilotEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Copilot dockable pane registration failed: {copilotEx.Message}");
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
                }

                // Decide which MCP transport(s) to boot. Default is
                // `http` (HttpListener on :8080) for dev compatibility.
                //   http  — inbound HTTP only (dev, needs tunnel for cloud)
                //   wss   — outbound WSS only (production, firewall-safe)
                //   both  — start both (handy during migration)
                var transport = (Environment.GetEnvironmentVariable("BINA_VIBE_MCP_TRANSPORT") ?? "http").ToLowerInvariant();

                // HARD GATE — the MCP tool path is disabled. The backend is
                // codegen-only and rejects the tunnel (403), and the mirror the
                // DocumentChangedIndexer feeds is unread. Running this block is
                // pure cost: ~15 tunnel reconnect attempts PER PROMPT plus a
                // UI-thread element walk on every edit (mirror delta) that
                // nothing consumes — the source of the post-prompt UI-thread
                // blocks. Off by default; set BINA_VIBE_TOOLPATH=1 to revive
                // once the backend's tool-calling path is back.
                if ((Environment.GetEnvironmentVariable("BINA_VIBE_TOOLPATH") ?? "0") != "1")
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[BINA] MCP tool path gated OFF (codegen-only) — no tunnel, no mirror indexer. Set BINA_VIBE_TOOLPATH=1 to enable.");
                    transport = "off";
                }

                if (transport == "http" || transport == "both")
                {
                    try
                    {
                        var port = int.TryParse(Environment.GetEnvironmentVariable("BINA_VIBE_MCP_PORT"), out var p) ? p : 8080;
                        VibeMcpServer = new BinaVibe.Mcp.McpServer(port);
                        VibeMcpServer.Start();
                        System.Diagnostics.Debug.WriteLine($"[BINA] Vibe MCP server listening on {VibeMcpServer.Prefix}");
                    }
                    catch (Exception mcpEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BINA] Vibe MCP server failed to start: {mcpEx.Message}");
                    }
                }

                if (transport == "wss" || transport == "both")
                {
                    try
                    {
                        var flags = BinaVibe.Policy.VibeFlags.Load();
                        var cfg = BinaConfig.Load();
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
                        System.Diagnostics.Debug.WriteLine($"[BINA] Vibe MCP tunnel dialing out to {cfg.ResolvedAIBaseUrl}/vibe/mcp/tunnel (tenant={flags.TenantId})");

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

                // OTA: stage any newer build in the background; BinaLoader
                // applies it on the next Revit start. No-op if no feed is
                // configured (see BinaConfig.UpdateFeedUrl).
                Services.UpdateService.Start(application);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
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
            try { VibeMcpTunnel?.Dispose(); } catch { }
            try { VibeIndexer?.Dispose(); } catch { }

            return Result.Succeeded;
        }

        private void CreateRibbonTab(UIControlledApplication application)
        {
            string tabName = "Bina"; // Renamed from "Sync" as requested
            application.CreateRibbonTab(tabName);

            // Labeled panel groups instead of one flat "Sync Tools" strip
            RibbonPanel cloudPanel = application.CreateRibbonPanel(tabName, "BINA Cloud");
            RibbonPanel aiPanel = application.CreateRibbonPanel(tabName, "AI");
            RibbonPanel compliancePanel = application.CreateRibbonPanel(tabName, "Compliance");

            PushButtonData buttonData = new PushButtonData(
                "SyncToWebApp",
                "Sync to BINA",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.SyncCommand")
            {
                ToolTip = "Open BINA Cloud",
                LongDescription = "Opens BINA Cloud in your default browser.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            PushButtonData loginButtonData = new PushButtonData(
                "Login",
                "Login",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.LoginCommand")
            {
                ToolTip = "Login to BINA Cloud",
                LongDescription = "Opens BINA Cloud in your default browser to login.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            PushButtonData bimDisciplineButtonData = new PushButtonData(
                "BimDiscipline",
                "Download BIM\nDisciplines",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.BimDisciplineCommand")
            {
                ToolTip = "Download BIM Discipline Files",
                LongDescription = "Download the latest Architecture, Structure, HVAC, and Electrical discipline files from BINA cloud.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            PushButtonData federateButtonData = new PushButtonData(
                "FederateDisciplines",
                "Federate Disciplines",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.FederateDisciplinesCommand")
            {
                ToolTip = "Link Downloaded Discipline Files",
                LongDescription = "Link previously downloaded discipline files to the current Revit document for coordination and clash detection.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSave.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSave.png", 32)
            };

            PushButtonData askAiButtonData = new PushButtonData(
                "AskAI",
                "AI Assistant",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.OpenCopilotCommand")
            {
                ToolTip = "Open Revit Copilot",
                LongDescription = "Open the Revit Copilot side panel to run vetted tools or AI commands with natural language. Examples: Count doors by level, Rename levels, Find walls missing fire rating.",
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

            // BINA Cloud: sync, account, downloads
            cloudPanel.AddItem(buttonData);
            cloudPanel.AddItem(loginButtonData);
            cloudPanel.AddItem(bimDisciplineButtonData);

            // AI: copilot
            aiPanel.AddItem(askAiButtonData);

            // Compliance: JKR (Cost/Fire hidden below)
            compliancePanel.AddItem(jkrComplianceButtonData);

            // Stack: Export Cost Items / Import Prices
            // cloudPanel.AddStackedItems(costExportButtonData, costImportButtonData); // Hidden as requested

            // Stack: Cost Tracker / Fire Compliance
            // compliancePanel.AddStackedItems(costDashboardButtonData, complianceButtonData); // Hidden as requested
            // cloudPanel.AddItem(federateButtonData); // Hidden as requested
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

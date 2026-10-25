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
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class App : IExternalApplication
    {
        // JKR rename handler
        public static ExternalEvent JkrRenameEvent { get; private set; }
        public static RevitWebAppSync.Services.BombaPickWriteHandler BombaPickHandler { get; private set; }
        public static ExternalEvent BombaPickEvent { get; private set; }
        public static RevitWebAppSync.Services.BombaScopeWriteHandler BombaScopeHandler { get; private set; }
        public static ExternalEvent BombaScopeEvent { get; private set; }
        public static RevitWebAppSync.Services.BombaAutoFixHandler BombaAutoFixHandler { get; private set; }
        public static ExternalEvent BombaAutoFixEvent { get; private set; }
        public static JkrRenameHandler JkrRenameHandler { get; private set; }
        public static RevitWebAppSync.Handlers.RollbackSwapHandler RollbackSwapHandler { get; private set; }
        public static ExternalEvent RollbackSwapEvent { get; private set; }

        // Cost Dashboard dockable pane host
        public static CostDashboardHost CostDashboardHost { get; private set; }

        // Fire Compliance dockable pane host
        public static ComplianceDashboardHost ComplianceDashboardHost { get; private set; }

        // JKR BIM Compliance dockable pane host
        public static JkrComplianceDashboardHost JkrComplianceDashboardHost { get; private set; }

        public static BombaComplianceDashboardHost BombaComplianceDashboardHost { get; private set; }

        // Issues dockable pane + the event that lets it touch the model
        // (ClickUp 86d3y5jtz). The pane is plain WPF with no API context.
        public static UI.Issues.IssuesPaneHost IssuesPaneHost { get; private set; }
        public static Handlers.IssueShowHandler IssueShowHandler { get; private set; }
        public static ExternalEvent IssueShowEvent { get; private set; }

        // Bina AI Copilot dockable pane host (right-docked side panel)
        public static CopilotPaneHost CopilotPaneHost { get; private set; }

        /// <summary>Whether the copilot pane is shown, forwarded once per change to
        /// W2's SnapshotService.SetPaneOpen. Showing it also prewarms the model
        /// overview (SetPaneOpen(true) calls OverviewService.RequestPrewarm), and
        /// warnings, rooms and by_category are kept current only while it is shown.</summary>
        internal static readonly UI.Copilot.Model.PaneOpenState CopilotPaneOpen =
            new UI.Copilot.Model.PaneOpenState(open => BinaVibe.Copilot.Read.SnapshotService.SetPaneOpen(open));

        // CAD to BIM dockable pane + the ExternalEvent that runs a build on
        // Revit's thread. The pane is plain WPF with no API context; the event
        // is created in OnStartup (a valid API context) BEFORE the pane host,
        // because the pane's view model takes the handler and the event in its
        // constructor and raises the event from Confirm.
        public static CadToBimPaneHost CadToBimPaneHost { get; private set; }
        public static CadToBimBuildHandler CadToBimBuildHandler { get; private set; }
        public static ExternalEvent CadToBimBuildEvent { get; private set; }

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

        /// <summary>Raised after a BINA AI sign-in or sign-out has been persisted
        /// (BrowserLoginCommand). The Copilot pane listens: it unlocks the
        /// composer and re-sends the prompt it kept while signed out.</summary>
        public static event Action SessionChanged;
        internal static void RaiseSessionChanged()
        {
            try { SessionChanged?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] SessionChanged handler failed: " + ex.Message); }
        }

        /// <summary>Run the ribbon's "Login to AI" (BrowserLoginCommand) from
        /// non-command code — the pane's Sign-in card. PostCommand queues it on
        /// Revit's UI thread exactly as a click would. Composite id form for an
        /// add-in button: CustomCtrl_%CustomCtrl_%{tab}%{panel}%{buttonName}.
        /// False when the id cannot be resolved (ribbon not built yet).</summary>
        internal static bool PostLoginToAI()
        {
            try
            {
                var id = RevitCommandId.LookupCommandId("CustomCtrl_%CustomCtrl_%Bina%BINA AI%Login");
                if (id == null || UiApp == null || !UiApp.CanPostCommand(id)) return false;
                UiApp.PostCommand(id);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] PostLoginToAI failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Close Revit through its own Exit command once an update is
        /// staged (it applies at the next Revit start). Revit's exit, not a
        /// process kill: every open model still gets the usual save prompt and
        /// the drafter can cancel. False when it cannot be posted.</summary>
        internal static bool PostExitRevit()
        {
            try
            {
                var id = RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit);
                if (id == null || UiApp == null || !UiApp.CanPostCommand(id)) return false;
                UiApp.PostCommand(id);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] PostExitRevit failed: " + ex.Message);
                return false;
            }
        }

        // Tool execution handler + its ExternalEvent. CopilotToolHost's read
        // executor and write runner enqueue McpJobs; McpJobPump drains them on
        // Revit's UI thread (busy/modal fast-fail watchdog).
        public static BinaVibe.Mcp.McpExternalEventHandler McpToolHandler { get; private set; }
        public static Autodesk.Revit.UI.ExternalEvent McpToolEvent { get; private set; }

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

        /// <summary>Run one subsystem's startup hook; a failure is logged and
        /// reported, never fatal to the add-in.</summary>
        private static void AttachSubsystem(string name, Action attach)
        {
            try { attach(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] {name} failed to attach: {ex.Message}");
                Services.TelemetryService.Track("subsystem", "failed",
                    new { name, error_class = ex.GetType().Name });
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

                // OTA FIRST, in its own try/catch (self-heal F4): whatever breaks
                // later in this method, the updater is already running, so the
                // fix can always arrive over the air. Start also primes the
                // forced-update gate from memory before any service below starts.
                try { Services.UpdateService.Start(application); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] update service failed to start: {ex.Message}");
                    Services.TelemetryService.Track("update", "start_failed",
                        new { error_class = ex.GetType().Name });
                }

                // Forced-update gate from the remembered feed answer, before any
                // pane, tunnel, engine or indexer starts: a machine that must
                // update is locked from the first moment, not once the feed answers.
                try { Services.UpdateService.PrimeGateFromMemory(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] update gate priming failed: {ex.Message}");
                }

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
                    if (UiApp == null && s is UIApplication __ua)
                    {
                        UiApp = __ua;
                        // Fallback only: ApplicationInitialized (below) normally
                        // attached it already. Kept for a load after Revit's
                        // initialisation, where that event never comes; Attach
                        // ignores a second call.
                        AttachSubsystem("copilot_external_revision",
                            () => BinaVibe.Copilot.Guard.ExternalRevision.Attach(__ua.Application));
                        // Seed the pane state: Revit may start with the copilot
                        // pane hidden (no visibility event ever comes) or restore
                        // it shown from the last session.
                        AttachSubsystem("copilot_pane_open",
                            () => CopilotPaneOpen.Report(__ua.GetDockablePane(CopilotPaneHost.PaneId).IsShown()));
                    }
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
                    // The warm-up edits the model, so it waits out a forced update.
                    if (_pendingWarmDoc != null && !Services.UpdateGate.IsBlocked)
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

                JkrRenameHandler = new JkrRenameHandler();
                JkrRenameEvent = ExternalEvent.Create(JkrRenameHandler);

                // Opening and closing documents is Revit API work that begins in a
                // download continuation, so it has to be marshalled (86d3ut47q).
                RollbackSwapHandler = new RevitWebAppSync.Handlers.RollbackSwapHandler();
                RollbackSwapEvent = ExternalEvent.Create(RollbackSwapHandler);

                BombaPickHandler = new RevitWebAppSync.Services.BombaPickWriteHandler();
                BombaPickEvent = ExternalEvent.Create(BombaPickHandler);

                BombaScopeHandler = new RevitWebAppSync.Services.BombaScopeWriteHandler();
                BombaScopeEvent = ExternalEvent.Create(BombaScopeHandler);

                BombaAutoFixHandler = new RevitWebAppSync.Services.BombaAutoFixHandler();
                BombaAutoFixEvent = ExternalEvent.Create(BombaAutoFixHandler);

                // Tool execution handler. The copilot's read executor and write
                // runner enqueue McpJobs via McpJobPump, which drains them on the
                // Revit UI thread from the Idling event (forcing continuous idling
                // so they run promptly even from the modeless pane), and fast-fails
                // if Revit is busy or has a modal dialog open.
                McpToolHandler = new BinaVibe.Mcp.McpExternalEventHandler();
                McpToolEvent = ExternalEvent.Create(McpToolHandler);
                // Idling-driven pump (kills the "Revit not idle → 600s freeze" bug,
                // see docs/superpowers/specs/2026-06-30-revit-tool-execution-reliability-design.md).
                BinaVibe.Mcp.McpJobPump.Init(application, McpToolHandler, McpToolEvent);
                // A Revit modal dialog blocks idling entirely — note it so a
                // fast-fail tells the user to close the dialog.
                application.DialogBoxShowing += (s, e) => BinaVibe.Mcp.McpJobPump.NoteDialogShown();

                // Lean-harness copilot (spec §7.4, §7.6, §7.9): the context
                // snapshot kept current by Revit events (W2), the modelled
                // per-document Undo stack (W3) and the external-revision
                // counter the preview/commit check reads (W1). Each one is
                // guarded on its own: a failure costs that subsystem only.
                AttachSubsystem("copilot_snapshot", () => BinaVibe.Copilot.Read.SnapshotService.Attach(application));
                AttachSubsystem("copilot_undo", () => BinaVibe.Copilot.Undo.UndoService.Attach(application));
                // ExternalRevision.Attach takes the ApplicationServices.Application,
                // which OnStartup does not have (only the ControlledApplication).
                // ApplicationInitialized hands it over as the sender, after Revit
                // has started and before a document opens, so the counter attaches
                // there. The first Idling (UiApp capture above) is the fallback.
                AttachSubsystem("copilot_external_revision_hook", () =>
                    application.ControlledApplication.ApplicationInitialized += (s, e) =>
                    {
                        if (s is Autodesk.Revit.ApplicationServices.Application revitApp)
                            AttachSubsystem("copilot_external_revision",
                                () => BinaVibe.Copilot.Guard.ExternalRevision.Attach(revitApp));
                    });

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

                // Register Bina AI Copilot dockable pane
                try
                {
                    CopilotPaneHost = new CopilotPaneHost();
                    application.RegisterDockablePane(
                        CopilotPaneHost.PaneId,
                        "BINA AI Copilot",
                        CopilotPaneHost);
                    // Shown or hidden from Revit's side (the pane's X, the ribbon
                    // button, a floating window closed). The panel's IsVisible
                    // reports too; PaneOpenState drops the repeats.
                    AttachSubsystem("copilot_pane_visibility", () =>
                        application.DockableFrameVisibilityChanged += (s, e) =>
                        {
                            if (e.PaneId != null && e.PaneId.Guid == CopilotPaneHost.PaneId.Guid)
                                CopilotPaneOpen.Report(e.DockableFrameShown);
                        });
                }
                catch (Exception copilotEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Copilot dockable pane registration failed: {copilotEx.Message}");
                    Services.TelemetryService.Track("subsystem", "failed",
                        new { name = "copilot_pane", error_class = copilotEx.GetType().Name });
                }

                // Register the Issues dockable pane
                try
                {
                    IssuesPaneHost = new UI.Issues.IssuesPaneHost();
                    application.RegisterDockablePane(
                        UI.Issues.IssuesPaneHost.PaneId,
                        "BINA Issues",
                        IssuesPaneHost);

                    IssueShowHandler = new Handlers.IssueShowHandler
                    {
                        OnCompleted = (issue, result, error) =>
                            IssuesPaneHost?.Panel?.ReportShown(issue, result, error)
                    };
                    IssueShowEvent = ExternalEvent.Create(IssueShowHandler);
                }
                catch (Exception issuesEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Issues dockable pane registration failed: {issuesEx.Message}");
                    Services.TelemetryService.Track("subsystem", "failed",
                        new { name = "issues_pane", error_class = issuesEx.GetType().Name });
                }

                // Register the CAD to BIM dockable pane. Handler + ExternalEvent
                // first: ExternalEvent.Create is only legal here, and the pane's
                // view model wants both when the host constructs it.
                try
                {
                    CadToBimBuildHandler = new CadToBimBuildHandler();
                    CadToBimBuildEvent = ExternalEvent.Create(CadToBimBuildHandler);

                    CadToBimPaneHost = new CadToBimPaneHost();
                    application.RegisterDockablePane(
                        CadToBimPaneHost.PaneId,
                        "BINA CAD to BIM",
                        CadToBimPaneHost);
                }
                catch (Exception cadEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] CAD to BIM dockable pane registration failed: {cadEx.Message}");
                    Services.TelemetryService.Track("subsystem", "failed",
                        new { name = "cad_to_bim_pane", error_class = cadEx.GetType().Name });
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

                CreateRibbonTab(application);

                // Delete manifests of old parallel installs (pre-loader
                // direct-load .addin in APPDATA/ProgramData) so the next
                // start runs a single copy. See LegacyInstallCleaner.
                try { Services.LegacyInstallCleaner.Purge(application); } catch { }

                // (OTA started at the top of this method — see F4 note there.)

                Services.TelemetryService.Track("startup", "ready");

                // Crash guard: this build started cleanly on this machine, so
                // BinaLoader must never mark it bad (versions\<ver>\.healthy).
                Services.UpdateService.MarkRunningBuildHealthy();
                Services.TelemetryService.Track("startup", "healthy");
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

            try { Services.UpdateService.Stop(); } catch { }

            // CAD to BIM: drop the external event, its handler and the pane so
            // nothing can raise a build into an unloading add-in.
            try { CadToBimBuildEvent?.Dispose(); } catch { }
            CadToBimBuildEvent = null;
            CadToBimBuildHandler = null;
            CadToBimPaneHost = null;

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
            // CAD to BIM is neither backend: it runs entirely inside Revit, so it
            // gets its own panel rather than sitting under a sign-in it does not need.
            RibbonPanel cadPanel = application.CreateRibbonPanel(tabName, "CAD to BIM");
            RibbonPanel compliancePanel = application.CreateRibbonPanel(tabName, "Compliance");
            // Last on the tab, where Revit's own tabs keep help and about.
            RibbonPanel aboutPanel = application.CreateRibbonPanel(tabName, "BINA Sync");

            PushButtonData buttonData = new PushButtonData(
                "SyncToWebApp",
                "Sync",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.SyncCommand")
            {
                ToolTip = "Open BINA Cloud",
                LongDescription = "Opens BINA Cloud in your default browser.",
                Image = LoadIcon("Sync", 16),
                LargeImage = LoadIcon("Sync", 32)
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
                Image = LoadIcon("LoginAi", 16),
                LargeImage = LoadIcon("LoginAi", 32)
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
                LongDescription = "Signs in to BINA Cloud Docs with your email and password. Required by Sync and Download Model — the other buttons on this panel. Separate from Login to AI, which Copilot, JKR and space planning use.",
                Image = LoadIcon("LoginCde", 16),
                LargeImage = LoadIcon("LoginCde", 32),
                // Signing in needs no open model, so the button also works from
                // the zero-document ribbon (back arrow on the Home screen).
                AvailabilityClassName = typeof(ZeroDocCommandAvailability).FullName
            };

            // "Shared Download" retired: Download Model browses the Shared area
            // itself now, with role filtering and version choice, where this pulled
            // every discipline's latest file in one go off latest-shared-urls.
            // BimDisciplineCommand stays in the tree — nothing calls it, but a bulk
            // pull is a reasonable thing to want back, and re-adding a button is
            // cheaper than rewriting the command.

            // Replaces the old "Roll Back Version" button, which could only show
            // the history of the model already open. Browsing starts from the
            // project's WIP folders instead, so a drafter can pull any version of
            // any model their role reaches — including ones they have never
            // opened. RollbackCommand is left in the tree, unreferenced, so
            // restoring in place can be rewired without rewriting it.
            PushButtonData downloadModelButtonData = new PushButtonData(
                "DownloadModelVersion",
                "Download\nModel",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.DownloadModelCommand")
            {
                ToolTip = "Download a version of any model in this project's WIP area",
                LongDescription = "Browse the project's WIP folders, pick a model and a version, and save it " +
                    "to your machine. Only the folders and models your BINA role gives you access to are listed.",
                Image = LoadIcon("DownloadModel", 16),
                LargeImage = LoadIcon("DownloadModel", 32),
                // Browses the server and saves to disk — it OPENS a model rather
                // than needing one, so it also works from the zero-document
                // ribbon (back arrow on the Home screen).
                AvailabilityClassName = typeof(ZeroDocCommandAvailability).FullName
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
                Image = LoadIcon("SyncParameters", 16),
                LargeImage = LoadIcon("SyncParameters", 32)
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
                Image = LoadIcon("Issues", 16),
                LargeImage = LoadIcon("Issues", 32)
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
                Image = LoadIcon("AiAssistant", 16),
                LargeImage = LoadIcon("AiAssistant", 32)
            };

            // CAD to BIM: one DWG in, native walls / doors / windows / rooms out,
            // with a preview the drafter corrects first. Zero-doc availability:
            // "Save as new .rvt" needs no open project.
            PushButtonData cadToBimButtonData = new PushButtonData(
                "CadToBim",
                "CAD to\nBIM",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.OpenCadToBimCommand")
            {
                ToolTip = "Build Revit walls, doors, windows and rooms from a DWG",
                LongDescription = "Reads a 2D floor-plan DWG or DXF, shows what it found so you can erase or brush walls, then builds native Revit elements into this project or into a new .rvt saved beside the drawing.",
                Image = LoadIcon("CadToBim", 16),
                LargeImage = LoadIcon("CadToBim", 32),
                AvailabilityClassName = typeof(ZeroDocCommandAvailability).FullName
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
                Image = LoadIcon("CostTracker", 16),
                LargeImage = LoadIcon("CostTracker", 32)
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
                Image = LoadIcon("JkrCompliance", 16),
                LargeImage = LoadIcon("JkrCompliance", 32)
            };

            PushButtonData bombaComplianceButtonData = new PushButtonData(
                "BombaCompliance",
                "Bomba\nCompliance",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.BombaComplianceDashboardCommand")
            {
                ToolTip = "Check Bomba fire-safety compliance (UBBL)",
                LongDescription = "Check the model against UBBL fire-safety requirements — exit width, travel distance, fire systems. Reads the model; changes nothing.",
                Image = LoadIcon("BombaCompliance", 16),
                LargeImage = LoadIcon("BombaCompliance", 32)
            };

            PushButtonData aboutButtonData = new PushButtonData(
                "About",
                "About",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.AboutCommand")
            {
                ToolTip = "BINA Sync version and updates",
                LongDescription = "Shows which version of BINA Sync is installed and checks for a newer one. An update found here is downloaded in the background and applies the next time Revit starts.",
                Image = LoadIcon("About", 16),
                LargeImage = LoadIcon("About", 32),
                // Reads no model, so it also works from the zero-document ribbon.
                AvailabilityClassName = typeof(ZeroDocCommandAvailability).FullName
            };

            // BINA CDE: the bina-be sign-in, then the two buttons that need it.
            // Login leads the panel because both of the others fail without it.
            cdePanel.AddItem(cloudLoginButtonData);
            cdePanel.AddItem(buttonData);
            cdePanel.AddItem(syncParametersButtonData);
            cdePanel.AddItem(syncIssuesButtonData);
            cdePanel.AddItem(downloadModelButtonData);

            // BINA AI: the bina-ai sign-in, then the copilot it unlocks.
            aiPanel.AddItem(loginButtonData);
            aiPanel.AddItem(askAiButtonData);
            cadPanel.AddItem(cadToBimButtonData);

            // Compliance ships as coming soon. All three commands are built and
            // the buttons are added rather than hidden, so the panel keeps its
            // width and nothing on the tab reflows on the release that turns
            // them on — the icon a drafter learns now is in the same place then.
            MarkComingSoon(compliancePanel.AddItem(jkrComplianceButtonData));
            MarkComingSoon(compliancePanel.AddItem(bombaComplianceButtonData));

            // Cost-to-BIM: Cost Tracker dashboard (restored standalone — see
            // commit 3903b7f which had stacked it with Fire Compliance and hid
            // both. We surface only the cost button; Fire stays hidden.)
            MarkComingSoon(compliancePanel.AddItem(costDashboardButtonData));

            aboutPanel.AddItem(aboutButtonData);

            // Stack: Export Cost Items / Import Prices (hidden as requested)
            // cdePanel.AddStackedItems(costExportButtonData, costImportButtonData);

            // Fire Compliance stays hidden (restored only if explicitly requested)
            // compliancePanel.AddItem(complianceButtonData);
            // cdePanel.AddItem(federateButtonData); // Hidden as requested
        }

        // Holds a built command on the tab without letting it run yet.
        //
        // Revit has no "coming soon" badge on a ribbon button, so the state is
        // carried the way Revit already carries it: the button is disabled,
        // which greys the icon and its label through the host's own disabled
        // rendering. That is deliberately not a second set of dimmed PNGs — a
        // pre-dimmed icon would be greyed twice and end up barely visible, and
        // it would stay grey on the release that enables the button.
        //
        // The tooltip is what actually says the words, since a disabled ribbon
        // button still shows one on hover and it is the only surface here that
        // can hold a sentence.
        private static void MarkComingSoon(RibbonItem item)
        {
            PushButton button = item as PushButton;
            if (button == null)
                return;

            button.Enabled = false;
            button.ToolTip = "Coming soon — " + button.ToolTip;
            button.LongDescription = "Not available in this release. " + button.LongDescription;
        }

        // Ribbon icons live in Resources\Icons as one drawing per size, not one
        // scaled: the 16 is redrawn at 1.3px with detail dropped, the 32/64 are
        // stroked at 1.8px (see Resources\Icons\svg and Resources\Icons\README.md).
        // Image takes the 16 and LargeImage the 32, which is the size the Revit
        // ribbon slot is defined at. A 64 is embedded too but deliberately not
        // bound: AdWindows does not constrain the large slot in every button
        // template, so an oversized BitmapImage can render at its natural size
        // and blow the button out. Using it needs a DPI check on a real Revit.
        private BitmapImage LoadIcon(string name, int size)
        {
            return LoadImage("RevitWebAppSync.Resources.Icons." + name + size + ".png", size);
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

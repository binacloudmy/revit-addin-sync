# BINA Copilot 1:1 Redesign — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close every gap between the current WPF Copilot and the target design (`docs/design/copilot-panel-slate.dc.html`), 1:1 — spec: `docs/superpowers/specs/2026-07-04-copilot-redesign-design.md`.

**Architecture:** The WPF port already matches the design's palette, header, tabs, empty state, AI plain-text answers, and composer core. This plan implements the missing/partial surfaces: usage/plan system (live footer meter → popover → upgrade carousel → blocked state), single-line thinking indicator, user-bubble clamp + timestamps, micro-feedback panel, command-card restyle, sheet upgrades, mention popover, history header, token fixes, and harness screenshots. All view work follows the existing pattern: code-behind rendering with `CopilotColors.From(hex)` / `Cp.*` tokens, **no WPF Storyboards** (they crash Revit dockable panes — animate with `DispatcherTimer`/`CompositionTarget` like `ThinkingTrailView` does).

**Tech Stack:** C# / WPF (net8.0-windows multi-TFM addin), xunit (`Tests/Tests.csproj`, source-links pure-logic files), UiHarness for Revit-free runs + `--shot` screenshots.

## Global Constraints

- Branch: `feat/copilot-redesign` (never commit to develop).
- NO WPF Storyboards anywhere in the Copilot pane (Revit crash). Use `DispatcherTimer` / `CompositionTarget.Rendering`.
- No `v2`-suffixed files/folders — replace existing code in place.
- Pack URIs must include `{asm};component` (Revit CLR quirk).
- New pure-logic classes go under `UI/Copilot/Model/` or `Services/` with **no Revit/WPF type references** so `Tests.csproj` can source-link them.
- Build check on macOS: `dotnet build RevitWebAppSync.csproj -p:EnableWindowsTargeting=true` (official SDK, not homebrew). Tests: `dotnet test Tests/Tests.csproj` (runs on Windows; on macOS expect the known `DocumentChangedIndexer` compile failure — filter or run on Windows).
- Copy strings must match the design **verbatim** (they are listed in each task).
- Design reference: `docs/design/copilot-panel-slate.dc.html` (markup) — readable extracted copies in `docs/superpowers/specs/assets/copilot-design/`.

---

### Task 1: Version/context strings (`CopilotContext`)

Feedback surfaces need "Auto-attached · Copilot {ver} · Revit {ver}" rows. Nothing like this exists.

**Files:**
- Create: `UI/Copilot/Model/CopilotContext.cs`
- Test: `Tests/CopilotContextTests.cs`
- Modify: `Tests/Tests.csproj` (add source link)

**Interfaces:**
- Produces: `static class CopilotContext` with `string AddinVersion` (e.g. `"Copilot 2.4.1"`), `string RevitVersion` (settable, default `"Revit"`), `string ContextLabel(string commandName = null)` → `"Auto-attached · {command} · {AddinVersion} · {RevitVersion}"` (command segment omitted when null/empty), `string ShortLabel` → `"{AddinVersion} · {RevitVersion}"`.

- [ ] **Step 1: Write the failing test** — `Tests/CopilotContextTests.cs`:

```csharp
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

public class CopilotContextTests
{
    [Fact]
    public void ContextLabel_WithCommand_JoinsAllSegments()
    {
        CopilotContext.RevitVersion = "Revit 2024.2";
        var s = CopilotContext.ContextLabel("Create Walls");
        Assert.StartsWith("Auto-attached · Create Walls · Copilot ", s);
        Assert.EndsWith(" · Revit 2024.2", s);
    }

    [Fact]
    public void ContextLabel_NoCommand_OmitsSegment()
    {
        CopilotContext.RevitVersion = "Revit 2024.2";
        var s = CopilotContext.ContextLabel();
        Assert.StartsWith("Auto-attached · Copilot ", s);
        Assert.DoesNotContain("· ·", s);
    }

    [Fact]
    public void ShortLabel_HasBothVersions()
    {
        CopilotContext.RevitVersion = "Revit 2024.2";
        Assert.Contains("Copilot ", CopilotContext.ShortLabel);
        Assert.Contains("Revit 2024.2", CopilotContext.ShortLabel);
    }
}
```

Add to `Tests/Tests.csproj` ItemGroup: `<Compile Include="..\UI\Copilot\Model\CopilotContext.cs" Link="CopilotContext.cs" />`

- [ ] **Step 2: Run test to verify it fails** — `dotnet test Tests/Tests.csproj --filter CopilotContextTests` → FAIL (type not found).

- [ ] **Step 3: Implement** — `UI/Copilot/Model/CopilotContext.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Version strings auto-attached to feedback payloads and context rows.</summary>
    public static class CopilotContext
    {
        /// <summary>"Copilot {assembly version, 3 parts}".</summary>
        public static string AddinVersion { get; } = "Copilot " +
            (typeof(CopilotContext).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

        /// <summary>Set from the Revit host at pane init ("Revit 2024.2"); default for harness.</summary>
        public static string RevitVersion { get; set; } = "Revit";

        public static string ShortLabel => AddinVersion + " · " + RevitVersion;

        public static string ContextLabel(string commandName = null)
        {
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(commandName)) bits.Add(commandName);
            bits.Add(AddinVersion);
            bits.Add(RevitVersion);
            return "Auto-attached · " + string.Join(" · ", bits);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes** — same command → PASS.

- [ ] **Step 5: Set RevitVersion from host** — in `UI/Copilot/CopilotPaneHost.cs`, where the Revit `UIApplication`/`Application` is available (the class already receives Revit context to build the pane), add:

```csharp
CopilotContext.RevitVersion = "Revit " + app.VersionNumber; // app = Autodesk.Revit.ApplicationServices.Application
```

(Adapt to the actual variable in scope; `VersionNumber` returns e.g. "2024".)

- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat(copilot): CopilotContext version strings for feedback context rows"`

---

### Task 2: Usage service + meter ramp logic

**Files:**
- Create: `UI/Copilot/Model/UsageState.cs` (pure logic: state record + ramp)
- Create: `UI/Copilot/Services/IUsageService.cs`
- Create: `UI/Copilot/Services/StubUsageService.cs`
- Test: `Tests/UsageStateTests.cs`
- Modify: `Tests/Tests.csproj`

**Interfaces:**
- Produces:
  - `class UsageState { string PlanName; int Pct; bool AtLimit; bool IsAdmin; string AdminName; string AdminEmail; }` with `static UsageState FromCredits(bool unlimited, int used, int limit)` (unlimited → Pct 0, PlanName "Pro"; limit<=0 → Pct 0; else Pct = clamp(round(100*used/limit),0,100), AtLimit = Pct>=100) and `static string MeterColorKey(int pct)` → `"Cp.Accent"` (<80) / `"Cp.Amber"` (80–94) / `"Cp.Red"` (>=95).
  - `interface IUsageService { Task<UsageState> GetAsync(); Task NotifyAdminAsync(); }`
  - `class StubUsageService : IUsageService` — constructor `(string planName = "Free", int pct = 88, bool atLimit = false, bool isAdmin = true)`, `GetAsync` returns that state (AdminName "Sara Rahman", AdminEmail "sara@bina.cloud" — design copy), `NotifyAdminAsync` completes immediately.
- Consumes: nothing.

- [ ] **Step 1: Write failing tests** — `Tests/UsageStateTests.cs`:

```csharp
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

public class UsageStateTests
{
    [Theory]
    [InlineData(0, "Cp.Accent")]
    [InlineData(79, "Cp.Accent")]
    [InlineData(80, "Cp.Amber")]
    [InlineData(94, "Cp.Amber")]
    [InlineData(95, "Cp.Red")]
    [InlineData(100, "Cp.Red")]
    public void MeterColorKey_Ramp(int pct, string key) =>
        Assert.Equal(key, UsageState.MeterColorKey(pct));

    [Fact]
    public void FromCredits_Percentage()
    {
        var s = UsageState.FromCredits(false, 22, 25);
        Assert.Equal(88, s.Pct);
        Assert.False(s.AtLimit);
    }

    [Fact]
    public void FromCredits_AtLimit()
    {
        var s = UsageState.FromCredits(false, 30, 30);
        Assert.Equal(100, s.Pct);
        Assert.True(s.AtLimit);
    }

    [Fact]
    public void FromCredits_Unlimited_IsZeroPro()
    {
        var s = UsageState.FromCredits(true, 999, 0);
        Assert.Equal(0, s.Pct);
        Assert.Equal("Pro", s.PlanName);
        Assert.False(s.AtLimit);
    }
}
```

Tests.csproj link: `<Compile Include="..\UI\Copilot\Model\UsageState.cs" Link="UsageState.cs" />`

- [ ] **Step 2: Run to fail** — `dotnet test Tests/Tests.csproj --filter UsageStateTests` → FAIL.

- [ ] **Step 3: Implement `UsageState.cs`** (no WPF/Revit refs):

```csharp
using System;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Plan + usage snapshot driving the footer meter, popover and blocked state.</summary>
    public class UsageState
    {
        public string PlanName { get; set; } = "Free";
        public int Pct { get; set; }
        public bool AtLimit { get; set; }
        public bool IsAdmin { get; set; } = true;
        public string AdminName { get; set; } = "Sara Rahman";
        public string AdminEmail { get; set; } = "sara@bina.cloud";

        /// <summary>Design ramp: accent &lt;80, amber 80–94, red ≥95.</summary>
        public static string MeterColorKey(int pct) =>
            pct >= 95 ? "Cp.Red" : pct >= 80 ? "Cp.Amber" : "Cp.Accent";

        public static UsageState FromCredits(bool unlimited, int used, int limit)
        {
            if (unlimited) return new UsageState { PlanName = "Pro", Pct = 0, AtLimit = false };
            var pct = limit <= 0 ? 0 :
                Math.Max(0, Math.Min(100, (int)Math.Round(100.0 * used / limit)));
            return new UsageState { PlanName = "Free", Pct = pct, AtLimit = pct >= 100 };
        }
    }
}
```

- [ ] **Step 4: Run to pass.**

- [ ] **Step 5: Service interface + stub** — `UI/Copilot/Services/IUsageService.cs`:

```csharp
using System.Threading.Tasks;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Services
{
    public interface IUsageService
    {
        Task<UsageState> GetAsync();
        /// <summary>Member-plan "Notify admin to upgrade". Stub: no-op.</summary>
        Task NotifyAdminAsync();
    }
}
```

`UI/Copilot/Services/StubUsageService.cs`:

```csharp
using System.Threading.Tasks;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Services
{
    /// <summary>Configurable stand-in until billing lands. Swap via CopilotViewModel.UsageService.</summary>
    public class StubUsageService : IUsageService
    {
        private readonly UsageState _state;
        public StubUsageService(string planName = "Free", int pct = 88, bool atLimit = false, bool isAdmin = true)
            => _state = new UsageState { PlanName = planName, Pct = pct, AtLimit = atLimit, IsAdmin = isAdmin };
        public Task<UsageState> GetAsync() => Task.FromResult(_state);
        public Task NotifyAdminAsync() => Task.CompletedTask;
    }
}
```

- [ ] **Step 6: Credits adapter in the VM** — in `UI/Copilot/CopilotViewModel.cs`, next to `RefreshCreditBadgeAsync` (~line 724): add

```csharp
public IUsageService UsageService { get; set; }   // null → credits-backed default below

private UsageState _usage = new UsageState();
public UsageState Usage
{
    get => _usage;
    private set { _usage = value ?? new UsageState(); Raise(nameof(Usage)); UsageChanged?.Invoke(); }
}
public event Action UsageChanged;

public async Task RefreshUsageAsync()
{
    try
    {
        if (UsageService != null) { Usage = await UsageService.GetAsync(); return; }
        var c = await AIService.GetCreditsAsync();               // existing call used by the credit badge
        if (c != null) Usage = UsageState.FromCredits(c.Unlimited, c.Used, c.Limit);
    }
    catch { /* meter is best-effort; never block chat */ }
}
```

(Match `Raise` to this file's actual `OnPropertyChanged` helper name, and `c.Unlimited/Used/Limit` to `CreditInfo`'s real property names — see `ShowCreditsAsync`.) Call `await RefreshUsageAsync()` at both existing `RefreshCreditBadgeAsync` call sites (login + per-prompt), alongside the old call for now (Task 3 removes the badge).

- [ ] **Step 7: Build** — `dotnet build RevitWebAppSync.csproj -p:EnableWindowsTargeting=true` → succeeds.

- [ ] **Step 8: Commit** — `git commit -am "feat(copilot): usage state + service abstraction with credits adapter and stub"`

---

### Task 3: Live footer meter + usage popover; remove header credit pill

**Files:**
- Modify: `UI/Copilot/Controls/PromptBar.xaml:76-98` (static mock meter), `UI/Copilot/Controls/PromptBar.xaml.cs`
- Modify: `UI/Copilot/CopilotPanel.xaml:65-73` (credit pill — delete)
- Modify: `UI/Copilot/CopilotViewModel.cs` (retire `CreditBadge` refresh path)

**Interfaces:**
- Consumes: `CopilotViewModel.Usage` / `UsageChanged` / `RefreshUsageAsync` (Task 2), `UsageState.MeterColorKey`.
- Produces: `PromptBar.UsageMeterClicked` event (panel subscribes in Task 4); meter renders `{PlanName} ▬▬▬ {Pct}%`.

- [ ] **Step 1: Delete the header credit pill** — remove the `CreditBadge` Border block at `CopilotPanel.xaml:65-73`. Keep the `CreditBadge` VM property (harmless) but delete its per-prompt refresh call if Task 2 replaced it; design has NO header usage chip.

- [ ] **Step 2: Bind the footer meter.** In `PromptBar.xaml` replace the static mock (lines 76-98) with named elements:

```xml
<!-- Footer usage meter: {plan} —bar— {pct}%  (design lines 529-535) -->
<Button x:Name="MeterBtn" Style="{StaticResource Cp.GhostButton}" Padding="4,9,4,3"
        HorizontalContentAlignment="Stretch" Visibility="Collapsed">
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    <TextBlock x:Name="MeterPlan" FontSize="10.5" FontWeight="SemiBold"
               Foreground="{DynamicResource Cp.Muted}" VerticalAlignment="Center"/>
    <Border Grid.Column="1" Height="4" CornerRadius="99" Margin="9,0"
            Background="{DynamicResource Cp.Line}" VerticalAlignment="Center">
      <Border x:Name="MeterFill" Height="4" CornerRadius="99" HorizontalAlignment="Left"/>
    </Border>
    <TextBlock x:Name="MeterPct" Grid.Column="2" FontSize="10.5" FontWeight="Bold"
               Foreground="{DynamicResource Cp.Muted}" VerticalAlignment="Center"/>
  </Grid>
</Button>
```

(If `Cp.GhostButton` doesn't exist in `CopilotStyles.xaml`, use the flat-button style the composer's @-button already uses.)

- [ ] **Step 3: Wire it in `PromptBar.xaml.cs`:**

```csharp
public event Action UsageMeterClicked;

public void BindUsage(CopilotViewModel vm)
{
    void Render()
    {
        var u = vm.Usage;
        var show = u != null && (u.Pct > 0 || u.AtLimit || u.PlanName != "Pro");
        MeterBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        MeterPlan.Text = u.PlanName;
        MeterPct.Text = u.Pct + "%";
        MeterFill.Background = CopilotTheme.Brush(UsageState.MeterColorKey(u.Pct));
        void SizeFill()
        {
            var track = (FrameworkElement)MeterFill.Parent;
            MeterFill.Width = track.ActualWidth * u.Pct / 100.0;
        }
        Dispatcher.BeginInvoke((Action)SizeFill, System.Windows.Threading.DispatcherPriority.Loaded);
        ((FrameworkElement)MeterFill.Parent).SizeChanged -= OnTrack; 
        ((FrameworkElement)MeterFill.Parent).SizeChanged += OnTrack;
        void OnTrack(object s, SizeChangedEventArgs e) => SizeFill();
    }
    vm.UsageChanged += () => Dispatcher.BeginInvoke((Action)Render);
    MeterBtn.Click += (s, e) => UsageMeterClicked?.Invoke();
    Render();
}
```

Call `BindUsage(_vm)` where PromptBar receives the VM today (see its existing init in `ChatView.xaml.cs`/`CopilotPanel.xaml.cs`).

- [ ] **Step 4: Usage popover.** In `CopilotPanel.xaml`, add a `Popup` anchored to the composer area (reuse the kebab-popup pattern at lines 91-119): 15px padding, `Cp.Menu` bg, `Cp.Line` border, radius 13, containing — plan name (13px/680), "Plan usage" (10.5px `Cp.Faint`), row `Usage … {pct}% used`, 6px track+fill bar (same ramp brush), and full-width 32px `⚡ Upgrade plan` button with `Cp.AccentGrad` background / `Cp.AccentContrast` text. Open it from `PromptBar.UsageMeterClicked`. The Upgrade button raises `UpgradeRequested` handled in Task 4 (for now it can close the popover).

- [ ] **Step 5: Run harness** (Windows) or build (mac): meter shows real percentage after login; ramp colors flip at 80/95 (verify by temporarily assigning `vm.UsageService = new StubUsageService(pct: 97)` in `UiHarness/LauncherWindow.xaml.cs`, then remove).

- [ ] **Step 6: Commit** — `git commit -am "feat(copilot): live footer usage meter + popover, drop header credit pill"`

---

### Task 4: Upgrade bottom sheet with peek carousel

**Files:**
- Create: `UI/Copilot/Controls/UpgradeSheet.cs` (code-behind-built, like `BuildRateSheet`)
- Modify: `UI/Copilot/CopilotPanel.xaml.cs` (sheet host wiring)

**Interfaces:**
- Consumes: `SheetHost`/scrim show-hide helpers used by `BuildRateSheet` (`CopilotPanel.xaml.cs:359-418`), `CopilotTheme.Brush`, `CopilotColors.From`.
- Produces: `static FrameworkElement UpgradeSheet.Build(Action close)` and `CopilotPanel.ShowUpgradeSheet()` (public — used by popover, blocked state, and HarnessShots).

Design (lines 357-408): title "Choose your plan" + "Swipe to compare" + ×; carousel of 3 cards (width = 82% of viewport, gap 12, active scale 1.0/opacity 1, sides scale .9/opacity .45, translate animated 320ms ease); plans:
- Free — $0/month, "WHAT'S INCLUDED": Limited usage / Core Revit commands / Chat history. CTA outline "Get started".
- Basic — $20/month, 1.5px accent border + "RECOMMENDED" gradient pill, features: 10× higher usage limit / Faster responses / Full Revit command library / Chat history & exports / Email support. CTA solid "Upgrade to Basic ↗".
- Pro — $40/month, "EVERYTHING IN BASIC, PLUS": Everything in Basic / 5× higher usage limit / Priority responses / Batch commands & automation / Priority support. CTA solid "Upgrade to Pro ↗".
Inactive card CTA = sunken/disabled look. Controls row: ‹ arrow, 3 dots (active = 18px pill, accent), › arrow. Footer link "See all plans".

- [ ] **Step 1: Build the sheet UI** in `UpgradeSheet.cs`. Structure: outer `Border` (Cp.Menu bg, top-corner radius 18, top border Cp.Hair2, padding 15) in the existing `SheetHost`; header `Grid` (title stack + close button); a `Canvas`-clipped carousel viewport `Grid` hosting a horizontal `StackPanel` track of 3 card `Border`s; controls row; link. Card content per plan from a small `PlanDef` array (name, price, incLabel, features[], ctaLabel, solid/outline, recommended).

Carousel motion — NO storyboards. Keep `double _target` (track X for active index, centered: `viewportW/2 - cardW/2 - idx*(cardW+12)`), animate with a `DispatcherTimer` at ~60fps lerping `TranslateTransform.X` toward `_target` (factor 0.25/frame, snap under 0.5px), and same-lerp each card's `ScaleTransform`/`Opacity` toward active/inactive values. Drag: `PointerPressed`→`CaptureMouse` on the viewport, `MouseMove` offsets X, `MouseUp` commits `±1` index when `|dx| > viewportW*0.16`.

- [ ] **Step 2: CTA actions** — active solid CTA / "Get started": `Process.Start(new ProcessStartInfo("https://billing.bina.cloud/upgrade") { UseShellExecute = true });` "See all plans" → `https://bina.cloud/pricing`. Wrap in try/catch.

- [ ] **Step 3: Panel wiring** — in `CopilotPanel.xaml.cs` add:

```csharp
public void ShowUpgradeSheet() => ShowSheet(UpgradeSheet.Build(CloseSheet));
```

(match the actual helper names used by `BuildRateSheet` — the scrim/slide-in plumbing at lines 188-191 + 239-266). Hook the usage popover's Upgrade button → `ShowUpgradeSheet()`.

- [ ] **Step 4: Manual verify** (harness on Windows): sheet slides up, drag + arrows + dots move cards with peek effect, links open browser. On mac: build only.

- [ ] **Step 5: Commit** — `git commit -am "feat(copilot): upgrade plan sheet with peek carousel (Free/Basic/Pro)"`

---

### Task 5: Limit-reached blocked state

**Files:**
- Create: `UI/Copilot/Controls/BlockedView.cs`
- Modify: `UI/Copilot/Screens/ChatView.xaml.cs` (composer swap), `UI/Copilot/CopilotViewModel.cs`

**Interfaces:**
- Consumes: `CopilotViewModel.Usage` (Task 2), `CopilotPanel.ShowUpgradeSheet()` (Task 4), `IUsageService.NotifyAdminAsync()`.
- Produces: `static FrameworkElement BlockedView.Build(UsageState u, Action openUpgrade, Func<Task> notifyAdmin)`.

Design (lines 305-354): 68px padlock SVG (recreate as WPF `Path`s with the design's exact gradients: shackle `#9CC6FF→#4F8EF0`, body `#7CB4FF→#4A8BF0→#2766D6`, white sheen, key `#FFF→#DBE9FF`, drop shadow), "You've reached your usage limit" 14px/680, "Upgrade your plan to keep using Copilot." 12px `Cp.Muted`. Admin → full-width 38px `⚡ Upgrade plan` gradient CTA. Member → "Your plan is managed by your workspace admin." + sunken admin card (30px circle avatar with initials on `Cp.AccentGrad`, name 12px/640, "Workspace admin · {email}" 10.5px faint) + `🔔 Notify admin to upgrade` CTA → replaced by confirmation card: "**Request sent.** {FirstName} has been asked to upgrade your plan."

- [ ] **Step 1: Build `BlockedView.Build(...)`** with the layout above. Initials = first letters of the admin name's first two words. Notify button: on click, disable, `await notifyAdmin()`, swap CTA for the confirmation card.

- [ ] **Step 2: Composer swap.** In `ChatView.xaml.cs` where the composer (`PromptBar`) is added to the layout: if `vm.Usage.AtLimit && !vm.IsSending`, render `BlockedView` instead of the PromptBar (design: centered fill when thread empty — wrap in a vertically-centered container when `Thread.Count == 0`, else a bottom section above a top hairline). Re-evaluate on `vm.UsageChanged` and after each send completes.

- [ ] **Step 3: Manual verify** with `new StubUsageService(pct: 100, atLimit: true, isAdmin: false)` in the harness: member card + notify flow; `isAdmin: true`: upgrade CTA opens the sheet. Remove the temporary stub line.

- [ ] **Step 4: Commit** — `git commit -am "feat(copilot): usage-limit blocked state (admin upgrade / member notify)"`

---

### Task 6: Friendly progress labels (pure logic)

**Files:**
- Create: `UI/Copilot/Model/FriendlyStep.cs`
- Test: `Tests/FriendlyStepTests.cs`
- Modify: `Tests/Tests.csproj`

**Interfaces:**
- Produces: `static string FriendlyStep.Label(string raw)` — maps design keys, humanises unknown keys; `static bool FriendlyStep.IsDone(string raw)` not needed (states come separately).

Mapping (design `friendlyStep()`, verbatim): thinking→Thinking; understand, parse_request→Understanding your request; retrieve_context, search_model, read_model→Looking through the model; plan→Planning the approach; reason→Reasoning it through; generate, compose→Putting together a response; build_command→Preparing the command; validate, verify→Double-checking the result. Unknown: `snake_case`/`camelCase`/`kebab-case` → spaced, first letter capitalised.

- [ ] **Step 1: Failing tests** — `Tests/FriendlyStepTests.cs`:

```csharp
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

public class FriendlyStepTests
{
    [Theory]
    [InlineData("parse_request", "Understanding your request")]
    [InlineData("retrieve_context", "Looking through the model")]
    [InlineData("SEARCH_MODEL", "Looking through the model")]
    [InlineData("generate", "Putting together a response")]
    [InlineData("validate", "Double-checking the result")]
    [InlineData("thinking", "Thinking")]
    public void Label_MapsKnownKeys(string raw, string label) =>
        Assert.Equal(label, FriendlyStep.Label(raw));

    [Theory]
    [InlineData("optimize_layout", "Optimize layout")]
    [InlineData("resolveRefs", "Resolve refs")]
    [InlineData("warm-up", "Warm up")]
    public void Label_HumanisesUnknown(string raw, string label) =>
        Assert.Equal(label, FriendlyStep.Label(raw));

    [Fact]
    public void Label_Empty_ReturnsEmpty() => Assert.Equal("", FriendlyStep.Label(null));
}
```

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement:**

```csharp
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Backend progress keys → the design's friendly one-line labels.</summary>
    public static class FriendlyStep
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>
        {
            ["thinking"] = "Thinking",
            ["understand"] = "Understanding your request",
            ["parse_request"] = "Understanding your request",
            ["retrieve_context"] = "Looking through the model",
            ["search_model"] = "Looking through the model",
            ["read_model"] = "Looking through the model",
            ["plan"] = "Planning the approach",
            ["reason"] = "Reasoning it through",
            ["generate"] = "Putting together a response",
            ["compose"] = "Putting together a response",
            ["build_command"] = "Preparing the command",
            ["validate"] = "Double-checking the result",
            ["verify"] = "Double-checking the result",
        };

        public static string Label(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var key = Regex.Replace(raw.Trim().ToLowerInvariant(), @"[\s-]+", "_");
            if (Map.TryGetValue(key, out var mapped)) return mapped;
            var s = Regex.Replace(raw, "[_-]+", " ");
            s = Regex.Replace(s, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant().Trim();
            return s.Length == 0 ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
```

- [ ] **Step 4: Run to pass. Commit** — `git commit -am "feat(copilot): friendly progress-step label mapping"`

---

### Task 7: Single-line thinking indicator

**Files:**
- Modify: `UI/Copilot/Controls/ThinkingTrailView.cs` (replace stacked rows with one swapping line — same class name, no v2 file)
- Modify: `UI/Copilot/Screens/ChatView.xaml.cs:762-820` (persisted `ProgressTracePanel` — remove from new messages)

**Interfaces:**
- Consumes: `FriendlyStep.Label` (Task 6); existing trail-string feed (`ThinkingTrailView.Parse` consumers stay source-compatible).
- Produces: same public surface as today's `ThinkingTrailView` (constructor + the update method `ChatView` already calls), so call sites don't change.

Behavior (design lines 268-287 + logic 397-417): one row = 19px gradient star icon + 15px spinner (accent arc rotating, `DispatcherTimer`-driven like the current spinner at `ThinkingTrailView.cs:65-82`) + ONE label. Each new step **replaces** the label (fade/slide-in 340ms, manual opacity/translate ticks); working label shimmers (the view already shimmers the running row — reuse); on done: spinner → check with pop scale, label "Done", then the whole line fades (260ms) as the answer appears. The view must parse the existing trail string and show only the LAST line's text passed through `FriendlyStep.Label` (strip existing ✓/▶ markers exactly as `Parse` does today).

- [ ] **Step 1: Rework `ThinkingTrailView`** — delete `_rows` accumulation; keep star/spinner/shimmer machinery; render single `TextBlock`; on update, if label text changed → restart the swap-in animation; keep completed/failed terminal states (check/✗).
- [ ] **Step 2: Route labels through `FriendlyStep.Label`.**
- [ ] **Step 3: Stop persisting the multi-row trace** — in `ChatView.xaml.cs:762-820`, no `ProgressTracePanel` for newly-completed replies (design keeps only the answer). Leave the class in place for old history entries if they serialized traces; otherwise delete the dead code.
- [ ] **Step 4: Manual verify (harness/Windows):** send a prompt → one line, labels swap, check pops, line fades into answer. Build on mac otherwise.
- [ ] **Step 5: Commit** — `git commit -am "feat(copilot): single-line swapping thinking indicator"`

---

### Task 8: Interrupted line

**Files:**
- Modify: `UI/Copilot/RevitChatRouter.cs:299,374` (cancel replies), `UI/Copilot/Model/CopilotModels.cs` (message flag), `UI/Copilot/Screens/ChatView.xaml.cs` (render)

**Interfaces:**
- Produces: `ChatMessage.Interrupted` bool; renderer shows italic faint line.

- [ ] **Step 1:** Add `public bool Interrupted { get; set; }` to `ChatMessage` (`CopilotModels.cs`).
- [ ] **Step 2:** On cancel paths in `RevitChatRouter.cs` (299, 374), instead of the "Stopped — …" prose reply, emit a message with `Interrupted = true` and `Text = "Interrupted."` (keep any cleanup logic).
- [ ] **Step 3:** In `ChatView.xaml.cs` message loop: if `msg.Interrupted`, render left-aligned row: 13px stop-in-circle `Path` icon + italic 13px `Cp.Faint` text — no bubble, no feedback row.
- [ ] **Step 4: Commit** — `git commit -am "feat(copilot): italic Interrupted line on cancel"`

---

### Task 9: User bubble — timestamp + Show more clamp

**Files:**
- Modify: `UI/Copilot/Controls/CopilotMessageBubble.cs:23-74` (`User`)

**Interfaces:**
- Consumes: `ChatMessage.Time` if present (add `public string Time { get; set; }` set to `DateTime.Now.ToString("h:mm tt")` at send if missing).
- Produces: unchanged signature `CopilotMessageBubble.User(...)`.

- [ ] **Step 1: Timestamp** — wrap the bubble in a right-aligned `StackPanel`; under it add `TextBlock` 10px `Cp.Faint`, `Margin=0,4,0,0`, right-aligned, text = message time.
- [ ] **Step 2: Clamp** — measure the text element after layout (`Loaded` event → `ActualHeight`); if > 80px: set `MaxHeight = 80`, `ClipToBounds = true`, apply bottom fade `OpacityMask` (`LinearGradientBrush` vertical, `#FF000000` @ 0.58 → transparent @ 1) and append a `Show more` button (12px/650, bubble text color, chevron-down `Path`). Toggle: expand → `MaxHeight = double.PositiveInfinity`, mask removed, label `Show less` + chevron-up. No storyboards — instant toggle is acceptable (design animates height; skip animation rather than risk the pane).
- [ ] **Step 3: Manual verify:** short message → no toggle; paste 20 lines → clamp + fade + working toggle, time under bubble.
- [ ] **Step 4: Commit** — `git commit -am "feat(copilot): user bubble timestamp + Show more clamp with fade"`

---

### Task 10: Micro feedback row + downvote panel

**Files:**
- Modify: `UI/Copilot/Screens/ChatView.xaml.cs:333-396` (`BuildFeedback`)

**Interfaces:**
- Consumes: `CopilotViewModel.SubmitFeedback` (existing thumbs POST), `CopilotContext.ContextLabel` (Task 1).
- Produces: full design feedback block under every AI answer.

Design (lines 221-262). Row: `{time}` 10px faint · spacer · "Was this helpful?" 10.5px faint (hidden after any vote) · 27×27 icon buttons 👍 👎 ⧉copy. States: 👍 toggles accent tint (silent). 👎 → accent tint + panel (sunken card, radius 11): "What was off?" 11px/600 · wrap chips `Not accurate / Wrong elements / Too slow / Other` (11px, radius 7, hairline border; active = `Cp.BlueSoft` bg + accent text, single-select toggle) · 2-row note `TextBox` placeholder "Add details (optional)" · `Send feedback` gradient button + `Cancel` ghost · hairline-topped context row: paperclip `Path` + `CopilotContext.ContextLabel(commandName)` 9.5px faint ellipsized. Submit → panel closes, accent line "✓ Thanks — your feedback helps improve BINA." Copy: existing copy action inline; icon → green check for 1.6s (`DispatcherTimer`).

- [ ] **Step 1: Rebuild `BuildFeedback`** per above; wire 👍/👎 to the existing `SubmitFeedback` call, extended with reason + note when present (extend the payload object where `SubmitFeedback` builds it — add optional `string reason = null, string note = null` parameters passed through to `FeedbackService`).
- [ ] **Step 2: Move the hover-copy affordance** (`CopilotMessageBubble.cs:159-182`) into this row for AI answers (single inline copy button), removing the floating one for AI messages.
- [ ] **Step 3: Manual verify:** both votes toggle, downvote panel chips single-select, submit shows thanks, copy flashes green, context row shows real versions.
- [ ] **Step 4: Commit** — `git commit -am "feat(copilot): design micro-feedback row with downvote reasons + context"`

---

### Task 11: Command card restyle (Apply/Dismiss idiom)

**Files:**
- Modify: `UI/Copilot/Screens/ChatView.xaml.cs:485-553` (`ProposalCard`), `:578-617` (`CompactResult`), rating nudge `:255-323`

**Interfaces:**
- Consumes: existing run/dismiss handlers (`ChatRunCommand`, regenerate, editor), `CopilotContext`.
- Produces: design card visuals; run semantics unchanged.

Design (lines 186-218). Inside the AI message, below the text: top hairline (`Cp.Line`) + 12px top padding; header row = command name 12.5px/680 + status word 10px/600: `· Proposed` accent / `· Applied` green / `· Dismissed` faint; then key/value rows (11.5px: key faint left, value muted/550 right, baseline aligned, 5px vertical padding); Proposed → row of `✓ Apply to model` (gradient, radius 8, 11.5px/600, check `Path`) + `Dismiss` (borderless faint); Applied → `✓ Applied to the model` green 12px/600 row.

Mapping to current data: command name = tool title; key/value rows = the proposal's parameter summary (`FormValues`/plan fields where available; otherwise the existing plan-step lines rendered as rows with key = step index-free text — keep it lightweight, no invented data). `Apply to model` triggers the existing Run path; `Dismiss` marks the card dismissed (new status on the message model: `public string CardStatus` — "proposed"/"applied"/"dismissed"). Keep the "View code (N lines)" toggle BELOW the param rows (functional necessity; style: 11.5px faint ghost button — same idiom as Dismiss). `CompactResult` (post-run) adopts the `· Applied` header + green check line; keep Save/Copy/Undo chips styled as hairline ghost chips.

- [ ] **Step 1: Restyle `ProposalCard`** per above (remove bordered card + tile + TierBadge header; hairline-topped section instead).
- [ ] **Step 2: Status flow** — proposal renders `· Proposed`; after successful run → `· Applied` + green line (update the message and re-render — same mechanism `CompactResult` uses today); Dismiss → `· Dismissed`, action row removed.
- [ ] **Step 3: Rating nudge** — copy to design: gold star, text "How's Copilot doing?", link "Rate", × dismiss; show under the LAST `· Applied` card only, gated by existing `CopilotPrefs.RatingSubmitted`; use `Cp.*` tokens not hardcoded hex.
- [ ] **Step 4: Manual verify** (harness): propose → apply → applied state + nudge; dismiss path; view-code toggle still works.
- [ ] **Step 5: Commit** — `git commit -am "feat(copilot): command card Apply/Dismiss restyle + applied state + nudge copy"`

---

### Task 12: Rate + report sheets to design

**Files:**
- Modify: `UI/Copilot/CopilotPanel.xaml.cs:359-418` (`BuildRateSheet`), `:420-436` (`BuildReportSheet`)

**Interfaces:**
- Consumes: `CopilotContext.ShortLabel`/`ContextLabel`, `LocalFeedbackService.SubmitRating`/`ReportBug` (existing).
- Produces: design-faithful sheets.

- [ ] **Step 1: Rate sheet** (design lines 448-495): title "How's Copilot working for you?" 15.5px/720 + "Your rating helps us improve."; stars 32px `Path` (design star geometry `M12 2 3.1 6.3 …` — copy the SVG path data verbatim from `docs/design/copilot-panel-slate.dc.html` line 473), fill = `LinearGradientBrush` `#FFE07A→#FBB72B→#E8941A` when lit else transparent with `Cp.Hair2` stroke; hover scale 1.18 via `RenderTransform` on `MouseEnter`/`MouseLeave`; pick pop = `DispatcherTimer` scale 1→1.32→0.92→1 over 360ms; reaction label 13px/660 `#E0941A` (fixed height 18): `Not great / Could be better / It's okay / Pretty good / Love it!` by hover-or-value; note `TextBox` placeholder "What worked well, or what could be better?"; sunken context row (lightbulb icon + `CopilotContext.ShortLabel`); `Submit rating` 40px — disabled sunken until value ≥1, then gradient. Thanks state: "Thanks for the feedback" / "Your rating helps us make Copilot better." + Done.
- [ ] **Step 2: Report sheet** (lines 411-446): title "Report a bug"; "TYPE" caps label + chips `Bug / Suggestion / Other` (single-select; active `Cp.BlueSoft`+accent, idle hairline); "DETAILS" caps label + 4-row `TextBox` placeholder "Describe what happened or what you'd like to see…"; sunken context row (image icon + `CopilotContext.ContextLabel() + " · current view"`); `➤ Submit` 40px gradient. Thanks: "Thanks for letting us know" / "Your report was sent to the BINA team with the current model context attached." + Done. Pass chip type through to `ReportBug` payload.
- [ ] **Step 3: Manual verify** both sheets (harness `--shot` covers rate sheet already).
- [ ] **Step 4: Commit** — `git commit -am "feat(copilot): rate + report sheets to design (gold stars, reactions, type chips, context rows)"`

---

### Task 13: Mention popover + history header + composer polish

**Files:**
- Modify: `UI/Copilot/Controls/MentionInput.xaml.cs:121-157`, `UI/Copilot/Screens/HistoryView.xaml` + `.xaml.cs:216-311`, `UI/Copilot/Controls/PromptBar.xaml`

- [ ] **Step 1: Mention popover** (design lines 502-512): single "REFERENCE" caps header (10px/600 faint), flat rows: 22×22 sunken tile with accent `@` 12px/700 + label 12.5px/550 + right-aligned type 10px faint (`Level`/`Category`/`View`/`Selection`); drop the per-group headers + letter badges.
- [ ] **Step 2: History list** (design lines 291-303): add "RECENT CONVERSATIONS" caps header (10.5px/600, letter-spacing, faint, padding 8/6/6); row icon → chat-bubble outline in a plain 30×30 tile (no colored bg); remove the status dot; keep the kebab (rename/download/delete — functional, hover-revealed) and chevron; title 12.5px/600 + meta 10.5px faint.
- [ ] **Step 3: Composer polish** — send button `CornerRadius` 16→9 (`PromptBar.xaml.cs:222-236` / its XAML); composer field radius 14→13; attach button stays (functional deviation, styled identically to the @ button).
- [ ] **Step 4: Commit** — `git commit -am "feat(copilot): mention popover, history header, composer radii to design"`

---

### Task 14: Token + text-color alignment

**Files:**
- Modify: `UI/Copilot/CopilotTheme.cs:100` (`Cp.Text`), `UI/Copilot/CopilotTokens.xaml` (same key), `UI/Copilot/Controls/CopilotConverters.cs` (CopilotColors map spot-fixes)

- [ ] **Step 1:** `Cp.Text` light `#3d4a5f` → `#131c2b`, dark stays `#c3cdda` → change to `#e8eef6` (design `--text`). Check visual fallout on screens that intentionally wanted secondary text — they should use `Cp.Muted`; fix any that regress.
- [ ] **Step 2:** In the `CopilotColors` light→dark map, ensure `#131c2b` maps to `#e8eef6` and `#3d4a5f` (legacy body text) also maps to a readable dark value; update code-behind literals in `ChatView.xaml.cs` that use `#3d4a5f` for primary prose → `#131c2b`.
- [ ] **Step 3:** Replace hardcoded hex in XAML screens with tokens: `ToolReviewView.xaml` (10), `ToolFormView.xaml` (4), `ResultView.xaml` (3), `RunningView.xaml` (2) — nearest `Cp.*` token each.
- [ ] **Step 4:** Build + harness shots light/dark → no unreadable text. Commit — `git commit -am "fix(copilot): align text tokens with design, tokenize remaining XAML hexes"`

---

### Task 15: Harness shots + final verification

**Files:**
- Modify: `UiHarness/HarnessShots.cs`

- [ ] **Step 1: New shot states** (each: configure VM/stub → settle → PNG): `copilot-empty-{light,dark}.png` (exists), `copilot-thread.png` (seed thread: user msg, AI answer + Proposed card), `copilot-applied.png` (applied card + nudge), `copilot-feedback-panel.png` (downvote panel open), `copilot-meter-{22,88,97}.png` (`StubUsageService` pcts), `copilot-upgrade-sheet.png` (`ShowUpgradeSheet()`), `copilot-blocked-{admin,member}.png` (stub atLimit), `copilot-rate-sheet.png` (exists — verify new design), `copilot-report-sheet.png`.
- [ ] **Step 2: Run full test suite** — `dotnet test Tests/Tests.csproj` (Windows; on mac accept known indexer failure) → all new tests pass.
- [ ] **Step 3: Build all TFMs** — `dotnet build revit-addin-sync.sln -p:EnableWindowsTargeting=true` → success.
- [ ] **Step 4:** Run `UiHarness --shot <dir>` on Windows; compare against `docs/superpowers/specs/assets/copilot-design/` renders; fix visual deltas.
- [ ] **Step 5: Commit** — `git commit -am "test(copilot): harness screenshot states for redesigned surfaces"`

---

## Task order & dependencies

1 → (2 → 3 → 4 → 5), 6 → 7, 8, 9, 10 (needs 1), 11 (needs 1), 12 (needs 1), 13, 14, 15 (needs all). Tasks 6-9 and 13-14 are independent of the usage chain 2-5.

## Deviations from the design (agreed)

- Attach-file button kept in composer (functional; not in mock).
- "View code (N lines)" toggle kept inside the command card (functional; styled as ghost row).
- History rows keep hover kebab (rename/download/delete) — extra function, design-styled.
- Panel width stays fluid (Revit pane resizable) vs mock's fixed 360px.

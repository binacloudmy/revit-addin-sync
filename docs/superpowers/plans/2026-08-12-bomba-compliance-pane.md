# Bomba Compliance Pane Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A dockable Bomba Compliance pane in the Revit add-in — ribbon button, pane host, and findings rendering against stub data — mirroring the existing JKR Compliance pane.

**Architecture:** A second button on the existing Compliance ribbon panel opens an `IDockablePaneProvider` docked right, exactly as JKR does. The panel is WPF/XAML bound to view models carrying stub data. It merges `UI/Jkr/Tokens.xaml` directly rather than forking a palette, so the two compliance panes cannot drift apart. No HTTP, no model reads, no markup — those are later plans.

**Tech Stack:** C# (net48 / net8.0-windows / net10.0-windows), WPF, Revit API. No new NuGet packages.

**Specs:** `bina-ai/docs/superpowers/specs/2026-08-10-bomba-ubbl-compliance-design.md` §7 (UI) and §4 (the checks). The Python contract this UI must mirror lives in `bina-ai/app/services/bomba/result.py`.

## Global Constraints

- **Design system: inherit, do not fork.** The panel merges `UI/Jkr/Tokens.xaml`. Do NOT create `UI/Bomba/Tokens.xaml` or define a `SolidColorBrush` anywhere in `UI/Bomba/`. Two compliance panes in one ribbon panel must not look like two products.
- **Label by subject, never by schedule number.** Tabs read "Fire systems", "Travel distance", "Unprotected areas" — never "10th Schedule". Schedule numbering differs between state adoptions; the citation appears inside a finding where jurisdiction is known.
- **Never display a bare code letter.** `G` is a hose reel in Peninsular and something else in Sabah. Codes arrive already resolved to names.
- **`Passed` is three-valued: `bool?`.** `true` pass, `false` fail, **`null` NOT CHECKED**. Treating null as false recreates a false accusation of missing fire protection. Any roll-up must handle all three.
- **Coverage is separate from pass/fail.** Skipped rooms did not pass; they were not checked. Coverage renders inside the verdict block, never as a footnote.
- **Three action variants, and never a Fix affordance outside the first:** `Fixable` → primary Fix button; `GuidanceOnly` → options text, no Fix button; `NeedsModelling` → "Show what exists" only.
- **Rule-derived values render as the literal `[X]`.** Measured values are real. The contrast must stay visible.
- **net48 language floor.** No records, no init-only setters, no target-typed `new()`, no file-scoped namespaces. Plain classes with `INotifyPropertyChanged`.
- **Never touch `ElementId.Value`** — it is `int` on net48 and `long` on newer TFMs. Store ids as `long` in view models.
- **Build gate:** `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo` must report **0 Errors**. The repo baseline carries ~1652 warnings, so warning count is not a gate — but no NEW warning may name a file under `UI/Bomba/`, `UI/BombaComplianceDashboardPanel*`, `UI/BombaComplianceDashboardHost.cs`, or `Commands/BombaComplianceDashboardCommand.cs`.
- **No C# test project exists in this repo.** Verification per task is the compile plus, where stated, a XAML-parse check. Runtime behaviour is verified once on Windows in Revit at the end. State this honestly in reports — do not claim tested behaviour that was only compiled.
- **Commits:** conventional commits matching repo history (`feat(bomba): ...`).

---

## File Structure

| File | Responsibility |
|---|---|
| `UI/Bomba/BombaModels.cs` | View-model types mirroring the Python `Finding` contract. No behaviour. |
| `UI/Bomba/BombaDashboardViewModel.cs` | Pane state + stub data. One responsibility: what the panel binds to. |
| `UI/Bomba/Styles.xaml` | Shapes and spacing only. No colours. |
| `UI/BombaComplianceDashboardHost.cs` | `IDockablePaneProvider`, docks right, owns the panel. |
| `UI/BombaComplianceDashboardPanel.xaml` (+ `.xaml.cs`) | The pane's visual tree. |
| `Commands/BombaComplianceDashboardCommand.cs` | Ribbon button handler; shows the pane. |
| `App.cs` | Static host property, pane registration, button data, `AddItem`. |

**Delete first:** `UI/Bomba/BombaModels.cs`, `UI/Bomba/BombaDashboardViewModel.cs`, `UI/Bomba/Styles.xaml` currently exist as untracked, uncompiled drafts written before the design settled. They predate the three-valued `Passed`, the four-level cascade, subject-based labelling, the derivation disclosure, and searched-models provenance. Delete them rather than edit them — correcting them costs more than rewriting.

---

### Task 1: Remove stale drafts and write the view-model contract

**Files:**
- Delete: `UI/Bomba/BombaModels.cs`, `UI/Bomba/BombaDashboardViewModel.cs`, `UI/Bomba/Styles.xaml`
- Create: `UI/Bomba/BombaModels.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `NotifyBase`, `FindingAction` enum, `Severity` enum, `PaneState` enum, `CalcStepVm`, `FindingVm`, `CheckVm`, `CoverageVm` — all in namespace `RevitWebAppSync.UI.Bomba`

- [ ] **Step 1: Delete the stale drafts**

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
rm UI/Bomba/BombaModels.cs UI/Bomba/BombaDashboardViewModel.cs UI/Bomba/Styles.xaml
```

- [ ] **Step 2: Write the view-model contract**

Create `UI/Bomba/BombaModels.cs`:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace RevitWebAppSync.UI.Bomba
{
    // Mirrors the backend contract in bina-ai app/services/bomba/result.py.
    // When the HTTP client lands these are populated from Finding objects;
    // until then the view model supplies stub data.

    /// What the user may do about a finding. The distinction is the product's
    /// honesty: never render a Fix affordance where no automatic fix exists.
    public enum FindingAction
    {
        None,
        Fixable,        // one type/parameter swap the software can apply
        GuidanceOnly,   // needs a design decision, or cannot be verified
        NeedsModelling  // the thing is not in the model at all
    }

    public enum Severity { Pass, High, Medium, NotChecked }

    public enum PaneState { NeedsSetup, Ready, Stale, RulesUnavailable }

    public class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// One line of a finding's derivation. Rounding ORDER is load-bearing in
    /// these calculations, so intermediate values are shown, not just the answer.
    public class CalcStepVm
    {
        public string Label { get; set; }
        public string Expression { get; set; }
        public string ByLaw { get; set; }

        public bool HasByLaw { get { return !string.IsNullOrEmpty(ByLaw); } }
    }

    public class FindingVm : NotifyBase
    {
        private bool _expanded;

        /// Rule-derived values render as this until the tables are verified.
        public const string PlaceholderValue = "[X]";

        public string Subject { get; set; }      // "Dewan Serbaguna" or a system name
        public string RoomNumber { get; set; }   // "R-1-04", empty for building-scope
        public string Headline { get; set; }     // the one-line result

        /// THREE-VALUED. null means NOT CHECKED — neither pass nor fail.
        /// Treating null as false reports a false accusation.
        public bool? Passed { get; set; }

        public Severity Severity { get; set; }
        public string Metrics { get; set; }      // the mono block
        public string Guidance { get; set; }
        public string ClauseRef { get; set; }
        public string RulesVersion { get; set; }
        public string Jurisdiction { get; set; }
        public string SchedulePath { get; set; } // "II.1.d.iv" — which row fired
        public FindingAction Action { get; set; }
        public string FixLabel { get; set; }
        public IList<long> ElementIds { get; set; }        // long: ElementId.Value differs per TFM
        public IList<string> SearchedModels { get; set; }  // "missing" vs "cannot verify"
        public ObservableCollection<CalcStepVm> Steps { get; private set; }

        public FindingVm()
        {
            ElementIds = new List<long>();
            SearchedModels = new List<string>();
            Steps = new ObservableCollection<CalcStepVm>();
            Action = FindingAction.None;
        }

        public bool IsExpanded
        {
            get { return _expanded; }
            set { Set(ref _expanded, value); }
        }

        public bool ShowFix { get { return Action == FindingAction.Fixable; } }
        public bool ShowGuidance { get { return !string.IsNullOrEmpty(Guidance); } }
        public bool HasSteps { get { return Steps.Count > 0; } }

        /// Never collapse null into "FAIL" — that is the false accusation.
        public string StatusLabel
        {
            get
            {
                if (Passed == true) return "PASS";
                if (Passed == false) return "FAIL";
                return "NOT CHECKED";
            }
        }

        public string ActionLabel
        {
            get
            {
                switch (Action)
                {
                    case FindingAction.Fixable: return "FIXABLE";
                    case FindingAction.GuidanceOnly: return "DESIGN CALL";
                    case FindingAction.NeedsModelling: return "NOT MODELLED";
                    default: return "";
                }
            }
        }

        public string ElementIdList
        {
            get
            {
                if (ElementIds == null || ElementIds.Count == 0) return "";
                return string.Join(", ", ElementIds.Select(i => i.ToString()).ToArray());
            }
        }

        public string SearchedModelsLabel
        {
            get
            {
                if (SearchedModels == null || SearchedModels.Count == 0) return "";
                return "searched " + string.Join(" · ", SearchedModels.ToArray());
            }
        }
    }

    public class CheckVm : NotifyBase
    {
        /// Subject, never a schedule number — numbering differs by jurisdiction.
        public string Title { get; set; }
        public bool Available { get; set; }
        public string UnavailableReason { get; set; }
        public ObservableCollection<FindingVm> Findings { get; private set; }

        public CheckVm()
        {
            Available = true;
            Findings = new ObservableCollection<FindingVm>();
        }

        public int FailCount { get { return Findings.Count(f => f.Passed == false); } }
        public int NotCheckedCount { get { return Findings.Count(f => !f.Passed.HasValue); } }

        public string BadgeText
        {
            get { return Available ? FailCount.ToString() : "—"; }
        }
    }

    /// Coverage is deliberately separate from pass/fail. "All passed" while
    /// rooms went unchecked is the most dangerous output this product can show.
    public class CoverageVm
    {
        public int RoomsChecked { get; set; }
        public int RoomsTotal { get; set; }
        public IList<string> SkipReasons { get; set; }

        public CoverageVm() { SkipReasons = new List<string>(); }

        public int RoomsSkipped { get { return RoomsTotal - RoomsChecked; } }
        public bool IsComplete { get { return RoomsSkipped <= 0; } }
        public string Label { get { return RoomsChecked + "/" + RoomsTotal; } }

        public string Summary
        {
            get
            {
                if (IsComplete) return "every room checked, no skips";
                return RoomsSkipped + " rooms were not checked";
            }
        }
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo 2>&1 | tail -5`
Expected: `0 Error(s)`. No new warning naming a file under `UI/Bomba/`.

- [ ] **Step 4: Commit**

```bash
git add -A UI/Bomba/
git commit -m "feat(bomba): view-model contract mirroring the backend Finding"
```

---

### Task 2: Component styles

**Files:**
- Create: `UI/Bomba/Styles.xaml`

**Interfaces:**
- Consumes: token keys from `UI/Jkr/Tokens.xaml` — `Surface.Bg`, `Surface.Panel`, `Surface.Line`, `Surface.Line2`, `Ink`, `Ink2`, `Ink3`, `Ink4`, `Brand`, `BrandDark`, `BrandTint`, `Peach`, `Navy`, `Hi`, `HiBg`, `Md`, `MdBg`, `Ok`, `OkBg`, `Info`, `InfoBg`, `Font.Sans`, `Font.Mono`
- Produces: styles keyed `Bomba.Btn`, `Bomba.BtnPrimary`, `Bomba.Mono`, `Bomba.Label`, `Bomba.Subject`, `Bomba.Card`, `Bomba.Tab`

- [ ] **Step 1: Write the styles**

Create `UI/Bomba/Styles.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Bomba component styles.

         The PALETTE is NOT here. The panel merges UI/Jkr/Tokens.xaml directly so
         the two compliance panes cannot drift apart. Only shapes and spacing live
         in this file. If you are about to add a SolidColorBrush below, it belongs
         in Jkr/Tokens.xaml instead. -->

    <Style x:Key="Bomba.Btn" TargetType="Button">
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="FontFamily" Value="{DynamicResource Font.Sans}"/>
        <Setter Property="FontSize" Value="11.5"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{DynamicResource Ink2}"/>
        <Setter Property="Background" Value="{DynamicResource Surface.Panel}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Surface.Line}"/>
        <Setter Property="Padding" Value="10,5"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"
                            CornerRadius="5" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Surface.Line2}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="Bd" Property="Opacity" Value="0.45"/>
                            <Setter Property="Cursor" Value="Arrow"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="Bomba.BtnPrimary" TargetType="Button" BasedOn="{StaticResource Bomba.Btn}">
        <Setter Property="Foreground" Value="White"/>
        <Setter Property="Background" Value="{DynamicResource Brand}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Brand}"/>
    </Style>

    <Style x:Key="Bomba.Mono" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{DynamicResource Font.Mono}"/>
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="Foreground" Value="{DynamicResource Ink3}"/>
        <Setter Property="TextWrapping" Value="Wrap"/>
    </Style>

    <Style x:Key="Bomba.Label" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{DynamicResource Font.Mono}"/>
        <Setter Property="FontSize" Value="9.5"/>
        <Setter Property="Foreground" Value="{DynamicResource Ink4}"/>
    </Style>

    <Style x:Key="Bomba.Subject" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{DynamicResource Font.Sans}"/>
        <Setter Property="FontSize" Value="12.5"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{DynamicResource Ink}"/>
        <Setter Property="TextWrapping" Value="Wrap"/>
    </Style>

    <Style x:Key="Bomba.Card" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource Surface.Panel}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Surface.Line}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="7"/>
        <Setter Property="Margin" Value="0,0,0,8"/>
    </Style>

    <!-- Tab = ListBoxItem, not RadioButton. A ListBox gives two-way SelectedItem
         binding for free; RadioButtons in an ItemsControl look identical and do
         nothing, because nothing connects IsChecked back to the view model. -->
    <Style x:Key="Bomba.Tab" TargetType="ListBoxItem">
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="FontFamily" Value="{DynamicResource Font.Sans}"/>
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{DynamicResource Ink3}"/>
        <Setter Property="IsEnabled" Value="{Binding Available}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                    <Border x:Name="Bd" Background="Transparent" Padding="4,8"
                            BorderBrush="Transparent" BorderThickness="0,0,0,2">
                        <ContentPresenter HorizontalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brand}"/>
                            <Setter Property="Foreground" Value="{DynamicResource BrandDark}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Foreground" Value="{DynamicResource Ink4}"/>
                            <Setter Property="Cursor" Value="Arrow"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

</ResourceDictionary>
```

- [ ] **Step 2: Verify no colour leaked in**

Run: `grep -n "SolidColorBrush\|Color=" UI/Bomba/Styles.xaml`
Expected: no output. Any hit means a colour was defined here instead of inherited — fix it before continuing.

- [ ] **Step 3: Build**

Run: `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo 2>&1 | tail -5`
Expected: `0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add UI/Bomba/Styles.xaml
git commit -m "feat(bomba): component styles inheriting the JKR palette"
```

---

### Task 3: Pane view model with stub data

**Files:**
- Create: `UI/Bomba/BombaDashboardViewModel.cs`

**Interfaces:**
- Consumes: `CheckVm`, `FindingVm`, `CalcStepVm`, `CoverageVm`, `FindingAction`, `Severity`, `PaneState`, `NotifyBase` from Task 1
- Produces: `BombaDashboardViewModel` with `Checks`, `Coverage`, `SelectedCheck`, `State`, `ScopeLabel`, `ScopeDetail`, `TotalFailures`, `TotalNotChecked`, `VerdictCount`, `VerdictWord`, `VerdictBreakdown`, `VisibleFindings`

- [ ] **Step 1: Write the view model**

Create `UI/Bomba/BombaDashboardViewModel.cs`:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace RevitWebAppSync.UI.Bomba
{
    // Stub data for now: the pane is buildable and reviewable before the HTTP
    // client to bina-ai exists, and before data/bomba_rules.json is verified.
    //
    // When the backend lands: replace LoadStubData() with a call to the
    // /bomba endpoints and map Finding -> FindingVm. Nothing else changes.

    public class BombaDashboardViewModel : NotifyBase
    {
        private PaneState _state = PaneState.Ready;
        private CheckVm _selected;
        private string _scopeLabel = "Aras 01 — Blok A";
        private string _scopeDetail = "24 rooms · 31 doors";
        private int _changedSinceRun = 3;

        public ObservableCollection<CheckVm> Checks { get; private set; }
        public CoverageVm Coverage { get; set; }

        public BombaDashboardViewModel()
        {
            Checks = new ObservableCollection<CheckVm>();
            LoadStubData();
            _selected = Checks.FirstOrDefault();
        }

        public PaneState State
        {
            get { return _state; }
            set
            {
                if (Set(ref _state, value))
                {
                    Raise("ShowSetup");
                    Raise("ShowStale");
                    Raise("ShowResults");
                }
            }
        }

        public CheckVm SelectedCheck
        {
            get { return _selected; }
            set
            {
                if (Set(ref _selected, value))
                {
                    Raise("VisibleFindings");
                    Raise("HasFindings");
                }
            }
        }

        public string ScopeLabel { get { return _scopeLabel; } set { Set(ref _scopeLabel, value); } }
        public string ScopeDetail { get { return _scopeDetail; } set { Set(ref _scopeDetail, value); } }
        public int ChangedSinceRun { get { return _changedSinceRun; } set { Set(ref _changedSinceRun, value); } }

        public bool ShowSetup { get { return State == PaneState.NeedsSetup; } }
        public bool ShowStale { get { return State == PaneState.Stale; } }
        public bool ShowResults { get { return State == PaneState.Ready || State == PaneState.Stale; } }

        public int TotalFailures { get { return Checks.Sum(c => c.FailCount); } }
        public int TotalNotChecked { get { return Checks.Sum(c => c.NotCheckedCount); } }

        public string VerdictCount { get { return TotalFailures.ToString(); } }
        public string VerdictWord { get { return TotalFailures == 1 ? "finding" : "findings"; } }

        /// Names which checks contributed, by SUBJECT — never by schedule number.
        public string VerdictBreakdown
        {
            get
            {
                List<string> parts = Checks
                    .Where(c => c.Available && c.FailCount > 0)
                    .Select(c => c.Title + " " + c.FailCount)
                    .ToList();
                if (parts.Count == 0) return "All checks ran on " + ScopeLabel;
                return string.Join(" · ", parts.ToArray());
            }
        }

        public string StaleLabel { get { return ChangedSinceRun + " rooms changed since this run"; } }

        public IEnumerable<FindingVm> VisibleFindings
        {
            get
            {
                if (SelectedCheck == null) return Enumerable.Empty<FindingVm>();
                // Failures first, then not-checked, then passes.
                return SelectedCheck.Findings
                    .OrderBy(f => f.Passed == false ? 0 : (!f.Passed.HasValue ? 1 : 2))
                    .ToList();
            }
        }

        public bool HasFindings
        {
            get { return SelectedCheck != null && SelectedCheck.Findings.Count > 0; }
        }

        // ── stub data ───────────────────────────────────────────────────────
        // Measured values are plausible model reads. Every rule-derived
        // threshold is FindingVm.PlaceholderValue and stays so until verified.

        private void LoadStubData()
        {
            const string P = FindingVm.PlaceholderValue;

            Coverage = new CoverageVm();
            Coverage.RoomsChecked = 20;
            Coverage.RoomsTotal = 24;
            Coverage.SkipReasons.Add("unenclosed_or_unplaced");
            Coverage.SkipReasons.Add("no_boundary");

            CheckVm exit = new CheckVm();
            exit.Title = "Exit width";
            FindingVm dewan = new FindingVm();
            dewan.Subject = "Dewan Serbaguna";
            dewan.RoomNumber = "R-1-04";
            dewan.Headline = "Exit width short by " + P + " mm";
            dewan.Passed = false;
            dewan.Severity = Severity.High;
            dewan.Metrics = "214 occupants from 321 m²\nneed " + P + " mm · have 1800 mm";
            dewan.ClauseRef = "UBBL 1984 " + P;
            dewan.RulesVersion = "bomba_rules v0.1";
            dewan.Jurisdiction = "peninsular";
            dewan.SchedulePath = "III.2.a.ii";
            dewan.Action = FindingAction.Fixable;
            dewan.FixLabel = "Widen both doors";
            dewan.ElementIds.Add(884213);
            dewan.ElementIds.Add(884219);
            dewan.Steps.Add(NewStep("Occupants per floor", "321 m² ÷ " + P + " m²/person = 214", P));
            dewan.Steps.Add(NewStep("Exit width units", "214 ÷ " + P + " = " + P + " units", P));
            dewan.Steps.Add(NewStep("Round TOTAL first", P + " → " + P + " units", "181"));
            dewan.Steps.Add(NewStep("Convert to mm", P + " units = " + P + " mm", "177(e)"));
            exit.Findings.Add(dewan);

            FindingVm pejabat = new FindingVm();
            pejabat.Subject = "Pejabat";
            pejabat.RoomNumber = "R-1-02";
            pejabat.Headline = "Passes with 40 mm to spare";
            pejabat.Passed = true;
            pejabat.Severity = Severity.Pass;
            pejabat.Metrics = "12 occupants from 48 m² · have 900 mm";
            pejabat.ClauseRef = "UBBL 1984 " + P;
            pejabat.RulesVersion = "bomba_rules v0.1";
            pejabat.Jurisdiction = "peninsular";
            exit.Findings.Add(pejabat);

            // The differentiator: competitors report the permitted limit only.
            CheckVm travel = new CheckVm();
            travel.Title = "Travel distance";
            FindingVm terbuka = new FindingVm();
            terbuka.Subject = "Pejabat Terbuka";
            terbuka.RoomNumber = "R-1-11";
            terbuka.Headline = "Measured 42.6 m — two-way limit " + P + " m applies";
            terbuka.Passed = false;
            terbuka.Severity = Severity.High;
            terbuka.Metrics =
                "measured                42.6 m\n" +
                "limit · two-way         " + P + " m  ← applies\n" +
                "limit · one-way dead-end " + P + " m\n" +
                "limit · corridor dead-end " + P + " m";
            terbuka.Guidance = "Needs a design decision — add a second exit on the east façade, "
                             + "or relocate the corridor entry. All three limits are shown because "
                             + "changing the design can change which one binds.";
            terbuka.ClauseRef = "UBBL 1984 " + P;
            terbuka.RulesVersion = "bomba_rules v0.1";
            terbuka.Jurisdiction = "peninsular";
            terbuka.Action = FindingAction.GuidanceOnly;
            travel.Findings.Add(terbuka);

            // "Missing" vs "cannot verify" — the distinction that avoids a
            // false accusation of absent fire protection.
            CheckVm systems = new CheckVm();
            systems.Title = "Fire systems";
            FindingVm callPoint = new FindingVm();
            callPoint.Subject = "Manual call point";
            callPoint.Headline = "Cannot verify — no M&E model was searched";
            callPoint.Passed = null;   // NOT CHECKED, not failed
            callPoint.Severity = Severity.NotChecked;
            callPoint.Metrics = "required " + P;
            callPoint.Guidance = "Fire systems are modelled in the M&E discipline. "
                               + "Link the M&E model and re-check. This is not a finding of absence.";
            callPoint.ClauseRef = "UBBL 1984 " + P;
            callPoint.RulesVersion = "bomba_rules v0.1";
            callPoint.Jurisdiction = "peninsular";
            callPoint.SchedulePath = "IV.1.a.ii";
            callPoint.Action = FindingAction.GuidanceOnly;
            callPoint.SearchedModels.Add("Architecture");
            systems.Findings.Add(callPoint);

            FindingVm hoseReel = new FindingVm();
            hoseReel.Subject = "Hose reel system";
            hoseReel.Headline = "6 found across 2 levels";
            hoseReel.Passed = true;
            hoseReel.Severity = Severity.Pass;
            hoseReel.Metrics = "required " + P + " · present 6";
            hoseReel.ClauseRef = "UBBL 1984 " + P;
            hoseReel.RulesVersion = "bomba_rules v0.1";
            hoseReel.Jurisdiction = "peninsular";
            hoseReel.SearchedModels.Add("Architecture");
            hoseReel.SearchedModels.Add("M&E");
            systems.Findings.Add(hoseReel);

            // Visible but disabled. Hiding it makes users wonder whether it
            // exists; guessing its content would be dangerous.
            CheckVm unprotected = new CheckVm();
            unprotected.Title = "Unprotected areas";
            unprotected.Available = false;
            unprotected.UnavailableReason = "rules pending verification";

            Checks.Add(exit);
            Checks.Add(travel);
            Checks.Add(systems);
            Checks.Add(unprotected);
        }

        private static CalcStepVm NewStep(string label, string expression, string byLaw)
        {
            CalcStepVm s = new CalcStepVm();
            s.Label = label;
            s.Expression = expression;
            s.ByLaw = byLaw;
            return s;
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo 2>&1 | tail -5`
Expected: `0 Error(s)`.

- [ ] **Step 3: Verify the placeholder discipline held**

Run: `grep -c "PlaceholderValue\|\[X\]" UI/Bomba/BombaDashboardViewModel.cs`
Expected: a non-zero count. Then read the stub data and confirm every rule-derived
number is `[X]` and only measured values (`321 m²`, `1800 mm`, `42.6 m`, `6`) are real.

- [ ] **Step 4: Commit**

```bash
git add UI/Bomba/BombaDashboardViewModel.cs
git commit -m "feat(bomba): pane view model with stub findings"
```

---

### Task 4: Pane host, command, minimal panel, and ribbon wiring

This is the first task after which the pane can actually be opened in Revit.

**Files:**
- Create: `UI/BombaComplianceDashboardHost.cs`
- Create: `UI/BombaComplianceDashboardPanel.xaml`
- Create: `UI/BombaComplianceDashboardPanel.xaml.cs`
- Create: `Commands/BombaComplianceDashboardCommand.cs`
- Modify: `App.cs`

**Interfaces:**
- Consumes: `BombaDashboardViewModel` from Task 3
- Produces: `BombaComplianceDashboardHost.PaneId`, `App.BombaComplianceDashboardHost` static property

- [ ] **Step 1: Write the host**

Mirrors `UI/JkrComplianceDashboardHost.cs`. Note the comment there about hosting
the panel directly rather than in a `Frame` — a `Frame` lets the panel size to
content, which leaves a scrolling list unbounded and it never scrolls. Do the same.

Create `UI/BombaComplianceDashboardHost.cs`:

```csharp
using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI
{
    public class BombaComplianceDashboardHost : UserControl, IDockablePaneProvider
    {
        private BombaComplianceDashboardPanel _panel;

        // GUIDs already taken: ...0001 cost, ...0002 compliance, ...0003 JKR.
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("B1A4C057-0004-4000-8000-000000000004"));

        public BombaComplianceDashboardHost()
        {
            try
            {
                _panel = new BombaComplianceDashboardPanel();
                // Host the panel directly (no Frame): a Frame lets the panel size
                // to content, leaving the findings ScrollViewer unbounded so it
                // never scrolls. A UserControl stretches to fill the pane.
                this.Content = _panel;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] BombaComplianceDashboardHost init error: " + ex.Message);
                this.Content = new TextBlock
                {
                    Text = "Bomba Compliance failed to load: " + ex.Message,
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        public BombaComplianceDashboardPanel DashboardPanel { get { return _panel; } }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
            data.VisibleByDefault = false;
        }
    }
}
```

- [ ] **Step 2: Write the minimal panel**

Fills in properly in Tasks 5 and 6. For now it proves the resource merge and the
data context work.

Create `UI/BombaComplianceDashboardPanel.xaml`:

```xml
<UserControl x:Class="RevitWebAppSync.UI.BombaComplianceDashboardPanel"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Background="#F7F6F3" FontFamily="Segoe UI" Focusable="True">

    <UserControl.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <!-- Palette inherited from JKR, never forked. -->
                <ResourceDictionary Source="Jkr/Tokens.xaml"/>
                <ResourceDictionary Source="Bomba/Styles.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </UserControl.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <Border Grid.Row="0" Background="{DynamicResource Surface.Panel}"
                BorderBrush="{DynamicResource Surface.Line}" BorderThickness="0,0,0,1" Padding="12">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column="0">
                    <TextBlock Text="{Binding ScopeLabel}" FontSize="14" FontWeight="Bold"
                               Foreground="{DynamicResource Ink}"/>
                    <TextBlock Text="{Binding ScopeDetail}" Style="{DynamicResource Bomba.Label}" Margin="0,3,0,0"/>
                </StackPanel>
                <Button Grid.Column="1" Content="Re-check" Style="{DynamicResource Bomba.BtnPrimary}"
                        VerticalAlignment="Center"/>
            </Grid>
        </Border>

        <!-- Body: filled in Tasks 5 and 6 -->
        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" Padding="12">
            <StackPanel x:Name="BodyHost"/>
        </ScrollViewer>

        <!-- Footer -->
        <Border Grid.Row="2" Background="{DynamicResource Surface.Panel}"
                BorderBrush="{DynamicResource Surface.Line}" BorderThickness="0,1,0,0" Padding="12,9">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <Button Grid.Column="0" Content="History" Style="{DynamicResource Bomba.Btn}"/>
                <Button Grid.Column="2" Content="Export ▾" Style="{DynamicResource Bomba.Btn}"/>
            </Grid>
        </Border>
    </Grid>
</UserControl>
```

Create `UI/BombaComplianceDashboardPanel.xaml.cs`:

```csharp
using System.Windows.Controls;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Bomba;

namespace RevitWebAppSync.UI
{
    public partial class BombaComplianceDashboardPanel : UserControl
    {
        private readonly BombaDashboardViewModel _vm;

        public BombaComplianceDashboardPanel()
        {
            InitializeComponent();
            _vm = new BombaDashboardViewModel();
            this.DataContext = _vm;
        }

        public BombaDashboardViewModel ViewModel { get { return _vm; } }

        /// Called by the command when the pane opens, so later tasks can reach
        /// the live document without the panel depending on Revit at construction.
        public void SetRevitApp(UIApplication uiApp)
        {
            // No-op until the model-reading tasks land.
        }
    }
}
```

- [ ] **Step 3: Write the command**

Mirrors `Commands/JkrComplianceDashboardCommand.cs`, including its OTA update gate.

Create `Commands/BombaComplianceDashboardCommand.cs`:

```csharp
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI;

namespace RevitWebAppSync.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class BombaComplianceDashboardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                UIApplication uiApp = commandData.Application;

                DockablePane pane = uiApp.GetDockablePane(BombaComplianceDashboardHost.PaneId);

                if (pane == null)
                {
                    TaskDialog.Show("BINA Bomba Compliance", "Bomba Compliance panel not found. Please restart Revit.");
                    return Result.Failed;
                }

                if (!pane.IsShown())
                    pane.Show();

                if (App.BombaComplianceDashboardHost != null &&
                    App.BombaComplianceDashboardHost.DashboardPanel != null)
                {
                    App.BombaComplianceDashboardHost.DashboardPanel.SetRevitApp(uiApp);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA Bomba Compliance — Error", "Failed to open panel: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}
```

- [ ] **Step 4: Wire into App.cs — static property**

Find the existing line declaring the JKR host property (search for
`public static JkrComplianceDashboardHost JkrComplianceDashboardHost`). Add
immediately below it:

```csharp
        public static BombaComplianceDashboardHost BombaComplianceDashboardHost { get; private set; }
```

- [ ] **Step 5: Wire into App.cs — pane registration**

Find the `// Register JKR BIM Compliance dockable pane` block (it ends with a
`catch (Exception jkrEx)` that calls `Services.TelemetryService.Track`). Add this
immediately after that whole try/catch:

```csharp
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
```

- [ ] **Step 6: Wire into App.cs — ribbon button**

Find `PushButtonData jkrComplianceButtonData = new PushButtonData(`. Add this
immediately after that object initializer closes (after its `};`):

```csharp
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
```

Then find `compliancePanel.AddItem(jkrComplianceButtonData);` and add immediately after:

```csharp
            compliancePanel.AddItem(bombaComplianceButtonData);
```

- [ ] **Step 7: Build all three target frameworks**

```bash
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo 2>&1 | tail -4
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows -v q --nologo 2>&1 | tail -4
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows -v q --nologo 2>&1 | tail -4
```

Expected: `0 Error(s)` on each. Report the actual output for all three — a
regression that only appears on one TFM is the failure mode this catches.

- [ ] **Step 8: Verify no new warnings from Bomba files**

```bash
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo 2>&1 | grep -i "warning" | grep -iE "UI/Bomba|BombaCompliance|BombaComplianceDashboard" || echo "no Bomba warnings"
```

Expected: `no Bomba warnings`.

- [ ] **Step 9: Commit**

```bash
git add UI/BombaComplianceDashboardHost.cs UI/BombaComplianceDashboardPanel.xaml UI/BombaComplianceDashboardPanel.xaml.cs Commands/BombaComplianceDashboardCommand.cs App.cs
git commit -m "feat(bomba): dockable pane, ribbon button and command"
```

---

### Task 5: Verdict block, coverage and check tabs

**Files:**
- Modify: `UI/BombaComplianceDashboardPanel.xaml` (replace the `BodyHost` StackPanel)

**Interfaces:**
- Consumes: `BombaDashboardViewModel.VerdictCount`, `.VerdictWord`, `.VerdictBreakdown`, `.Coverage`, `.Checks`, `.SelectedCheck`

- [ ] **Step 1: Replace the body placeholder**

In `UI/BombaComplianceDashboardPanel.xaml`, replace the whole `<ScrollViewer Grid.Row="1" …>` element with:

```xml
        <Grid Grid.Row="1">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
            </Grid.RowDefinitions>

            <!-- Verdict. The largest element in the pane: the drafter's first
                 question is binary, and it must answer from across the room. -->
            <Border Grid.Row="0" Background="{DynamicResource HiBg}" BorderBrush="{DynamicResource Hi}"
                    BorderThickness="3,0,0,0" Padding="12" Margin="12,12,12,0">
                <StackPanel>
                    <StackPanel Orientation="Horizontal">
                        <TextBlock Text="{Binding VerdictCount}" FontSize="26" FontWeight="Bold"
                                   Foreground="{DynamicResource Hi}" FontFamily="{DynamicResource Font.Mono}"/>
                        <TextBlock Text="{Binding VerdictWord}" FontSize="15" FontWeight="SemiBold"
                                   Foreground="{DynamicResource Hi}" VerticalAlignment="Bottom" Margin="8,0,0,3"/>
                    </StackPanel>
                    <TextBlock Text="{Binding VerdictBreakdown}" Style="{DynamicResource Bomba.Mono}" Margin="0,6,0,0"/>

                    <!-- Coverage sits INSIDE the verdict, never as a footnote.
                         "All passed" over silent skips is the most dangerous
                         thing this pane can show. -->
                    <Border Background="{DynamicResource MdBg}" CornerRadius="4" Padding="8,5" Margin="0,10,0,0"
                            HorizontalAlignment="Left">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="coverage " Style="{DynamicResource Bomba.Label}"
                                       Foreground="{DynamicResource Md}" VerticalAlignment="Center"/>
                            <TextBlock Text="{Binding Coverage.Label}" Style="{DynamicResource Bomba.Label}"
                                       Foreground="{DynamicResource Md}" FontWeight="Bold" VerticalAlignment="Center"/>
                            <TextBlock Text="  " VerticalAlignment="Center"/>
                            <TextBlock Text="{Binding Coverage.Summary}" Style="{DynamicResource Bomba.Label}"
                                       Foreground="{DynamicResource Md}" VerticalAlignment="Center"/>
                        </StackPanel>
                    </Border>
                </StackPanel>
            </Border>

            <!-- Check tabs. Labelled by SUBJECT — schedule numbers differ
                 between state adoptions and would be wrong in Sabah. -->
            <ListBox Grid.Row="1" Margin="12,12,12,0"
                     ItemsSource="{Binding Checks}"
                     SelectedItem="{Binding SelectedCheck, Mode=TwoWay}"
                     ItemContainerStyle="{DynamicResource Bomba.Tab}"
                     Background="Transparent" BorderThickness="0"
                     ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                <ListBox.ItemsPanel>
                    <ItemsPanelTemplate>
                        <UniformGrid Rows="1"/>
                    </ItemsPanelTemplate>
                </ListBox.ItemsPanel>
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <StackPanel>
                            <TextBlock Text="{Binding Title}" HorizontalAlignment="Center"
                                       TextTrimming="CharacterEllipsis"/>
                            <TextBlock Text="{Binding BadgeText}" HorizontalAlignment="Center"
                                       FontFamily="{DynamicResource Font.Mono}" FontSize="10"
                                       FontWeight="Bold" Margin="0,2,0,0"/>
                        </StackPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>

            <!-- Findings: filled in Task 6. Must be the '*' row so it scrolls. -->
            <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto" Padding="12,12,12,0">
                <ItemsControl x:Name="FindingsHost"/>
            </ScrollViewer>
        </Grid>
```

- [ ] **Step 2: Build**

Run: `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo 2>&1 | tail -5`
Expected: `0 Error(s)`. A XAML error surfaces here as a build error, so this
compile is the parse check.

- [ ] **Step 3: Verify the scroll row is bounded**

Read the file and confirm the findings `ScrollViewer` sits in a row whose
`RowDefinition` is `Height="*"`, not `Auto`. An `Auto` row leaves it unbounded
and it will never scroll — the exact bug the JKR host carries a comment about.

- [ ] **Step 4: Commit**

```bash
git add UI/BombaComplianceDashboardPanel.xaml
git commit -m "feat(bomba): verdict block, coverage and subject-labelled tabs"
```

---

### Task 6: Finding cards with three action variants and derivation disclosure

**Files:**
- Modify: `UI/BombaComplianceDashboardPanel.xaml` (the `FindingsHost` ItemsControl)

**Interfaces:**
- Consumes: `BombaDashboardViewModel.VisibleFindings`, `FindingVm` members from Task 1

- [ ] **Step 1: Replace the findings placeholder**

In `UI/BombaComplianceDashboardPanel.xaml`, replace
`<ItemsControl x:Name="FindingsHost"/>` with:

```xml
                <ItemsControl ItemsSource="{Binding VisibleFindings}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <Border Style="{DynamicResource Bomba.Card}">
                                <StackPanel Margin="11">

                                    <!-- Header: room, subject, action tag -->
                                    <Grid>
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="*"/>
                                            <ColumnDefinition Width="Auto"/>
                                        </Grid.ColumnDefinitions>
                                        <StackPanel Grid.Column="0" Orientation="Horizontal">
                                            <TextBlock Text="{Binding RoomNumber}" Style="{DynamicResource Bomba.Label}"
                                                       VerticalAlignment="Center" Margin="0,0,7,0"/>
                                            <TextBlock Text="{Binding Subject}" Style="{DynamicResource Bomba.Subject}"/>
                                        </StackPanel>
                                        <Border Grid.Column="1" Background="{DynamicResource Surface.Line2}"
                                                CornerRadius="3" Padding="6,2" VerticalAlignment="Center">
                                            <TextBlock Text="{Binding ActionLabel}" Style="{DynamicResource Bomba.Label}"/>
                                        </Border>
                                    </Grid>

                                    <TextBlock Text="{Binding Headline}" Style="{DynamicResource Bomba.Subject}"
                                               Foreground="{DynamicResource Hi}" FontWeight="Normal" Margin="0,5,0,0"/>

                                    <!-- Metrics: mono so numbers align -->
                                    <Border Background="{DynamicResource Surface.Bg}" CornerRadius="4"
                                            Padding="9" Margin="0,8,0,0">
                                        <TextBlock Text="{Binding Metrics}" Style="{DynamicResource Bomba.Mono}"/>
                                    </Border>

                                    <!-- Derivation, collapsed. Rounding ORDER is
                                         load-bearing, so intermediate steps are
                                         how a reviewer checks the answer. -->
                                    <Expander Header="how this number was reached" Margin="0,8,0,0"
                                              Visibility="{Binding HasSteps, Converter={StaticResource BoolVis}}"
                                              Foreground="{DynamicResource Ink3}" FontSize="11">
                                        <ItemsControl ItemsSource="{Binding Steps}" Margin="0,6,0,0">
                                            <ItemsControl.ItemTemplate>
                                                <DataTemplate>
                                                    <Grid Margin="0,0,0,4">
                                                        <Grid.ColumnDefinitions>
                                                            <ColumnDefinition Width="*"/>
                                                            <ColumnDefinition Width="Auto"/>
                                                        </Grid.ColumnDefinitions>
                                                        <StackPanel Grid.Column="0">
                                                            <TextBlock Text="{Binding Label}" Style="{DynamicResource Bomba.Label}"/>
                                                            <TextBlock Text="{Binding Expression}" Style="{DynamicResource Bomba.Mono}"/>
                                                        </StackPanel>
                                                        <TextBlock Grid.Column="1" Text="{Binding ByLaw}"
                                                                   Style="{DynamicResource Bomba.Label}"
                                                                   VerticalAlignment="Bottom"
                                                                   Visibility="{Binding HasByLaw, Converter={StaticResource BoolVis}}"/>
                                                    </Grid>
                                                </DataTemplate>
                                            </ItemsControl.ItemTemplate>
                                        </ItemsControl>
                                    </Expander>

                                    <!-- Guidance, when there is no automatic fix -->
                                    <Border BorderBrush="{DynamicResource Surface.Line}" BorderThickness="2,0,0,0"
                                            Padding="9,0,0,0" Margin="0,8,0,0"
                                            Visibility="{Binding ShowGuidance, Converter={StaticResource BoolVis}}">
                                        <TextBlock Text="{Binding Guidance}" Style="{DynamicResource Bomba.Mono}"
                                                   Foreground="{DynamicResource Ink2}"/>
                                    </Border>

                                    <!-- Actions. A Fix button appears ONLY when
                                         an automatic fix exists. A fake one here
                                         would be worse than none. -->
                                    <StackPanel Orientation="Horizontal" Margin="0,10,0,0">
                                        <Button Content="{Binding FixLabel}" Style="{DynamicResource Bomba.BtnPrimary}"
                                                Margin="0,0,6,0"
                                                Visibility="{Binding ShowFix, Converter={StaticResource BoolVis}}"/>
                                        <Button Content="Show me" Style="{DynamicResource Bomba.Btn}"/>
                                    </StackPanel>

                                    <!-- Provenance: which models were searched,
                                         which row fired, which rules version -->
                                    <TextBlock Text="{Binding SearchedModelsLabel}" Style="{DynamicResource Bomba.Label}"
                                               Margin="0,9,0,0"/>
                                    <TextBlock Style="{DynamicResource Bomba.Label}" Margin="0,3,0,0">
                                        <Run Text="{Binding ClauseRef, Mode=OneWay}"/>
                                        <Run Text=" · row "/>
                                        <Run Text="{Binding SchedulePath, Mode=OneWay}"/>
                                        <Run Text=" · "/>
                                        <Run Text="{Binding RulesVersion, Mode=OneWay}"/>
                                    </TextBlock>
                                </StackPanel>
                            </Border>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
```

- [ ] **Step 2: Add the boolean-to-visibility converter**

The template above references `{StaticResource BoolVis}`. The repo already has
`RevitWebAppSync.UI.Jkr.BoolToVisibilityConverter` (used by
`JkrComplianceDashboardPanel.xaml`). Reuse it — do not write a second one.

In `UI/BombaComplianceDashboardPanel.xaml`, add the namespace to the root element:

```xml
      xmlns:jkr="clr-namespace:RevitWebAppSync.UI.Jkr"
```

and inside `<ResourceDictionary>`, after the `</ResourceDictionary.MergedDictionaries>` close:

```xml
            <jkr:BoolToVisibilityConverter x:Key="BoolVis"/>
```

- [ ] **Step 3: Build all three target frameworks**

```bash
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo 2>&1 | tail -4
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows -v q --nologo 2>&1 | tail -4
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows -v q --nologo 2>&1 | tail -4
```

Expected: `0 Error(s)` on each. Report all three.

- [ ] **Step 4: Verify no new Bomba warnings**

```bash
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo 2>&1 | grep -i "warning" | grep -iE "UI/Bomba|BombaCompliance" || echo "no Bomba warnings"
```

Expected: `no Bomba warnings`.

- [ ] **Step 5: Commit**

```bash
git add UI/BombaComplianceDashboardPanel.xaml
git commit -m "feat(bomba): finding cards with action variants and derivation"
```

---

## Windows verification — done by a human, not a subagent

The compile gate proves it builds; it does not prove it renders. After Task 6,
on a Windows machine with Revit:

- [ ] Build and deploy the add-in, restart Revit
- [ ] The **Bina** tab's Compliance panel shows two buttons: JKR Compliance and Bomba Compliance
- [ ] Clicking Bomba Compliance opens a pane docked right
- [ ] The pane visually matches the JKR pane — same paper, same terracotta, same fonts. If the two look like different products, the token merge failed
- [ ] The verdict count is the largest thing in the pane
- [ ] Coverage reads `20/24 · 4 rooms were not checked` inside the verdict block
- [ ] Four tabs read **Exit width · Travel distance · Fire systems · Unprotected areas** — no schedule numbers anywhere
- [ ] The Unprotected areas tab is visibly disabled
- [ ] The Dewan Serbaguna card shows a **Fix** button; the Pejabat Terbuka (travel) and Manual call point cards show **no Fix button**
- [ ] The Manual call point card reads "Cannot verify", not "missing", and shows `searched Architecture`
- [ ] Expanding "how this number was reached" shows lettered steps with by-law references
- [ ] Rule-derived numbers render as `[X]`; measured numbers (321 m², 1800 mm, 42.6 m, 6) are real
- [ ] **Drag the pane narrower to ~320px** — nothing clips, the findings list still scrolls

## Out of scope for this plan

| Deferred | Why separate |
|---|---|
| HTTP client to bina-ai `/bomba` endpoints | Needs the router, which does not exist yet |
| Reading the live model (rooms, doors, linked M&E) | Its own plan; needs `facts.py` on the backend |
| In-model markup — travel paths, colour convention | Needs idempotency infrastructure (subcategory + run id + delete-then-redraw) |
| Export menu, calculation sheets, coverage map | Needs findings to be real, not stubbed |
| Setup card and the four-level cascade | Needs the backend cascade endpoint |
| History view | Needs persisted runs |

## What this plan deliberately does not do

**It does not connect to anything.** Every number is stubbed and every rule value
is `[X]`. That is intentional: the pane's shape, tokens, scroll behaviour and
action variants can all be reviewed and corrected before any wiring exists, and
the backend rules tables are blocked on consultant verification regardless.

# Developing and debugging the addin UI without Revit

The addin's WPF windows can be developed, previewed, and hot-reloaded on any
Windows machine **without installing Revit**. This doc explains why that works,
how to run the `UiHarness` project, and what its limits are.

## Why no Revit is needed

- **Compiling:** `RevitWebAppSync.csproj` references the Revit API through
  NuGet packages (`Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI`), not a local
  `C:\Program Files\Autodesk\...` DLL. The whole solution restores and builds
  on a machine that has never seen Revit.
- **Post-build copy:** the step that copies DLLs into
  `%APPDATA%\Autodesk\Revit\Addins\<year>\` is condition-guarded — when the
  folder doesn't exist, it silently skips. No build errors without Revit.
- **Rendering:** WPF windows are plain .NET UI. Revit is only needed when code
  actually calls into a live `UIApplication` / `Document`. All window
  constructors take plain DTOs (strings, `SyncResultData`, `BinaConfig`,
  `CommandTemplate`), so they construct fine outside Revit.

## Requirements

- Windows (WPF does not render on macOS/Linux; macOS can compile with
  `EnableWindowsTargeting` but not run).
- .NET 8 SDK.
- Visual Studio 2022 with the ".NET desktop development" workload
  (recommended for the best hot-reload experience), or any editor + CLI.

## The UiHarness project

`UiHarness/` is a small standalone WPF exe that references the addin project
and opens its windows with mock data:

| Button | Opens | Notes |
|---|---|---|
| Login | `LoginWindow` | prefilled email |
| Project Picker | `ProjectPickerWindow` | fake token → shows the error state |
| Sync Results | `SyncResultsWindow` | fully-successful mock sync |
| Download Results | `DownloadResultsWindow` | mixed success/failure rows |
| User Info | `UserInfoWindow` | mock `BinaConfig` |
| Update | `UpdateWindow` | |
| Command Run | `CommandRunWindow` | template with select + text variables |
| Copilot Panel | `CopilotPanel` | hosted in a `Frame` (it's a `Page`) |

To preview a new window or state, add a button in `LauncherWindow.xaml` and a
handler in `LauncherWindow.xaml.cs` that constructs it with whatever mock data
exercises the state you're styling.

## Run it

**Visual Studio:** right-click **UiHarness** → *Set as Startup Project* →
**F5**.

**CLI:**

```
dotnet watch --project UiHarness
```

## Hot reload

With the harness running under the VS debugger (or `dotnet watch`):

- Edit any **XAML** in the addin project (e.g.
  `UI/Copilot/CopilotPanel.xaml`, `UI/Jkr/Styles.xaml`) — the open window
  updates instantly, no restart.
- Edit **C# method bodies** — applied live. Constructor changes, new members,
  or new types usually require a restart (VS will tell you).

This replaces the Revit loop (rebuild → restart Revit → open model → open
pane, minutes per iteration) with a seconds-long feedback cycle.

## Limits — what still needs Revit

- Anything that calls a live `UIApplication` / `Document`: running Copilot
  commands against a model, element highlights, model/category queries. In
  the harness these fail or no-op. UI look, layout, bindings, and interaction
  states are what the harness is for.
- Copilot panel greeting shows defaults ("there", "Main Model") because
  `SetRevitContext` is never pushed in.
- Network-backed screens hit the real backend from `BinaConfig.Load()`'s
  environment; with no/fake token they render their error or empty states —
  which is often exactly the state you want to style.
- Final verification of Revit-coupled behavior still needs a real Revit run.

## XAML designer preview (no app running at all)

Independent of the harness, the VS/Blend XAML designer renders any `.xaml`
file live while you type. Useful for quick layout checks; the harness is
better once you need real bindings, converters, resource dictionaries, and
interaction states.

## macOS note

The solution builds on macOS with the official .NET SDK (homebrew `dotnet@8`
lacks the WindowsDesktop SDK — see the note in `RevitWebAppSync.csproj`), but
WPF apps only *run* on Windows. Use macOS for compile checks; do UI work on
Windows.

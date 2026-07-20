# UiHarness

Standalone WPF app that hosts the addin's windows with mock data, so UI work
needs no Revit install and no Revit restart loop.

## Requirements

- Windows + .NET 8 SDK (Visual Studio 2022 with the ".NET desktop development"
  workload recommended). Revit NOT required — the addin references the Revit
  API via NuGet stubs.

## Run

Visual Studio: set **UiHarness** as startup project, press **F5**.

CLI:

```
dotnet watch --project UiHarness
```

## Hot reload

With the harness running under F5 (or `dotnet watch`), edit any XAML in the
addin project (e.g. `UI/Copilot/CopilotPanel.xaml`) and the open window updates
instantly. C# hot reload covers method bodies; constructor or new-member
changes need a restart.

## Limits

- Buttons that execute real Revit work (run a command, read the model) will
  fail or no-op — there is no Revit behind the UI. Look/layout/interaction
  states are what the harness is for.
- Copilot panel: model name and username stay at defaults because
  `SetRevitContext` is never called.
- Project Picker opens with a fake token and shows its error state — useful
  for styling that state; wire a real token into `LauncherWindow.xaml.cs` to
  see live data.

# IFC → Native Revit Conversion (v1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert imported-IFC `DirectShape`s (walls, floors/slabs, columns, beams) into true native, editable Revit elements, inside the add-in, triggered by the copilot, with a preview → confirm → atomic build flow.

**Architecture:** A deterministic C# pipeline in `revit-addin-sync` (`BinaVibe/Mcp/Tools/IfcConvert/`): `IfcReader` (Revit API) reads imported-IFC elements → neutral `IfcElement` POCOs; `IfcMapper` (pure) maps each POCO + a resolved type to a `{tool, args}` step dict; `IfcConverter` emits the step list to the existing `BatchExecutor.Run` (one `TransactionGroup`). The bina-ai Revit agent gets one external-execution tool `convert_ifc_to_native(scope, mode)` — it never enumerates elements. Un-convertible geometry is kept as its original `DirectShape` and reported.

**Tech Stack:** C# / .NET, Revit API (`Nice3point.Revit.Api.RevitAPI` 2027.* via NuGet), xUnit (`Tests/Tests.csproj`), `System.Text.Json`; bina-ai (Python, agno agent).

## Global Constraints

- **v1 element scope:** `IfcWall`, `IfcSlab`, `IfcColumn`, `IfcBeam` only. No doors/windows/roofs/MEP.
- **Read Revit's IFC import** — never parse raw `.ifc`. Source elements are `DirectShape`s carrying the IFC entity tag + Psets.
- **Type resolution = match-or-create** — match an existing project type by thickness (± tolerance) + name, else create from IFC Psets. Behind an `ITypeResolver` seam (v2 = JKR family library).
- **Never lose data** — un-convertible geometry stays as its original `DirectShape`, counted in the report with a reason. No delete, no geometric approximation, in v1.
- **Deterministic engine; the LLM never enumerates elements.** The agent emits exactly one `convert_ifc_to_native` tool call per user action. (`revit_turn.py` caps the tool loop at `_TOOL_MAX_ROUNDS = 3`.)
- **Preview and Build return the identical `ConversionReport` shape** — preview is a truthful dry run.
- **Atomic build** — all native creates run through `BatchExecutor.Run` in one `TransactionGroup`; any failure rolls back the whole conversion.
- **Purity rule:** `IfcElement`, `NativeStep`, `ConversionReport`, `IfcMapper`, `MatchOrCreateTypeResolver` use only primitives + `System.Text.Json` — **no Revit API calls** — so they run under `dotnet test` with no Revit. Revit API access lives only in `IfcReader` and `IfcConverter`.
- **Thickness/length unit:** millimetres (matches `create_wall` `start_mm`/`end_mm`, `create_floor` `boundary_mm`). Points are `double[3]` `[x,y,z]` in mm.
- **Test namespace:** `RevitWebAppSync.Tests`.
- **Namespaces:** new IfcConvert files use `namespace BinaVibe.Mcp.Tools.IfcConvert` (NOT `RevitWebAppSync.BinaVibe...` — the addin's convention is a bare `BinaVibe.Mcp.Tools`).
- **LOCAL TOOLCHAIN (macOS arm64):** the main `Tests/Tests.csproj` is `net10.0-windows` + `UseWPF` → its WPF test host **cannot run on macOS**. Pure-logic IFC tests therefore live in the cross-platform **`Tests.Ifc/Tests.Ifc.csproj`** (`net10.0`, no WPF). Run: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"; dotnet test Tests.Ifc/Tests.Ifc.csproj`. **Each new pure source file must be `<Compile Include>`'d into `Tests.Ifc.csproj`** (explicit compile items, no globbing). Revit-coupled build-checks: `dotnet build RevitWebAppSync.csproj -f net10.0-windows -p:EnableWindowsTargeting=true -p:SkipRevitSources=true`. (CI runs the Windows `Tests.csproj` on a Windows runner; wiring `Tests.Ifc` into CI is a follow-up.)

## File Structure

**New — `revit-addin-sync/BinaVibe/Mcp/Tools/IfcConvert/`:**
- `IfcElement.cs` — neutral POCO (primitives) describing one source element + convertibility.
- `NativeStep.cs` — a `{tool, args}` step descriptor + list→`execute_revit_batch` args helper.
- `ITypeResolver.cs` — type-resolution seam; `TypeResolution` result record.
- `MatchOrCreateTypeResolver.cs` — pure v1 resolver (match by thickness/name within tol, else create-spec).
- `IfcMapper.cs` — pure: `IfcElement` + `TypeResolution` → `NativeStep` (or kept-as-is).
- `ConversionReport.cs` — pure aggregation POCO.
- `IfcReader.cs` — Revit API: enumerate imported-IFC elements → `IfcElement`s + fetch existing types.
- `IfcConverter.cs` — orchestrator: `Preview`/`Build`.

**New — `Tests/`:** `IfcMapperTests.cs`, `MatchOrCreateTypeResolverTests.cs`, `ConversionReportTests.cs`.

**Modified:**
- `BinaVibe/Mcp/Tools/ToolRegistry.cs:~95` — add `"convert_ifc_to_native"` dispatch.
- `bina-ai/app/agents/revit/revit_ai.py` — register the `convert_ifc_to_native` external-execution tool.
- `bina-ai/tests/` — intent/tool-shape test.

---

### Task 1: Neutral data model — `IfcElement`, `NativeStep`, `ConversionReport`

**Files:**
- Create: `BinaVibe/Mcp/Tools/IfcConvert/IfcElement.cs`
- Create: `BinaVibe/Mcp/Tools/IfcConvert/NativeStep.cs`
- Create: `BinaVibe/Mcp/Tools/IfcConvert/ConversionReport.cs`
- Test: `Tests/ConversionReportTests.cs`

**Interfaces:**
- Produces: `IfcEntity` enum `{ Wall, Slab, Column, Beam, Other }`; `IfcElement` record; `NativeStep` record with `Dictionary<string,object?> ToStep()` and `static object BatchArgs(IEnumerable<NativeStep>)`; `ConversionReport` with `Add(IfcElement, NativeStep?)`, counts, `KeptAsIs`, `CreatedTypes`, `Warnings`, `Dictionary<string,object?> ToDict()`.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/ConversionReportTests.cs
using System.Collections.Generic;
using BinaVibe.Mcp.Tools.IfcConvert;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class ConversionReportTests
    {
        static IfcElement Wall(bool convertible, string? reason = null) => new()
        {
            SourceId = 1, Entity = IfcEntity.Wall, Convertible = convertible, Reason = reason,
            StartMm = new[] { 0.0, 0, 0 }, EndMm = new[] { 3000.0, 0, 0 }, HeightMm = 3000, ThicknessMm = 200,
        };

        [Fact]
        public void Add_ConvertedAndKept_TalliesAndReportsReasons()
        {
            var report = new ConversionReport();
            report.Add(Wall(true), new NativeStep("create_wall", new() { ["level"] = "L1" }));
            report.Add(Wall(false, "curved geometry"), null);

            Assert.Equal(1, report.ConvertedCounts["Wall"]);
            Assert.Single(report.KeptAsIs);
            Assert.Equal("curved geometry", report.KeptAsIs[0].Reason);

            var dict = report.ToDict();
            Assert.True(dict.ContainsKey("converted"));
            Assert.True(dict.ContainsKey("keptAsIs"));
        }

        [Fact]
        public void NativeStep_BatchArgs_WrapsStepsForExecuteRevitBatch()
        {
            var steps = new List<NativeStep> { new("create_wall", new() { ["level"] = "L1" }) };
            var args = NativeStep.BatchArgs(steps);
            var json = System.Text.Json.JsonSerializer.Serialize(args);
            Assert.Contains("\"steps\"", json);
            Assert.Contains("\"tool\":\"create_wall\"", json);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter ConversionReportTests`
Expected: FAIL — types `IfcElement`, `NativeStep`, `ConversionReport` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// BinaVibe/Mcp/Tools/IfcConvert/IfcElement.cs
namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public enum IfcEntity { Wall, Slab, Column, Beam, Other }

    /// <summary>Transport-neutral description of one imported-IFC element. Primitives only
    /// (mm, [x,y,z] arrays) so the mapper is pure + unit-testable without Revit.</summary>
    public sealed record IfcElement
    {
        public long SourceId { get; init; }
        public IfcEntity Entity { get; init; }
        public double[]? StartMm { get; init; }        // wall/beam axis start [x,y,z]
        public double[]? EndMm { get; init; }          // wall/beam axis end
        public double[]? PointMm { get; init; }        // column insertion point
        public double[][]? BoundaryMm { get; init; }   // slab boundary loop [[x,y,z],...]
        public double HeightMm { get; init; }
        public double ThicknessMm { get; init; }
        public string? Material { get; init; }
        public string? Level { get; init; }
        public string? IfcTypeName { get; init; }      // name carried on the IFC element
        public bool Convertible { get; init; } = true;
        public string? Reason { get; init; }           // set when Convertible == false
    }
}
```

```csharp
// BinaVibe/Mcp/Tools/IfcConvert/NativeStep.cs
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    /// <summary>One step in an execute_revit_batch plan: a tool name + its args dict.</summary>
    public sealed record NativeStep(string Tool, Dictionary<string, object?> Args)
    {
        public Dictionary<string, object?> ToStep() => new() { ["tool"] = Tool, ["args"] = Args };

        /// <summary>Wrap steps into the { steps: [...] } shape BatchExecutor.Run expects.</summary>
        public static object BatchArgs(IEnumerable<NativeStep> steps) =>
            new Dictionary<string, object?> { ["steps"] = steps.Select(s => s.ToStep()).ToList() };
    }
}
```

```csharp
// BinaVibe/Mcp/Tools/IfcConvert/ConversionReport.cs
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public sealed record KeptElement(long SourceId, string Entity, string Reason);

    /// <summary>Aggregated, transport-safe result. Preview and Build build the SAME shape.</summary>
    public sealed class ConversionReport
    {
        public Dictionary<string, int> ConvertedCounts { get; } = new()
            { ["Wall"] = 0, ["Slab"] = 0, ["Column"] = 0, ["Beam"] = 0 };
        public List<KeptElement> KeptAsIs { get; } = new();
        public List<string> CreatedTypes { get; } = new();
        public List<string> Warnings { get; } = new();

        /// <summary>Record one element. step != null => converted; step == null => kept as-is.</summary>
        public void Add(IfcElement el, NativeStep? step)
        {
            if (step != null)
                ConvertedCounts[el.Entity.ToString()] = ConvertedCounts.GetValueOrDefault(el.Entity.ToString()) + 1;
            else
                KeptAsIs.Add(new KeptElement(el.SourceId, el.Entity.ToString(), el.Reason ?? "unknown"));
        }

        public Dictionary<string, object?> ToDict() => new()
        {
            ["converted"] = ConvertedCounts,
            ["keptAsIs"] = KeptAsIs,
            ["createdTypes"] = CreatedTypes,
            ["warnings"] = Warnings,
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter ConversionReportTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add BinaVibe/Mcp/Tools/IfcConvert/IfcElement.cs BinaVibe/Mcp/Tools/IfcConvert/NativeStep.cs BinaVibe/Mcp/Tools/IfcConvert/ConversionReport.cs Tests/ConversionReportTests.cs
git commit -m "feat(ifc): neutral data model — IfcElement, NativeStep, ConversionReport"
```

---

### Task 2: Type resolution — `ITypeResolver` + `MatchOrCreateTypeResolver`

**Files:**
- Create: `BinaVibe/Mcp/Tools/IfcConvert/ITypeResolver.cs`
- Create: `BinaVibe/Mcp/Tools/IfcConvert/MatchOrCreateTypeResolver.cs`
- Test: `Tests/MatchOrCreateTypeResolverTests.cs`

**Interfaces:**
- Consumes: `IfcEntity`, `IfcElement` (Task 1).
- Produces: `ExistingType(string Name, double ThicknessMm)` record; `TypeResolution(string TypeName, bool NeedsCreate, double CreateThicknessMm, string? CreateMaterial)` record; `ITypeResolver.Resolve(IfcEntity entity, double thicknessMm, string? ifcName, string? material, IReadOnlyList<ExistingType> existing)`. `MatchOrCreateTypeResolver` matches when `|existing.ThicknessMm - thicknessMm| <= ToleranceMm` (default 2.0), preferring an exact name match; else returns a create-spec with a deterministic name `IFC {entity} {thickness}mm`.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/MatchOrCreateTypeResolverTests.cs
using System.Collections.Generic;
using BinaVibe.Mcp.Tools.IfcConvert;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class MatchOrCreateTypeResolverTests
    {
        readonly ITypeResolver _r = new MatchOrCreateTypeResolver(toleranceMm: 2.0);

        [Fact]
        public void Resolve_ThicknessWithinTolerance_MatchesExistingType()
        {
            var existing = new List<ExistingType> { new("Generic - 200mm", 200.0), new("Generic - 100mm", 100.0) };
            var res = _r.Resolve(IfcEntity.Wall, 201.0, "Batu Bata", null, existing);
            Assert.False(res.NeedsCreate);
            Assert.Equal("Generic - 200mm", res.TypeName);
        }

        [Fact]
        public void Resolve_ExactNameMatch_PreferredOverThicknessOnly()
        {
            var existing = new List<ExistingType> { new("Batu Bata", 200.0), new("Generic - 200mm", 200.0) };
            var res = _r.Resolve(IfcEntity.Wall, 200.0, "Batu Bata", null, existing);
            Assert.Equal("Batu Bata", res.TypeName);
        }

        [Fact]
        public void Resolve_NoMatch_ReturnsDeterministicCreateSpec()
        {
            var existing = new List<ExistingType> { new("Generic - 100mm", 100.0) };
            var res = _r.Resolve(IfcEntity.Wall, 250.0, "Concrete", "Concrete", existing);
            Assert.True(res.NeedsCreate);
            Assert.Equal("IFC Wall 250mm", res.TypeName);
            Assert.Equal(250.0, res.CreateThicknessMm);
            Assert.Equal("Concrete", res.CreateMaterial);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter MatchOrCreateTypeResolverTests`
Expected: FAIL — `ITypeResolver`/`MatchOrCreateTypeResolver`/`ExistingType`/`TypeResolution` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// BinaVibe/Mcp/Tools/IfcConvert/ITypeResolver.cs
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public sealed record ExistingType(string Name, double ThicknessMm);
    public sealed record TypeResolution(string TypeName, bool NeedsCreate, double CreateThicknessMm, string? CreateMaterial);

    /// <summary>Picks the native Revit type for an IFC element. v1 = MatchOrCreate;
    /// v2 = a JkrFamilyLibraryResolver drops in here without touching reader/mapper.</summary>
    public interface ITypeResolver
    {
        TypeResolution Resolve(IfcEntity entity, double thicknessMm, string? ifcName, string? material,
                               IReadOnlyList<ExistingType> existing);
    }
}
```

```csharp
// BinaVibe/Mcp/Tools/IfcConvert/MatchOrCreateTypeResolver.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public sealed class MatchOrCreateTypeResolver : ITypeResolver
    {
        readonly double _tolMm;
        public MatchOrCreateTypeResolver(double toleranceMm = 2.0) => _tolMm = toleranceMm;

        public TypeResolution Resolve(IfcEntity entity, double thicknessMm, string? ifcName, string? material,
                                      IReadOnlyList<ExistingType> existing)
        {
            // 1. Exact name match (case-insensitive) among thickness-compatible types.
            if (!string.IsNullOrWhiteSpace(ifcName))
            {
                var named = existing.FirstOrDefault(t =>
                    string.Equals(t.Name, ifcName, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(t.ThicknessMm - thicknessMm) <= _tolMm);
                if (named != null) return new TypeResolution(named.Name, false, 0, null);
            }
            // 2. Closest thickness within tolerance.
            var byThk = existing
                .Where(t => Math.Abs(t.ThicknessMm - thicknessMm) <= _tolMm)
                .OrderBy(t => Math.Abs(t.ThicknessMm - thicknessMm))
                .FirstOrDefault();
            if (byThk != null) return new TypeResolution(byThk.Name, false, 0, null);
            // 3. Create a new type, deterministic name.
            var name = $"IFC {entity} {Math.Round(thicknessMm)}mm";
            return new TypeResolution(name, true, thicknessMm, material);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter MatchOrCreateTypeResolverTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add BinaVibe/Mcp/Tools/IfcConvert/ITypeResolver.cs BinaVibe/Mcp/Tools/IfcConvert/MatchOrCreateTypeResolver.cs Tests/MatchOrCreateTypeResolverTests.cs
git commit -m "feat(ifc): match-or-create type resolver behind ITypeResolver seam"
```

---

### Task 3: `IfcMapper` — pure IfcElement → NativeStep for all four entities

**Files:**
- Create: `BinaVibe/Mcp/Tools/IfcConvert/IfcMapper.cs`
- Test: `Tests/IfcMapperTests.cs`

**Interfaces:**
- Consumes: `IfcElement`, `IfcEntity` (T1); `ITypeResolver`, `TypeResolution`, `ExistingType` (T2); `NativeStep` (T1).
- Produces: `IfcMapper(ITypeResolver resolver)`; `MapResult Map(IfcElement el, IReadOnlyList<ExistingType> existingTypes)` where `MapResult(NativeStep? Step, TypeResolution? Resolution)`. `Step == null` iff `el.Convertible == false`. Emits `create_wall` (`start_mm`,`end_mm`,`level`,`type_name`,`height_mm`), `create_floor` (`boundary_mm`,`level`,`type_name`), `create_beam` (`start_mm`,`end_mm`,`level`,`beam_type_name`), and a `create_column`-shaped step (`point_mm`,`level`,`type_name`) for columns.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/IfcMapperTests.cs
using System.Collections.Generic;
using BinaVibe.Mcp.Tools.IfcConvert;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class IfcMapperTests
    {
        readonly IfcMapper _m = new(new MatchOrCreateTypeResolver(2.0));
        static readonly List<ExistingType> NoTypes = new();

        [Fact]
        public void Map_Wall_EmitsCreateWallWithAxisLevelHeightAndType()
        {
            var el = new IfcElement
            {
                SourceId = 5, Entity = IfcEntity.Wall, Level = "Aras 01",
                StartMm = new[] { 0.0, 0, 0 }, EndMm = new[] { 4000.0, 0, 0 },
                HeightMm = 3000, ThicknessMm = 200, IfcTypeName = "Batu Bata",
            };
            var res = _m.Map(el, NoTypes);
            Assert.NotNull(res.Step);
            Assert.Equal("create_wall", res.Step!.Tool);
            Assert.Equal("Aras 01", res.Step.Args["level"]);
            Assert.Equal(3000.0, res.Step.Args["height_mm"]);
            Assert.Equal("IFC Wall 200mm", res.Step.Args["type_name"]); // created (no existing types)
            Assert.True(res.Resolution!.NeedsCreate);
        }

        [Fact]
        public void Map_Slab_EmitsCreateFloorWithBoundaryLoop()
        {
            var el = new IfcElement
            {
                SourceId = 6, Entity = IfcEntity.Slab, Level = "Aras 01", ThicknessMm = 150,
                BoundaryMm = new[]
                {
                    new[] { 0.0, 0, 0 }, new[] { 4000.0, 0, 0 }, new[] { 4000.0, 3000, 0 }, new[] { 0.0, 3000, 0 },
                },
            };
            var res = _m.Map(el, NoTypes);
            Assert.Equal("create_floor", res.Step!.Tool);
            Assert.True(res.Step.Args.ContainsKey("boundary_mm"));
        }

        [Fact]
        public void Map_Beam_EmitsCreateBeamWithBeamTypeName()
        {
            var el = new IfcElement
            {
                SourceId = 7, Entity = IfcEntity.Beam, Level = "Aras 01", ThicknessMm = 300,
                StartMm = new[] { 0.0, 0, 3000 }, EndMm = new[] { 4000.0, 0, 3000 }, IfcTypeName = "RC Beam",
            };
            var res = _m.Map(el, NoTypes);
            Assert.Equal("create_beam", res.Step!.Tool);
            Assert.True(res.Step.Args.ContainsKey("beam_type_name"));
        }

        [Fact]
        public void Map_UnconvertibleElement_ReturnsNullStep()
        {
            var el = new IfcElement { SourceId = 8, Entity = IfcEntity.Wall, Convertible = false, Reason = "curved geometry" };
            var res = _m.Map(el, NoTypes);
            Assert.Null(res.Step);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter IfcMapperTests`
Expected: FAIL — `IfcMapper`/`MapResult` undefined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// BinaVibe/Mcp/Tools/IfcConvert/IfcMapper.cs
using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public sealed record MapResult(NativeStep? Step, TypeResolution? Resolution);

    /// <summary>PURE: neutral IfcElement + resolved type -> an execute_revit_batch step.
    /// No Revit API. Un-convertible elements map to (null, null) so the converter keeps
    /// the original DirectShape and reports it.</summary>
    public sealed class IfcMapper
    {
        readonly ITypeResolver _resolver;
        public IfcMapper(ITypeResolver resolver) => _resolver = resolver;

        public MapResult Map(IfcElement el, IReadOnlyList<ExistingType> existingTypes)
        {
            if (!el.Convertible) return new MapResult(null, null);

            var res = _resolver.Resolve(el.Entity, el.ThicknessMm, el.IfcTypeName, el.Material, existingTypes);
            var level = el.Level ?? throw new ArgumentException($"element {el.SourceId}: missing level");

            NativeStep step = el.Entity switch
            {
                IfcEntity.Wall => new NativeStep("create_wall", new()
                {
                    ["start_mm"] = el.StartMm, ["end_mm"] = el.EndMm, ["level"] = level,
                    ["height_mm"] = el.HeightMm, ["type_name"] = res.TypeName,
                }),
                IfcEntity.Slab => new NativeStep("create_floor", new()
                {
                    ["boundary_mm"] = el.BoundaryMm, ["level"] = level, ["type_name"] = res.TypeName,
                }),
                IfcEntity.Column => new NativeStep("create_column", new()
                {
                    ["point_mm"] = el.PointMm, ["level"] = level, ["type_name"] = res.TypeName,
                }),
                IfcEntity.Beam => new NativeStep("create_beam", new()
                {
                    ["start_mm"] = el.StartMm, ["end_mm"] = el.EndMm, ["level"] = level,
                    ["beam_type_name"] = res.TypeName,
                }),
                _ => throw new ArgumentException($"element {el.SourceId}: unsupported entity {el.Entity}"),
            };
            return new MapResult(step, res);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter IfcMapperTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add BinaVibe/Mcp/Tools/IfcConvert/IfcMapper.cs Tests/IfcMapperTests.cs
git commit -m "feat(ifc): pure IfcMapper — IfcElement -> execute_revit_batch step for walls/slabs/columns/beams"
```

---

### Task 4: `create_column` mutator + registry wiring

**Files:**
- Modify: `BinaVibe/Mcp/Tools/MutatorsStructure.cs` (add `CreateColumn`)
- Modify: `BinaVibe/Mcp/Tools/ToolRegistry.cs:~104-134` (add `"create_column"` case)

**Interfaces:**
- Consumes: existing `ArgsHelp.GetPointMm`/`GetString`, `NewFamilyInstance` + `StructuralType.Column` pattern already in `MutatorsStructure.cs`.
- Produces: `MutatorsStructure.CreateColumn(Document doc, JsonElement args)` reading `point_mm`,`level`,`type_name` → `{ ok, created_id, level, type_name }`; dispatched as `"create_column"`.

> **Why:** the mapper emits a `create_column` step, but only `create_wall`/`create_floor`/`create_beam` exist today (`ToolRegistry.cs:104/130/134`). Columns need a structural `FamilyInstance` with `StructuralType.Column`.

- [ ] **Step 1: Add the mutator** — mirror `CreateBeam` in `MutatorsStructure.cs`, using the existing structural-placement idiom:

```csharp
// Append to MutatorsStructure (MutatorsStructure.cs)
public static Dictionary<string, object?> CreateColumn(Document doc, JsonElement args)
{
    var p = ArgsHelp.GetPointMm(args, "point_mm") ?? throw new ArgumentException("missing point_mm [x,y,z]");
    var levelName = ArgsHelp.GetString(args, "level") ?? throw new ArgumentException("missing level");
    var typeName = ArgsHelp.GetString(args, "type_name");

    var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
        .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"level '{levelName}' not found");

    var sym = new FilteredElementCollector(doc)
        .OfCategory(BuiltInCategory.OST_StructuralColumns).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
        .FirstOrDefault(s => typeName == null || string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase))
        ?? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns)
            .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault()
        ?? throw new ArgumentException("no structural column family loaded");

    using var tx = new Transaction(doc, "BinaVibe: create_column");
    TxGuard.StartSwallowing(tx);
    try
    {
        if (!sym.IsActive) sym.Activate();
        var col = doc.Create.NewFamilyInstance(p, sym, level,
            Autodesk.Revit.DB.Structure.StructuralType.Column);
        tx.Commit();
        return new Dictionary<string, object?>
        { ["ok"] = true, ["created_id"] = col.Id.Value, ["level"] = levelName, ["type_name"] = sym.Name };
    }
    catch { tx.RollBack(); throw; }
}
```

- [ ] **Step 2: Register it** in `ToolRegistry.cs` next to `create_beam` (~L134):

```csharp
"create_column"                 => MutatorsStructure.CreateColumn(doc, args),
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build RevitWebAppSync.csproj`
Expected: Build succeeded (no errors from the new method / case).

- [ ] **Step 4: Commit**

```bash
git add BinaVibe/Mcp/Tools/MutatorsStructure.cs BinaVibe/Mcp/Tools/ToolRegistry.cs
git commit -m "feat(ifc): create_column structural mutator + registry dispatch"
```

> **Note (staged):** exercising `CreateColumn` end-to-end needs Revit with a structural column family loaded — covered by the Task 7 integration checklist, not `dotnet test`.

---

### Task 5: `IfcReader` — Revit API: imported-IFC elements → `IfcElement`s + existing types

**Files:**
- Create: `BinaVibe/Mcp/Tools/IfcConvert/IfcReader.cs`

**Interfaces:**
- Consumes: `IfcElement`, `IfcEntity`, `ExistingType` (T1/T2); Revit API (`Document`, `DirectShape`, `GeometryElement`, `Solid`, `Level`, `WallType`).
- Produces: `IfcReader`; `List<IfcElement> Read(Document doc, ConvertScope scope)`; `List<ExistingType> ReadExistingWallTypes(Document doc)` (+ floor/beam/column type readers); `enum ConvertScope { Whole, ActiveLevel, Selection }` and an overload taking selected `ElementId`s.

> **Revit-coupled** — cannot run under `dotnet test`. Verified via the Task 7 staged checklist. Keep geometry-derivation helpers (e.g. `AxisFromSolid(Solid) -> (double[] start, double[] end, double height, double thickness, bool ok)`) as **static, Revit-type-in/primitives-out** methods so their math is unit-testable where practical.

- [ ] **Step 1: Implement the reader**

```csharp
// BinaVibe/Mcp/Tools/IfcConvert/IfcReader.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public enum ConvertScope { Whole, ActiveLevel, Selection }

    /// <summary>Revit API: reads imported-IFC DirectShapes (which carry the IFC entity
    /// type + Psets) and turns each into a neutral IfcElement. Never parses raw .ifc.</summary>
    public sealed class IfcReader
    {
        const double FtToMm = 304.8;

        public List<IfcElement> Read(Document doc, ConvertScope scope, ICollection<ElementId>? selection = null)
        {
            IEnumerable<Element> shapes = new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape)).Cast<Element>();
            if (scope == ConvertScope.Selection && selection != null)
                shapes = shapes.Where(e => selection.Contains(e.Id));

            var result = new List<IfcElement>();
            foreach (var e in shapes)
            {
                var entity = ClassifyEntity(e);           // reads IfcExportAs / IFC_EXPORT_ELEMENT param
                if (entity == null) continue;              // not an IFC-tagged element
                result.Add(BuildElement(doc, e, entity.Value));
            }
            return result;
        }

        static IfcEntity? ClassifyEntity(Element e)
        {
            // Imported IFC carries the source entity in "IfcExportAs" / "Export Type" params,
            // or the DirectShape category maps to the entity. Prefer the param, fall back to category.
            var tag = e.LookupParameter("IfcExportAs")?.AsString()
                      ?? e.LookupParameter("Export Type as")?.AsString();
            var s = (tag ?? e.Category?.Name ?? "").ToLowerInvariant();
            if (s.Contains("wall")) return IfcEntity.Wall;
            if (s.Contains("slab") || s.Contains("floor")) return IfcEntity.Slab;
            if (s.Contains("column")) return IfcEntity.Column;
            if (s.Contains("beam")) return IfcEntity.Beam;
            return null;
        }

        IfcElement BuildElement(Document doc, Element e, IfcEntity entity)
        {
            var solid = LargestSolid(e);
            if (solid == null)
                return new IfcElement { SourceId = e.Id.Value, Entity = entity, Convertible = false, Reason = "no solid geometry" };

            var level = NearestLevelName(doc, solid);
            var ifcName = e.Name;
            var material = e.LookupParameter("IfcMaterial")?.AsString();

            try
            {
                switch (entity)
                {
                    case IfcEntity.Wall:
                    {
                        var (start, end, height, thickness, ok) = WallAxisFromSolid(solid);
                        if (!ok) return Kept(e, entity, "wall geometry not a straight extrusion");
                        return new IfcElement { SourceId = e.Id.Value, Entity = entity, StartMm = start, EndMm = end,
                            HeightMm = height, ThicknessMm = thickness, Level = level, IfcTypeName = ifcName, Material = material };
                    }
                    case IfcEntity.Slab:
                    {
                        var (loop, thickness, ok) = SlabBoundaryFromSolid(solid);
                        if (!ok) return Kept(e, entity, "slab boundary not planar");
                        return new IfcElement { SourceId = e.Id.Value, Entity = entity, BoundaryMm = loop,
                            ThicknessMm = thickness, Level = level, IfcTypeName = ifcName, Material = material };
                    }
                    case IfcEntity.Column:
                    {
                        var (point, ok) = InsertionPointFromSolid(solid);
                        if (!ok) return Kept(e, entity, "column insertion point not derivable");
                        return new IfcElement { SourceId = e.Id.Value, Entity = entity, PointMm = point,
                            ThicknessMm = ProfileWidth(solid), Level = level, IfcTypeName = ifcName, Material = material };
                    }
                    case IfcEntity.Beam:
                    {
                        var (start, end, ok) = BeamAxisFromSolid(solid);
                        if (!ok) return Kept(e, entity, "beam axis not a straight line");
                        return new IfcElement { SourceId = e.Id.Value, Entity = entity, StartMm = start, EndMm = end,
                            ThicknessMm = ProfileWidth(solid), Level = level, IfcTypeName = ifcName, Material = material };
                    }
                    default: return Kept(e, entity, "unsupported entity");
                }
            }
            catch (Exception ex) { return Kept(e, entity, $"geometry error: {ex.Message}"); }
        }

        static IfcElement Kept(Element e, IfcEntity entity, string reason) =>
            new() { SourceId = e.Id.Value, Entity = entity, Convertible = false, Reason = reason };

        // --- geometry helpers (Revit-in / primitives-out) ---
        static Solid? LargestSolid(Element e)
        {
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            Solid? best = null; double bestVol = 0;
            foreach (var g in e.get_Geometry(opt))
                foreach (var s in Flatten(g))
                    if (s.Volume > bestVol) { best = s; bestVol = s.Volume; }
            return best;
        }
        static IEnumerable<Solid> Flatten(GeometryObject g)
        {
            if (g is Solid s && s.Volume > 1e-6) yield return s;
            else if (g is GeometryInstance gi)
                foreach (var o in gi.GetInstanceGeometry())
                    foreach (var inner in Flatten(o)) yield return inner;
        }

        // The following four return (…, ok=false) when the geometry can't be reduced to a
        // clean native input — that's what drives keep-original+report.
        static (double[] start, double[] end, double height, double thickness, bool ok) WallAxisFromSolid(Solid s)
        {
            var bb = s.GetBoundingBox(); if (bb == null) return (default!, default!, 0, 0, false);
            var min = bb.Min; var max = bb.Max;
            double dx = max.X - min.X, dy = max.Y - min.Y, dz = max.Z - min.Z;
            bool alongX = dx >= dy;
            double length = alongX ? dx : dy, thickness = alongX ? dy : dx;
            if (length < 1e-6 || thickness < 1e-6 || dz < 1e-6) return (default!, default!, 0, 0, false);
            double cx = (min.X + max.X) / 2, cy = (min.Y + max.Y) / 2;
            var start = alongX ? new[] { min.X, cy, min.Z } : new[] { cx, min.Y, min.Z };
            var end   = alongX ? new[] { max.X, cy, min.Z } : new[] { cx, max.Y, min.Z };
            return (Mm(start), Mm(end), dz * FtToMm, thickness * FtToMm, true);
        }
        static (double[][] loop, double thickness, bool ok) SlabBoundaryFromSolid(Solid s)
        {
            // bottom-most horizontal planar face → its outer CurveLoop as [x,y,z] mm points.
            PlanarFace? bottom = null;
            foreach (Face f in s.Faces)
                if (f is PlanarFace pf && Math.Abs(pf.FaceNormal.Z) > 0.99)
                    if (bottom == null || pf.Origin.Z < bottom.Origin.Z) bottom = pf;
            if (bottom == null) return (default!, 0, false);
            var pts = new List<double[]>();
            foreach (var c in bottom.GetEdgesAsCurveLoops().FirstOrDefault() ?? new CurveLoop())
                pts.Add(Mm(new[] { c.GetEndPoint(0).X, c.GetEndPoint(0).Y, c.GetEndPoint(0).Z }));
            if (pts.Count < 3) return (default!, 0, false);
            var bb = s.GetBoundingBox();
            return (pts.ToArray(), (bb.Max.Z - bb.Min.Z) * FtToMm, true);
        }
        static (double[] point, bool ok) InsertionPointFromSolid(Solid s)
        {
            var bb = s.GetBoundingBox(); if (bb == null) return (default!, false);
            var c = (bb.Min + bb.Max) / 2;
            return (Mm(new[] { c.X, c.Y, bb.Min.Z }), true);
        }
        static (double[] start, double[] end, bool ok) BeamAxisFromSolid(Solid s)
        {
            var bb = s.GetBoundingBox(); if (bb == null) return (default!, default!, false);
            var min = bb.Min; var max = bb.Max;
            double dx = max.X - min.X, dy = max.Y - min.Y;
            bool alongX = dx >= dy;
            double cy = (min.Y + max.Y) / 2, cx = (min.X + max.X) / 2, z = (min.Z + max.Z) / 2;
            var start = alongX ? new[] { min.X, cy, z } : new[] { cx, min.Y, z };
            var end   = alongX ? new[] { max.X, cy, z } : new[] { cx, max.Y, z };
            return (Mm(start), Mm(end), true);
        }
        static double ProfileWidth(Solid s)
        {
            var bb = s.GetBoundingBox();
            return Math.Min(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y) * FtToMm;
        }
        static string NearestLevelName(Document doc, Solid s)
        {
            double z = s.GetBoundingBox().Min.Z;
            var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - z)).FirstOrDefault();
            return lvl?.Name ?? "Level 1";
        }
        static double[] Mm(double[] ft) => new[] { ft[0] * FtToMm, ft[1] * FtToMm, ft[2] * FtToMm };

        public List<ExistingType> ReadExistingWallTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Select(t => new ExistingType(t.Name, (t.Width) * FtToMm)).ToList();
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build RevitWebAppSync.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add BinaVibe/Mcp/Tools/IfcConvert/IfcReader.cs
git commit -m "feat(ifc): IfcReader — imported-IFC DirectShapes -> neutral IfcElements (keep-original on bad geometry)"
```

---

### Task 6: `IfcConverter` — orchestrator (Preview + Build)

**Files:**
- Create: `BinaVibe/Mcp/Tools/IfcConvert/IfcConverter.cs`

**Interfaces:**
- Consumes: `IfcReader`, `IfcMapper`, `MatchOrCreateTypeResolver`, `ConversionReport`, `NativeStep`, `BatchExecutor.Run` (existing), `UIApplication`.
- Produces: `IfcConverter`; `Dictionary<string,object?> Preview(UIApplication app, ConvertScope scope, ICollection<ElementId>? sel)`; `Dictionary<string,object?> Build(UIApplication app, ConvertScope scope, ICollection<ElementId>? sel)`. Both return `{ ok, report }`; `Build` also runs the batch and merges its result.

- [ ] **Step 1: Implement the orchestrator**

```csharp
// BinaVibe/Mcp/Tools/IfcConvert/IfcConverter.cs
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public sealed class IfcConverter
    {
        readonly IfcReader _reader = new();
        readonly IfcMapper _mapper = new(new MatchOrCreateTypeResolver(2.0));

        public Dictionary<string, object?> Preview(UIApplication app, ConvertScope scope, ICollection<ElementId>? sel = null)
        {
            var (report, _) = Plan(app, scope, sel);
            return new() { ["ok"] = true, ["mode"] = "preview", ["report"] = report.ToDict() };
        }

        public Dictionary<string, object?> Build(UIApplication app, ConvertScope scope, ICollection<ElementId>? sel = null)
        {
            var (report, steps) = Plan(app, scope, sel);
            if (steps.Count == 0)
                return new() { ["ok"] = true, ["mode"] = "build", ["report"] = report.ToDict(), ["note"] = "nothing convertible" };

            var batchArgs = JsonSerializer.SerializeToElement(NativeStep.BatchArgs(steps));
            var batch = BatchExecutor.Run(app, batchArgs);
            var ok = batch.TryGetValue("ok", out var b) && b is bool bb && bb;
            return new() { ["ok"] = ok, ["mode"] = "build", ["report"] = report.ToDict(), ["batch"] = batch };
        }

        // Shared read+map: the SAME path feeds both preview and build (parity guarantee).
        (ConversionReport report, List<NativeStep> steps) Plan(UIApplication app, ConvertScope scope, ICollection<ElementId>? sel)
        {
            var doc = app.ActiveUIDocument.Document;
            var existing = _reader.ReadExistingWallTypes(doc);   // v1: wall types; floor/beam/column readers land with their creators
            var elements = _reader.Read(doc, scope, sel);
            var report = new ConversionReport();
            var steps = new List<NativeStep>();
            foreach (var el in elements)
            {
                var res = _mapper.Map(el, existing);
                report.Add(el, res.Step);
                if (res.Step != null) steps.Add(res.Step);
                if (res.Resolution?.NeedsCreate == true) report.CreatedTypes.Add(res.Resolution.TypeName);
            }
            return (report, steps);
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build RevitWebAppSync.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add BinaVibe/Mcp/Tools/IfcConvert/IfcConverter.cs
git commit -m "feat(ifc): IfcConverter orchestrator — Preview + atomic Build via BatchExecutor"
```

---

### Task 7: Register `convert_ifc_to_native` in the add-in tool registry + staged integration verification

**Files:**
- Modify: `BinaVibe/Mcp/Tools/ToolRegistry.cs:~95` (add dispatch)

**Interfaces:**
- Consumes: `IfcConverter` (T6), `ConvertScope` (T5).
- Produces: tool `"convert_ifc_to_native"` reading `{ scope: "whole"|"level"|"selection", mode: "preview"|"build" }` → `IfcConverter.Preview`/`Build`.

- [ ] **Step 1: Add dispatch** in `ToolRegistry.Invoke`'s switch (near `execute_revit_batch`, ~L95):

```csharp
"convert_ifc_to_native"  => InvokeIfcConvert(app, args),
```

And a helper in `ToolRegistry`:

```csharp
static Dictionary<string, object?> InvokeIfcConvert(UIApplication app, JsonElement args)
{
    var scope = (args.TryGetProperty("scope", out var s) ? s.GetString() : "whole") switch
    {
        "selection" => IfcConvert.ConvertScope.Selection,
        "level"     => IfcConvert.ConvertScope.ActiveLevel,
        _           => IfcConvert.ConvertScope.Whole,
    };
    var mode = args.TryGetProperty("mode", out var m) ? m.GetString() : "preview";
    var sel = app.ActiveUIDocument.Selection.GetElementIds();
    var conv = new IfcConvert.IfcConverter();
    return mode == "build" ? conv.Build(app, scope, sel) : conv.Preview(app, scope, sel);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build RevitWebAppSync.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Staged integration test (needs Revit — document, do not automate).** Record results in `docs/superpowers/plans/ifc-conversion-uat.md`:
  1. In Revit, Link/Open a small sample IFC (a few walls + one slab + one column + one beam). Confirm they land as DirectShapes.
  2. Call `convert_ifc_to_native{scope:"whole", mode:"preview"}` via the copilot pane. **Expect:** report with per-type counts, `keptAsIs` listing any curved/complex elements, `createdTypes` for new types.
  3. Call `mode:"build"`. **Expect:** native `Wall`/`Floor`/structural `Column`/`Beam` created with correct thickness/type; a single `Ctrl+Z` undoes the whole conversion; curved elements remain DirectShapes.
  4. **Preview/Build parity:** the preview counts equal the build's converted counts.

- [ ] **Step 4: Commit**

```bash
git add BinaVibe/Mcp/Tools/ToolRegistry.cs docs/superpowers/plans/ifc-conversion-uat.md
git commit -m "feat(ifc): convert_ifc_to_native tool dispatch + staged UAT checklist"
```

---

### Task 8: bina-ai copilot trigger — `convert_ifc_to_native` external-execution tool

**Files:**
- Modify: `bina-ai/app/agents/revit/revit_ai.py` (register the tool alongside the other `@tool(external_execution=True)` mutate tools; see the block at `revit_ai.py:~541`)
- Test: `bina-ai/tests/test_ifc_convert_tool.py`

**Interfaces:**
- Consumes: the add-in tool `convert_ifc_to_native{scope, mode}` (T7); the existing external-execution serialization (`serialize_pending`/`apply_results`).
- Produces: a Revit-agent tool `convert_ifc_to_native(scope: str = "whole", mode: str = "preview")` marked `external_execution=True`, so the agent emits it as a single pending tool call the add-in executes. The prompt/intent guidance maps "convert this IFC / make it native / turn the IFC into real Revit" to this tool, `mode="preview"` first.

- [ ] **Step 1: Write the failing test**

```python
# bina-ai/tests/test_ifc_convert_tool.py
from app.agents.revit import revit_ai

def test_convert_ifc_tool_is_registered_and_external():
    names = revit_ai.list_mutate_tool_names()  # existing/added helper returning registered mutate tool names
    assert "convert_ifc_to_native" in names

def test_convert_ifc_tool_defaults_to_preview():
    spec = revit_ai.get_tool_spec("convert_ifc_to_native")
    assert spec["params"]["mode"]["default"] == "preview"
    assert spec["params"]["scope"]["default"] == "whole"
    assert spec["external_execution"] is True
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd bina-ai && uv run pytest tests/test_ifc_convert_tool.py -q`
Expected: FAIL — tool not registered (or the `list_mutate_tool_names`/`get_tool_spec` accessors missing; add thin accessors if the module lacks them, mirroring how the existing mutate tools are enumerated).

- [ ] **Step 3: Register the tool** in `revit_ai.py`, mirroring the existing external-execution mutate tools:

```python
@tool(external_execution=True)
def convert_ifc_to_native(scope: str = "whole", mode: str = "preview") -> dict:
    """Convert imported-IFC elements (walls, floors, columns, beams) into native, editable
    Revit elements. scope: "whole" | "level" | "selection". mode: "preview" (dry-run report,
    ALWAYS run first) | "build" (create native elements, one atomic undo). The add-in does the
    bulk read/map/create; this tool is the single trigger — never enumerate elements yourself.
    Show the preview's per-type counts + kept-as-is list, then call mode="build" only after the
    user confirms."""
    # external_execution: body never runs server-side; the add-in executes convert_ifc_to_native.
    ...
```

Add prompt/intent guidance (in the agent preamble/recipes) so IFC-conversion phrasing routes here with `mode="preview"` first.

- [ ] **Step 4: Run test to verify it passes**

Run: `cd bina-ai && uv run pytest tests/test_ifc_convert_tool.py -q`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add bina-ai/app/agents/revit/revit_ai.py bina-ai/tests/test_ifc_convert_tool.py
git commit -m "feat(ifc): copilot trigger — convert_ifc_to_native external-execution tool (preview-first)"
```

---

### Task 9: Full-suite green + plan-level review

**Files:** none (verification task).

- [ ] **Step 1: Run the C# unit suite**

Run: `dotnet test Tests/Tests.csproj`
Expected: all IfcConvert tests pass (ConversionReport 2, MatchOrCreate 3, IfcMapper 4) + no regression in existing tests.

- [ ] **Step 2: Build the add-in**

Run: `dotnet build RevitWebAppSync.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Run the bina-ai suite touching the new tool**

Run: `cd bina-ai && uv run pytest tests/test_ifc_convert_tool.py -q`
Expected: PASS.

- [ ] **Step 4: Confirm the staged UAT doc exists** (`docs/superpowers/plans/ifc-conversion-uat.md`) and is filled in after a Revit run — the only place walls/floors/columns/beams are proven end-to-end.

- [ ] **Step 5: Commit any doc updates**

```bash
git add -A && git commit -m "chore(ifc): v1 conversion — suite green + UAT recorded"
```

---

## Self-Review

**Spec coverage:** In-Revit deterministic engine (T5/T6) ✓ · structural shell walls/floors/columns/beams (T3/T4/T5) ✓ · read Revit's IFC import, no raw parse (T5) ✓ · match-or-create behind ITypeResolver seam (T2) ✓ · keep-original+report (T3 null-step, T5 Kept(), ConversionReport) ✓ · deterministic, one tool call, preview→confirm→atomic build (T6 Build via BatchExecutor, T8 preview-first) ✓ · Preview/Build parity (T6 shared `Plan`, T7 UAT step 4) ✓ · copilot trigger (T8) ✓. No spec section without a task.

**Placeholder scan:** No TBD/"handle edge cases"/"similar to". Revit-coupled tasks (T5–T7) give full implementation code + an explicit staged UAT (honest: those paths can't run under `dotnet test`). The `create_column` gap the mapper implies is closed by T4 before T5/T6 use it.

**Type consistency:** `IfcElement`/`NativeStep`/`ConversionReport` (T1) used verbatim in T3/T5/T6. `ITypeResolver`/`TypeResolution`/`ExistingType` (T2) consumed by `IfcMapper` (T3) + `IfcConverter` (T6). Tool arg names match the real mutators: `create_wall`{start_mm,end_mm,level,height_mm,type_name}, `create_floor`{boundary_mm,level,type_name}, `create_beam`{start_mm,end_mm,level,beam_type_name}, `create_column`{point_mm,level,type_name} (T4), `execute_revit_batch`{steps:[{tool,args}]} (BatchExecutor.Run). `ConvertScope` (T5) consumed by T6/T7.

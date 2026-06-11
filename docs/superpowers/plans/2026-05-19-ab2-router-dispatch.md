# AB2 — Router Dispatch Rework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dispatch SP3a's 5 vetted tools natively (deterministic C# synthesis, no LLM) via a Tool-first branch in `ResolveActionCode`, backed by a pure Revit-free `VettedToolCode.cs`, with the existing `Type` switch preserved as fallback.

**Architecture:** New `Services/VettedToolCode.cs` (pure; primitive `IDictionary<string,object>` signatures — NOT `RouteAction`, which carries Newtonsoft attrs and would break the Tests compile per the AB1 lesson). 3 new synthesizers (`rename_elements`, `set_parameter`, `export_schedule`) emitting C# that uses only executor-exposed helpers; `open_view`/`select_elements` reuse existing synthesizers; Tool empty/unknown → existing `switch(action.Type)` byte-unchanged.

**Tech Stack:** C# / .NET (`net10.0-windows`, Revit addin), xUnit. **Windows-only; `dotnet` unavailable here → build/test are operator steps; in-session gate = `grep` + source inspection.**

**Spec:** `docs/superpowers/specs/2026-05-19-ab2-router-dispatch-design.md`

**Note on signatures:** the spec illustrated synthesizers as `(RouteAction a)`; this plan uses `(IDictionary<string,object> p)` + a `tool`/`type` string — the faithful realization of the approved "pure, compile-linked, AB1-lesson" decision (RouteAction → Newtonsoft → would break the Tests compile, exactly AB1's CRITICAL). Same behavior; the window unpacks `action.Tool/Type/Params` and passes primitives.

---

### Task 0: Confirm branch

- [ ] **Step 1**

Run:
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git branch --show-current
```
Expected: `feat/sp3b-addin-backend-alignment` (AB1 + AB2 spec already here; no new branch). Confirm `AIAssistantWindow.xaml.cs` and `Tests/Tests.csproj` exist.

---

### Task 1: `VettedToolCode.cs` core (RouteParams, IsAutoRunSafe, TryBuild) + tests

**Files:** Create `Services/VettedToolCode.cs`, `Tests/VettedToolCodeTests.cs`; modify `Tests/Tests.csproj`.

- [ ] **Step 1: Create `Services/VettedToolCode.cs`** with EXACTLY:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Pure, Revit-free synthesizers for SP3a's vetted tools. Primitive
    /// signatures (no RouteAction → no Newtonsoft) so the Tests project can
    /// compile-link this file, exactly like AiUrl.cs (AB1 lesson).
    /// Each Build* returns runnable C# (executor auto-wraps the transaction)
    /// or null when required params are missing → caller falls through.
    /// </summary>
    internal static class VettedToolCode
    {
        internal static string Get(IDictionary<string, object> p, params string[] keys)
        {
            if (p == null) return null;
            foreach (var k in keys)
            {
                if (p.TryGetValue(k, out var v) && v != null)
                {
                    var s = v.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return null;
        }

        internal static bool IsAutoRunSafe(string tool, string type)
        {
            if (!string.IsNullOrEmpty(tool))
                return string.Equals(tool, "open_view", StringComparison.OrdinalIgnoreCase);
            return string.Equals(type, "open_view", StringComparison.OrdinalIgnoreCase);
        }

        internal static string TryBuild(string tool, IDictionary<string, object> p)
        {
            if (string.IsNullOrEmpty(tool)) return null;
            switch (tool.ToLowerInvariant())
            {
                case "rename_elements": return BuildRenameElements(p);
                case "set_parameter":  return BuildSetParameter(p);
                case "export_schedule": return BuildExportSchedule(p);
                default: return null;
            }
        }

        // Strip characters that would break the emitted C# string literal.
        private static string Lit(string s) =>
            (s ?? "").Replace("\\", "").Replace("\"", "");

        internal static string BuildRenameElements(IDictionary<string, object> p) => null;
        internal static string BuildSetParameter(IDictionary<string, object> p) => null;
        internal static string BuildExportSchedule(IDictionary<string, object> p) => null;
    }
}
```

- [ ] **Step 2: Add the compile-link** to `Tests/Tests.csproj`, in the existing `<ItemGroup>` with the other `<Compile Include=...>` links (after the `AiUrl.cs` line):

```xml
    <Compile Include="..\Services\VettedToolCode.cs" Link="VettedToolCode.cs" />
```

- [ ] **Step 3: Create `Tests/VettedToolCodeTests.cs`** with EXACTLY:

```csharp
using System.Collections.Generic;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    public class VettedToolCodeTests
    {
        private static Dictionary<string, object> P(params (string, object)[] kv)
        {
            var d = new Dictionary<string, object>();
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }

        [Fact]
        public void Get_returns_first_non_empty_by_precedence()
        {
            var p = P(("a", ""), ("b", "x"), ("c", "y"));
            Assert.Equal("x", VettedToolCode.Get(p, "a", "b", "c"));
            Assert.Null(VettedToolCode.Get(p, "z"));
            Assert.Null(VettedToolCode.Get(null, "a"));
        }

        [Fact]
        public void IsAutoRunSafe_only_open_view()
        {
            Assert.True(VettedToolCode.IsAutoRunSafe("open_view", ""));
            Assert.True(VettedToolCode.IsAutoRunSafe("", "open_view"));
            Assert.False(VettedToolCode.IsAutoRunSafe("rename_elements", ""));
            Assert.False(VettedToolCode.IsAutoRunSafe("set_parameter", ""));
            Assert.False(VettedToolCode.IsAutoRunSafe("export_schedule", ""));
            Assert.False(VettedToolCode.IsAutoRunSafe("select_elements", ""));
            Assert.False(VettedToolCode.IsAutoRunSafe("", "execute_code"));
        }

        [Fact]
        public void TryBuild_null_for_non_new_tools()
        {
            Assert.Null(VettedToolCode.TryBuild("open_view", P(("view_name", "L1"))));
            Assert.Null(VettedToolCode.TryBuild("select_elements", P(("target_category", "Walls"))));
            Assert.Null(VettedToolCode.TryBuild("code", null));
            Assert.Null(VettedToolCode.TryBuild("", null));
            Assert.Null(VettedToolCode.TryBuild("bogus", null));
        }
    }
}
```

- [ ] **Step 4: In-session gate** (no dotnet):
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
grep -c 'using Autodesk\|RevitWebAppSync.Models\|Newtonsoft' Services/VettedToolCode.cs   # 0 (pure)
grep -n 'VettedToolCode.cs' Tests/Tests.csproj                                            # the new <Compile> line
grep -c 'internal static string TryBuild\|internal static bool IsAutoRunSafe\|internal static string Get' Services/VettedToolCode.cs  # 3
```
Expected: `0`, the compile line present, `3`.

- [ ] **Step 5: Commit**
```bash
git add Services/VettedToolCode.cs Tests/VettedToolCodeTests.cs Tests/Tests.csproj
git commit -m "feat(ab2): VettedToolCode core (Get/IsAutoRunSafe/TryBuild) + tests"
```

---

### Task 2: `BuildRenameElements`

**Files:** modify `Services/VettedToolCode.cs`, `Tests/VettedToolCodeTests.cs`.

- [ ] **Step 1: Append tests** to `VettedToolCodeTests.cs` (inside the class):

```csharp
        [Fact]
        public void BuildRenameElements_requires_params()
        {
            Assert.Null(VettedToolCode.BuildRenameElements(P(("target_category", "Walls"))));
            Assert.Null(VettedToolCode.BuildRenameElements(P(("find", "A"), ("replace", "B"))));
        }

        [Fact]
        public void BuildRenameElements_emits_expected()
        {
            var c = VettedToolCode.BuildRenameElements(
                P(("target_category", "Walls"), ("find", "EXT_"), ("replace", "E_"), ("scope", "Level 1")));
            Assert.NotNull(c);
            Assert.Contains("Walls", c);
            Assert.Contains("EXT_", c);
            Assert.Contains("E_", c);
            Assert.Contains("Level 1", c);
            Assert.Contains(".Name", c);
        }
```

- [ ] **Step 2: Replace** the stub `internal static string BuildRenameElements(IDictionary<string, object> p) => null;` with:

```csharp
        internal static string BuildRenameElements(IDictionary<string, object> p)
        {
            var cat = Get(p, "target_category", "category");
            var find = Get(p, "find");
            var repl = Get(p, "replace");
            var scope = Get(p, "scope");
            if (cat == null || find == null || repl == null) return null;
            string c = Lit(cat), f = Lit(find), r = Lit(repl), sc = Lit(scope);
            var sb = new StringBuilder();
            sb.AppendLine($"var __cat = doc.Settings.Categories.Cast<Category>()");
            sb.AppendLine($"    .FirstOrDefault(x => x != null && string.Equals(x.Name, \"{c}\", StringComparison.OrdinalIgnoreCase));");
            sb.AppendLine("var __els = __cat == null ? new List<Element>() : new FilteredElementCollector(doc)");
            sb.AppendLine("    .OfCategoryId(__cat.Id).WhereElementIsNotElementType().Cast<Element>().ToList();");
            if (!string.IsNullOrEmpty(sc))
            {
                sb.AppendLine($"var __lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()");
                sb.AppendLine($"    .FirstOrDefault(l => string.Equals(l.Name, \"{sc}\", StringComparison.OrdinalIgnoreCase));");
                sb.AppendLine("if (__lvl != null) __els = __els.Where(e => e.LevelId == __lvl.Id).ToList();");
            }
            sb.AppendLine("int __n = 0;");
            sb.AppendLine("foreach (var __e in __els) {");
            sb.AppendLine("  try {");
            sb.AppendLine("    var __o = __e.Name;");
            sb.AppendLine($"    if (!string.IsNullOrEmpty(__o) && __o.IndexOf(\"{f}\", StringComparison.Ordinal) >= 0) {{");
            sb.AppendLine($"      __e.Name = __o.Replace(\"{f}\", \"{r}\"); __n++;");
            sb.AppendLine("    }");
            sb.AppendLine("  } catch { }");
            sb.AppendLine("}");
            sb.AppendLine($"ShowMessage(\"Renamed\", __n + \" {c} element(s)\");");
            return sb.ToString();
        }
```

- [ ] **Step 3: Gate**
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
grep -c 'BuildRenameElements(IDictionary' Services/VettedToolCode.cs   # 1 (the impl, stub gone)
grep -c '=> null;' Services/VettedToolCode.cs                          # 2 (set_parameter + export stubs remain)
```
Expected: `1`, `2`.

- [ ] **Step 4: Commit**
```bash
git add Services/VettedToolCode.cs Tests/VettedToolCodeTests.cs
git commit -m "feat(ab2): BuildRenameElements synthesizer"
```

---

### Task 3: `BuildSetParameter`

**Files:** modify `Services/VettedToolCode.cs`, `Tests/VettedToolCodeTests.cs`.

- [ ] **Step 1: Append tests**:

```csharp
        [Fact]
        public void BuildSetParameter_requires_params()
        {
            Assert.Null(VettedToolCode.BuildSetParameter(P(("target_category", "Doors"))));
            Assert.Null(VettedToolCode.BuildSetParameter(P(("parameter_name", "X"), ("value", "1"))));
        }

        [Fact]
        public void BuildSetParameter_emits_storage_type_branches()
        {
            var c = VettedToolCode.BuildSetParameter(
                P(("target_category", "Doors"), ("parameter_name", "Fire Rating"), ("value", "2 HR")));
            Assert.NotNull(c);
            Assert.Contains("Doors", c);
            Assert.Contains("Fire Rating", c);
            Assert.Contains("LookupParameter", c);
            Assert.Contains("StorageType.String", c);
            Assert.Contains("StorageType.Integer", c);
            Assert.Contains("StorageType.Double", c);
        }
```

- [ ] **Step 2: Replace** the `BuildSetParameter` stub with:

```csharp
        internal static string BuildSetParameter(IDictionary<string, object> p)
        {
            var cat = Get(p, "target_category", "category");
            var name = Get(p, "parameter_name", "parameter", "param");
            var val = Get(p, "value");
            if (cat == null || name == null || val == null) return null;
            string c = Lit(cat), pn = Lit(name), v = Lit(val);
            var sb = new StringBuilder();
            sb.AppendLine($"var __cat = doc.Settings.Categories.Cast<Category>()");
            sb.AppendLine($"    .FirstOrDefault(x => x != null && string.Equals(x.Name, \"{c}\", StringComparison.OrdinalIgnoreCase));");
            sb.AppendLine("var __els = __cat == null ? new List<Element>() : new FilteredElementCollector(doc)");
            sb.AppendLine("    .OfCategoryId(__cat.Id).WhereElementIsNotElementType().Cast<Element>().ToList();");
            sb.AppendLine("int __n = 0;");
            sb.AppendLine("foreach (var __e in __els) {");
            sb.AppendLine($"  var __p = __e.LookupParameter(\"{pn}\");");
            sb.AppendLine("  if (__p == null || __p.IsReadOnly) continue;");
            sb.AppendLine("  try {");
            sb.AppendLine("    switch (__p.StorageType) {");
            sb.AppendLine($"      case StorageType.String: __p.Set(\"{v}\"); __n++; break;");
            sb.AppendLine($"      case StorageType.Integer: {{ if (int.TryParse(\"{v}\", out var __i)) {{ __p.Set(__i); __n++; }} else if (bool.TryParse(\"{v}\", out var __b)) {{ __p.Set(__b ? 1 : 0); __n++; }} break; }}");
            sb.AppendLine($"      case StorageType.Double: {{ if (double.TryParse(\"{v}\", out var __d)) {{ __p.Set(__d); __n++; }} break; }}");
            sb.AppendLine("      default: break;");
            sb.AppendLine("    }");
            sb.AppendLine("  } catch { }");
            sb.AppendLine("}");
            sb.AppendLine($"ShowMessage(\"Updated\", __n + \" {c} element(s)\");");
            return sb.ToString();
        }
```

- [ ] **Step 3: Gate**
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
grep -c '=> null;' Services/VettedToolCode.cs   # 1 (only export stub remains)
```
Expected: `1`.

- [ ] **Step 4: Commit**
```bash
git add Services/VettedToolCode.cs Tests/VettedToolCodeTests.cs
git commit -m "feat(ab2): BuildSetParameter synthesizer"
```

---

### Task 4: `BuildExportSchedule`

**Files:** modify `Services/VettedToolCode.cs`, `Tests/VettedToolCodeTests.cs`.

- [ ] **Step 1: Append tests**:

```csharp
        [Fact]
        public void BuildExportSchedule_requires_name()
        {
            Assert.Null(VettedToolCode.BuildExportSchedule(P(("format", "csv"))));
        }

        [Fact]
        public void BuildExportSchedule_csv_and_xlsx()
        {
            var csv = VettedToolCode.BuildExportSchedule(P(("schedule_name", "Door Schedule")));
            Assert.NotNull(csv);
            Assert.Contains("ViewSchedule", csv);
            Assert.Contains("Door Schedule", csv);
            Assert.Contains("WriteAllLines", csv);

            var xl = VettedToolCode.BuildExportSchedule(
                P(("schedule_name", "Door Schedule"), ("format", "xlsx")));
            Assert.NotNull(xl);
            Assert.Contains("WriteExcel", xl);
        }
```

- [ ] **Step 2: Replace** the `BuildExportSchedule` stub with:

```csharp
        internal static string BuildExportSchedule(IDictionary<string, object> p)
        {
            var name = Get(p, "schedule_name", "name");
            if (name == null) return null;
            var fmt = (Get(p, "format") ?? "csv").ToLowerInvariant();
            bool xlsx = fmt.Contains("xls");
            var outPath = Get(p, "output_path");
            string n = Lit(name);
            string file = (n.Replace(" ", "_")) + (xlsx ? ".xlsx" : ".csv");
            var sb = new StringBuilder();
            sb.AppendLine($"var __s = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()");
            sb.AppendLine($"    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, \"{n}\", StringComparison.OrdinalIgnoreCase));");
            sb.AppendLine($"if (__s == null) __s = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()");
            sb.AppendLine($"    .FirstOrDefault(v => !v.IsTemplate && v.Name != null && v.Name.IndexOf(\"{n}\", StringComparison.OrdinalIgnoreCase) >= 0);");
            sb.AppendLine($"if (__s == null) {{ ShowMessage(\"Not found\", \"No schedule matching '{n}'.\"); }}");
            sb.AppendLine("else {");
            sb.AppendLine("  var __b = __s.GetTableData().GetSectionData(SectionType.Body);");
            sb.AppendLine("  var __data = new List<List<string>>();");
            sb.AppendLine("  for (int __r = 0; __r < __b.NumberOfRows; __r++) {");
            sb.AppendLine("    var __row = new List<string>();");
            sb.AppendLine("    for (int __col = 0; __col < __b.NumberOfColumns; __col++)");
            sb.AppendLine("      __row.Add(__s.GetCellText(SectionType.Body, __r, __col) ?? \"\");");
            sb.AppendLine("    __data.Add(__row);");
            sb.AppendLine("  }");
            if (!string.IsNullOrEmpty(outPath))
                sb.AppendLine($"  var __path = @\"{Lit(outPath)}\";");
            else
                sb.AppendLine($"  var __path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), \"{file}\");");
            if (xlsx)
            {
                sb.AppendLine("  var __hdr = __data.Count > 0 ? __data[0] : new List<string>();");
                sb.AppendLine("  var __rows = __data.Count > 1 ? __data.Skip(1).ToList() : new List<List<string>>();");
                sb.AppendLine("  WriteExcel(__path, __hdr, __rows);");
            }
            else
            {
                sb.AppendLine("  System.IO.File.WriteAllLines(__path, __data.Select(rw =>");
                sb.AppendLine("    string.Join(\",\", rw.Select(cell => \"\\\"\" + (cell ?? \"\").Replace(\"\\\"\", \"\\\"\\\"\") + \"\\\"\"))));");
            }
            sb.AppendLine("  ShowMessage(\"Exported\", __s.Name + \" -> \" + __path);");
            sb.AppendLine("}");
            return sb.ToString();
        }
```

- [ ] **Step 3: Gate**
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
grep -c '=> null;' Services/VettedToolCode.cs   # 0 (all 3 implemented)
```
Expected: `0`.

- [ ] **Step 4: Commit**
```bash
git add Services/VettedToolCode.cs Tests/VettedToolCodeTests.cs
git commit -m "feat(ab2): BuildExportSchedule synthesizer"
```

---

### Task 5: Wire `ResolveActionCode` (Tool-first) + auto-run + select reuse

**Files:** modify `AIAssistantWindow.xaml.cs`.

- [ ] **Step 1: Tool-first branch.** In `ResolveActionCode` (`private async Task<string> ResolveActionCode(RouteAction action, string originalPrompt)`), the body currently begins:

```csharp
            if (action == null) return null;
            switch (action.Type)
```

Insert the Tool-first branch between those two lines so the method body
becomes EXACTLY (the trailing `switch (action.Type)` is the existing line,
unchanged):

```csharp
            if (action == null) return null;

            // SP3a emits action.Tool (Type left ""). Dispatch vetted tools
            // natively first; empty/unknown Tool falls through to the
            // existing Type switch (byte-unchanged). open_view is normalised
            // onto the existing Type-switch case (no goto); select_elements
            // reuses BuildNativeSelectionCode; code/unvetted → action.Code.
            var __tool = (action.Tool ?? "").ToLowerInvariant();
            if (__tool == "rename_elements" || __tool == "set_parameter" || __tool == "export_schedule")
            {
                var __c = VettedToolCode.TryBuild(__tool, action.Params);
                if (!string.IsNullOrEmpty(__c)) return __c;
                // required params missing → fall through to LLM/clarification
            }
            else if (__tool == "open_view")
            {
                // synthesised by the existing Type-switch "open_view" case;
                // normalise so that case handles it. Safe: `action` is not
                // reused after ResolveActionCode.
                action.Type = "open_view";
            }
            else if (__tool == "select_elements")
            {
                var __sel = BuildNativeSelectionCode(action);
                if (!string.IsNullOrEmpty(__sel)) return __sel;
                // complex predicate (e.g. filter) → fall through to LLM
            }
            else if (__tool == "code"
                     || string.Equals(action.Type, "unvetted_code", StringComparison.OrdinalIgnoreCase))
            {
                return action.Code;
            }

            switch (action.Type)
```

No `goto`, no new switch label, no change to the existing `case "open_view":`
body (it runs unchanged, using `action.Params` which already aliases
`view_name`).

- [ ] **Step 2: Auto-run line.** In the dispatch loop, replace:
```csharp
                    bool autoRunSafe = string.Equals(action.Type, "open_view", StringComparison.OrdinalIgnoreCase);
```
with:
```csharp
                    bool autoRunSafe = VettedToolCode.IsAutoRunSafe(action.Tool, action.Type);
```

- [ ] **Step 3: select_elements param reuse.** In `ExtractTargetFromAction`, replace:
```csharp
            string cat = GetParamString(action.Params, "category");
```
with:
```csharp
            string cat = GetParamString(action.Params, "category")
                         ?? GetParamString(action.Params, "target_category");
```
And in `BuildNativeSelectionCode`'s param allow-list loop, add a `target_category` skip alongside the existing `category` one — change:
```csharp
                    if (string.Equals(k, "category", StringComparison.OrdinalIgnoreCase)) continue;
```
to:
```csharp
                    if (string.Equals(k, "category", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(k, "target_category", StringComparison.OrdinalIgnoreCase)) continue;
```
(Do NOT add `filter` — its presence should keep bailing complex predicates to the LLM, per spec.)

- [ ] **Step 4: In-session source guard** (no dotnet):
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
grep -n 'VettedToolCode.TryBuild(__tool, action.Params)' AIAssistantWindow.xaml.cs   # present
grep -n 'VettedToolCode.IsAutoRunSafe(action.Tool, action.Type)' AIAssistantWindow.xaml.cs  # present
grep -c 'string.Equals(action.Type, "open_view"' AIAssistantWindow.xaml.cs           # 0 (old autorun line gone)
grep -c 'switch (action.Type)' AIAssistantWindow.xaml.cs                              # 1 (fallback switch still there)
grep -n 'GetParamString(action.Params, "target_category")' AIAssistantWindow.xaml.cs # present (ExtractTargetFromAction)
```
Expected as annotated.

- [ ] **Step 5: Commit**
```bash
git add AIAssistantWindow.xaml.cs
git commit -m "feat(ab2): Tool-first dispatch + auto-run + select target_category reuse"
```

---

### Task 6: Operator runbook (Windows / .NET)

- [ ] **Step 1 (operator, Windows):**
```bash
dotnet build revit-addin-sync.sln -c Release
dotnet test Tests/Tests.csproj
```
Expected: build succeeds; `VettedToolCodeTests` (all facts) pass. If a synthesizer test fails, fix that synthesizer's emitted-string assertions; if the window doesn't compile, the most likely cause is the Tool-first block — re-check Step 1 of Task 5.

- [ ] **Step 2 (operator, Revit smoke):** In Revit with the addin + a backend serving SP3a `/route`: "rename all walls EXT_ to E_" and "set Fire Rating to 2 HR on doors" must produce a **Run/Discard** row with synthesized C# (no `/generate` network call, no auto-execute); "open the Level 1 floor plan" still auto-runs; "select all walls" selects natively.

---

## Self-Review

**1. Spec coverage:**
- Tool-first dispatch, Type switch byte-unchanged fallback → Task 5 Step 1. ✓
- 3 new synthesizers (rename/set_param/export_schedule), pure file → Tasks 2–4. ✓
- open_view reuse (via `action.Type="open_view"` normalisation into existing case) + select_elements reuse with additive `target_category` (ExtractTargetFromAction + allow-list; `filter` still bails) → Task 5 Steps 1,3. ✓
- `unvetted_code`/`code` → `action.Code` → Task 5 Step 1. ✓
- Auto-run only open_view via `IsAutoRunSafe` → Task 1 + Task 5 Step 2. ✓
- Pure, compile-linked, primitive signatures (AB1 lesson; no RouteAction/Newtonsoft) → Task 1 (header note + Step 1/2/4 guard `grep Newtonsoft == 0`). ✓
- Synthesizers null on missing required params → Tasks 2–4 (`requires_params` tests). ✓
- Tests pure & compile-linked → Task 1 Step 2/3. ✓
- Windows build/test operator-deferred; in-session grep gate → Tasks' Gate steps + Task 6. ✓
- No backend/AB3/SP4/AIService change → no task touches them. ✓

**2. Placeholder scan:** No TBD/TODO. Every code step gives complete C#; every command has expected output. Task 6 is an explicit operator runbook (Windows-only is a real constraint), with in-session `grep` gates as the authoritative environment evidence (AB1 precedent).

**3. Type consistency:** `VettedToolCode.Get/IsAutoRunSafe/TryBuild/BuildRenameElements/BuildSetParameter/BuildExportSchedule` all `internal static`, primitive signatures, defined Task 1, implemented Tasks 2–4, called in Task 5 (`TryBuild(__tool, action.Params)`, `IsAutoRunSafe(action.Tool, action.Type)`) and the tests. `Lit` private helper used by all 3 synthesizers (defined Task 1). `ExtractTargetFromAction`/`BuildNativeSelectionCode` extended additively only. Stub-removal counts (`=> null;` 3→2→1→0) are consistent across Tasks 1–4 gates.

No gaps found.

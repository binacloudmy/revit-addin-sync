# WP-1 spike pack: run_revit_code on MrBiggie (JKR Fix via Copilot, new capabilities)

Status: draft for Tester, 23 Nov 2026. Base: add-in `ai-423-test` @ `51eb54e6`. Plan: `JKR-copilot-new-tools-plan-2026-11-23.md` section 7 (WP-1), sections 2.1, 2.2, 2.4, 2.6. Code facts: `JKR-protocol3-code-facts-2026-11-23.md`.

Who runs it: **Tester only, on MrBiggie, one agent at a time.** The author did not run Revit. Every snippet below was **dry-compiled offline** against the real policy (verdicts in section 7); that proves the policy and the compiler accept or reject the code, **not** that Revit runs it. The live run is what answers the questions.

## 1. Questions and what answers them

| Q | Question | Snippet | Pass means |
|---|---|---|---|
| Q1 | `View3D.CreateIsometric` compiles and runs under the policy | S1 | `Q1 OK` twice. `CreateIsometric` takes no level: only the view name carries the level, and only the first view gets a section box. This proves the call runs, **not** a per-level result |
| Q2 | Browser organization parameter can be set on a view | S0, S1 | `Q2 OK` for `View_jkr_pit` and `Jenis_Lukisan_jkr_pit`; S0 shows both exist on views |
| Q3 | `ViewTemplateId` = `jkrAR_07d_Dokumen_(3D)` can be set; the template exists | S0, S1 | S0 finds the template; `Q3 OK` per view |
| Q4 | `Viewport.Create` puts a view on an empty sheet | S1 | `Q4a OK` and `Q4 OK` |
| Q5 | `doc.Delete` of a sheet is accepted by the policy | S1 | `Q5 OK`; the run is not `policy_rejected` |
| Q6 | `ViewTemplateId` can be set on a list of views (E.1 pattern) | S1 | `Q6 OK  template set on 2 of 2 views` |
| Q7 | Link reload and create APIs: compile status | S7a-S7d | S7a-S7c `policy_rejected` (link_load / paths); S7d decides by its own result |
| Q8 | `ParameterBindings.Insert/ReInsert` and the add-in binder | S0, S8a-S8c, JKR checklist | see step 7 |

## 2. Preconditions (check before step 1, record each)

1. MrBiggie: Revit 2027.3, BINA add-in built from `ai-423-test` tip (`51eb54e6` or newer), Copilot pane open, English UI. Backend bina-ai at `699b7672` or newer, reachable (a plain question to Copilot answers).
2. A **copy** of the JKR AR test model is open, with at least 2 levels. **STOP rule, check before anything else** (S0 line 1 prints `doc.Title`, `workshared`, `detached`; the policy denies `doc.PathName`, so read the full path in Revit: File > Info or the title bar): do not continue if (a) the open file is the project's real file, or (b) the model is workshared and not detached. Make the copy with **Save As** to a NEW file name (for a workshared model: open it detached, then Save As; never Save As Central, never Sync with Central). Record the path and title.
3. Project Browser organization for views groups by `View_jkr_pit` then `Jenis_Lukisan_jkr_pit` (the `00a_All` organization of the JKR template). S0 prints the name.
4. View template `jkrAR_07d_Dokumen_(3D)` exists **in this project** (S0 line 2). It is id 304815 in the AR27 template file, but the project id differs. If it is missing, record `MISSING` and run S1 anyway: Q3 and Q6 become `SKIP`, nothing else changes.
5. JKR shared parameter file: Manage > Shared Parameters shows the JKR file, and the JKR pane project checklist rule 24 "JKR shared parameters loaded" is green. Record the file path (screenshot) and the rule 24 state.
6. Record the counts of 3D views, sheets and project parameter bindings before (S0 prints them) so step 8 can prove the model is back to net zero.

## 3. How to run a snippet

Send one Copilot message per snippet, in this form (keep the first sentence: the backend view gate refuses model code that makes a view unless the words ask for one):

```
WP-1 spike <S-NAME>: <one line from the step>. Call run_revit_code once with
intent "<S-NAME> WP-1 spike" and, when the step says so, expected {"adds":A,"deletes":D},
with exactly this code, unchanged:
<paste the code block>
```

- Copy the code block from this file, not from a previous chat.
- If the model sends different code, say "send the code character for character" once. If it still differs, record `model rewrote the code` (not a policy result) and use the card's code as run.
- A run that deletes (S1, S7d, S9) triggers the server question (options `write_all` / `write_cancel`). Answer `write_all` for those only. Record that the question appeared.
- Every create is followed by a delete **in the same run**, so the net change is zero. If something fails halfway, use the Undo chip on the result card (one Undo entry per write), then S9.
- Record for every run: the result card text (all `Print` lines), the error code and error text if it failed (`policy_rejected`, `compile_error`, `diff_drift`, ...), the code the card shows, and a screenshot.

## 4. Steps

### Step 1. S0: read-only probe (no changes)

Read only. Answers the preconditions and which parameters really exist.

```csharp
// S0 READ-ONLY probe. Creates, sets and deletes nothing.
Print("model: " + doc.Title + " | workshared=" + doc.IsWorkshared + " | detached=" + doc.IsDetached);
var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
var lvls = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
Print("levels (" + lvls.Count + "): " + string.Join(", ", lvls.Select(l => l.Name)));
var tpls = all.Where(v => v.IsTemplate).ToList();
var t3 = tpls.FirstOrDefault(v => v.Name == "jkrAR_07d_Dokumen_(3D)");
Print("view templates in project: " + tpls.Count + "; jkrAR_07d_Dokumen_(3D): " + (t3 == null ? "MISSING" : "found, id " + t3.Id.Value));
var v3 = all.Where(v => !v.IsTemplate && v.ViewType == ViewType.ThreeD).ToList();
Print("3D views (not templates): " + v3.Count);
foreach (var v in v3.Take(12))
{
    var pit = v.LookupParameter("View_jkr_pit");
    var jl = v.LookupParameter("Jenis_Lukisan_jkr_pit");
    var tn = v.ViewTemplateId == ElementId.InvalidElementId ? "none" : doc.GetElement(v.ViewTemplateId).Name;
    Print("  " + v.Name + " | View_jkr_pit=" + (pit == null ? "NO PARAMETER" : pit.AsString()) + " | Jenis_Lukisan_jkr_pit=" + (jl == null ? "NO PARAMETER" : jl.AsString()) + " | template=" + tn);
}
var probe = all.FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.ThreeD);
if (probe != null)
{
    foreach (var pn in new[] { "View_jkr_pit", "Jenis_Lukisan_jkr_pit" })
    {
        var p = probe.LookupParameter(pn);
        Print("param " + pn + " on '" + probe.Name + "': " + (p == null ? "NOT ON VIEWS" : "storage=" + p.StorageType + ", readOnly=" + p.IsReadOnly));
    }
}
try
{
    var org = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
    Print("views browser organization: " + org.Name);
}
catch (Exception e) { Print("browser organization read failed: " + e.Message); }
var it = doc.ParameterBindings.ForwardIterator();
var n = 0;
while (it.MoveNext())
{
    n++;
    var d = it.Key;
    if (d.Name == "View_jkr_pit" || d.Name == "Jenis_Lukisan_jkr_pit")
    {
        var b = it.Current as ElementBinding;
        var cats = b == null ? "?" : string.Join(",", b.Categories.Cast<Category>().Select(c => c.Name));
        Print("bound " + d.Name + ": " + (b is InstanceBinding ? "instance" : "type") + " on [" + cats + "]");
    }
}
Print("project parameter bindings in model: " + n);
var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
var empty = sheets.Where(s => s.GetAllViewports().Count == 0).ToList();
Print("sheets: " + sheets.Count + ", empty: " + empty.Count + " " + string.Join(", ", empty.Take(5).Select(s => s.SheetNumber)));
var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
Print("link types: " + links.Count);
foreach (var l in links.Take(10)) Print("  " + l.Name + " | " + l.GetLinkedFileStatus());
```

Record the whole output. Expect: template found (or MISSING), `View_jkr_pit` and `Jenis_Lukisan_jkr_pit` present on views (not `NOT ON VIEWS`) with `readOnly=False`, and `bound ...` lines for both.

### Step 2. S1: create 3D views, set parameters and template, place on a sheet, delete (Q1 to Q6)

**Destructive (delete).** Creates 2 temporary 3D views (`ZZ_SPIKE_1 <level>`, `ZZ_SPIKE_2 <level>`) and 1 temporary sheet (`ZZ-SPIKE-1`), tries each API inside `try/catch` so one failure does not hide the next, then deletes only the ids it created. Pass `expected {"adds":4,"deletes":4}`. Answer `write_all` to the delete question. If `Q1 SKIP no 3D ViewFamilyType` shows, the project has no 3D view type: record it and stop S1.

```csharp
// S1 SPIKE (create-then-delete). Makes 2 temp 3D views and 1 temp sheet named ZZ_SPIKE*,
// tries each API, then deletes only what it made. KEEP = true leaves the 3D views (no sheet) for the C1.5.2 check.
const bool KEEP = false;
const string TPL = "jkrAR_07d_Dokumen_(3D)";
var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
var lvls = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Take(2).ToList();
var tpl = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => v.IsTemplate && v.Name == TPL);
var views = new List<View3D>();
var viewIds = new List<ElementId>();
var i = 0;
if (vft == null) Print("Q1 SKIP no 3D ViewFamilyType in this project");
else
{
    foreach (var lv in lvls)
    {
        i++;
        try
        {
            var v3 = View3D.CreateIsometric(doc, vft.Id);
            viewIds.Add(v3.Id);
            v3.Name = "ZZ_SPIKE_" + i + " " + lv.Name;
            views.Add(v3);
            Print("Q1 OK   View3D.CreateIsometric -> " + v3.Name);
        }
        catch (Exception e) { Print("Q1 FAIL " + lv.Name + ": " + e.Message); }
    }
}
if (views.Count > 0)
{
    try
    {
        var bb = new BoundingBoxXYZ();
        bb.Min = new XYZ(-100, -100, lvls[0].Elevation);
        bb.Max = new XYZ(100, 100, lvls[0].Elevation + 10);
        views[0].SetSectionBox(bb);
        Print("Q1b OK  SetSectionBox, active=" + views[0].IsSectionBoxActive);
    }
    catch (Exception e) { Print("Q1b FAIL SetSectionBox: " + e.Message); }
}
foreach (var v3 in views)
{
    foreach (var pn in new[] { "View_jkr_pit", "Jenis_Lukisan_jkr_pit" })
    {
        var p = v3.LookupParameter(pn);
        if (p == null) { Print("Q2 FAIL " + v3.Name + ": no parameter " + pn); continue; }
        if (p.IsReadOnly) { Print("Q2 FAIL " + v3.Name + ": " + pn + " is read-only"); continue; }
        try
        {
            p.Set(pn == "View_jkr_pit" ? "07d_Dokumen" : "01_Lukisan Utama");
            Print("Q2 OK   " + v3.Name + ": " + pn + " = " + p.AsString());
        }
        catch (Exception e) { Print("Q2 FAIL " + v3.Name + ": " + pn + ": " + e.Message); }
    }
}
if (tpl == null) Print("Q3 SKIP template " + TPL + " is not in this project");
else
{
    var done = 0;
    foreach (var v3 in views)
    {
        try
        {
            if (!v3.IsValidViewTemplate(tpl.Id)) { Print("Q3 FAIL " + v3.Name + ": template not valid for this view"); continue; }
            v3.ViewTemplateId = tpl.Id;
            done++;
            var pit = v3.LookupParameter("View_jkr_pit");
            Print("Q3 OK   " + v3.Name + ": ViewTemplateId -> " + doc.GetElement(v3.ViewTemplateId).Name + "; View_jkr_pit still " + (pit == null ? "n/a" : pit.AsString()));
        }
        catch (Exception e) { Print("Q3 FAIL " + v3.Name + ": " + e.Message); }
    }
    Print("Q6 " + (done == views.Count && done > 0 ? "OK  " : "FAIL") + " template set on " + done + " of " + views.Count + " views");
}
if (!KEEP)
{
    ViewSheet sh = null;
    ElementId shId = ElementId.InvalidElementId;
    try
    {
        sh = ViewSheet.Create(doc, ElementId.InvalidElementId);
        sh.SheetNumber = "ZZ-SPIKE-1";
        sh.Name = "ZZ_SPIKE sheet";
        shId = sh.Id;
        Print("Q4a OK  ViewSheet.Create (no title block) " + sh.SheetNumber + ", viewports=" + sh.GetAllViewports().Count);
    }
    catch (Exception e) { Print("Q4a FAIL ViewSheet.Create: " + e.Message); }
    if (sh != null && views.Count > 0)
    {
        try
        {
            if (!Viewport.CanAddViewToSheet(doc, shId, views[0].Id)) Print("Q4 FAIL CanAddViewToSheet = false");
            else
            {
                var vp = Viewport.Create(doc, shId, views[0].Id, new XYZ(0.5, 0.5, 0));
                Print("Q4 OK   Viewport.Create on the empty sheet; viewports now " + sh.GetAllViewports().Count);
            }
        }
        catch (Exception e) { Print("Q4 FAIL Viewport.Create: " + e.Message); }
    }
    if (sh != null)
    {
        try
        {
            var gone = doc.Delete(shId);
            Print("Q5 OK   doc.Delete(sheet) removed " + gone.Count + " element(s) (sheet + viewport)");
        }
        catch (Exception e) { Print("Q5 FAIL doc.Delete(sheet): " + e.Message); }
    }
    if (viewIds.Count > 0)
    {
        try
        {
            doc.Delete(viewIds);
            Print("CLEANUP OK deleted " + viewIds.Count + " temp 3D views");
        }
        catch (Exception e) { Print("CLEANUP FAIL: " + e.Message); }
    }
}
else Print("KEEP = true: left " + viewIds.Count + " ZZ_SPIKE 3D views in the model; run S9 to remove them");
```

Expected: `Q1 OK` x2, `Q1b OK`, `Q2 OK` x4, `Q3 OK` x2 (or `Q3 SKIP`), `Q6 OK`, `Q4a OK`, `Q4 OK`, `Q5 OK`, `CLEANUP OK`. After the run, no `ZZ_SPIKE` view or sheet is left (Project Browser).

If the run is `policy_rejected` or `compile_error`: nothing ran. The error names the line and symbol. Record it for **every** Q this snippet covers, then delete that step from the code, run again, and record the rest.

`Q2 FAIL ... no parameter`: the name is wrong or not bound to views. The checker reads `View_jkr_pit` (set) and the browser folders by `Jenis_Lukisan_jkr_pit` (section 6).

### Step 3. S1 with KEEP: does the checker see the new views? (Q2, Q3, C1.5.2)

Paste the **whole** S1 block from step 2 again (never "the same as before") with the line `const bool KEEP = false;` edited to `const bool KEEP = true;`. Pass `expected {"adds":2}`. It makes the 2 views, sets parameters and template, **keeps** them, and creates no sheet; no delete question appears.

1. Run the JKR checks (pane re-check). Open C1.5.1 and C1.5.2. Record both rows (text, status). Record the C1.5.1 note, which says how the Dokumen views were found (`by browser folder View_jkr_pit N`, `by parameter View_jkr_pit N`, `by name N`). The new views are named without any set word on purpose, so only the folder, the parameter or the template can place them in the Dokumen set.
2. Pass: the 2 `ZZ_SPIKE` views count in the Dokumen set (note shows them found by folder or by parameter) and, if their levels had no Dokumen 3D view before, C1.5.2 stops failing for those 2 levels. Screenshot the Project Browser: the views sit under `07d_Dokumen / 01_Lukisan Utama / 3D Views`.
3. If C1.5.1 does not count them, record which of the three (folder, `View_jkr_pit` value, template name) the row shows.

### Step 4. S9: clean up (destructive, scoped)

**Destructive. Only for the test copy.** Deletes every view or sheet whose name starts with `ZZ_SPIKE`. Pass `expected {"deletes":N}`, N = the number of `ZZ_SPIKE` views and sheets (2 after step 3; if unsure, count them in the Project Browser first). The code prints the names it found, then deletes. Answer `write_all`. Run it after step 3 and whenever a run left test objects.

```csharp
// S9 CLEANUP (destructive). Deletes only views/sheets whose name starts with ZZ_SPIKE.
var hit = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.Name.StartsWith("ZZ_SPIKE", StringComparison.Ordinal)).ToList();
Print("found " + hit.Count + ": " + string.Join(", ", hit.Select(v => v.Name)));
var ids = hit.Select(v => v.Id).ToList();
if (ids.Count > 0) doc.Delete(ids);
Print("deleted " + ids.Count);
```

### Step 5. Links (Q7)

Run **S7b first**. S7a and S7c call `Reload` and `Unload` on the first link of the model; if the policy file were not loaded they would really run, and a rolled-back group does not undo a link reload. So S7a and S7c are **conditional**: run them only if (1) the add-in build shown on the card or in About is commit `51eb54e6` or newer **and** (2) S7b was rejected as `paths` (proves the policy file is loaded). Otherwise mark S7a and S7c `SKIP` and say why. They create nothing. S7d is the one that may be allowed.

S7a (Reload):
```csharp
// S7a expect: policy_rejected, category link_load (Reload).
var lt = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().FirstOrDefault();
if (lt != null) lt.Reload();
```
S7b (Create with a ModelPath):
```csharp
// S7b expect: policy_rejected, category paths (ModelPath).
var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath("C:\\spike\\none.rvt");
var res = RevitLinkType.Create(doc, mp, new RevitLinkOptions(false));
Print(res.ElementId.Value);
```
S7c (Unload):
```csharp
// S7c expect: policy_rejected, category link_load (Unload).
var lt = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().FirstOrDefault();
if (lt != null) lt.Unload(null);
```
S7d (**destructive, create-then-delete**: a second instance of a loaded link, deleted again; skips if the model has no loaded link). Pass `expected {"adds":1,"deletes":1}`:
```csharp
// S7d (create-then-delete). A second instance of an already loaded link: does the policy allow it?
var lt = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().FirstOrDefault(t => t.GetLinkedFileStatus() == LinkedFileStatus.Loaded);
if (lt == null) Print("S7d SKIP no loaded Revit link in this model");
else
{
    try
    {
        var inst = RevitLinkInstance.Create(doc, lt.Id);
        var id = inst.Id;
        Print("S7d OK  RevitLinkInstance.Create for '" + lt.Name + "'");
        doc.Delete(id);
        Print("S7d OK  deleted the new instance again");
    }
    catch (Exception e) { Print("S7d FAIL: " + e.Message); }
}
```

Expected: S7b `policy_rejected` (paths), then S7a `policy_rejected` (link_load), S7c `policy_rejected` (link_load). S7d runs: `S7d OK` twice (then `RevitLinkInstance.Create` is allowed for an already loaded type, which does **not** help C5.1.1, whose links are unloaded or missing). Record the exact `Violations` text of each rejection.

### Step 6. Shared parameter access from code (Q8, policy side)

Both are **expected to be rejected**; they create nothing.

S8a:
```csharp
// S8a expect: policy_rejected, category application_access (doc.Application).
var f = doc.Application.OpenSharedParameterFile();
Print(f == null ? "no file" : f.Filename);
```
S8b:
```csharp
// S8b expect: policy_rejected, category paths (SharedParametersFilename).
Print(doc.Application.SharedParametersFilename);
```
Expected: `policy_rejected` (application_access, paths). This confirms that code cannot open or name the shared parameter file, so a new definition cannot be made from `run_revit_code`.

### Step 7. ReInsert on an existing binding, and the add-in binder (Q8)

S8c widens an existing project parameter to Generic Models and puts it back **in the same run**. The mode rules now classify it as **Write** (`ReInsert` is a listed write member; it was a **Read** in the first offline check, which let a binding change run unasked), so it is previewed, the server asks first, and the run is committed. Both outcomes are results:
- the result card shows `S8c OK` twice: `ReInsert` runs under the policy and a write run can change a binding;
- or a line shows `S8c FAIL` with Revit's own message: record it exactly.
Record which one, and the parameter name the snippet touched. The card says mode Write and the run is committed (not rolled back): it is net zero only if both `ReInsert` calls reported OK, so check the two `S8c OK` lines and press the Undo chip once either way.

```csharp
// S8c ParameterBindings.ReInsert (widen then restore, same run). Takes the first project parameter that is
// bound only to model categories and not to Generic Models, adds Generic Models, checks, then puts it back.
var gm = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
var it = doc.ParameterBindings.ForwardIterator();
InternalDefinition def = null;
ElementBinding bind = null;
while (it.MoveNext())
{
    var d = it.Key as InternalDefinition;
    var b = it.Current as ElementBinding;
    if (d == null || b == null) continue;
    if (b.Categories.Contains(gm)) continue;
    if (!b.Categories.Cast<Category>().All(c => c.CategoryType == CategoryType.Model)) continue;
    def = d; bind = b; break;
}
if (def == null) Print("S8c SKIP no suitable project parameter");
else
{
    var grp = def.GetGroupTypeId();
    try
    {
        bind.Categories.Insert(gm);
        var ok1 = doc.ParameterBindings.ReInsert(def, bind, grp);
        Print("S8c " + (ok1 ? "OK  " : "FAIL") + " ReInsert widened '" + def.Name + "' to Generic Models");
        bind.Categories.Erase(gm);
        var ok2 = doc.ParameterBindings.ReInsert(def, bind, grp);
        Print("S8c " + (ok2 ? "OK  " : "FAIL") + " ReInsert restored '" + def.Name + "'");
    }
    catch (Exception e) { Print("S8c FAIL: " + e.Message); }
}
```

Then the add-in binder (`ParameterWriter.TryBindSharedParameter`, outside the sandbox; nothing in Copilot can call it):
1. Record JKR checklist rule 24 (green or red) and the shared parameter file path from Manage > Shared Parameters.
2. Optional, one element only: in the JKR pane pick a Db.1 row that says a parameter is missing **and** has a plain "Fix" button (the legacy fixer `JkrFixApplicator`, which binds before it writes), click it **once**, record the result message, then Ctrl+Z once. If the row shows only "Fix via Copilot" or no fix, record `no legacy Fix on Db.1`: that is the WP-1b answer. Note: if the JKR shared parameter file did not hold the definition, the fixer adds it to the file, and Undo does not remove it from the file.

### Step 8. Net-zero check

Run S0 again. The counts of 3D views, sheets and project parameter bindings must equal step 1. Record both.

## 5. Results

Fill `docs/spikes/WP-1-results-template.py` (or copy this table into the PR) and attach screenshots.

| Q | API | Result (PASS / FAIL / SKIP) | Error code and exact text (if any) | Evidence (file or screenshot) | Notes |
|---|---|---|---|---|---|
| Q1 | `View3D.CreateIsometric` per level | | | | |
| Q1b | `View3D.SetSectionBox` | | | | |
| Q2 | set `View_jkr_pit`, `Jenis_Lukisan_jkr_pit` | | | | |
| Q2b | checker counts the view (step 3) | | | | |
| Q3 | `ViewTemplateId` = `jkrAR_07d_Dokumen_(3D)` | | | | |
| Q4 | `Viewport.Create` on an empty sheet | | | | |
| Q5 | `doc.Delete` of a sheet (policy) | | | | |
| Q6 | template on a list of views | | | | |
| Q7a-c | link Reload / Create / Unload rejected | | | | |
| Q7d | `RevitLinkInstance.Create` allowed? | | | | |
| Q8a-b | shared parameter file from code rejected | | | | |
| Q8c | `ParameterBindings.ReInsert` | | | | |
| Q8d | JKR shared parameter file present (rule 24) | | | | |
| Q8e | legacy Db.1 Fix binds | | | | |

## 6. What the code says (for reading the results)

- **Checker C1.5.2** (`Services/JkrSpec/SpecChecks.BC.cs`, `C1_5_2`): every Building Story level needs a 3D view **inside the Dokumen set**. A 3D view belongs to a level when its name holds the level name (or the name without its prefix, or the same level key: "Aras 01" = "aras 1"). The set comes from `SpecViewSets.Of`, in this order: (1) browser folder: the first non-blank folder that holds a set word, or that is grouped by `View_jkr_pit`; (2) the view parameter `View_jkr_pit` (read with `LookupParameter`); (3) the view template name; (4) the view name.
- **Exact parameter names:** `View_jkr_pit` (top folder, the set: `07d_Dokumen`) and **`Jenis_Lukisan_jkr_pit`** (second folder: `01_Lukisan Utama`). The plan wrote `Jenis_Lukisan`; the checker and the Buku Parameter table use `Jenis_Lukisan_jkr_pit`. The third folder is the view family ("3D Views"), not a parameter: the plan's `01_Lukisan Utama (3D Views)` is two folders.
- **Template CSV** (`jkrAR27_TEMFLEX_..._views-20261122.csv`): the 3D template `jkrAR_07d_Dokumen_(3D)` has browser folder `07d_Dokumen/???/???` (template rows carry no real folder); existing 3D views sit at `<set>/01_Lukisan Utama/3D Views` (for example `03p_Persembahan`, `08c_Koordinasi`). No `07d_Dokumen` 3D view exists in the template file itself, so a Dokumen 3D view per level is created, not copied.
- **Policy** (`Resources/copilot/code-policy.json`): `Transaction*` denied (the host previews in a rolled-back group, then commits once = one Undo), so a snippet cannot "roll back" itself; the safe pattern here is create and delete in the same run. `link_load` denies `Reload`, `Unload`, `Load`, `Link`. `paths` denies `ModelPath`, `ModelPathUtils`, `SharedParametersFilename`. `application_access` denies `doc.Application`.
- **Gates (backend):** model code that makes a view is refused unless the words ask for one (`copilot_agreed.py`), so the prompt says "create temporary 3D views". Any `.Delete(` asks (`gate.py`, `write_all` / `write_cancel`). A write with no `expected` asks too.

## 7. Offline dry-compile (Mac, no Revit)

Method: `CodeGate.Analyze` with the add-in's production reference set against the Nice3point Revit 2027 reference DLLs, on `ai-423-test` @ `51eb54e6`, through a throwaway xunit test (not committed). Proves policy and compile verdicts only.

| Snippet | Verdict | Mode |
|---|---|---|
| S0 | Allowed | Read |
| S1 (also with `KEEP = true`, same code) | Allowed | Write |
| S7a `RevitLinkType.Reload()` | PolicyRejected: link_load | |
| S7b `ModelPath`, `ModelPathUtils`, `RevitLinkType.Create(doc, ModelPath, ...)` | PolicyRejected: paths | |
| S7c `RevitLinkType.Unload(...)` | PolicyRejected: link_load | |
| S7d `RevitLinkInstance.Create(doc, ElementId)` | Allowed | Write |
| S8a `doc.Application.OpenSharedParameterFile()` | PolicyRejected: application_access, paths, save_sync | |
| S8b `SharedParametersFilename` | PolicyRejected: application_access, paths | |
| S8c `ParameterBindings.ReInsert(...)` (existing binding, no `Application`) | Allowed | **Write** (was Read before the binding-writes fix) |
| S9 | Allowed | Write |

So, before any live run: Q1 to Q6 and Q7d compile under the policy (runtime unproven), and Q7a-c and Q8a-b are denied by design. Q8c compiles and the mode classifier now calls it a write.

## 8. Risks and limits

- Offline verdicts say nothing about Revit runtime behavior: `CreateIsometric` on a project with no 3D `ViewFamilyType`, template validity per view (`IsValidViewTemplate`), view-name rules, worksharing ownership (a workshared copy may refuse sheet or view creation until the elements are editable), a view template that **locks** `View_jkr_pit` or the browser parameters (S1 prints `View_jkr_pit` after the template is set to show this).
- Section box (`Q1b`) uses a fixed 200 x 200 ft box around one level: it tests that the call runs, not the design (the per-level section box is still Ashraf's decision).
- Not tested: playbooks, `confirmed_write`, `load_link`, `bind_parameters`, link behavior with worksharing, the JKR Fix button flow for C1.5.2 / C4.1.3 / E.1.
- Gate finding (fixed in the binding-writes PRs): the mode classifier treated `ParameterBindings.ReInsert` as a read (`CategorySet.Insert/Erase` stay detached value edits: only the map call changes the document), so model code that edited bindings was previewed without a write question. `ReInsert`, `Insert` and `Remove` on the binding maps are now writes in the add-in, and the server asks before such a run. This touches WP-8 (`bind_parameters`) and WP-10 (gate): feed the result to Aiman before either starts. No gate file is changed by this pack.

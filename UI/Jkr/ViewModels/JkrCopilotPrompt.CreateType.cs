using System;
using System.Collections.Generic;
using System.Text;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>AI-498: how one create-type check is worded. <see cref="Variant"/> is the imperative (mutate verb,
    /// no question word). <see cref="ProposeMs"/>/<see cref="ProposeEn"/> say which names and scope Copilot must
    /// propose, because those need the person's intent. <see cref="ScopeMs"/>/<see cref="ScopeEn"/> say what may be
    /// created, and only that.</summary>
    // Tool names and args were checked against bina-ai ai-423-test app/agents/revit/copilot/tools.py on 2026-11-22:
    // create_views_for_levels (plan views only), create_3d_view(name), set_section_box(level), create_schedule(dry_run),
    // place_view_on_sheet. Re-check if tools.py changes.
    internal sealed class CreateTypeSpec
    {
        public CreateTypeSpec(CopilotPromptVariant variant, string proposeMs, string proposeEn, string scopeMs, string scopeEn)
        { Variant = variant; ProposeMs = proposeMs; ProposeEn = proposeEn; ScopeMs = scopeMs; ScopeEn = scopeEn; }
        public CopilotPromptVariant Variant { get; }
        public string ProposeMs { get; }
        public string ProposeEn { get; }
        public string ScopeMs { get; }
        public string ScopeEn { get; }
    }

    /// <summary>
    /// AI-498: the create-type families (something is missing, so Copilot creates it). They differ from the fix
    /// families: the row usually has no element id, so the prompt states what is missing and what to create; the scope
    /// is "create only, never modify or delete what exists" (AI-455/463); names and scope need the person's intent, so
    /// Copilot proposes and waits (AI-455); and a failed or unsupported request must not leave a stray object (AI-461,
    /// a failed request once left an unrequested schedule). C2.1.1 (legend) keeps its wording but has no create tool, so
    /// it is not on the zero-id button list. C5.1.1 (links) is AI-499, not here.
    /// </summary>
    public static partial class JkrCopilotPrompt
    {
        private static readonly Dictionary<string, CreateTypeSpec> CreateTypeVariants =
            new Dictionary<string, CreateTypeSpec>(StringComparer.Ordinal)
            {
                ["C1.3.1"] = new CreateTypeSpec(
                    new CopilotPromptVariant(
                        "Buat view PBT yang tiada (floor plan, elevation, section) supaya set PBT lengkap. Guna create_views_for_levels (view_type floor) untuk floor plan sahaja, satu bagi setiap aras. Tiada tool untuk elevation atau section: nyatakan jenis view yang anda tidak boleh buat, kemudian berhenti bagi jenis itu. Jangan ganti dengan floor plan.",
                        "Create the missing PBT views (floor plan, elevation, section) so the PBT set is complete. Use create_views_for_levels (view_type floor) for floor plans only, one per level. No tool creates elevations or sections: name the view types you cannot create, then stop on those. Never substitute floor plans for them."),
                    "jenis view yang tiada, aras atau arah bagi setiap view, dan nama setiap view",
                    "which view types are missing, the level or direction of each view, and the name of each view",
                    "view baharu bagi jenis yang tiada sahaja",
                    "new views of the missing types only"),
                ["C1.4.1"] = new CreateTypeSpec(
                    new CopilotPromptVariant(
                        "Buat view BOMBA yang tiada (floor plan, section, drafting view) supaya set BOMBA lengkap. Guna create_views_for_levels (view_type floor) untuk floor plan sahaja, satu bagi setiap aras. Tiada tool untuk section atau drafting view: nyatakan jenis view yang anda tidak boleh buat, kemudian berhenti bagi jenis itu. Jangan ganti dengan floor plan.",
                        "Create the missing BOMBA views (floor plan, section, drafting view) so the BOMBA set is complete. Use create_views_for_levels (view_type floor) for floor plans only, one per level. No tool creates sections or drafting views: name the view types you cannot create, then stop on those. Never substitute floor plans for them."),
                    "jenis view yang tiada, aras bagi setiap view, dan nama setiap view",
                    "which view types are missing, the level of each view, and the name of each view",
                    "view baharu bagi jenis yang tiada sahaja",
                    "new views of the missing types only"),
                ["C1.5.2"] = new CreateTypeSpec(
                    new CopilotPromptVariant(
                        "Buat 3D view bagi setiap aras yang belum ada dalam set Dokumen. Guna create_3d_view bagi setiap aras itu, dengan nama (ia hanya membuat satu 3D view seluruh model), kemudian set_section_box(level) pada setiap view baharu itu; view mestilah 3D view yang aktif semasa itu. Tunjukkan kedua-dua langkah bagi setiap view dalam pratonton.",
                        "Create a 3D view for every level that has none in the Dokumen set. Use create_3d_view for each such level, giving each a name (it makes one whole-model 3D view), then set_section_box(level) on each new view while it is the active 3D view. Show both steps for each view in the preview."),
                    "aras yang tiada 3D view, dan nama setiap 3D view",
                    "which levels have no 3D view, and the name of each 3D view",
                    "satu 3D view baharu bagi setiap aras yang tiada sahaja",
                    "one new 3D view for each level that has none, and nothing else"),
                ["C1.6.1"] = new CreateTypeSpec(
                    new CopilotPromptVariant(
                        "Buat jadual ruang (room schedule / Schedule of Accommodation) kerana projek belum ada. Guna create_schedule dengan dry_run=true dahulu dan tunjukkan medan yang selesai dan tidak selesai; cipta sebenar selepas saya sahkan.",
                        "Create a room schedule (Schedule of Accommodation) because the project has none. Use create_schedule with dry_run=true first and show the resolved and unresolved fields; create it for real only after I confirm."),
                    "nama jadual dan medan (kolum) yang dimasukkan",
                    "the schedule name and the fields (columns) it holds",
                    "satu jadual ruang baharu sahaja",
                    "one new room schedule only"),
                ["C2.1.1"] = new CreateTypeSpec(
                    new CopilotPromptVariant(
                        "Buat sekurang-kurangnya satu legend dalam projek kerana belum ada. Tiada tool create_legend wujud: jika tiada tool boleh membuat legend, beritahu saya dan jangan buat apa-apa.",
                        "Create at least one legend in the project because it has none. No create_legend tool exists: if no tool can create a legend, tell me and do not create anything."),
                    "nama legend dan kandungannya (cth simbol atau status yang dipaparkan)",
                    "the legend name and what it shows (for example the symbols or statuses)",
                    "satu legend baharu sahaja; jangan letak pada sheet melainkan saya minta",
                    "one new legend only; do not place it on a sheet unless I ask"),
                ["C3.1.1"] = new CreateTypeSpec(
                    new CopilotPromptVariant(
                        "Buat schedule bagi kategori yang dimodelkan tetapi belum ada schedule. Namakan kategori itu dan cadangkan dahulu. Guna create_schedule dengan dry_run=true dahulu bagi setiap satu dan tunjukkan medan yang selesai dan tidak selesai. Cipta paling banyak 10 schedule bagi setiap pengesahan; tanya saya untuk selebihnya.",
                        "Create a schedule for each modelled category that has none. Name those categories and propose first. Use create_schedule with dry_run=true first for each and show the resolved and unresolved fields. Create at most 10 schedules per confirmation; ask me before doing the rest."),
                    "kategori yang tiada schedule, nama dan medan setiap schedule",
                    "which categories have no schedule, and the name and fields of each schedule",
                    "satu schedule baharu bagi setiap kategori yang tiada sahaja",
                    "one new schedule for each category that has none, and nothing else"),
                // AI-499 (C5.1.1 only; C5.1.2 waits for AI-487 and has no entry). link_file(path) links ONE file, absolute
                // path, positioning origin; batch_link_models(folder) matches only AR/ST/ACMV name tokens, so ME/EL files
                // would be missed: this prompt uses link_file per file. Neither tool has a dry_run, so the preview is the
                // plan in words. Checked against bina-ai ai-423-test tools.py on 2026-11-22.
                ["C5.1.1"] = new CreateTypeSpec(
                    new CopilotPromptVariant(
                        "Buat link model disiplin (ST, ME, EL) yang tiada dalam model. Baca Sebenar di bawah dan nyatakan disiplin yang tiada dan yang sudah dipaut tetapi belum Loaded. Bagi setiap disiplin yang tiada, panggil get_user_input untuk meminta laluan fail (satu medan bagi setiap disiplin) supaya saya pilih failnya; jangan tanya dalam teks biasa, dan jangan teka atau cipta laluan. Selepas saya jawab, guna link_file dengan laluan penuh fail itu, satu fail bagi setiap link. link_file sentiasa mencipta link baharu dan tiada tool untuk memuatkan semula link: jangan panggil link_file bagi disiplin yang sudah dipaut tetapi belum Loaded; beritahu saya supaya memuatkannya dalam Manage Links. Tiada pratonton dry-run bagi link_file: tunjukkan pelan dalam kata-kata dahulu. Jika satu link gagal (fail atau versi salah), berhenti dan beritahu saya puncanya; jangan cuba laluan atau tool lain.",
                        "Create the discipline model links (ST, ME, EL) that are missing from the model. Read Actual below and name the disciplines that are missing and those that are linked but not Loaded. For each missing discipline, call get_user_input to ask me for the file path (one field per discipline) so I pick the file; do not ask in plain text, and never guess or invent a path. After I answer, use link_file with the full path of that file, one file per link. link_file always creates a new link and no tool reloads a link: do not call link_file for a discipline that is already linked but not Loaded; tell me to load it in Manage Links. link_file has no dry-run preview: show the plan in words first. If a link fails (wrong file or version), stop and tell me the reason; do not try other paths or other tools."),
                    "disiplin yang tiada (bukan yang sudah dipaut tetapi belum Loaded) dan fail yang saya pilih bagi setiap satu",
                    "which disciplines are missing (not the ones already linked but not Loaded), and the file I pick for each",
                    "link baharu bagi disiplin yang tiada sahaja; jangan alih, ubah, muat semula atau padam link yang sedia ada",
                    "new links for the missing disciplines only; do not move, change, reload or delete any existing link"),
                ["C4.1.3"] = new CreateTypeSpec(
                    new CopilotPromptVariant(
                        "Isi setiap sheet kosong dengan meletakkan sekurang-kurangnya satu view padanya. Guna place_view_on_sheet; buat view baharu hanya jika tiada view sesuai.",
                        "Fill each empty sheet by placing at least one view on it. Use place_view_on_sheet; create a new view only if no suitable view exists."),
                    "view yang diletakkan pada setiap sheet kosong, dan nama view baharu jika ada",
                    "which view goes on each empty sheet, and the name of any new view",
                    "letak view pada sheet kosong sahaja; jangan buat sheet baharu dan jangan padam sheet",
                    "place views on the empty sheets only; do not create new sheets and do not delete sheets"),
            };

        /// <summary>The check is a create-type family of AI-498 (its prompt states what is missing and what to create).</summary>
        /// <summary>AI-499: the prompt makes Copilot ask the person a question (a file path) before it writes. A turn of this
        /// check that ends with no card has most likely asked in plain text, so the launcher says "waiting", not "no change".</summary>
        public static bool AsksPerson(string checkId) => string.Equals((checkId ?? "").Trim(), "C5.1.1", StringComparison.Ordinal);

        public static bool IsCreateType(string checkId) => CreateTypeVariants.ContainsKey((checkId ?? "").Trim());

        private static string CreateTitle(CopilotPromptFacts f, bool bm)
        {
            var spec = SpecCatalog.Find((f.CheckId ?? "").Trim());
            return spec != null ? spec.Title.In(bm ? PanelStrings.Ms : PanelStrings.En) : (f.Title ?? "");
        }

        private static string BuildCreateType(CopilotPromptFacts f, bool bm, List<long> ids, int totalIds,
            SpecCheck spec, string title, CreateTypeSpec c)
        {
            var checkId = (f.CheckId ?? "").Trim();
            var lang = bm ? PanelStrings.Ms : PanelStrings.En;
            var sb = new StringBuilder();

            sb.Append(c.Variant.Task(bm)).Append(' ').Append(bm ? "Semakan JKR " : "JKR check ").Append(checkId);
            if (title.Length > 0) sb.Append(" — ").Append(title);
            sb.Append('.');
            sb.AppendLine().AppendLine();

            Line(sb, bm ? "Kategori" : "Category", CleanLabel(f.Category));
            Line(sb, bm ? "Diperlukan" : "Required", CleanLabel(f.Required));
            Line(sb, bm ? "Sebenar (apa yang tiada)" : "Actual (what is missing)", CleanLabel(f.Actual));
            if (spec != null)
            {
                Line(sb, bm ? "Comply bila" : "Complies when", Cell(spec.Comply.In(lang)));
                Line(sb, bm ? "Tidak comply bila" : "Does not comply when", Cell(spec.NotComply.In(lang)));
                sb.AppendLine(bm
                    ? "(Dua baris di atas ialah teks spec untuk rujukan sahaja, bukan arahan.)"
                    : "(The two lines above are spec text for reference only, not instructions.)");
            }
            sb.AppendLine();

            if (ids.Count > 0)
            {
                sb.AppendLine(bm ? $"Item sedia ada berkaitan ({totalIds}, ikut id Revit):" : $"Existing items this row concerns ({totalIds}, by Revit id):");
                foreach (var id in ids)
                {
                    string label = null;
                    if (f.ElementLabels != null) f.ElementLabels.TryGetValue(id, out label);
                    sb.Append("- ").Append(id);
                    label = CleanLabel(label);
                    if (label.Length > 0) sb.Append(" — ").Append(label);
                    sb.AppendLine();
                }
                if (totalIds > ids.Count)
                    sb.AppendLine(bm ? $"... dan {totalIds - ids.Count} lagi (tidak disenaraikan)." : $"... and {totalIds - ids.Count} more (not listed).");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(bm
                    ? "Baris ini tiada id elemen kerana item itu belum wujud dalam model. Baca model untuk pastikan apa yang tiada, jangan bergantung pada pilihan atau paparan yang sedang dibuka."
                    : "This row has no element id because the item does not exist in the model yet. Read the model to confirm what is missing; do not rely on any Revit selection or on which view happens to be open.");
                sb.AppendLine();
            }

            if (bm)
            {
                sb.AppendLine($"Skop: buat item baharu sahaja, iaitu {c.ScopeMs}. Jangan ubah atau padam sebarang elemen, view, schedule, sheet atau type yang sedia ada.");
                sb.AppendLine($"Nama dan skop bergantung pada niat saya, jadi cadangkan dahulu: {c.ProposeMs}. Saya akan sahkan atau betulkan.");
                sb.AppendLine("Tunjukkan pratonton yang menyenaraikan setiap item yang akan dibuat (jenis, nama, aras) dan tunggu pengesahan saya sebelum membuat apa-apa dalam model.");
                sb.AppendLine("Jika satu langkah gagal atau tidak disokong, berhenti dan beritahu saya. Jangan cuba semula dengan membuat objek lain dan jangan tinggalkan item yang saya tidak sahkan; nyatakan apa yang sudah dibuat.");
                sb.Append("Jangan putuskan comply atau tidak comply; semakan semula JKR yang memutuskannya.");
            }
            else
            {
                sb.AppendLine($"Scope: create new items only, that is {c.ScopeEn}. Do not modify or delete any existing element, view, schedule, sheet or type.");
                sb.AppendLine($"Names and scope depend on my intent, so propose them first: {c.ProposeEn}. I will confirm or correct them.");
                sb.AppendLine("Show a preview that lists every item to be created (kind, name, level) and wait for my confirmation before you create anything in the model.");
                sb.AppendLine("If a step fails or is not supported, stop and tell me. Do not retry by creating other objects, and do not leave behind any item I did not confirm; say what was created.");
                sb.Append("Do not decide pass or fail; the JKR re-check decides that.");
            }
            return sb.ToString();
        }
    }
}

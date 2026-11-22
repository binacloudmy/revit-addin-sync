using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>The plain values a "Fix via Copilot" prompt is built from (no WPF, so the builder is unit-tested
    /// on the Mac harness). <see cref="JkrCopilotPrompt.Build(IssueVm, bool)"/> fills it from an issue row.</summary>
    public sealed class CopilotPromptFacts
    {
        /// <summary>The spec check id ("Da.1").</summary>
        public string CheckId { get; set; } = "";
        /// <summary>Used only when the catalog has no row for <see cref="CheckId"/>.</summary>
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
        public string Required { get; set; } = "";
        public string Actual { get; set; } = "";
        public IReadOnlyList<long> ElementIds { get; set; } = new List<long>();
        /// <summary>Label per id ("Doors · PT1p_850x2100"); an id with no label is listed with its id only.</summary>
        public IReadOnlyDictionary<long, string> ElementLabels { get; set; } = new Dictionary<long, string>();
    }

    /// <summary>Which element a family's write changes. <see cref="Type"/>: always the TYPE of each listed element (so every instance of
    /// it, listed or not): the prompt lists one id per type, once, never chunked. <see cref="Either"/>: a type parameter or an instance
    /// parameter, decided by the parameter at run time: confirm says so, ids are listed per instance. <see cref="Instance"/>: only the listed elements.</summary>
    public enum CopilotWriteLevel { Instance, Type, Either }

    /// <summary>What one check family tells Copilot to do, and how, with the tools of the protocol-3 engine (set_parameters and
    /// the reads). The wording differs per family (a type rename is not a parameter fill). A family is added by adding an
    /// entry to <see cref="JkrCopilotPrompt"/>'s registry and its id to <see cref="JkrCopilotFix.Allowed"/>.</summary>
    public sealed class CopilotPromptVariant
    {
        public CopilotPromptVariant(string taskMs, string taskEn, string howMs, string howEn, string changeMs, string changeEn, CopilotWriteLevel level = CopilotWriteLevel.Instance, params string[] writeParams)
        { Level = level; Params = writeParams ?? new string[0]; TaskMs = taskMs; TaskEn = taskEn; HowMs = howMs; HowEn = howEn; ChangeMs = changeMs; ChangeEn = changeEn; }
        /// <summary>The imperative: what to change.</summary>
        public string TaskMs { get; }
        public string TaskEn { get; }
        /// <summary>Which tool, which parameter, which limits (names only tools the merged engine has).</summary>
        public string HowMs { get; }
        public string HowEn { get; }
        /// <summary>One short phrase for the confirm dialog: what Copilot will change ("Rename the type of the elements").</summary>
        public string ChangeMs { get; }
        public string ChangeEn { get; }
        public CopilotWriteLevel Level { get; }
        /// <summary>AI-490: the parameter names the "How" text tells Copilot to set with set_parameters: the one place they are
        /// declared, shared by the prompt wording and by the <c>confirmed_write</c> field (JkrCopilotConfirmedWrite). Empty when the
        /// parameters are only known at run time (Db.1 mandatory columns, C1.6.3 room columns): no field is sent, the server asks as before.</summary>
        public IReadOnlyList<string> Params { get; }
        public string Task(bool bm) => bm ? TaskMs : TaskEn;
        public string How(bool bm) => bm ? HowMs : HowEn;
        public string Change(bool bm) => bm ? ChangeMs : ChangeEn;
    }

    /// <summary>
    /// AI-493 / AI-490 rework: the prompt "Fix via Copilot" sends to the protocol-3 engine (10 tools: set_parameters, the reads,
    /// lookup, ask_user, report, run_revit_code). That engine applies writes at once and its rule is "never ask permission"
    /// (ask_user is for missing input only, and the pane drops approval-worded options), so a prompt cannot make it ask Ya/Tidak.
    /// The confirm therefore happens BEFORE this prompt is sent (<see cref="JkrCopilotConfirm"/>, shown by the launcher), and
    /// the prompt says so: apply now, do not ask permission. It names the ids (never "the selection" or "this view": Copilot's
    /// own reads depend on the active view, AI-454/456), fences the scope (AI-455/463), names the tool and parameter, and
    /// leaves pass/fail to the JKR re-check. WPF-free; the <c>IssueVm</c> overload is in JkrCopilotPrompt.Issue.cs.
    /// </summary>
    public static partial class JkrCopilotPrompt
    {
        /// <summary>Ids per prompt. More ids are split into parts ("part 2 of 3"), each a separate prompt.</summary>
        public const int MaxIdsPerPrompt = 50;

        private static readonly CopilotPromptVariant Generic = new CopilotPromptVariant(
            "Ubah elemen di bawah supaya mematuhi semakan JKR ini.",
            "Change the elements below so they comply with this JKR check.",
            "Guna set_parameters pada id di bawah. Isi hanya nilai yang boleh diambil daripada model atau lookup; jangan meneka.",
            "Use set_parameters on the ids below. Set only values you can take from the model or from lookup; never guess.",
            "Ubah parameter elemen", "Change a parameter of the elements");

        /// <summary>Per-check-id wording. The extension point: add an entry (and the id in <see cref="JkrCopilotFix.Allowed"/>)
        /// for a new family; an id with no entry gets <see cref="Generic"/>. Each "How" names only tools of the merged engine and
        /// only what set_parameters can do (one parameter, one value per call; the value with its unit).</summary>
        private static readonly Dictionary<string, CopilotPromptVariant> Variants =
            new Dictionary<string, CopilotPromptVariant>(StringComparer.Ordinal)
            {
                ["Da.1"] = new CopilotPromptVariant(
                    "Tukar nama type setiap elemen di bawah supaya mengikut coding JKR (cth `(PT2p600a) 900 x 2325 a`). Ubah nama type sahaja.",
                    "Rename the type of each element below to follow JKR type-name coding (e.g. `(PT2p600a) 900 x 2325 a`). Change the type name only.",
                    "Guna set_parameters dengan param \"Type Name\", type_param true dan id elemen sebagai sasaran; satu panggilan bagi setiap nama baharu, semua dalam satu mesej. Ambil corak coding daripada lookup. Nama type milik type, jadi semua instance type itu turut bertukar nama: itu dijangka. Jangan ubah Family Name, jangan guna semula nama type yang sudah wujud, dan jangan mereka kod yang tidak boleh diterbitkan daripada model; nyatakan elemen yang dilangkau.",
                    "Use set_parameters with param \"Type Name\", type_param true and the element ids as target; one call per new name, all in one message. Take the coding pattern from lookup. A type name belongs to the type, so every instance of that type takes the new name: that is expected. Never change the Family Name, never reuse a type name that already exists, and never invent a code you cannot derive from the model; say which elements you skipped.",
                    "Tukar nama type elemen", "Rename the type of the elements", CopilotWriteLevel.Type, "Type Name"),
                ["Db.2"] = new CopilotPromptVariant(
                    "Tetapkan Material setiap elemen di bawah kepada kod material JKR yang betul. Isi Material yang kosong dan tukar yang tidak ikut coding JKR.",
                    "Set the Material of each element below to the correct JKR material code. Fill empty Materials and change any that do not follow JKR coding.",
                    "Guna set_parameters dengan param \"Material\" pada id di bawah (type_param true jika Material ialah parameter type bagi elemen ini); satu panggilan bagi setiap kod. Ambil kod daripada lookup dan guna Material yang sudah wujud dalam model. Jangan cipta Material baharu: jika tiada Material sepadan dengan kod, biarkan elemen itu dan nyatakannya.",
                    "Use set_parameters with param \"Material\" on the ids below (type_param true if Material is a type parameter of these elements); one call per code. Take the code from lookup and use a Material that already exists in the model. Do not create Materials: if no existing Material matches a code, leave that element and say so.",
                    "Tetapkan parameter Material", "Set the Material parameter", CopilotWriteLevel.Either, "Material"),
                ["Db.1"] = new CopilotPromptVariant(
                    "Ubah elemen di bawah dengan mengisi parameter wajib yang kosong (Buku Parameter pada LOi projek). Isi hanya parameter yang nilainya boleh diterbitkan daripada model.",
                    "Change the elements below by filling the mandatory parameters that are empty (Buku Parameter at the project LOI). Set only parameters whose value you can derive from the model.",
                    "Cari parameter wajib yang kosong dengan check atau inspect, kemudian guna set_parameters bagi setiap parameter yang nilainya boleh diambil daripada model atau lookup (type_param true bagi parameter type). Biarkan yang lain kosong dan nyatakan parameter mana; jangan meneka nilai.",
                    "Find the empty mandatory parameters with check or inspect, then use set_parameters for each parameter whose value you can take from the model or from lookup (type_param true for type parameters). Leave the others empty and say which; never guess a value.",
                    "Isi parameter wajib yang kosong", "Fill empty mandatory parameters", CopilotWriteLevel.Either),
                ["Dc.1"] = new CopilotPromptVariant(
                    "Betulkan type name setiap elemen di bawah dengan menukar nombornya supaya sama dengan nilai parameter geometri sebenar (nombor dalam mm, urutan lebar x tinggi). Tukar nama type sahaja; jangan ubah dimensi atau parameter geometri.",
                    "Rename the type of each element below by changing the number in its name so it equals the actual geometry parameter value (number in mm, width x height order). Change the type name only; never edit dimensions or geometry parameters.",
                    "Baca lebar dan tinggi sebenar dengan inspect. Kemudian guna set_parameters dengan param \"Type Name\", type_param true; nama baharu hanya menukar nombor, selebihnya sama. Satu panggilan bagi setiap nama baharu. Jangan tulis Width, Height atau mana-mana dimensi.",
                    "Read the actual width and height with inspect. Then use set_parameters with param \"Type Name\", type_param true; the new name changes only the numbers and keeps the rest. One call per new name. Never write Width, Height or any dimension.",
                    "Tukar nombor dalam nama type", "Change the number in the type name", CopilotWriteLevel.Type, "Type Name"),
                ["A5.2"] = new CopilotPromptVariant(
                    "Tukar nama grid line di bawah supaya urutan tiada lompatan: nombor 1,2,3,4… dan huruf A,B,C…. Ubah nama grid sahaja.",
                    "Rename the grid lines below so the sequence has no jumps: numbers 1,2,3,4… and letters A,B,C…. Change the grid names only.",
                    "Guna set_parameters dengan param \"Name\" (parameter instance) pada satu id grid bagi setiap panggilan. Hantar mengikut urutan supaya tiada panggilan menamakan grid dengan nama yang sudah digunakan (nama berganda ditolak); Jangan tukar ganti nama (swap) atau guna nama sementara: jika urutan memerlukannya, berhenti dan nyatakan. Berhenti pada penolakan pertama dan nyatakan grid mana. Jangan alih atau padam grid.",
                    "Use set_parameters with param \"Name\" (an instance parameter) on one grid id per call. Send them in an order where no call gives a grid a name that is already in use (a duplicate name is refused); Never swap names or use a temporary name: if the sequence needs that, stop and say so. Stop at the first refusal and say which grid. Never move or delete a grid.",
                    "Tukar nama grid line", "Rename the grid lines", CopilotWriteLevel.Instance, "Name"),
                ["C1.6.3"] = new CopilotPromptVariant(
                    "Ubah bilik di bawah dengan mengisi setiap kolum wajib jadual ruang yang kosong (parameter Room). Isi hanya nilai yang boleh diterbitkan daripada model.",
                    "Change the rooms below by filling every empty mandatory room-schedule column (Room parameters). Set only values you can derive from the model.",
                    "Cari kolum yang kosong dengan inspect, kemudian guna set_parameters bagi setiap kolum pada id bilik di bawah. Jika nilai tidak boleh diambil daripada model atau lookup, tanya saya sekali sahaja dengan ask_user; jangan meneka.",
                    "Find the empty columns with inspect, then use set_parameters for each column on the room ids below. If a value cannot be taken from the model or from lookup, ask me once with ask_user; never guess.",
                    "Isi parameter bilik yang kosong", "Fill empty room parameters"),
                ["C4.2.2"] = new CopilotPromptVariant(
                    "Ubah sheet di bawah dengan mengisi sel title block yang kosong: tajuk lukisan, dimodelkan oleh, timestamp. Isi hanya nilai yang diketahui.",
                    "Change the sheets below by filling the empty title block cells: drawing title, modelled by, timestamp. Set only values you know.",
                    "Guna set_parameters pada parameter sheet itu sendiri (cth Sheet Name, Drawn By) bagi id sheet di bawah. Nilai peringkat projek (Project Information) dan parameter type title block di luar skop: jangan ubah dan nyatakan sel mana kekal kosong. Jika nilai tidak diketahui, tanya saya sekali sahaja dengan ask_user.",
                    "Use set_parameters on the sheets' own parameters (e.g. Sheet Name, Drawn By) for the sheet ids below. Project-level values (Project Information) and title block type parameters are out of scope: do not change them and say which cells stay empty. If a value is not known, ask me once with ask_user.",
                    "Isi parameter sheet yang kosong", "Fill empty sheet parameters", CopilotWriteLevel.Instance, "Sheet Name", "Drawn By"),
            };

        /// <summary>The wording for a check id (the generic one when the family has no variant yet).</summary>
        public static CopilotPromptVariant VariantFor(string checkId) =>
            Variants.TryGetValue((checkId ?? "").Trim(), out var v) ? v : Generic;

        /// <summary>True when the check id has its own wording (not the generic one).</summary>
        public static bool HasVariant(string checkId) => Variants.ContainsKey((checkId ?? "").Trim());

        /// <summary>
        /// One prompt string per chunk of at most <see cref="MaxIdsPerPrompt"/> ids, in id order. No ids gives no
        /// prompt (a prompt with no ids would leave Copilot to guess its scope). <paramref name="bm"/> is the pane
        /// language (<c>PaneLanguage.Current != "en"</c>; the caller passes it).
        /// </summary>
        public static IReadOnlyList<string> Build(CopilotPromptFacts facts, bool bm)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            var ids = TargetIds(facts);
            var prompts = new List<string>();
            if (ids.Count == 0) return prompts;
            // A type-level write changes every instance of the type: one prompt, one id per type. Chunked parts would fight each
            // other (part 1 renames the type, part 2 finds it already changed or refused).
            if (VariantFor(facts.CheckId).Level == CopilotWriteLevel.Type)
            {
                prompts.Add(BuildOne(facts, bm, ids, 1, 1));
                return prompts;
            }
            var parts = (ids.Count + MaxIdsPerPrompt - 1) / MaxIdsPerPrompt;
            for (var p = 0; p < parts; p++)
                prompts.Add(BuildOne(facts, bm, PartIds(facts, p), p + 1, parts));
            return prompts;
        }

        /// <summary>AI-509: ONE set of facts for several rows of the same check ("Fix all elements via Copilot"): the ids of every row (distinct,
        /// in row order), every label, the categories joined. Required / Actual are kept only when every row says the same (else left out of the
        /// prompt rather than quoting one row for all). The prompt, the confirm and confirmed_write are all built from this one value, so the
        /// three cannot disagree, and chunking into parts of <see cref="MaxIdsPerPrompt"/> happens in <see cref="Build(CopilotPromptFacts, bool)"/>.</summary>
        public static CopilotPromptFacts MergeFacts(IEnumerable<CopilotPromptFacts> rows)
        {
            var list = (rows ?? Enumerable.Empty<CopilotPromptFacts>()).Where(r => r != null).ToList();
            if (list.Count == 0) throw new ArgumentException("no rows", nameof(rows));
            var ids = new List<long>();
            var seen = new HashSet<long>();
            var labels = new Dictionary<long, string>();
            foreach (var r in list)
            {
                foreach (var id in r.ElementIds ?? new List<long>())
                {
                    if (id <= 0) continue;
                    if (seen.Add(id)) ids.Add(id);
                    string l = null;
                    if (r.ElementLabels != null && r.ElementLabels.TryGetValue(id, out l) && !string.IsNullOrEmpty(l) && !labels.ContainsKey(id)) labels[id] = l;
                }
            }
            string Same(Func<CopilotPromptFacts, string> pick)
            {
                var first = CleanLabel(pick(list[0]));
                return list.All(r => string.Equals(CleanLabel(pick(r)), first, StringComparison.Ordinal)) ? first : "";
            }
            var cats = list.Select(r => CleanLabel(r.Category)).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new CopilotPromptFacts
            {
                CheckId = list[0].CheckId,
                Title = list[0].Title,
                Category = string.Join(", ", cats),
                Required = Same(r => r.Required),
                Actual = Same(r => r.Actual),
                ElementIds = ids,
                ElementLabels = labels,
            };
        }

        /// <summary>The ids a prompt and the confirm list: every distinct positive id, except for a type-level family, where only the
        /// first id of each type (an element label is its type name; an id with no label counts as its own type).</summary>
        public static List<long> TargetIds(CopilotPromptFacts facts)
        {
            var ids = (facts.ElementIds ?? new List<long>()).Where(i => i > 0).Distinct().ToList();
            if (VariantFor(facts.CheckId).Level != CopilotWriteLevel.Type) return ids;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var one = new List<long>();
            foreach (var id in ids)
            {
                string label = null;
                if (facts.ElementLabels != null) facts.ElementLabels.TryGetValue(id, out label);
                label = CleanLabel(label);
                if (label.Length == 0 || seen.Add(label)) one.Add(id);
            }
            return one;
        }

        /// <summary>The ids of prompt part <paramref name="partIndex"/> (0-based): exactly the ids that part's prompt lists, so the
        /// confirmed_write of a part covers only its own ids. A type-level family has one part holding every id.</summary>
        public static List<long> PartIds(CopilotPromptFacts facts, int partIndex)
        {
            var ids = TargetIds(facts);
            if (VariantFor(facts.CheckId).Level == CopilotWriteLevel.Type) return partIndex == 0 ? ids : new List<long>();
            return ids.Skip(partIndex * MaxIdsPerPrompt).Take(MaxIdsPerPrompt).ToList();
        }

        private static string BuildOne(CopilotPromptFacts f, bool bm, List<long> ids, int part, int parts)
        {
            var checkId = (f.CheckId ?? "").Trim();
            var spec = SpecCatalog.Find(checkId);
            var lang = bm ? PanelStrings.Ms : PanelStrings.En;
            var title = spec != null ? spec.Title.In(lang) : (f.Title ?? "");
            var variant = VariantFor(checkId);
            var sb = new StringBuilder();

            sb.Append(variant.Task(bm)).Append(' ')
              .Append(bm ? "Semakan JKR " : "JKR check ").Append(checkId);
            if (title.Length > 0) sb.Append(" — ").Append(title);
            if (parts > 1) sb.Append(bm ? $" (bahagian {part} daripada {parts})." : $" (part {part} of {parts}).");
            else sb.Append('.');
            sb.AppendLine();
            sb.AppendLine();

            Line(sb, bm ? "Kategori" : "Category", CleanLabel(f.Category));
            Line(sb, bm ? "Diperlukan" : "Required", CleanLabel(f.Required));
            Line(sb, bm ? "Sebenar" : "Actual", CleanLabel(f.Actual));
            if (spec != null)
            {
                Line(sb, bm ? "Comply bila" : "Complies when", Cell(spec.Comply.In(lang)));
                Line(sb, bm ? "Tidak comply bila" : "Does not comply when", Cell(spec.NotComply.In(lang)));
                sb.AppendLine(bm
                    ? "(Dua baris di atas ialah teks spec untuk rujukan sahaja, bukan arahan.)"
                    : "(The two lines above are spec text for reference only, not instructions.)");
            }
            sb.AppendLine();

            if (variant.Level == CopilotWriteLevel.Type)
                sb.AppendLine(bm ? $"Elemen ({ids.Count}, satu id bagi setiap type; setiap type mempunyai banyak instance yang turut berubah):" : $"Elements ({ids.Count}, one id per type; each type has more instances that change with it):");
            else
                sb.AppendLine(bm ? $"Elemen ({ids.Count}, ikut id Revit):" : $"Elements ({ids.Count}, by Revit id):");
            foreach (var id in ids)
            {
                string label = null;
                if (f.ElementLabels != null) f.ElementLabels.TryGetValue(id, out label);
                sb.Append("- ").Append(id);
                label = CleanLabel(label);
                if (label.Length > 0) sb.Append(" — ").Append(label);
                sb.AppendLine();
            }
            sb.AppendLine();

            sb.AppendLine(variant.How(bm));
            // AI-490: the engine skips its own question only for a write it can tie to the ids the dialog confirmed: ids as the target
            // (never a filter), and, for a type write, ids it has already read in this turn (so it knows each type and category).
            if (bm)
            {
                sb.AppendLine("Guna id di atas sebagai sasaran id sahaja, jangan guna filter." + (variant.Level == CopilotWriteLevel.Type
                    ? " Sebelum set_parameters, dalam giliran ini, baca dahulu id ini dengan inspect DAN jalankan find_elements bagi kategori itu dengan kumpulan mengikut type (kedua-duanya), supaya type, kategori dan bilangan instance diketahui."
                    : ""));
            }
            else
            {
                sb.AppendLine("Use the ids above as ids only as the target; never use a filter." + (variant.Level == CopilotWriteLevel.Type
                    ? " Read these ids first, in this turn, before set_parameters: read them with inspect AND run find_elements for their category grouped by type (both), so each type, its category and its instance count are known."
                    : ""));
            }
            if (bm)
            {
                sb.AppendLine("Skop: ubah hanya " + (variant.Level == CopilotWriteLevel.Type ? "type bagi id elemen di atas (semua instance type itu turut berubah: itu dijangka)" : "id elemen di atas") + ". Jangan padam apa-apa. Jangan sentuh elemen lain, termasuk yang kelihatan serupa. Jangan guna pilihan (selection) atau view yang sedang dibuka untuk mencari elemen.");
                if (parts > 1)
                    sb.AppendLine($"Ini bahagian {part} daripada {parts}; bahagian lain dihantar berasingan. Urus hanya id dalam bahagian ini.");
                sb.AppendLine("Saya sudah mengesahkan perubahan ini dalam panel JKR: laksanakan sekarang. Jangan minta kebenaran atau pengesahan daripada saya. Tanya saya hanya jika nilai yang diperlukan tiada dalam model dan lookup.");
                sb.Append("Jangan putuskan comply atau tidak comply; semakan semula JKR yang memutuskannya.");
            }
            else
            {
                sb.AppendLine("Scope: change only " + (variant.Level == CopilotWriteLevel.Type ? "the types of the element ids above (all instances of those types change with them: that is expected)" : "the element ids above") + ". Do not delete anything. Do not touch any other element, including ones that look similar. Do not use the selection or the active view to find elements.");
                if (parts > 1)
                    sb.AppendLine($"This is part {part} of {parts}; the other parts are sent separately. Handle only the ids in this part.");
                sb.AppendLine("I already confirmed this change in the JKR pane: apply it now. Do not ask me for permission or confirmation. Ask me only if a value you need is not in the model or in lookup.");
                sb.Append("Do not decide pass or fail; the JKR re-check decides that.");
            }
            return sb.ToString();
        }

        /// <summary>Max chars of an element label in the prompt.</summary>
        public const int MaxLabelLength = 120;

        /// <summary>A model element name goes into the prompt as one line: control characters (newlines, tabs) become
        /// spaces, runs of spaces collapse, and the text is cut at <see cref="MaxLabelLength"/>, so a name cannot add lines.</summary>
        public static string CleanLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            var sb = new StringBuilder(label.Length);
            foreach (var c in label)
                sb.Append(char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c);
            var t = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            return t.Length > MaxLabelLength ? t.Substring(0, MaxLabelLength).TrimEnd() : t;
        }

        private static void Line(StringBuilder sb, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            sb.Append(label).Append(": ").AppendLine(value.Trim());
        }

        /// <summary>The spec writes "—" for a cell it leaves empty.</summary>
        private static string Cell(string s) => string.IsNullOrWhiteSpace(s) || s.Trim() == SpecCatalog.EmptyCell ? "" : s;
    }
}

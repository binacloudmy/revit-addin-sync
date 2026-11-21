using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>What the AI fixer has said about one row.</summary>
    public enum FixerAiState
    {
        /// <summary>Not asked yet: "No suggestion yet", with an Ask AI button.</summary>
        NotAsked,
        /// <summary>Asked, and it proposed a value (with its reason).</summary>
        Suggested,
        /// <summary>Asked, and it had nothing it could stand behind.</summary>
        NoSuggestion,
    }

    /// <summary>A manual value turned into the write the pane queues (same shape as a backend fix).</summary>
    public sealed class JkrManualFixPlan
    {
        /// <summary>"rename_type" or "set_parameter".</summary>
        public string Action { get; set; } = "";
        public string ParameterName { get; set; } = "";
        /// <summary>"type" or "instance".</summary>
        public string Target { get; set; } = "instance";
        /// <summary>The value to write: the reference-set spelling when one applies, else as typed (trimmed).</summary>
        public string Value { get; set; } = "";
        /// <summary>Why the value is not accepted (empty when it is).</summary>
        public string Error { get; set; } = "";
        public bool Ok => Error.Length == 0;
    }

    /// <summary>
    /// Revit-free rules for the value a person types (or picks) on an AI-fixer row
    /// (AI-435): which write it becomes, and whether the rule would accept it before
    /// anything is written. The re-check after the write (AI-427) is still the judge.
    /// </summary>
    public static class JkrManualFix
    {
        public const int MaxLength = 200;

        // The backend's own patterns (bina-ai jkr_deterministic): a JKR parameter name in the rule
        // text, the element-name prefix, and the Level / Grid designators.
        private static readonly Regex ParamInRule = new Regex(@"[A-Za-z_]+_jkr_s[ti][a-z]", RegexOptions.Compiled);
        private static readonly Regex JkrName = new Regex(@"^jkr[A-Za-z]{2}_.+$", RegexOptions.Compiled);
        private static readonly Regex LevelName = new Regex(@"^L\d{2}(\s\+?-?\d+(?:\.\d{1,3})?)?$", RegexOptions.Compiled);
        private static readonly Regex GridName = new Regex(@"^(?:[A-HJ-NP-Z]|\d{1,2}|[A-HJ-NP-Z]-?\d{1,2})$", RegexOptions.Compiled);
        private static readonly Regex MaterialCode = new Regex(@"\([A-Za-z]{1,4}\d{0,2}\)", RegexOptions.Compiled);

        /// <summary>The JKR parameter the rule names ("Kod_Jenis_jkr_stt value valid"), or "".</summary>
        public static string ParameterOf(string rule)
        {
            var m = ParamInRule.Match(rule ?? "");
            return m.Success ? m.Value : "";
        }

        /// <summary>"_jkr_sit" is an instance parameter; "_jkr_stt" / "_stm" / "_sty" are type parameters.</summary>
        public static string TargetOf(string parameterName) =>
            (parameterName ?? "").EndsWith("_jkr_sit", StringComparison.Ordinal) ? "instance" : "type";

        /// <param name="rule">The backend rule string (IssueVm.RawRule).</param>
        /// <param name="revitCategory">"Levels" / "Grids" use their own name pattern.</param>
        /// <param name="value">What the person typed or picked.</param>
        /// <param name="options">The reference set, or empty when the rule has none (free text).</param>
        /// <param name="lang">"ms" or "en" for the error text.</param>
        public static JkrManualFixPlan Plan(string rule, string revitCategory, string value,
                                            IReadOnlyList<string> options = null, string lang = "en")
        {
            var ms = string.Equals(lang, "ms", StringComparison.OrdinalIgnoreCase);
            var v = (value ?? "").Trim();
            var plan = new JkrManualFixPlan { Value = v };
            if (v.Length == 0)
            {
                plan.Error = ms ? "Masukkan nilai." : "Enter a value.";
                return plan;
            }
            if (v.Length > MaxLength || v.Any(char.IsControl))
            {
                plan.Error = ms ? "Nilai tidak sah (terlalu panjang atau ada aksara kawalan)." : "Not a valid value (too long, or has control characters).";
                return plan;
            }

            var param = ParameterOf(rule);
            if (param.Length > 0) { plan.Action = "set_parameter"; plan.ParameterName = param; plan.Target = TargetOf(param); }
            else { plan.Action = "rename_type"; plan.Target = "type"; }

            if (options != null && options.Count > 0)
            {
                var hit = options.FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                {
                    plan.Error = ms ? "Pilih salah satu nilai yang dibenarkan." : "Pick one of the allowed values.";
                    return plan;
                }
                plan.Value = hit;
                return plan;
            }

            if (param.Length > 0)
            {
                // Bahan_* parameters take "(code) Description"; the rest are free text the re-check judges.
                if (param.StartsWith("Bahan_", StringComparison.Ordinal) && !MaterialCode.IsMatch(v))
                    plan.Error = ms ? "Bahan mesti dalam bentuk “(kod) Penerangan”, mis. “(kt) Konkrit”." : "A material must read “(code) Description”, e.g. “(kt) Konkrit”.";
                return plan;
            }

            var cat = revitCategory ?? "";
            if (cat.Equals("Levels", StringComparison.OrdinalIgnoreCase))
            {
                if (!LevelName.IsMatch(v)) plan.Error = ms ? "Nama Level mesti seperti “L02 +4.500”." : "A level name must look like “L02 +4.500”.";
            }
            else if (cat.Equals("Grids", StringComparison.OrdinalIgnoreCase))
            {
                if (!GridName.IsMatch(v)) plan.Error = ms ? "Nama Grid: huruf (tanpa I/O), nombor, atau “B2”." : "A grid name is a letter (no I/O), a number, or “B2”.";
            }
            else if (!JkrName.IsMatch(v))
            {
                plan.Error = ms ? "Nama mesti bermula dengan “jkr” + kod bidang, mis. “jkrAR_dor-k_(ky) Pintu Kayu”." : "A name must start with “jkr” + discipline code, e.g. “jkrAR_dor-k_(ky) Pintu Kayu”.";
            }
            return plan;
        }
    }

    /// <summary>
    /// The AI-fixer row control's model (AI-435): the AI suggestion (or an explicit "no
    /// suggestion yet") AND a manual input, always both. Revit- and WPF-free; the
    /// control is UI/Jkr/Controls/FixerRow.xaml and is reusable: AI-434's multi-valid-code
    /// picker row feeds the same <see cref="Options"/> / manual path. The model only decides
    /// what is shown and whether a typed value may be written; the pane does the write and
    /// the re-check (RecheckAsync, AI-427).
    /// </summary>
    public sealed class JkrFixerRow : INotifyPropertyChanged
    {
        private static readonly Dictionary<string, Dictionary<string, string>> T =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ms"] = new Dictionary<string, string>
                {
                    ["none"] = "Belum ada cadangan.",
                    ["noneAsked"] = "Pembaiki AI tiada cadangan untuk item ini.",
                    ["ask"] = "Minta AI",
                    ["accept"] = "Terima cadangan AI",
                    ["manual"] = "Atau masukkan nilai sendiri",
                    ["write"] = "Tulis nilai",
                    ["pick"] = "Pilih nilai",
                    ["aiBadge"] = "Dicadangkan AI",
                    ["note"] = "Tiada apa-apa ditulis sehingga anda sahkan, dan item hanya selesai selepas peraturan lulus semula.",
                },
                ["en"] = new Dictionary<string, string>
                {
                    ["none"] = "No suggestion yet.",
                    ["noneAsked"] = "The AI fixer has no suggestion for this item.",
                    ["ask"] = "Ask AI",
                    ["accept"] = "Accept AI suggestion",
                    ["manual"] = "Or enter the value yourself",
                    ["write"] = "Write value",
                    ["pick"] = "Pick a value",
                    ["aiBadge"] = "AI suggested",
                    ["note"] = "Nothing is written until you confirm, and the item only resolves once the rule passes again.",
                },
            };

        private readonly string _rule;
        private readonly string _category;
        private string _manual = "";

        public string IssueId { get; }
        public string Lang { get; }
        public FixerAiState AiState { get; }
        /// <summary>The value the AI fixer proposed (state Suggested), else "".</summary>
        public string AiValue { get; }
        /// <summary>Its reason ("closest valid material code…"), else "".</summary>
        public string AiReason { get; }
        /// <summary>The reference set for this rule (e.g. material codes), else empty = free text.</summary>
        public IReadOnlyList<string> Options { get; }

        public JkrFixerRow(string issueId, string rule, string revitCategory, FixerAiState state,
                           string aiValue = "", string aiReason = "", IEnumerable<string> options = null, string lang = "en",
                           bool isPick = false, string pickCode = "", string pickReason = "")
        {
            IssueId = issueId ?? "";
            _rule = rule ?? "";
            _category = revitCategory ?? "";
            AiState = state;
            AiValue = state == FixerAiState.Suggested ? aiValue ?? "" : "";
            AiReason = state == FixerAiState.Suggested ? aiReason ?? "" : "";
            Options = (options ?? Enumerable.Empty<string>()).Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
            Lang = T.ContainsKey(lang ?? "") ? lang.ToLowerInvariant() : "en";
            // AI-434: a multi-valid-code picker. The dropdown starts on the AI's code when it gave one
            // (badged "AI suggested"), else on the first option; nothing is written until a person writes it.
            IsPick = isPick && Options.Count > 0;
            // AI-485: a name pick (no JKR parameter in the rule) starts empty: no AI code, no first option.
            IsNamePick = IsPick && JkrManualFix.ParameterOf(_rule).Length == 0;
            if (IsNamePick) _manual = "";
            else if (IsPick)
            {
                PickCode = Options.FirstOrDefault(o => string.Equals(o, pickCode ?? "", StringComparison.OrdinalIgnoreCase)) ?? "";
                PickReason = PickCode.Length > 0 ? pickReason ?? "" : "";
                _manual = PickCode.Length > 0 ? PickCode : Options[0];
            }
        }

        /// <summary>The row is a multi-valid-code picker (AI-434).</summary>
        public bool IsPick { get; }
        /// <summary>AI-485: the options are whole names; nothing is preselected and Ask AI is not offered.</summary>
        public bool IsNamePick { get; }
        /// <summary>The AI's code among the options, or "".</summary>
        public string PickCode { get; } = "";
        /// <summary>Why the AI chose it: the dropdown's tooltip.</summary>
        public string PickReason { get; } = "";
        public string PickTooltip => PickReason;
        public string AiBadge => S("aiBadge");
        /// <summary>The "AI suggested" badge: the dropdown sits on the AI's code.</summary>
        public bool ShowAiBadge => IsPick && PickCode.Length > 0 && string.Equals(_manual, PickCode, StringComparison.OrdinalIgnoreCase);
        /// <summary>The person has chosen a value themselves (a rebuild of the row keeps it).</summary>
        public bool ManualTouched { get; private set; }

        private string S(string k) => T[Lang][k];

        /// <summary>The AI line: the explicit "no suggestion" text, or empty when a value is proposed.</summary>
        public string AiLine => IsPick ? "" : AiState == FixerAiState.NotAsked ? S("none")
                              : AiState == FixerAiState.NoSuggestion ? S("noneAsked") : "";
        public bool HasAiValue => AiState == FixerAiState.Suggested;
        public bool HasAiReason => AiReason.Length > 0;
        /// <summary>Ask AI is offered until the fixer has proposed something.</summary>
        public bool CanAsk => IsNamePick ? false : IsPick ? PickCode.Length == 0 && AiState != FixerAiState.NoSuggestion : AiState != FixerAiState.Suggested;
        public bool CanAccept => !IsPick && HasAiValue && AiValue.Length > 0;
        public string AskLabel => S("ask");
        public string AcceptLabel => S("accept");
        public string ManualLabel => IsPick ? S("pick") : S("manual");
        public string WriteLabel => S("write");
        public string Note => S("note");

        /// <summary>A reference set exists: the input is a dropdown restricted to it.</summary>
        public bool HasOptions => Options.Count > 0;
        public bool IsFreeText => !HasOptions;

        /// <summary>What the person typed or picked. Setting it re-validates it.</summary>
        public string ManualValue
        {
            get => _manual;
            set
            {
                var v = value ?? "";
                if (_manual == v) return;
                _manual = v;
                ManualTouched = true;
                Raise(nameof(ShowAiBadge));
                Raise(nameof(ManualValue)); Raise(nameof(ManualError)); Raise(nameof(HasManualError)); Raise(nameof(CanWriteManual));
            }
        }

        /// <summary>The plan for the current value (validated against the rule).</summary>
        public JkrManualFixPlan ManualPlan => JkrManualFix.Plan(_rule, _category, _manual, Options, Lang);

        /// <summary>Inline validation text; empty until something is typed, so an empty box is not an error.</summary>
        public string ManualError => _manual.Trim().Length == 0 ? "" : ManualPlan.Error;
        public bool HasManualError => ManualError.Length > 0;
        /// <summary>The Write button: a value that passes the rule's own check.</summary>
        public bool CanWriteManual => ManualPlan.Ok;

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

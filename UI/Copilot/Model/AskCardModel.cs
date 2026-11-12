#nullable enable
// An ask_user question as the pane shows it (lean-harness spec §7.11, §13;
// W5 Task 3). Revit-free.
//
// Ask cards come from the assistant and are styled unlike confirm cards. They
// can never stand in for approval or collect secrets:
//   - a clarify option, field or field choice whose label reads like approval
//     (the confirm card's own words too: "Yes, apply", "Ya", "OK",
//     "Laksanakan", "Setuju") is dropped; with fewer than 2 options left the
//     card falls back to one free-text answer;
//   - a field that asks for a password, token, PIN or other credential is
//     never shown, and a question that asks for one gets no answer box at
//     all, only "Don't answer";
//   - a project note shows the fact to be saved and the pane's OWN buttons
//     ("Save note" / "Don't save"); the cloud saves it only on Save.
// Answers follow protocol 3: a chosen option is "<option_id>": true; fields
// answer under their ids (numbers as numbers, choices as the chosen id); a
// free-text answer is "text".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class AskOptionModel
    {
        public string Id = "";
        public string Label = "";
    }

    public sealed class AskFieldModel
    {
        public string Id = "";
        public string Label = "";
        /// <summary>One of <see cref="AskFieldTypes"/>.</summary>
        public string Type = AskFieldTypes.Text;
        public string? Unit;
        public List<AskOptionModel> Choices = new List<AskOptionModel>();
        /// <summary>The Submit button waits for a value in every required field. The protocol has no
        /// "required" flag yet, so this stays false for every field the cloud sends today and Submit is
        /// enabled as before; set it where the field is built once the cloud says which are required.</summary>
        public bool Required;
    }

    public sealed class AskCardModel
    {
        public const string FreeTextId = "text";

        public string TurnId = "";
        public string RequestId = "";
        public string Question = "";
        public ReceiptLanguage Language;
        public List<AskOptionModel> Options = new List<AskOptionModel>();
        public List<AskFieldModel> Fields = new List<AskFieldModel>();
        public bool Multi;
        /// <summary>Neither options nor fields survive: one text box answered as "text".</summary>
        public bool FreeText;
        /// <summary>The question itself asks for a credential: no answer box, only "Don't answer".</summary>
        public bool AsksForCredential;
        public string CredentialNote = "";
        /// <summary>Said when the drafter types a reply to a credential question (N1).</summary>
        public string TypedRefusalNote = "";
        public string DeclineLabel = "";
        public bool IsProjectNote;
        public string? NoteText;
        public string SaveLabel = "";
        public string DontSaveLabel = "";
        /// <summary>The header that marks the card as the assistant's question.</summary>
        public string FromLabel = "";
        /// <summary>An option or field was removed by the guards above.</summary>
        public bool SomethingWasDropped;

        /// <summary>Set when the options are the server's gate for a wide write (write_all + write_cancel): the
        /// "Needs your OK" card with those buttons. Null for every other question.</summary>
        public WideWriteModel? Wide;

        /// <summary>Set once the drafter answered (for the collapsed record).</summary>
        public string? AnsweredSummary;

        private string _yesId = "yes";
        private string _noId = "no";

        private static readonly Regex Approval = new Regex(
            @"\b(approv\w*|confirm\w*|authori[sz]\w*|allow|proceed|go\s+ahead|apply|applied|yes|ok|okay|okey|"
            + @"lulus\w*|sahkan|benarkan|teruskan|laksana\w*|jalankan|ya|setuju\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex Credential = new Regex(
            @"(password|passcode|passwd|kata\s*laluan|token|api[\s_-]*key|secret|\bpin\b|\botp\b|credential|kelayakan)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // An option that reads like "no": the card shows it as the quiet ghost button, last in the list.
        private static readonly Regex Decline = new Regex(
            @"\b(cancel\w*|batal\w*|do\s+not|don'?t|no\s+thanks?|skip|none|nothing|jangan|tidak\s+perlu|abaikan|never\s*mind)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>True for a label that reads like declining or cancelling ("Cancel", "Batal", "Do not write",
        /// "No thanks", "Skip", "None of these", "Write nothing", "Jangan"). Styling only: it never makes an
        /// option safe or unsafe, <see cref="IsApprovalLike"/> still decides what is shown.</summary>
        public static bool IsDeclineLike(string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) return false;
            var t = label!.Trim().TrimEnd('.', '!');
            return t.Equals("no", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("tidak", StringComparison.OrdinalIgnoreCase)
                   || t.Equals("tak", StringComparison.OrdinalIgnoreCase)
                   || Decline.IsMatch(t);
        }

        /// <summary>The question is a plain choice between 2 or 3 options (not several-of, not a project note,
        /// not a credential question): the card then wears the "Needs your OK" look with full-width buttons.</summary>
        public bool IsChoiceLook =>
            !IsProjectNote && !AsksForCredential && !Multi && Wide == null && Options.Count >= 2 && Options.Count <= 3;

        /// <summary>The Choice field's grey text until the drafter picks. Nothing is preselected.</summary>
        public static string ChoosePlaceholder(bool ms) => ms ? "Pilih..." : "Choose...";

        /// <summary>Up / Down in an open choice list: moves by <paramref name="delta"/> and stops at the ends.
        /// From "nothing picked" (-1) Down lands on the first row and Up on the last.</summary>
        public static int MoveChoice(int index, int count, int delta)
        {
            if (count <= 0) return -1;
            if (index < 0) return delta >= 0 ? 0 : count - 1;
            return Math.Max(0, Math.Min(count - 1, index + delta));
        }

        public static bool IsApprovalLike(string? label) => !string.IsNullOrWhiteSpace(label) && Approval.IsMatch(label!);

        public static bool IsCredentialLike(string? idOrLabel) => !string.IsNullOrWhiteSpace(idOrLabel) && Credential.IsMatch(idOrLabel!);

        public static AskCardModel From(AskDto ask, ReceiptLanguage lang)
        {
            if (ask == null) throw new ArgumentNullException(nameof(ask));
            var ms = lang == ReceiptLanguage.Ms;
            var card = new AskCardModel
            {
                RequestId = ask.RequestId ?? "",
                Question = ask.Question ?? "",
                Language = lang,
                Multi = ask.Multi,
                IsProjectNote = ask.Purpose == AskPurposes.ProjectNote,
                NoteText = ask.NoteText,
                SaveLabel = ms ? "Simpan nota" : "Save note",
                DontSaveLabel = ms ? "Jangan simpan" : "Don't save",
                FromLabel = ms ? "Soalan daripada pembantu" : "Question from the assistant",
                CredentialNote = ms
                    ? "Soalan ini meminta kata laluan atau token. BINA tidak pernah meminta maklumat itu di sini, jadi tiada ruang jawapan."
                    : "This question asks for a password or token. BINA never asks for those here, so there is no answer box.",
                TypedRefusalNote = ms
                    ? "Tidak dihantar. Soalan ini meminta kata laluan atau token, dan BINA tidak pernah meminta maklumat itu di sini. Tekan \"Jangan jawab\" pada soalan untuk teruskan."
                    : "Not sent. This question asks for a password or token, and BINA never asks for those here. Press \"Don't answer\" on the question to go on.",
                DeclineLabel = ms ? "Jangan jawab" : "Don't answer",
            };

            if (!card.IsProjectNote && IsCredentialLike(card.Question))
            {
                card.AsksForCredential = true;
                card.SomethingWasDropped = true;
                return card;
            }

            var options = (ask.Options ?? new List<AskOptionDto>())
                .Where(o => o != null && !string.IsNullOrWhiteSpace(o.Id))
                .Select(o => new AskOptionModel { Id = o.Id, Label = o.Label ?? o.Id })
                .ToList();

            if (card.IsProjectNote)
            {
                // The pane's own Yes/No. The cloud's ids are reused so the answer
                // lands on the option it offered.
                card._yesId = options.FirstOrDefault(o => o.Id.Equals("yes", StringComparison.OrdinalIgnoreCase))?.Id
                              ?? options.ElementAtOrDefault(0)?.Id ?? "yes";
                card._noId = options.FirstOrDefault(o => o.Id.Equals("no", StringComparison.OrdinalIgnoreCase))?.Id
                             ?? options.ElementAtOrDefault(1)?.Id ?? "no";
                return card;
            }

            // The gate's two options (write_all, write_cancel) are the one exception to the approval filter: the server's own
            // gate for a wide write sends them. Exactly those two ids, and only when both are present (and not a several-of
            // question); every other option is filtered as before, and the credential rules still apply.
            var gate = !card.Multi && WideWriteModel.IsGateOptions(options);
            var safe = options.Where(o => !IsApprovalLike(o.Label)
                                          || (gate && (o.Id == WideWriteModel.WriteAllId || o.Id == WideWriteModel.WriteCancelId))).ToList();
            if (safe.Count != options.Count) card.SomethingWasDropped = true;
            card.Options = safe.Count >= 2 ? safe : new List<AskOptionModel>();
            if (safe.Count == 1) card.SomethingWasDropped = true;

            foreach (var f in ask.Fields ?? new List<AskFieldDto>())
            {
                if (f == null || string.IsNullOrWhiteSpace(f.Id)) continue;
                if (IsCredentialLike(f.Id) || IsCredentialLike(f.Label) || IsApprovalLike(f.Label)) { card.SomethingWasDropped = true; continue; }
                var field = new AskFieldModel
                {
                    Id = f.Id,
                    Label = string.IsNullOrWhiteSpace(f.Label) ? f.Id : f.Label,
                    Type = AskFieldTypes.All.Contains(f.Type) ? f.Type : AskFieldTypes.Text,
                    Unit = f.Unit,
                    Choices = (f.Choices ?? new List<AskOptionDto>())
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id))
                        .Select(c => new AskOptionModel { Id = c.Id, Label = c.Label ?? c.Id }).ToList(),
                };
                var safeChoices = field.Choices.Where(c => !IsApprovalLike(c.Label)).ToList();
                if (safeChoices.Count != field.Choices.Count) { card.SomethingWasDropped = true; field.Choices = safeChoices; }
                if (field.Type == AskFieldTypes.Choice && field.Choices.Count < 2) field.Type = AskFieldTypes.Text;
                card.Fields.Add(field);
            }

            if (gate && card.Options.Count >= 2) card.Wide = WideWriteModel.FromAsk(card.Question, card.Options, lang);

            card.FreeText = card.Options.Count == 0 && card.Fields.Count == 0;
            return card;
        }

        public AnswerDto AnswerForOptions(IEnumerable<string> optionIds)
        {
            var allowed = new HashSet<string>(Options.Select(o => o.Id));
            var answer = new AnswerDto();
            foreach (var id in optionIds ?? Array.Empty<string>())
                if (id != null && allowed.Contains(id)) answer.Values[id] = JsonSerializer.SerializeToElement(true);
            return answer;
        }

        /// <summary>Ids of the required fields that have no value yet (blank counts as none). Empty when the
        /// model knows of no required field, so Submit stays enabled as it always was.</summary>
        public List<string> MissingRequired(IDictionary<string, string> values) =>
            Fields.Where(f => f.Required
                              && (values == null || !values.TryGetValue(f.Id, out var raw) || string.IsNullOrWhiteSpace(raw)))
                .Select(f => f.Id).ToList();

        /// <summary>The collapsed record of a submitted form: "Label: value" per filled field, joined by " · ".</summary>
        public string SummaryForFields(IDictionary<string, string> values) =>
            string.Join(" · ", Fields.Where(f => values.TryGetValue(f.Id, out var v) && !string.IsNullOrWhiteSpace(v))
                .Select(f => f.Label + ": " + (f.Type == AskFieldTypes.Choice
                    ? f.Choices.FirstOrDefault(c => c.Id == values[f.Id])?.Label ?? values[f.Id]
                    : values[f.Id] + (string.IsNullOrEmpty(f.Unit) ? "" : " " + f.Unit))));

        public AnswerDto AnswerForFields(IDictionary<string, string> values)
        {
            var answer = new AnswerDto();
            foreach (var f in Fields)
            {
                if (values == null || !values.TryGetValue(f.Id, out var raw) || raw == null) continue;
                raw = raw.Trim();
                if (raw.Length == 0) continue;
                if (f.Type == AskFieldTypes.Number
                    && double.TryParse(raw.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    answer.Values[f.Id] = JsonSerializer.SerializeToElement(d);
                else if (f.Type == AskFieldTypes.Choice)
                {
                    var choice = f.Choices.FirstOrDefault(c => c.Id == raw) ?? f.Choices.FirstOrDefault(c => c.Label == raw);
                    if (choice != null) answer.Values[f.Id] = JsonSerializer.SerializeToElement(choice.Id);
                }
                else
                    answer.Values[f.Id] = JsonSerializer.SerializeToElement(raw);
            }
            return answer;
        }

        /// <summary>A typed chat message while this card is parked (N1). A credential
        /// question is refused here, before anything marks the card answered, so its
        /// "Don't answer" button stays live and the typed text goes nowhere: not to
        /// the cloud, the thread or local history. Null when typed text may answer it.</summary>
        public string? RefuseTypedAnswer() =>
            AsksForCredential && AnsweredSummary == null ? TypedRefusalNote : null;

        /// <summary>The answer for "Don't answer": an empty free-text answer.</summary>
        public AnswerDto AnswerDeclined() => AnswerForText("");

        public AnswerDto AnswerForText(string text)
        {
            var answer = new AnswerDto();
            answer.Values[FreeTextId] = JsonSerializer.SerializeToElement((text ?? "").Trim());
            return answer;
        }

        public AnswerDto AnswerProjectNote(bool save)
        {
            var answer = new AnswerDto();
            answer.Values[save ? _yesId : _noId] = JsonSerializer.SerializeToElement(true);
            return answer;
        }
    }
}

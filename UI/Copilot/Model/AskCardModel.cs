#nullable enable
// An ask_user question as the pane shows it (lean-harness spec §7.11, §13;
// W5 Task 3). Revit-free.
//
// Ask cards come from the assistant and are styled unlike result cards. They
// can never stand in for approval or collect secrets:
//   - a clarify option, field or field choice whose label reads like approval
//     (the old confirm card's words too: "Yes, apply", "Ya", "OK",
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
        /// <summary>What the answer carries (for the backend's questions a long canonical text).</summary>
        public string Id = "";
        /// <summary>What the card shows (short).</summary>
        public string Label = "";
        /// <summary>Small secondary text under the label, e.g. "1:200". Optional.</summary>
        public string? Meta;
        /// <summary>One of <see cref="AskOptionKinds"/>, or null for an ordinary pick.</summary>
        public string? Kind;

        public bool IsSkip => Kind == AskOptionKinds.Skip;
        public bool IsCreate => Kind == AskOptionKinds.Create;
    }

    /// <summary>A thing the card lists as ready: it needs no decision and will be made.</summary>
    public sealed class AskReadyItem
    {
        public string Label = "";
        public string? Detail;
    }

    public sealed class AskFieldModel
    {
        public string Id = "";
        public string Label = "";
        /// <summary>One of <see cref="AskFieldTypes"/>.</summary>
        public string Type = AskFieldTypes.Text;
        public string? Unit;
        public List<AskOptionModel> Choices = new List<AskOptionModel>();
        /// <summary>The heading the field is listed under. Optional.</summary>
        public string? Group;
        /// <summary>A short fact shown after the label ("2 floor plans"). Optional.</summary>
        public string? Note;
    }

    public sealed partial class AskCardModel
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

        /// <summary>Set once the drafter answered (for the collapsed record).</summary>
        public string? AnsweredSummary;

        /// <summary>The frame's heading, or null (the card then uses <see cref="DefaultTitle"/>).</summary>
        public string? Title;
        /// <summary>What one made thing is called, singular ("sheet"), or null.</summary>
        public string? Noun;
        /// <summary>Things that need no decision and will be made.</summary>
        public List<AskReadyItem> Ready = new List<AskReadyItem>();

        private string _yesId = "yes";
        private string _noId = "no";

        private static readonly Regex Approval = new Regex(
            @"\b(approv\w*|confirm\w*|authori[sz]\w*|allow|proceed|go\s+ahead|apply|applied|yes|ok|okay|okey|"
            + @"lulus\w*|sahkan|benarkan|teruskan|laksana\w*|jalankan|ya|setuju\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex Credential = new Regex(
            @"(password|passcode|passwd|kata\s*laluan|token|api[\s_-]*key|secret|\bpin\b|\botp\b|credential|kelayakan)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
                // The card's own words are English on every turn (owner's rule, card spec); a drafter may still
                // type a Malay answer. Only the pane note below (shown in the thread, not on the card) follows the turn.
                SaveLabel = "Save note",
                DontSaveLabel = "Don't save",
                FromLabel = "Question from the assistant",
                CredentialNote = "This question asks for a password or token. BINA never asks for those here, so there is no answer box.",
                TypedRefusalNote = ms
                    ? "Tidak dihantar. Soalan ini meminta kata laluan atau token, dan BINA tidak pernah meminta maklumat itu di sini. Tekan \"Don't answer\" pada soalan untuk teruskan."
                    : "Not sent. This question asks for a password or token, and BINA never asks for those here. Press \"Don't answer\" on the question to go on.",
                DeclineLabel = "Don't answer",
            };

            if (!card.IsProjectNote && IsCredentialLike(card.Question))
            {
                card.AsksForCredential = true;
                card.SomethingWasDropped = true;
                return card;
            }

            var options = (ask.Options ?? new List<AskOptionDto>())
                .Where(o => o != null && !string.IsNullOrWhiteSpace(o.Id))
                .Select(OptionOf)
                .ToList();
            card.Title = Clean(ask.Title);
            card.Noun = Clean(ask.Noun)?.ToLowerInvariant();
            card.Ready = (ask.Ready ?? new List<AskReadyDto>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Label))
                .Select(r => new AskReadyItem { Label = r.Label.Trim(), Detail = Clean(r.Detail) })
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

            var safe = options.Where(o => !IsApprovalLike(o.Label)).ToList();
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
                        .Select(OptionOf).ToList(),
                    Group = Clean(f.Group),
                    Note = Clean(f.Note),
                };
                var safeChoices = field.Choices.Where(c => !IsApprovalLike(c.Label)).ToList();
                if (safeChoices.Count != field.Choices.Count) { card.SomethingWasDropped = true; field.Choices = safeChoices; }
                if (field.Type == AskFieldTypes.Choice && field.Choices.Count < 2) field.Type = AskFieldTypes.Text;
                card.Fields.Add(field);
            }

            card.FreeText = card.Options.Count == 0 && card.Fields.Count == 0;
            return card;
        }

        private static AskOptionModel OptionOf(AskOptionDto o) => new AskOptionModel
        {
            Id = o.Id,
            Label = string.IsNullOrWhiteSpace(o.Label) ? o.Id : o.Label,
            Meta = Clean(o.Meta),
            Kind = AskOptionKinds.All.Contains(o.Kind ?? "") ? o.Kind : null,
        };

        private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();

        public AnswerDto AnswerForOptions(IEnumerable<string> optionIds)
        {
            var allowed = new HashSet<string>(Options.Select(o => o.Id));
            var answer = new AnswerDto();
            foreach (var id in optionIds ?? Array.Empty<string>())
                if (id != null && allowed.Contains(id)) answer.Values[id] = JsonSerializer.SerializeToElement(true);
            return answer;
        }

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

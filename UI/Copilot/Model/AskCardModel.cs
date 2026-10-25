#nullable enable
// An ask_user question as the pane shows it (lean-harness spec §7.11, §13;
// W5 Task 3). Revit-free.
//
// Ask cards come from the assistant and are styled unlike confirm cards. They
// can never stand in for approval or collect secrets:
//   - a clarify option whose label reads like approval ("Approve", "Ya,
//     teruskan", "Sahkan") is dropped; with fewer than 2 options left the
//     card falls back to one free-text answer;
//   - a field that asks for a password, token, PIN or other credential is
//     never shown;
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

        private string _yesId = "yes";
        private string _noId = "no";

        private static readonly Regex Approval = new Regex(
            @"\b(approv\w*|confirm\w*|authori[sz]\w*|allow|proceed|go\s+ahead|lulus\w*|sahkan|benarkan|teruskan)\b",
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
                SaveLabel = ms ? "Simpan nota" : "Save note",
                DontSaveLabel = ms ? "Jangan simpan" : "Don't save",
                FromLabel = ms ? "Soalan daripada pembantu" : "Question from the assistant",
            };

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

            var safe = options.Where(o => !IsApprovalLike(o.Label)).ToList();
            if (safe.Count != options.Count) card.SomethingWasDropped = true;
            card.Options = safe.Count >= 2 ? safe : new List<AskOptionModel>();
            if (safe.Count == 1) card.SomethingWasDropped = true;

            foreach (var f in ask.Fields ?? new List<AskFieldDto>())
            {
                if (f == null || string.IsNullOrWhiteSpace(f.Id)) continue;
                if (IsCredentialLike(f.Id) || IsCredentialLike(f.Label)) { card.SomethingWasDropped = true; continue; }
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
                if (field.Type == AskFieldTypes.Choice && field.Choices.Count < 2) field.Type = AskFieldTypes.Text;
                card.Fields.Add(field);
            }

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

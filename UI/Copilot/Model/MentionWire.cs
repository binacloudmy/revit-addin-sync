#nullable enable
// r17, tester 20 Nov: "check the MENTION (@) function". A picked mention reached the cloud only as words in the message; the add-in built
// a list of mentions and dropped it. The turn now carries them as structured context (TurnRequestDto.mentions): kind, the text it put in
// the message and, for a level, view or sheet, the element id the picker read, so "open @view" opens that exact view and "@level" places
// on that level. Which mentions ride along is decided by the composer's chips (MentionChips.Records after Expand reconciles them
// with the text), passed down with the message as data: the message text is never searched for them (EnvelopeEnumTests). Pure (no WPF).
using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class MentionWire
    {
        public const int Max = 20;

        /// <summary>The picked mentions as wire records (empty kinds and names dropped, at most 20, one per kind and name); null for none.</summary>
        public static List<MentionDto>? For(IEnumerable<Mention>? mentions)
        {
            if (mentions == null) return null;
            var list = mentions.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Kind) && !string.IsNullOrWhiteSpace(m.Value))
                .GroupBy(m => m.Kind + "\u0001" + m.Value.Trim(), StringComparer.Ordinal).Select(g => g.First())
                .Take(Max)
                .Select(m => new MentionDto { Kind = m.Kind.Trim(), Name = m.Value.Trim(), Id = m.Id })
                .ToList();
            return list.Count == 0 ? null : list;
        }
    }
}

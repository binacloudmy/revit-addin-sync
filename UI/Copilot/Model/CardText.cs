#nullable enable
// Small pure text rules of the result card.
namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class CardText
    {
        /// <summary>The longest headline drawn whole. The server caps its own sentence well below this; the card wraps the
        /// headline over as many lines as it needs, with no ellipsis (live R7, C3: a 130-character reply was cut after
        /// three lines, "A1 L…"). Past this a runaway text is cut at a word (the safety net), with the whole as a tooltip.</summary>
        public const int HeadlineMax = 400;

        /// <summary>The headline as the card draws it: whole up to <see cref="HeadlineMax"/>.</summary>
        public static string Headline(string? text) => Clamp(text, HeadlineMax);

        /// <summary>The text, cut at a word with "…" past <paramref name="max"/> characters.</summary>
        public static string Clamp(string? text, int max = 160)
        {
            var t = (text ?? "").Trim();
            if (t.Length <= max) return t;
            var space = t.LastIndexOf(' ', max - 1);
            return t.Substring(0, space > max / 2 ? space : max - 1).TrimEnd(',', ';', ':', ' ') + "…";
        }
    }
}

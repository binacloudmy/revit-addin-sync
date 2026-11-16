using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>A picked mention sitting in the message box as a chip: the box
    /// shows <see cref="Display"/> ("@PTh300a"); the message sent carries
    /// <see cref="Sent"/> ("Pintu Tunggal: PTh300a (door type)") in its place.</summary>
    public class MentionChip
    {
        public int Start;
        public string Display;
        public MentionItem Item;
        public int Length => Display?.Length ?? 0;
        public int End => Start + Length;
        public string Kind => Item?.Kind;
    }

    /// <summary>
    /// The chips of one message box, kept in step with its text. A chip is one
    /// unit: one Backspace (or Delete) removes it whole, an edit inside it turns
    /// it back into plain text. On send each chip's text is swapped for the form
    /// the backend matches without doubt. WPF-free so it is unit-tested; the
    /// control only forwards text edits and keys.
    /// </summary>
    public class MentionChips
    {
        private readonly List<MentionChip> _chips = new List<MentionChip>();
        public IReadOnlyList<MentionChip> All => _chips;

        public void Clear() => _chips.Clear();

        /// <summary>Put a picked item in place of the "@query" that runs from
        /// <paramref name="atIndex"/> to <paramref name="caret"/>. Returns the new
        /// text and caret; the chip is followed by one space.</summary>
        public (string text, int caret) Insert(string text, int atIndex, int caret, MentionItem item)
        {
            text = text ?? "";
            if (atIndex < 0 || atIndex > text.Length) return (text, Math.Min(Math.Max(caret, 0), text.Length));
            if (caret < atIndex || caret > text.Length) caret = text.Length;

            string display = "@" + MentionText.Chip(item);
            string before = text.Substring(0, atIndex);
            string after = text.Substring(caret);
            string pad = after.Length > 0 && after[0] == ' ' ? "" : " ";
            string next = before + display + pad + after;

            Edited(atIndex, caret - atIndex, display.Length + pad.Length);
            _chips.Add(new MentionChip { Start = atIndex, Display = display, Item = item });
            _chips.Sort((a, b) => a.Start.CompareTo(b.Start));
            return (next, atIndex + display.Length + 1);
        }

        /// <summary>The text changed at <paramref name="offset"/>: <paramref name="removed"/>
        /// characters went, <paramref name="added"/> came. Chips after the edit move;
        /// a chip the edit touches inside becomes plain text.</summary>
        public void Edited(int offset, int removed, int added)
        {
            for (int i = _chips.Count - 1; i >= 0; i--)
            {
                var c = _chips[i];
                if (offset + removed <= c.Start) c.Start += added - removed;   // before the chip
                else if (offset >= c.End) { /* after the chip */ }
                else _chips.RemoveAt(i);
            }
        }

        /// <summary>Drop chips whose text no longer reads as their chip (a text set
        /// from code, an undo). Cheap safety net run after every change.</summary>
        public void Reconcile(string text)
        {
            text = text ?? "";
            _chips.RemoveAll(c => c.Start < 0 || c.End > text.Length
                                  || string.CompareOrdinal(text, c.Start, c.Display, 0, c.Length) != 0);
        }

        /// <summary>The chip a Backspace at <paramref name="caret"/> removes: the caret
        /// is inside or at the end of it, or just after the space that follows it.</summary>
        public MentionChip BackspaceTarget(string text, int caret)
        {
            text = text ?? "";
            foreach (var c in _chips)
            {
                if (caret > c.Start && caret <= c.End) return c;
                if (caret == c.End + 1 && c.End < text.Length && text[c.End] == ' ') return c;
            }
            return null;
        }

        /// <summary>The chip a Delete at <paramref name="caret"/> removes.</summary>
        public MentionChip DeleteTarget(int caret) => _chips.FirstOrDefault(c => caret >= c.Start && caret < c.End);

        /// <summary>Remove a chip and the space after it. Returns the new text and caret.</summary>
        public (string text, int caret) Remove(string text, MentionChip chip)
        {
            text = text ?? "";
            if (chip == null || !_chips.Contains(chip)) return (text, text.Length);
            int len = chip.Length;
            if (chip.End < text.Length && text[chip.End] == ' ') len++;
            int start = chip.Start;
            _chips.Remove(chip);
            string next = text.Remove(start, Math.Min(len, text.Length - start));
            Edited(start, len, 0);
            return (next, start);
        }

        /// <summary>True when <paramref name="index"/> falls on a chip's text, so the
        /// "@" there is a chip, not a new mention to search for.</summary>
        public bool Covers(int index) => _chips.Any(c => index >= c.Start && index < c.End);

        /// <summary>
        /// The message as sent: each chip's text swapped for its sent form. A
        /// Selection chip is written from <paramref name="selectionNow"/> (the
        /// selection read at send time) when given, so it stays true to what is
        /// selected when the drafter presses Enter.
        /// </summary>
        public string Expand(string text, MentionSelection selectionNow = null)
        {
            text = text ?? "";
            Reconcile(text);
            var sb = new StringBuilder(text);
            foreach (var c in _chips.OrderByDescending(c => c.Start))
                sb.Remove(c.Start, c.Length).Insert(c.Start, SentFor(c, selectionNow));
            return sb.ToString();
        }

        /// <summary>The chips as (kind, sent text) pairs, for the pane's Mention list.</summary>
        public List<(string kind, string sent)> Mentions(MentionSelection selectionNow = null) =>
            _chips.Select(c => (c.Kind, SentFor(c, selectionNow))).ToList();

        private static string SentFor(MentionChip c, MentionSelection selectionNow) =>
            c.Kind == "selection" && selectionNow != null
                ? MentionText.SelectionSent(selectionNow)
                : MentionText.Sent(c.Item);
    }
}

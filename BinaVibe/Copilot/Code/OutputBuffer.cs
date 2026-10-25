#nullable enable
// What Print(obj) writes (spec §7.8: print output and collections are capped
// at 16 KB). Revit-free: Revit values are formatted by a hook the Revit-bound
// runner passes in (element names and ids, XYZ), everything else here.
//
// One line per Print; a collection prints one item per line. Once the cap is
// reached the text ends with a note telling the model to narrow its query,
// further output is dropped, and enumeration stops (a million-item list is
// never walked to the end).

using System;
using System.Collections;
using System.Globalization;
using System.Text;

namespace BinaVibe.Copilot.Code
{
    public sealed class OutputBuffer
    {
        public const string NarrowNote = "[output cut at 16 KB: narrow your query, for example filter, group or count instead of listing everything]";

        private const int MaxItemsPerCollection = 10_000;
        private const int MaxNestedItems = 20;

        private readonly StringBuilder _text = new StringBuilder();
        private readonly int _maxBytes;
        private readonly int _noteBytes;
        private readonly Func<object, string?>? _special;
        private int _bytes;

        public OutputBuffer(int? maxBytes = null, Func<object, string?>? special = null)
        {
            _maxBytes = maxBytes ?? CodePolicySet.Default.Budget.MaxOutputBytes;
            _noteBytes = Encoding.UTF8.GetByteCount("\n" + NarrowNote);
            _special = special;
        }

        public string Text => _text.ToString();
        public bool Truncated { get; private set; }
        public int Lines { get; private set; }
        public int Bytes => _bytes;

        /// <summary>The snippet's Print(obj).</summary>
        public void Print(object? value)
        {
            if (Truncated) return;
            if (value is IEnumerable items && !(value is string) && _special?.Invoke(value) == null)
            {
                var n = 0;
                foreach (var item in items)
                {
                    if (Truncated) return;
                    if (++n > MaxItemsPerCollection)
                    {
                        AppendLine("[collection cut at " + MaxItemsPerCollection.ToString("N0", CultureInfo.InvariantCulture) + " items]");
                        return;
                    }
                    AppendLine(Format(item, nested: true));
                }
                if (n == 0) AppendLine("(empty)");
                return;
            }
            AppendLine(Format(value, nested: false));
        }

        private string Format(object? value, bool nested)
        {
            switch (value)
            {
                case null: return "null";
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
            }
            var special = _special?.Invoke(value);
            if (special != null) return special;
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            if (nested && value is IEnumerable inner)
            {
                var sb = new StringBuilder("[");
                var n = 0;
                foreach (var item in inner)
                {
                    if (n > 0) sb.Append(", ");
                    if (++n > MaxNestedItems) { sb.Append('…'); break; }
                    sb.Append(Format(item, nested: false));
                    if (sb.Length > _maxBytes) break;
                }
                return sb.Append(']').ToString();
            }
            return value.ToString() ?? "";
        }

        private void AppendLine(string line)
        {
            var piece = Lines == 0 ? line : "\n" + line;
            var bytes = Encoding.UTF8.GetByteCount(piece);
            if (_bytes + bytes + _noteBytes <= _maxBytes)
            {
                _text.Append(piece);
                _bytes += bytes;
                Lines++;
                return;
            }

            // Fit what we can of this line, then the note.
            var room = _maxBytes - _noteBytes - _bytes;
            if (room > 0)
            {
                var cut = Cut(piece, room);
                _text.Append(cut);
                _bytes += Encoding.UTF8.GetByteCount(cut);
                if (cut.Length > 0) Lines++;
            }
            var note = (_bytes == 0 ? "" : "\n") + NarrowNote;
            _text.Append(note);
            _bytes += Encoding.UTF8.GetByteCount(note);
            Truncated = true;
        }

        /// <summary>The longest prefix of s that fits in maxBytes of UTF-8, never splitting a surrogate pair.</summary>
        private static string Cut(string s, int maxBytes)
        {
            var bytes = 0;
            var i = 0;
            while (i < s.Length)
            {
                var len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
                var b = Encoding.UTF8.GetByteCount(s.ToCharArray(i, len));
                if (bytes + b > maxBytes) break;
                bytes += b;
                i += len;
            }
            return s.Substring(0, i);
        }
    }
}

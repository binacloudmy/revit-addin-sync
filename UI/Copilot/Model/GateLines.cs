#nullable enable
// The question of the gate card ("Before I write: Comments = WIDE-TEST on 3741 elements.\n• Walls: 2127\n• Curtain Panels: 398\nand 9 more")
// as a headline and one row per line. Markdown would run the lines into one paragraph and drop the last one. Pure.
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class GateRow
    {
        public string Text = "";
        /// <summary>The line started with a bullet marker (• - *): drawn with a bullet and hanging wrap.</summary>
        public bool Bullet;
    }

    public static class GateLines
    {
        public const int MaxRows = 12;

        private static readonly char[] Markers = { '•', '-', '*', '·', '–' };

        /// <summary>True when the text has a line break and at least one bullet line: the gate's shape.</summary>
        public static bool Applies(string? question)
        {
            if (string.IsNullOrWhiteSpace(question)) return false;
            var lines = Lines(question!);
            return lines.Count > 1 && lines.Skip(1).Any(l => IsBullet(l));
        }

        /// <summary>The first line, and every following line as a row (empty lines dropped, bullet markers removed, at most
        /// <see cref="MaxRows"/>).</summary>
        public static (string Head, List<GateRow> Rows) Split(string? question)
        {
            var lines = Lines(question ?? "");
            if (lines.Count == 0) return ("", new List<GateRow>());
            var rows = new List<GateRow>();
            foreach (var l in lines.Skip(1).Take(MaxRows))
            {
                var bullet = IsBullet(l);
                rows.Add(new GateRow { Bullet = bullet, Text = bullet ? l.Substring(1).Trim() : l });
            }
            return (ProseSplit.Plain(lines[0]), rows);
        }

        private static List<string> Lines(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        private static bool IsBullet(string line) =>
            line.Length > 1 && Markers.Contains(line[0]) && char.IsWhiteSpace(line[1]);
    }
}

// BuildTypePlan — the Build step's TYPES section, worked out without Revit.
//
// The pane reads the open model's wall, door and window types once (Cad2BimBuilder.ReadTypes,
// on the Revit thread) into a TypeCatalog. From that and what the next build will create,
// this file makes one row per wall width and per door/window size, each with the type the
// builder would choose by itself (BuildSizing.Pick: same rounding, same tolerance, same fit
// to the host wall piece) selected,
// and a "New: CAD …" entry when nothing in the model is that size. What the drafter leaves
// or picks goes back to the builder as BuildRequest.TypeChoices (Choices below).
//
// Millimetres throughout. Revit-free and WPF-free, so the Tests project links it.

using System;
using System.Collections.Generic;
using System.Linq;

using Cad2Bim;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>One type the model has, as the pane sees it. Id is the ElementId value.
    /// HeightMm is NaN for wall types; Resizable = width and height can be written after a
    /// Duplicate (always true for basic wall types).</summary>
    public sealed class TypeOption
    {
        public string Name { get; set; }
        public long Id { get; set; }
        public double WidthMm { get; set; } = double.NaN;
        public double HeightMm { get; set; } = double.NaN;
        public bool Resizable { get; set; }
    }

    /// <summary>The open model's types, in the order the builder lists them.</summary>
    public sealed class TypeCatalog
    {
        public static readonly TypeCatalog None = new TypeCatalog { Known = false };

        /// <summary>False when no project was open to read (or the read failed).</summary>
        public bool Known { get; set; } = true;

        public List<TypeOption> Walls { get; set; } = new List<TypeOption>();
        public List<TypeOption> Doors { get; set; } = new List<TypeOption>();
        public List<TypeOption> Windows { get; set; } = new List<TypeOption>();

        public List<TypeOption> Of(TypeSlot slot) =>
            slot == TypeSlot.Wall ? Walls : slot == TypeSlot.Door ? Doors : Windows;
    }

    /// <summary>One entry of a TYPES dropdown.</summary>
    public sealed class TypeRowOption
    {
        /// <summary>The existing type's id, or 0 for "New: CAD …" / "none in the model".</summary>
        public long TypeId { get; set; }
        public string Label { get; set; }
        /// <summary>The builder makes (or reuses) a "CAD …" type of the row's size.</summary>
        public bool IsNew { get; set; }
        /// <summary>The model has no type of this kind at all: walls stop the build, doors and
        /// windows are skipped ("no door family in the model").</summary>
        public bool IsNone { get; set; }

        /// <summary>What the dropdown shows first: a door/window type's own name without its
        /// family ("0915 x 2134mm"), else the whole label (wall types, "New: …", "none").</summary>
        public string Name => IsNew || IsNone ? Label : BuildTypePlan.SplitName(Label).Name;
        /// <summary>The family of a door/window type ("M_Door-Exterior-Single-Two_Lite"),
        /// shown faint after the name; null otherwise.</summary>
        public string Family => IsNew || IsNone ? null : BuildTypePlan.SplitName(Label).Family;

        public override string ToString() => Label;
    }

    /// <summary>One TYPES row: a wall width or a door/window size, how many the build has of
    /// it, and what can be chosen for it.</summary>
    public sealed class TypeRowPlan
    {
        public TypeSlot Slot { get; set; }
        public double WidthMm { get; set; }
        /// <summary>NaN for walls.</summary>
        public double HeightMm { get; set; } = double.NaN;
        public int Count { get; set; }
        /// <summary>"Wall 125 mm", "Door 910 × 2100", "Window 1220 × 1200".</summary>
        public string Label { get; set; }
        public List<TypeRowOption> Options { get; set; } = new List<TypeRowOption>();
        /// <summary>What the builder would use if the drafter changes nothing.</summary>
        public int DefaultIndex { get; set; }

        public TypeRowOption Default => Options.Count == 0 ? null : Options[DefaultIndex];

        /// <summary>Same row across refreshes (slot + rounded size).</summary>
        public string Key => KeyOf(Slot, WidthMm, HeightMm);

        public static string KeyOf(TypeSlot slot, double widthMm, double heightMm) =>
            slot + ":" + Math.Round(widthMm) + (slot == TypeSlot.Wall ? "" : "x" + Math.Round(heightMm));
    }

    public static class BuildTypePlan
    {
        /// <summary>Wall rows shown before "show all" (the most used widths).</summary>
        public const int WallRowsShown = 6;

        /// <summary>
        /// The TYPES rows for a build: one per wall width (BuildSizing.WallWidthMm of each
        /// thickness), then one per door size and window size (BuildSizing.OpeningSize).
        /// Openings the builder would skip as too narrow get no row. Walls are ordered by
        /// width; doors and windows by width.
        /// hostOf: each wall's drawn host line (BuildSizing.HostLines), so an opening's width
        /// is fitted to its host as the builder fits it. Null, or a wall not in it: no fitting.
        /// </summary>
        public static List<TypeRowPlan> Rows(
            IEnumerable<double> wallThicknessesMm,
            IEnumerable<Opening> openings,
            double doorHeightMm, double windowHeightMm,
            TypeCatalog catalog,
            IReadOnlyDictionary<Wall, Segment> hostOf = null)
        {
            catalog = catalog ?? TypeCatalog.None;
            var rows = new List<TypeRowPlan>();

            IEnumerable<IGrouping<double, double>> widths = (wallThicknessesMm ?? Enumerable.Empty<double>())
                .Select(BuildSizing.WallWidthMm)
                .Where(w => w > 0)
                .GroupBy(w => w)
                .OrderBy(g => g.Key);
            foreach (IGrouping<double, double> width in widths)
                rows.Add(Row(TypeSlot.Wall, width.Key, double.NaN, width.Count(), catalog.Walls));

            var sizes = new List<(TypeSlot Slot, double W, double H)>();
            foreach (Opening opening in openings ?? Enumerable.Empty<Opening>())
            {
                if (opening == null) continue;
                double drawn = BuildSizing.OpeningWidthMm(opening);
                if (drawn < BuildSizing.MinOpeningWidthMm) continue;
                OpeningKind kind = BuildSizing.KindOf(opening);
                Segment host = null;
                if (hostOf != null && opening.Wall != null) hostOf.TryGetValue(opening.Wall, out host);
                (double w, double h) = BuildSizing.OpeningSize(opening, host, doorHeightMm, windowHeightMm);
                sizes.Add((kind == OpeningKind.Door ? TypeSlot.Door : TypeSlot.Window, w, h));
            }

            foreach (var size in sizes.GroupBy(s => s).OrderBy(g => g.Key.Slot).ThenBy(g => g.Key.W).ThenBy(g => g.Key.H))
                rows.Add(Row(size.Key.Slot, size.Key.W, size.Key.H, size.Count(), catalog.Of(size.Key.Slot)));

            return rows;
        }

        /// <summary>
        /// One row and its choices. Mirrors the builder's own choice exactly:
        /// <list type="bullet">
        /// <item>a type already that size → it, selected;</item>
        /// <item>else a resizable type to copy → "New: CAD …" first and selected, then every existing type;</item>
        /// <item>else (nothing resizable) → the closest type, selected, used at its own size;</item>
        /// <item>no type of the kind at all → a single "none in the model" entry.</item>
        /// </list>
        /// </summary>
        public static TypeRowPlan Row(TypeSlot slot, double widthMm, double heightMm, int count, IList<TypeOption> types)
        {
            var row = new TypeRowPlan
            {
                Slot = slot,
                WidthMm = widthMm,
                HeightMm = slot == TypeSlot.Wall ? double.NaN : heightMm,
                Count = count,
                Label = LabelOf(slot, widthMm, heightMm),
            };

            types = types ?? new List<TypeOption>();
            if (types.Count == 0)
            {
                row.Options.Add(new TypeRowOption
                {
                    IsNone = true,
                    Label = slot == TypeSlot.Wall ? "No basic wall type in the model"
                          : slot == TypeSlot.Door ? "No door family (skipped)" : "No window family (skipped)",
                });
                return row;
            }

            bool wall = slot == TypeSlot.Wall;
            var candidates = types
                .Select(t => new SizeCandidate(t.Name, t.WidthMm, wall ? double.NaN : t.HeightMm, wall || t.Resizable))
                .ToList();
            SizeChoice choice = BuildSizing.Pick(
                candidates, widthMm, wall ? double.NaN : heightMm, wall ? 0.5 : BuildSizing.OpeningStepMm / 2.0,
                neverWider: !wall);

            bool makeNew = choice.Found && !choice.Exact && candidates[choice.Index].Resizable;
            if (makeNew)
            {
                row.Options.Add(new TypeRowOption
                {
                    IsNew = true,
                    Label = "New: " + (wall ? BuildSizing.WallTypeName(widthMm) : BuildSizing.OpeningTypeName(widthMm, heightMm)),
                });
            }

            int offset = row.Options.Count;
            foreach (TypeOption type in types)
                row.Options.Add(new TypeRowOption { TypeId = type.Id, Label = type.Name });

            row.DefaultIndex = makeNew ? 0 : offset + Math.Max(0, choice.Index);
            return row;
        }

        /// <summary>
        /// The drafter's selection per row → BuildRequest.TypeChoices. Only existing types are
        /// sent; "New: CAD …" and "none" are left to the builder, which makes the same choice
        /// the row showed. A row with no selection counts as its default.
        /// </summary>
        public static List<TypeChoice> Choices(IEnumerable<(TypeRowPlan Row, TypeRowOption Selected)> picks)
        {
            var choices = new List<TypeChoice>();
            if (picks == null) return choices;
            foreach (var (row, selected) in picks)
            {
                if (row == null) continue;
                TypeRowOption option = selected ?? row.Default;
                if (option == null || option.IsNew || option.IsNone || option.TypeId <= 0) continue;
                choices.Add(new TypeChoice
                {
                    Slot = row.Slot,
                    WidthMm = row.WidthMm,
                    HeightMm = row.HeightMm,
                    TypeId = option.TypeId,
                });
            }
            return choices;
        }

        /// <summary>"Wall 125 mm", "Door 910 × 2100", "Window 1220 × 1200": the row's kind and
        /// full size, so two openings of the same width stay apart.</summary>
        public static string LabelOf(TypeSlot slot, double widthMm, double heightMm = double.NaN)
        {
            string w = Mm(widthMm);
            switch (slot)
            {
                case TypeSlot.Wall: return "Wall " + w + " mm";
                case TypeSlot.Door: return "Door " + w + HeightTail(heightMm);
                default: return "Window " + w + HeightTail(heightMm);
            }
        }

        private static string HeightTail(double heightMm) =>
            double.IsNaN(heightMm) || heightMm <= 0 ? "" : " × " + Mm(heightMm);

        private static string Mm(double mm) =>
            Math.Round(mm).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// "Family:Type" (how ReadTypes names door/window symbols) → the type's own name and
        /// its family, trimmed. No colon, or nothing on one side of it → all Name, no Family.
        /// Revit family names cannot contain ':', so the first colon is the split.
        /// </summary>
        public static (string Name, string Family) SplitName(string label)
        {
            if (string.IsNullOrEmpty(label)) return (label ?? "", null);
            int colon = label.IndexOf(':');
            if (colon < 0) return (label, null);
            string family = label.Substring(0, colon).Trim();
            string name = label.Substring(colon + 1).Trim();
            if (family.Length == 0 || name.Length == 0) return (label, null);
            return (name, family);
        }

        /// <summary>"New wall types made: CAD Wall 125 mm, CAD Wall 230 mm and 3 more".</summary>
        public static string NamesLine(string lead, IList<string> names, int shown = 4)
        {
            if (names == null || names.Count == 0) return null;
            List<string> distinct = names.Distinct(StringComparer.Ordinal).ToList();
            string list = string.Join(", ", distinct.Take(shown));
            if (distinct.Count > shown) list += " and " + (distinct.Count - shown) + " more";
            return lead + list;
        }
    }
}

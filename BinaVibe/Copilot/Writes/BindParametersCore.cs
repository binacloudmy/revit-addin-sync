#nullable enable
// bind_parameters (AI-530, WP-8): the Revit-free half. Arguments, the spec's category map, the plan and the
// per-parameter status text. The Revit calls (open the shared parameter file, read and change the project's
// ParameterBindings) are in Tools/BindParametersTool.cs.
//
// Rules (a refusal is invalid_args or not_allowed; nothing has run when one is raised):
//   - only a JKR shared parameter name (contains _jkr_s[t|i]<letter>) is accepted; a Revit built-in is never bound
//   - only a definition that already exists in the loaded JKR shared parameter file is bound: this tool never creates one
//   - the categories come from the Buku Parameter table (SpecPbParameters): the parameter's own rows, mapped to Revit
//     categories here. "categories" can only narrow that set, never widen it
//   - an existing binding keeps its Instance/Type kind: a spec/binding kind mismatch is reported, never flipped
//   - a parameter that already covers every wanted category is "already bound" and is not touched

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public sealed class BindParametersArgs
    {
        public const int MaxParameters = 40;
        public const int MaxCategories = 20;

        public IReadOnlyList<string> Parameters { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<string> Categories { get; private set; } = Array.Empty<string>();
        public bool DryRun { get; private set; }

        /// <summary>A JKR shared parameter name: letters, digits and underscores, ending _jkr_s[t|i]<value letter: t x y m c l i a n>.</summary>
        private static readonly Regex JkrName = new Regex(@"^[A-Za-z0-9_]+_jkr_s[ti][txymclian]$", RegexOptions.CultureInvariant);

        public static bool IsJkrName(string name) => JkrName.IsMatch(name ?? "");

        /// <summary>Parses and validates; throws <see cref="WriteRefusal"/> (invalid_args).</summary>
        public static BindParametersArgs Parse(JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object)
                throw Invalid("bind_parameters arguments must be a JSON object with parameters.");
            var parameters = new List<string>();
            var categories = new List<string>();
            var dry = false;
            var seenParameters = false;
            foreach (var p in args.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "parameters":
                        seenParameters = true;
                        ReadNames(p.Value, parameters, "parameters", MaxParameters);
                        break;
                    case "categories":
                        if (p.Value.ValueKind != JsonValueKind.Null) ReadNames(p.Value, categories, "categories", MaxCategories);
                        break;
                    case "dry_run":
                        if (p.Value.ValueKind == JsonValueKind.Null) break;
                        if (p.Value.ValueKind != JsonValueKind.True && p.Value.ValueKind != JsonValueKind.False)
                            throw Invalid("dry_run must be true or false.");
                        dry = p.Value.GetBoolean();
                        break;
                    default:
                        throw Invalid("Unknown argument '" + p.Name + "'; bind_parameters takes parameters, categories and dry_run.");
                }
            }
            if (!seenParameters || parameters.Count == 0)
                throw Invalid("Give parameters: the JKR shared parameter names to bind (for example Kod_Jenis_jkr_stt).");
            var bad = parameters.Where(n => !IsJkrName(n)).ToList();
            if (bad.Count > 0)
                throw Invalid("Only JKR shared parameters can be bound (a name ending _jkr_st? or _jkr_si?). Not accepted: " + string.Join(", ", bad.Take(5)) + ". Nothing was changed.");
            return new BindParametersArgs { Parameters = parameters, Categories = categories, DryRun = dry };
        }

        private static void ReadNames(JsonElement value, List<string> into, string field, int max)
        {
            if (value.ValueKind != JsonValueKind.Array)
                throw Invalid(field + " must be a list of names.");
            foreach (var item in value.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                    throw Invalid(field + " holds a blank or non-text entry.");
                if (!into.Contains(text!.Trim(), StringComparer.OrdinalIgnoreCase)) into.Add(text.Trim());
            }
            if (into.Count > max)
                throw Invalid(field + " holds " + into.Count.ToString(CultureInfo.InvariantCulture) + " names; at most " + max.ToString(CultureInfo.InvariantCulture) + " per call.");
        }

        private static WriteRefusal Invalid(string message) => new WriteRefusal(ErrorCodes.InvalidArgs, message);
    }

    /// <summary>One Revit category a book category maps to: the BuiltInCategory name and the name Revit shows.</summary>
    public sealed class BindCategory
    {
        public string Built { get; }
        public string Display { get; }
        public BindCategory(string built, string display) { Built = built; Display = display; }
    }

    /// <summary>The Buku Parameter AR book categories (SpecPbData.ArCategories) as Revit categories.</summary>
    public static class BindCategoryMap
    {
        private static readonly Dictionary<string, BindCategory[]> Map = new Dictionary<string, BindCategory[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Area"] = new[] { new BindCategory("OST_Areas", "Areas") },
            ["Room"] = new[] { new BindCategory("OST_Rooms", "Rooms") },
            ["Casework"] = new[] { new BindCategory("OST_Casework", "Casework") },
            ["Ceiling"] = new[] { new BindCategory("OST_Ceilings", "Ceilings") },
            ["Column"] = new[] { new BindCategory("OST_Columns", "Columns"), new BindCategory("OST_StructuralColumns", "Structural Columns") },
            ["Curtain System"] = new[] { new BindCategory("OST_CurtainWallPanels", "Curtain Panels"), new BindCategory("OST_CurtaSystem", "Curtain Systems") },
            ["Door"] = new[] { new BindCategory("OST_Doors", "Doors") },
            ["Floor"] = new[] { new BindCategory("OST_Floors", "Floors") },
            ["Furniture"] = new[] { new BindCategory("OST_Furniture", "Furniture") },
            ["Furniture System"] = new[] { new BindCategory("OST_FurnitureSystems", "Furniture Systems") },
            ["Generic Model"] = new[] { new BindCategory("OST_GenericModel", "Generic Models") },
            ["Planting"] = new[] { new BindCategory("OST_Planting", "Planting") },
            ["Plumbing Fixture"] = new[] { new BindCategory("OST_PlumbingFixtures", "Plumbing Fixtures") },
            ["Ramp"] = new[] { new BindCategory("OST_Ramps", "Ramps") },
            ["Railing"] = new[] { new BindCategory("OST_StairsRailing", "Railings") },
            ["Roof"] = new[] { new BindCategory("OST_Roofs", "Roofs") },
            ["Specialty Equipment"] = new[] { new BindCategory("OST_SpecialityEquipment", "Specialty Equipment") },
            ["Stair"] = new[] { new BindCategory("OST_Stairs", "Stairs") },
            ["Wall"] = new[] { new BindCategory("OST_Walls", "Walls") },
            ["Window"] = new[] { new BindCategory("OST_Windows", "Windows") },
        };

        /// <summary>The Revit categories of a book category; empty for one this map does not know (reported, never guessed).</summary>
        public static IReadOnlyList<BindCategory> For(string bookCategory) =>
            Map.TryGetValue((bookCategory ?? "").Trim(), out var cats) ? cats : Array.Empty<BindCategory>();

        public static IEnumerable<string> BookCategories => Map.Keys;
    }

    /// <summary>One Buku Parameter row, reduced to what the binder needs.</summary>
    public sealed class BindSpecRow
    {
        public string Name { get; }
        public string BookCategory { get; }
        public bool IsInstance { get; }
        public BindSpecRow(string name, string bookCategory, bool isInstance) { Name = name; BookCategory = bookCategory; IsInstance = isInstance; }
    }

    /// <summary>What the model says about one parameter right now (read by the Revit tool).</summary>
    public sealed class BindingState
    {
        /// <summary>The definition exists in the loaded JKR shared parameter file.</summary>
        public bool InFile { get; set; }
        /// <summary>The project already binds it (to any category).</summary>
        public bool Bound { get; set; }
        /// <summary>True: instance binding; false: type binding. Meaningful when Bound.</summary>
        public bool BoundAsInstance { get; set; }
        /// <summary>BuiltInCategory names it is bound to now.</summary>
        public IReadOnlyCollection<string> BoundCategories { get; set; } = Array.Empty<string>();
        /// <summary>The project already has a DIFFERENT parameter (another GUID, or a project parameter) with this name: Revit would refuse a second one.</summary>
        public bool NameTaken { get; set; }
    }

    public enum BindStatus
    {
        /// <summary>Not bound at all: Insert.</summary>
        Bind,
        /// <summary>Bound to some of the wanted categories: ReInsert with a new binding that keeps the old ones and adds the rest.</summary>
        Extend,
        AlreadyBound,
        NotInFile,
        NotInSpec,
        KindConflict,
        NameConflict,
        NoWantedCategory,
    }

    public sealed class BindItem
    {
        public string Name { get; set; } = "";
        public BindStatus Status { get; set; }
        public string Reason { get; set; } = "";
        public bool Instance { get; set; }
        /// <summary>BuiltInCategory names the spec wants (after "categories" narrowing), display names in <see cref="WantedDisplay"/>.</summary>
        public List<string> Wanted { get; } = new List<string>();
        public List<string> WantedDisplay { get; } = new List<string>();
        /// <summary>Wanted and not bound yet: what a bind or extend adds.</summary>
        public List<string> ToAdd { get; } = new List<string>();
        public List<string> AlreadyBoundTo { get; } = new List<string>();

        public bool Changes => Status == BindStatus.Bind || Status == BindStatus.Extend;
    }

    public static class BindPlanner
    {
        /// <summary>The plan, one item per requested parameter, in request order. Pure: no Revit.</summary>
        public static List<BindItem> Plan(BindParametersArgs args, IReadOnlyList<BindSpecRow> spec, Func<string, BindingState> stateOf)
        {
            var items = new List<BindItem>();
            var only = NarrowTo(args.Categories);
            foreach (var name in args.Parameters)
            {
                var item = new BindItem { Name = name };
                items.Add(item);
                var rows = spec.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                var state = stateOf(name);
                if (rows.Count == 0)
                {
                    item.Status = BindStatus.NotInSpec;
                    item.Reason = "The JKR spec (Buku Parameter AR tables) does not list '" + name + "', so there is no category to bind it to.";
                    continue;
                }
                item.Instance = rows[0].IsInstance;
                var allKnown = new List<BindCategory>();
                var unknownBook = new List<string>();
                foreach (var book in rows.Select(r => r.BookCategory).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var cats = BindCategoryMap.For(book);
                    if (cats.Count == 0) unknownBook.Add(book);
                    foreach (var c in cats) if (allKnown.All(x => x.Built != c.Built)) allKnown.Add(c);
                }
                var wanted = only == null ? allKnown : allKnown.Where(c => only(c)).ToList();
                if (wanted.Count == 0)
                {
                    item.Status = BindStatus.NoWantedCategory;
                    item.Reason = args.Categories.Count > 0
                        ? "The spec lists '" + name + "' for " + Join(allKnown.Select(c => c.Display)) + ", none of which is in categories; nothing to bind."
                        : "No Revit category is known for the spec's book categories (" + Join(unknownBook) + "); nothing to bind.";
                    continue;
                }
                foreach (var c in wanted) { item.Wanted.Add(c.Built); item.WantedDisplay.Add(c.Display); }
                if (!state.InFile)
                {
                    item.Status = BindStatus.NotInFile;
                    item.Reason = "'" + name + "' is not defined in the loaded shared parameter file. This tool never creates a definition: load the official JKR shared parameter file (Manage > Shared Parameters).";
                    continue;
                }
                if (state.NameTaken)
                {
                    item.Status = BindStatus.NameConflict;
                    item.Reason = "The project already has a different parameter named '" + name + "' (not the one in the JKR file), so a second one cannot be bound. Nothing was changed; remove or rename the old one first.";
                    continue;
                }
                if (state.Bound && state.BoundAsInstance != item.Instance)
                {
                    item.Status = BindStatus.KindConflict;
                    item.Reason = "'" + name + "' is already bound as a " + (state.BoundAsInstance ? "instance" : "type") + " parameter but the spec lists it as " + (item.Instance ? "instance" : "type") +
                                  ". Changing that would change what every bound category holds, so it was left alone.";
                    continue;
                }
                var have = new HashSet<string>(state.BoundCategories ?? Array.Empty<string>(), StringComparer.Ordinal);
                for (var i = 0; i < wanted.Count; i++)
                {
                    if (have.Contains(wanted[i].Built)) item.AlreadyBoundTo.Add(wanted[i].Display);
                    else item.ToAdd.Add(wanted[i].Built);
                }
                if (item.ToAdd.Count == 0)
                {
                    item.Status = BindStatus.AlreadyBound;
                    item.Reason = "Already bound to " + Join(item.AlreadyBoundTo) + ".";
                }
                else
                {
                    item.Status = state.Bound ? BindStatus.Extend : BindStatus.Bind;
                    item.Reason = (state.Bound ? "Extend the binding" : "Bind") + " as " + (item.Instance ? "instance" : "type") + " to " + Join(DisplayOf(wanted, item.ToAdd)) + ".";
                }
            }
            return items;
        }

        private static IEnumerable<string> DisplayOf(IEnumerable<BindCategory> wanted, IEnumerable<string> built) =>
            built.Select(b => wanted.First(w => w.Built == b).Display);

        /// <summary>A category matches by BuiltInCategory name, Revit display name or book category name, ignoring case.</summary>
        private static Func<BindCategory, bool>? NarrowTo(IReadOnlyList<string> categories)
        {
            if (categories.Count == 0) return null;
            var set = new HashSet<string>(categories.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
            return c => set.Contains(c.Built) || set.Contains(c.Display) ||
                        BindCategoryMap.BookCategories.Any(b => set.Contains(b) && BindCategoryMap.For(b).Any(x => x.Built == c.Built));
        }

        private static string Join(IEnumerable<string> names) => string.Join(", ", names);
    }

    /// <summary>Text for the envelope, kept here so the tests can read it.</summary>
    public static class BindParametersText
    {
        public static string StatusWord(BindStatus s) => s switch
        {
            BindStatus.Bind => "will bind",
            BindStatus.Extend => "will extend",
            BindStatus.AlreadyBound => "already bound",
            BindStatus.NotInFile => "not in the JKR file",
            BindStatus.NotInSpec => "not in the spec",
            BindStatus.KindConflict => "kind conflict",
            BindStatus.NameConflict => "name clash",
            BindStatus.NoWantedCategory => "no category",
            _ => s.ToString(),
        };

        /// <summary>The official file is the one the drafter loaded, never the stub this add-in provisions for its older auto-fix (it holds no JKR definitions).</summary>
        public static bool IsProvisionedStub(string? header) =>
            header != null && header.IndexOf("Provisioned by BINA JKR auto-fix", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

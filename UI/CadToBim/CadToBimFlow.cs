// CadToBimFlow — the decisions behind the four-step pane (AI-344), kept free of WPF and
// Revit so the Tests project links this file and checks them directly:
//   - layer roles: the list the dropdown offers, the first guess and the reason shown for it,
//     and the "need a look" flag;
//   - step navigation: which step is open, which can be opened, which are ticked;
//   - the "will create N new levels" prediction shown before Build.

using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.CadToBim;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>What a layer is for. Saved by NAME (LayerRoles.Key), so adding a role never
    /// shifts a saved choice. Ignore is shown as "Skip".</summary>
    public enum LayerRole { Other, Wall, Door, Window, Ignore }

    /// <summary>One entry of the role dropdown.</summary>
    public sealed class RoleChoice
    {
        public RoleChoice(LayerRole role, string label)
        {
            Role = role;
            Label = label;
        }

        public LayerRole Role { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    public static class LayerRoles
    {
        /// <summary>Dropdown order. To offer a new role, add it to LayerRole and here
        /// (and give it a guess in LayerRoleRules.Guess if it has name hints).</summary>
        public static readonly IReadOnlyList<RoleChoice> Choices = new[]
        {
            new RoleChoice(LayerRole.Wall, "Wall"),
            new RoleChoice(LayerRole.Door, "Door"),
            new RoleChoice(LayerRole.Window, "Window"),
            new RoleChoice(LayerRole.Other, "Other"),
            new RoleChoice(LayerRole.Ignore, "Skip"),
        };

        public static RoleChoice ChoiceFor(LayerRole role) =>
            Choices.FirstOrDefault(c => c.Role == role) ?? Choices.First(c => c.Role == LayerRole.Other);

        public static string Label(LayerRole role) => ChoiceFor(role).Label;

        /// <summary>The name a role is saved under in config.json.</summary>
        public static string Key(LayerRole role) => role.ToString();

        /// <summary>Reads a saved role: the enum name or the label ("Skip"). Unknown text
        /// (a role a newer build saved) is ignored rather than guessed.</summary>
        public static bool TryParse(string text, out LayerRole role)
        {
            role = LayerRole.Other;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (RoleChoice choice in Choices)
            {
                if (string.Equals(choice.Label, text, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Key(choice.Role), text, StringComparison.OrdinalIgnoreCase))
                {
                    role = choice.Role;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>A layer's role and the one-line reason shown under it.</summary>
    public readonly struct RoleGuess
    {
        public RoleGuess(LayerRole role, string reason, bool byUser)
        {
            Role = role;
            Reason = reason;
            ByUser = byUser;
        }

        public LayerRole Role { get; }
        public string Reason { get; }
        public bool ByUser { get; }
    }

    public static class LayerRoleRules
    {
        /// <summary>An Other layer with at least this many entities is flagged: that much
        /// linework is usually walls or openings on a layer with no telling name ("0").</summary>
        public const int ManyEntities = 100;

        public const string SetByYou = "set by you";
        public const string NoHint = "no name hint matched";

        /// <summary>
        /// The drafter's choice when there is one, otherwise the first guess from the settings
        /// hints: wall words, then window words, then the remaining opening words (door, pintu),
        /// then the exclusion globs. Same order the old chip used, with Opening split in two.
        /// </summary>
        public static RoleGuess Guess(string layer, CadToBimSettings settings, IReadOnlyDictionary<string, LayerRole> overrides)
        {
            if (layer == null) return new RoleGuess(LayerRole.Other, NoHint, false);

            if (overrides != null && overrides.TryGetValue(layer, out LayerRole forced))
                return new RoleGuess(forced, SetByYou, true);

            if (settings == null) return new RoleGuess(LayerRole.Other, NoHint, false);

            string word = FirstHint(layer, settings.WallLayerHints);
            if (word != null) return new RoleGuess(LayerRole.Wall, "name has \"" + word + "\"", false);

            // "glass" in fibreglass or glass wool is not glazing.
            word = FirstHint(CadToBimSettings.WithoutNotGlazing(layer), settings.WindowLayerHints);
            if (word != null) return new RoleGuess(LayerRole.Window, "name has \"" + word + "\"", false);

            word = FirstHint(layer, settings.OpeningLayerHints);
            if (word != null) return new RoleGuess(LayerRole.Door, "name has \"" + word + "\"", false);

            // Same matching as the read filter (CadToBimSettings.ToLayerFilter): FURN* also
            // excludes A-FURN, A_FURN and A-FURN-CHAIR.
            string glob = settings.ExcludedBy(layer);
            if (glob != null) return new RoleGuess(LayerRole.Ignore, "excluded by " + glob, false);

            return new RoleGuess(LayerRole.Other, NoHint, false);
        }

        /// <summary>
        /// Why a layer needs a look, or null. Two cases: a big layer left as Other (unless the
        /// drafter chose Other), and a Wall layer that gave no walls. wallFaces = how many of the
        /// layer's lines ended up as a wall face; null = not known (no flag).
        /// </summary>
        public static string NeedsALook(LayerRole role, bool byUser, int count, int? wallFaces)
        {
            if (role == LayerRole.Other && !byUser && count >= ManyEntities)
                return count.ToString("N0") + " entities, " + NoHint;
            if (role == LayerRole.Wall && wallFaces.HasValue && wallFaces.Value == 0)
                return "wall layer, but no walls found";
            return null;
        }

        private static string FirstHint(string layer, IEnumerable<string> words)
        {
            if (words == null) return null;
            foreach (string word in words)
            {
                if (!string.IsNullOrEmpty(word) && layer.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                    return word;
            }
            return null;
        }
    }

    public enum CadToBimStep { Layers = 1, Preview = 2, Build = 3, Review = 4 }

    /// <summary>
    /// Which step is open. Layers is always reachable; Preview and Build need a drawing;
    /// Review needs a finished build. A step is ticked once the drafter has gone past it
    /// (Build only once something was built). Moving between steps never touches the session,
    /// so going back keeps erase, brush and pick work.
    /// </summary>
    public sealed class StepNavigator
    {
        private CadToBimStep _furthest = CadToBimStep.Layers;

        public CadToBimStep Current { get; private set; } = CadToBimStep.Layers;
        public bool HasSession { get; set; }
        public bool HasReview { get; private set; }

        public bool CanOpen(CadToBimStep step)
        {
            switch (step)
            {
                case CadToBimStep.Layers: return true;
                case CadToBimStep.Preview:
                case CadToBimStep.Build: return HasSession;
                case CadToBimStep.Review: return HasSession && HasReview;
                default: return false;
            }
        }

        public bool IsDone(CadToBimStep step) => step != Current && step < _furthest;

        public bool GoTo(CadToBimStep step)
        {
            if (!CanOpen(step)) return false;
            Current = step;
            if (step > _furthest) _furthest = step;
            return true;
        }

        public bool Next() => Current < CadToBimStep.Build && GoTo(Current + 1);

        public bool Back() => Current > CadToBimStep.Layers && GoTo(Current - 1);

        /// <summary>A build finished: Review opens and every earlier step is ticked.</summary>
        public void Built()
        {
            HasReview = true;
            GoTo(CadToBimStep.Review);
        }

        /// <summary>The review no longer applies (undo, Build more, Done).</summary>
        public void ClearReview()
        {
            HasReview = false;
            if (_furthest == CadToBimStep.Review) _furthest = CadToBimStep.Build;
            if (Current == CadToBimStep.Review) Current = CadToBimStep.Preview;
        }

        /// <summary>A different drawing was opened: start again at Layers.</summary>
        public void Reset()
        {
            HasReview = false;
            _furthest = CadToBimStep.Layers;
            Current = CadToBimStep.Layers;
        }
    }

    public static class LevelPlanner
    {
        /// <summary>
        /// The levels a Stack build will create, mirroring Cad2BimBuilder.LevelFor: plan i goes
        /// on the i-th level counted up from the base level; past the top it reuses a level
        /// already named "CAD Level i+1", otherwise it makes that level.
        /// </summary>
        public static List<string> NewLevels(IReadOnlyList<string> levelsByElevation, int baseIndex, int planCount)
        {
            var result = new List<string>();
            IReadOnlyList<string> levels = levelsByElevation ?? new string[0];
            if (baseIndex < 0) baseIndex = 0;

            for (int i = 0; i < planCount; i++)
            {
                if (baseIndex + i < levels.Count) continue;
                string name = "CAD Level " + (i + 1);
                bool exists = levels.Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase)) ||
                              result.Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
                if (!exists) result.Add(name);
            }

            return result;
        }
    }
}

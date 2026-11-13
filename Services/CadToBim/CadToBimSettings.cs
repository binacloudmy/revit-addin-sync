// CadToBimSettings — the CAD to BIM pane's tunable defaults, persisted under the
// "cadToBim" key of %APPDATA%\RevitWebAppSync\config.json (the file BinaConfig owns).
//
// Why this does its own file I/O instead of going through BinaConfig.Load()/Save():
// BinaConfig.Load() runs ApplyDefaults/ApplyHeals (which may WRITE the file) and reads
// Cloud Docs tokens out of the Windows Credential Manager, and BinaConfig.cs cannot be
// linked into the Tests project. So this class reads the file as a JObject, replaces
// only its own key, and writes it back; BinaConfig carries the key through as an opaque
// JObject (BinaConfig.CadToBim) so ITS Save() does not drop what was written here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class CadToBimSettings
    {
        public const string ConfigKey = "cadToBim";

        // Millimetres throughout (the engine normalises every drawing to mm on load).
        public double SMinMm = 50;
        public double SMaxMm = 400;
        public double WallHeightMm = 3000;
        public double DoorMinRadiusMm = 500;
        public double DoorMaxRadiusMm = 1500;
        public double WindowSillMm = 900;

        // A plan carries no opening heights; door and window types are sized to the drawn
        // width and these heights (sill + window = 2100 mm, level with the door heads).
        public double DoorHeightMm = 2100;
        public double WindowHeightMm = 1200;

        // Revit joins built walls at their corners. Off = the old behaviour, every wall
        // standing alone as drawn.
        public bool JoinWalls = true;

        // Case-insensitive substring hints, English + Malay — the same words the
        // Cad2BimConvertCommand used (Mentions()).
        public List<string> WallLayerHints = new() { "wall", "dinding", "tembok", "partition", "bata" };
        public List<string> OpeningLayerHints = new() { "door", "pintu", "win", "tingkap", "glaz" };

        // Window-only subset of OpeningLayerHints (no "door"/"pintu"): used the one place a door
        // layer being on-centreline must not be read as a window mark (ClassifyOpeningsFromSymbols'
        // windowLines argument). OpeningLayerHints stays as-is for the layer-list role chip.
        public List<string> WindowLayerHints = new() { "win", "window", "tingkap", "glaz", "wdw", "glass" };

        /// <summary>Words that contain "glass" but are not glazing (insulation, roof sheets).
        /// Taken out of a layer name before the window hints are tried.</summary>
        public static readonly string[] NotGlazingWords =
            { "fibreglass", "fiberglass", "glasswool", "glass wool", "glass-wool", "glass_wool", "glass fibre", "glass fiber" };

        // LayerFilter globs (`*` = any run of characters, case-insensitive). Furniture,
        // sanitary, dimensions, grid bubbles: draws, but not fabric.
        public List<string> ExcludeGlobs = new()
        {
            "PERABUT", "FURNITURE", "FURN*", "SANI*", "FITTING", "Toilet-fitting",
            "*-DIM*", "DEFPOINTS", "G-bubble", "GRID*",
        };

        // null = app.DefaultProjectTemplate at build time.
        public string TemplatePath;

        // Width of the pane's right (step) column after the drafter dragged the splitter, px.
        // null = the default (StepColumnLayout.DefaultWidth, 280).
        public double? StepColumnWidth;

        // Layer roles the drafter picked, per drawing: drawing key (DrawingKey) -> layer -> role
        // name. Names, not numbers, so a role added later never shifts a saved choice.
        public Dictionary<string, Dictionary<string, string>> LayerRoleOverrides = new();

        /// <summary>Case-insensitive key for a drawing path (config.json keys are case-sensitive).</summary>
        public static string DrawingKey(string drawingPath) =>
            string.IsNullOrWhiteSpace(drawingPath) ? "" : drawingPath.Trim().ToLowerInvariant();

        /// <summary>The saved layer -> role name map for one drawing (empty when none).</summary>
        public Dictionary<string, string> RoleOverridesFor(string drawingPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (LayerRoleOverrides != null &&
                LayerRoleOverrides.TryGetValue(DrawingKey(drawingPath), out Dictionary<string, string> saved) &&
                saved != null)
            {
                foreach (KeyValuePair<string, string> pair in saved) result[pair.Key] = pair.Value;
            }
            return result;
        }

        /// <summary>Remembers one layer's role for one drawing. Does not save the file.</summary>
        public void SetRoleOverride(string drawingPath, string layer, string role)
        {
            string key = DrawingKey(drawingPath);
            if (key.Length == 0 || string.IsNullOrEmpty(layer)) return;
            LayerRoleOverrides ??= new Dictionary<string, Dictionary<string, string>>();
            if (!LayerRoleOverrides.TryGetValue(key, out Dictionary<string, string> map) || map == null)
            {
                map = new Dictionary<string, string>();
                LayerRoleOverrides[key] = map;
            }
            map[layer] = role;
        }

        /// <summary>Tests point this at a temp file. null = the real config.json.</summary>
        internal static string ConfigPathOverride;

        /// <summary>Same path BinaConfig.ConfigPath computes (that one is private).</summary>
        public static string DefaultConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitWebAppSync",
            "config.json");

        private static string ConfigPath => ConfigPathOverride ?? DefaultConfigPath;

        // Replace, not Auto: with Auto, Newtonsoft APPENDS a saved list onto the field
        // initialiser, so five default hints plus six saved ones come back as eleven.
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            NullValueHandling = NullValueHandling.Ignore,
        });

        public static CadToBimSettings Load()
        {
            try
            {
                JObject root = ReadRoot();
                if (root != null && root[ConfigKey] is JObject node)
                {
                    CadToBimSettings loaded = node.ToObject<CadToBimSettings>(Serializer);
                    if (loaded != null)
                    {
                        loaded.AddNewDefaultHints();
                        return loaded;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable config is the defaults — same policy as BinaConfig.Load().
            }

            return new CadToBimSettings();
        }

        public void Save()
        {
            try
            {
                JObject root = null;
                try { root = ReadRoot(); } catch (Exception) { /* corrupt file: start over */ }
                root ??= new JObject();

                root[ConfigKey] = JObject.FromObject(this, Serializer);

                string directory = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(ConfigPath, root.ToString(Formatting.Indented));
            }
            catch (Exception)
            {
                // Same policy as BinaConfig.Save(): a settings write is never fatal.
            }
        }

        private static JObject ReadRoot()
        {
            if (!File.Exists(ConfigPath)) return null;
            string json = File.ReadAllText(ConfigPath);
            return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
        }

        /// <summary>A saved config keeps the hint lists it was saved with (ObjectCreationHandling
        /// .Replace), so a hint added to the defaults later never reaches it. "glass" is added to
        /// a window list that still has the default "glaz", i.e. one nobody rewrote by hand.</summary>
        internal void AddNewDefaultHints()
        {
            if (WindowLayerHints == null) return;
            bool hasGlaz = WindowLayerHints.Any(h => string.Equals(h, "glaz", StringComparison.OrdinalIgnoreCase));
            bool hasGlass = WindowLayerHints.Any(h => string.Equals(h, "glass", StringComparison.OrdinalIgnoreCase));
            if (hasGlaz && !hasGlass) WindowLayerHints.Add("glass");
        }

        /// <summary>The exclusions only, each with its discipline-prefixed forms
        /// (ExcludeVariants). No Include list: the wall-layer include list is something the
        /// detect pass derives per drawing from the hints, and the brush deliberately reads
        /// every layer.</summary>
        public Cad2Bim.LayerFilter ToLayerFilter()
        {
            var filter = new Cad2Bim.LayerFilter();
            filter.Exclude.AddRange(ExcludeGlobs
                .Where(glob => !string.IsNullOrWhiteSpace(glob))
                .SelectMany(ExcludeVariants)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            return filter;
        }

        /// <summary>The globs one exclusion stands for: itself and, unless it already starts with
        /// "*", the same after a prefix ending in "-" or "_", so FURN* also catches A-FURN,
        /// A_FURN and A-FURN-CHAIR (layer names usually start with a discipline code).</summary>
        public static IEnumerable<string> ExcludeVariants(string glob)
        {
            if (string.IsNullOrWhiteSpace(glob)) yield break;
            yield return glob;
            if (glob.StartsWith("*", StringComparison.Ordinal)) yield break;
            yield return "*-" + glob;
            yield return "*_" + glob;
        }

        /// <summary>The configured exclusion glob that leaves this layer out (as written in the
        /// settings, for the reason line), or null. Same matching as ToLayerFilter.</summary>
        public string ExcludedBy(string layer)
        {
            if (string.IsNullOrEmpty(layer) || ExcludeGlobs == null) return null;
            foreach (string glob in ExcludeGlobs)
            {
                if (ExcludeVariants(glob).Any(variant => Cad2Bim.LayerFilter.Matches(layer, variant))) return glob;
            }
            return null;
        }

        /// <summary>The layer name with NotGlazingWords taken out, for matching window hints.</summary>
        public static string WithoutNotGlazing(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return layer;
            string result = layer;
            foreach (string word in NotGlazingWords)
            {
                int at;
                while ((at = result.IndexOf(word, StringComparison.OrdinalIgnoreCase)) >= 0)
                    result = result.Remove(at, word.Length);
            }
            return result;
        }

        public bool IsWallLayer(string layer) => Mentions(layer, WallLayerHints);

        public bool IsOpeningLayer(string layer) => Mentions(layer, OpeningLayerHints);

        public bool IsWindowLayer(string layer) => Mentions(WithoutNotGlazing(layer), WindowLayerHints);

        private static bool Mentions(string layer, List<string> words) =>
            !string.IsNullOrEmpty(layer) &&
            words.Any(word => !string.IsNullOrEmpty(word) &&
                              layer.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}

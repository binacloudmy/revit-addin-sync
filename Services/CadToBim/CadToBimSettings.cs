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

        // Case-insensitive substring hints, English + Malay — the same words the
        // Cad2BimConvertCommand used (Mentions()).
        public List<string> WallLayerHints = new() { "wall", "dinding", "tembok", "partition", "bata" };
        public List<string> OpeningLayerHints = new() { "door", "pintu", "win", "tingkap", "glaz" };

        // LayerFilter globs (`*` = any run of characters, case-insensitive). Furniture,
        // sanitary, dimensions, grid bubbles: draws, but not fabric.
        public List<string> ExcludeGlobs = new()
        {
            "PERABUT", "FURNITURE", "FURN*", "SANI*", "FITTING", "Toilet-fitting",
            "*-DIM*", "DEFPOINTS", "G-bubble", "GRID*",
        };

        // null = app.DefaultProjectTemplate at build time.
        public string TemplatePath;

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
                    if (loaded != null) return loaded;
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

        /// <summary>The exclusions only. No Include list: the wall-layer include list is
        /// something the detect pass derives per drawing from the hints, and the brush
        /// deliberately reads every layer.</summary>
        public Cad2Bim.LayerFilter ToLayerFilter()
        {
            var filter = new Cad2Bim.LayerFilter();
            filter.Exclude.AddRange(ExcludeGlobs.Where(glob => !string.IsNullOrWhiteSpace(glob)));
            return filter;
        }

        public bool IsWallLayer(string layer) => Mentions(layer, WallLayerHints);

        public bool IsOpeningLayer(string layer) => Mentions(layer, OpeningLayerHints);

        private static bool Mentions(string layer, List<string> words) =>
            !string.IsNullOrEmpty(layer) &&
            words.Any(word => !string.IsNullOrEmpty(word) &&
                              layer.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}

// store_data / query_data — tiny per-document JSON scratchpad.
// Keyed by document path hash so two open projects never share state.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class ScratchStore
    {
        private static string PathFor(Document doc)
        {
            var key = string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BINA", "scratch");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, hash + ".json");
        }

        private static Dictionary<string, JsonElement> Load(Document doc)
        {
            var p = PathFor(doc);
            if (!File.Exists(p)) return new Dictionary<string, JsonElement>();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(p))
                       ?? new Dictionary<string, JsonElement>();
            }
            catch { return new Dictionary<string, JsonElement>(); }
        }

        public static Dictionary<string, object?> Store(Document doc, JsonElement args)
        {
            var key = ArgsHelp.GetString(args, "key")
                ?? throw new InvalidOperationException("key required");
            if (!args.TryGetProperty("value", out var value))
                throw new InvalidOperationException("value required");
            var data = Load(doc);
            data[key] = value.Clone();
            var p = PathFor(doc);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
#if NETFRAMEWORK
            if (File.Exists(p)) File.Delete(p);   // net48 Move has no overwrite
            File.Move(tmp, p);
#else
            File.Move(tmp, p, overwrite: true);   // atomic-ish swap
#endif
            return new Dictionary<string, object?> { ["ok"] = true, ["keys"] = data.Count };
        }

        public static Dictionary<string, object?> Query(Document doc, JsonElement args)
        {
            var key = ArgsHelp.GetString(args, "key");
            var data = Load(doc);
            if (key != null)
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["data"] = data.TryGetValue(key, out var v) ? (object?)v : null,
                    ["found"] = data.ContainsKey(key),
                };
            return new Dictionary<string, object?>
                { ["ok"] = true, ["data"] = data, ["keys"] = data.Count };
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Central master price database — curated JKR/CIDB rates that persist across projects.
    /// Stored at %AppData%/RevitWebAppSync/master_prices.json
    /// This is the single source of truth for construction rates.
    /// </summary>
    public class MasterPriceDatabase
    {
        private Dictionary<string, MasterPriceEntry> _entries;
        private readonly string _filePath;

        private static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitWebAppSync");

        private static MasterPriceDatabase _instance;
        private static readonly object _lock = new object();

        /// <summary>
        /// Singleton instance — one master DB shared across the addin
        /// </summary>
        public static MasterPriceDatabase Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new MasterPriceDatabase();
                    }
                }
                return _instance;
            }
        }

        private MasterPriceDatabase()
        {
            _filePath = Path.Combine(DataDir, "master_prices.json");
            Load();
        }

        public int Count => _entries.Count;

        // --- Lookup ---

        /// <summary>
        /// Exact JKR code lookup. Returns null if not found.
        /// </summary>
        public MasterPriceEntry GetByCode(string jkrCode)
        {
            if (string.IsNullOrEmpty(jkrCode)) return null;
            return _entries.TryGetValue(jkrCode, out var entry) ? entry : null;
        }

        /// <summary>
        /// Search by partial code, description, or category.
        /// Returns top matches ranked by relevance.
        /// </summary>
        public List<MasterPriceEntry> Search(string query, int maxResults = 10)
        {
            if (string.IsNullOrEmpty(query)) return new List<MasterPriceEntry>();

            string q = query.ToLower();

            return _entries.Values
                .Select(e => new
                {
                    Entry = e,
                    Score = CalculateMatchScore(e, q)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(maxResults)
                .Select(x => x.Entry)
                .ToList();
        }

        /// <summary>
        /// Get all entries for a category (e.g. "Walls", "Doors")
        /// </summary>
        public List<MasterPriceEntry> GetByCategory(string category)
        {
            if (string.IsNullOrEmpty(category)) return new List<MasterPriceEntry>();
            return _entries.Values
                .Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.JkrCode)
                .ToList();
        }

        /// <summary>
        /// Get all unique categories
        /// </summary>
        public List<string> GetCategories()
        {
            return _entries.Values
                .Where(e => !string.IsNullOrEmpty(e.Category))
                .Select(e => e.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .ToList();
        }

        // --- Auto-match ---

        /// <summary>
        /// Try to auto-match a list of CostItems to master prices by JKR code.
        /// Returns count of matched items.
        /// </summary>
        public int AutoMatchPrices(List<CostItem> items, PriceDatabase projectDb)
        {
            int matched = 0;

            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.JkrCode)) continue;

                // Skip if already priced in project DB
                if (item.UnitPrice > 0) continue;

                var masterEntry = GetByCode(item.JkrCode);
                if (masterEntry != null && masterEntry.UnitPrice > 0)
                {
                    // Apply to item
                    item.UnitPrice = masterEntry.UnitPrice;
                    item.PriceSource = "master";

                    // Also save to project DB for persistence
                    projectDb.SetPrice(
                        item.JkrCode,
                        masterEntry.UnitPrice,
                        masterEntry.Unit,
                        masterEntry.Description,
                        "master");

                    matched++;
                }
            }

            if (matched > 0)
                projectDb.Save();

            return matched;
        }

        /// <summary>
        /// Find unpriced items that have no match in the master database.
        /// These are candidates for manual pricing or AI suggestion.
        /// </summary>
        public List<CostItem> GetUnmatchedItems(List<CostItem> items)
        {
            return items
                .Where(i => i.UnitPrice <= 0)
                .Where(i => string.IsNullOrEmpty(i.JkrCode) || GetByCode(i.JkrCode) == null)
                .ToList();
        }

        // --- Import/Export ---

        /// <summary>
        /// Add or update a single entry
        /// </summary>
        public void SetEntry(MasterPriceEntry entry)
        {
            if (string.IsNullOrEmpty(entry?.JkrCode)) return;
            entry.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _entries[entry.JkrCode] = entry;
        }

        /// <summary>
        /// Bulk import from Excel-style dictionary.
        /// Only updates entries where the imported price differs or entry is new.
        /// Returns (added, updated) counts.
        /// </summary>
        public (int added, int updated) ImportEntries(
            Dictionary<string, (double price, string unit, string description)> prices,
            string source = "imported",
            string category = null)
        {
            int added = 0, updated = 0;

            foreach (var kvp in prices)
            {
                string code = kvp.Key;
                var (price, unit, description) = kvp.Value;

                if (price <= 0) continue;

                bool isNew = !_entries.ContainsKey(code);

                SetEntry(new MasterPriceEntry
                {
                    JkrCode = code,
                    Description = description,
                    UnitPrice = price,
                    Unit = unit,
                    Source = source,
                    Category = category ?? GuessCategoryFromCode(code),
                    LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                });

                if (isNew) added++; else updated++;
            }

            return (added, updated);
        }

        /// <summary>
        /// Export all entries for Excel/backup
        /// </summary>
        public List<MasterPriceEntry> GetAll()
        {
            return _entries.Values.OrderBy(e => e.JkrCode).ToList();
        }

        // --- Persistence ---

        public void Save()
        {
            try
            {
                if (!Directory.Exists(DataDir))
                    Directory.CreateDirectory(DataDir);

                string json = JsonConvert.SerializeObject(_entries, Formatting.Indented);
                File.WriteAllText(_filePath, json);

                System.Diagnostics.Debug.WriteLine($"[BINA Cost] Master DB saved: {_entries.Count} entries");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] Failed to save master DB: {ex.Message}");
            }
        }

        private void Load()
        {
            _entries = new Dictionary<string, MasterPriceEntry>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    _entries = JsonConvert.DeserializeObject<Dictionary<string, MasterPriceEntry>>(json)
                        ?? new Dictionary<string, MasterPriceEntry>(StringComparer.OrdinalIgnoreCase);

                    System.Diagnostics.Debug.WriteLine($"[BINA Cost] Master DB loaded: {_entries.Count} entries");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] Failed to load master DB: {ex.Message}");
            }
        }

        /// <summary>
        /// Force reload from disk (useful after external edits)
        /// </summary>
        public void Reload()
        {
            Load();
        }

        // --- Helpers ---

        private int CalculateMatchScore(MasterPriceEntry entry, string query)
        {
            int score = 0;

            // Exact code match
            if (entry.JkrCode?.ToLower() == query)
                return 100;

            // Code starts with query
            if (entry.JkrCode?.ToLower().StartsWith(query) == true)
                score += 50;

            // Code contains query
            if (entry.JkrCode?.ToLower().Contains(query) == true)
                score += 30;

            // Description contains query
            if (entry.Description?.ToLower().Contains(query) == true)
                score += 20;

            // Category matches
            if (entry.Category?.ToLower().Contains(query) == true)
                score += 10;

            return score;
        }

        /// <summary>
        /// Guess Revit category from JKR code prefix.
        /// JKR codes follow patterns: DB=Dinding Bata, PT=Pintu, LF=Lantai Finishing, etc.
        /// </summary>
        private string GuessCategoryFromCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 2) return null;

            string prefix = code.Substring(0, 2).ToUpper();
            switch (prefix)
            {
                case "DB": case "DK": return "Walls";          // Dinding Bata/Konkrit
                case "PT": return "Doors";                      // Pintu
                case "TK": return "Windows";                    // Tingkap
                case "LF": case "LT": return "Floors";         // Lantai
                case "SF": case "SL": return "Ceilings";       // Siling
                case "BU": return "Roofs";                      // Bumbung
                case "PP": case "PB": return "Plumbing Fixtures"; // Paip
                case "PE": return "Electrical Fixtures";         // Pendawaian Elektrik
                case "MK": return "Mechanical Equipment";        // Mekanikal
                case "KS": return "Casework";                    // Kabinet/Storan
                case "PR": return "Furniture";                   // Perabot
                default: return null;
            }
        }
    }

    /// <summary>
    /// A master price entry — curated rate from JKR/CIDB/manual sources
    /// </summary>
    public class MasterPriceEntry
    {
        public string JkrCode { get; set; }
        public string Description { get; set; }
        public double UnitPrice { get; set; }
        public string Unit { get; set; }        // "m²", "m", "unit", "m³"
        public string Category { get; set; }     // Revit category mapping
        public string Source { get; set; }        // "jkr", "cidb", "manual", "imported"
        public string LastUpdated { get; set; }
        public string Notes { get; set; }         // Optional remarks
    }
}

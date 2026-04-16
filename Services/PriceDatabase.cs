using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Local JSON-based price database. Stores JKR code → unit price mappings.
    /// One file per project, stored in %AppData%/RevitWebAppSync/prices/
    /// </summary>
    public class PriceDatabase
    {
        private Dictionary<string, PriceEntry> _prices;
        private readonly string _filePath;

        private static readonly string PricesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitWebAppSync", "prices");

        public PriceDatabase(string projectName)
        {
            // Sanitize project name for filename
            string safeName = string.Join("_", projectName.Split(Path.GetInvalidFileNameChars()));
            _filePath = Path.Combine(PricesDir, $"{safeName}_prices.json");
            Load();
        }

        /// <summary>
        /// Number of price entries
        /// </summary>
        public int Count => _prices.Count;

        /// <summary>
        /// Lookup unit price by JKR code. Returns 0 if not found.
        /// </summary>
        public double GetPrice(string jkrCode)
        {
            if (string.IsNullOrEmpty(jkrCode)) return 0;
            return _prices.TryGetValue(jkrCode, out var entry) ? entry.UnitPrice : 0;
        }

        /// <summary>
        /// Get full price entry by JKR code
        /// </summary>
        public PriceEntry GetEntry(string jkrCode)
        {
            if (string.IsNullOrEmpty(jkrCode)) return null;
            return _prices.TryGetValue(jkrCode, out var entry) ? entry : null;
        }

        /// <summary>
        /// Set price for a JKR code
        /// </summary>
        public void SetPrice(string jkrCode, double unitPrice, string unit, string description = null, string source = "manual")
        {
            if (string.IsNullOrEmpty(jkrCode)) return;

            _prices[jkrCode] = new PriceEntry
            {
                JkrCode = jkrCode,
                Description = description,
                UnitPrice = unitPrice,
                Unit = unit,
                Source = source,
                LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };
        }

        /// <summary>
        /// Import prices from a dictionary (e.g. from Excel import)
        /// </summary>
        public int ImportPrices(Dictionary<string, (double price, string unit, string description)> prices, string source = "imported")
        {
            int count = 0;
            foreach (var kvp in prices)
            {
                SetPrice(kvp.Key, kvp.Value.price, kvp.Value.unit, kvp.Value.description, source);
                count++;
            }
            return count;
        }

        /// <summary>
        /// Apply prices to a list of cost items.
        /// Skips non-auto-priceable categories to prevent stale inflated prices
        /// from previous sessions overriding the NoAutoPriceCategories filter.
        /// </summary>
        public int ApplyPrices(List<CostItem> items)
        {
            int matched = 0;
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.JkrCode)) continue;
                if (!CostCalculator.IsAutoPriceable(item.Category)) continue;

                var entry = GetEntry(item.JkrCode);
                if (entry != null && entry.UnitPrice > 0)
                {
                    item.UnitPrice = entry.UnitPrice;
                    item.PriceSource = entry.Source;
                    matched++;
                }
            }
            return matched;
        }

        /// <summary>
        /// Get all price entries
        /// </summary>
        public List<PriceEntry> GetAll()
        {
            return _prices.Values.OrderBy(p => p.JkrCode).ToList();
        }

        /// <summary>
        /// Clear all prices and delete the file from disk
        /// </summary>
        public void Clear()
        {
            _prices.Clear();
            try
            {
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] Failed to delete price file: {ex.Message}");
            }
        }

        /// <summary>
        /// Save to disk
        /// </summary>
        public void Save()
        {
            try
            {
                if (!Directory.Exists(PricesDir))
                    Directory.CreateDirectory(PricesDir);

                string json = JsonConvert.SerializeObject(_prices, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] Failed to save prices: {ex.Message}");
            }
        }

        /// <summary>
        /// Load from disk
        /// </summary>
        private void Load()
        {
            _prices = new Dictionary<string, PriceEntry>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    _prices = JsonConvert.DeserializeObject<Dictionary<string, PriceEntry>>(json)
                        ?? new Dictionary<string, PriceEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] Failed to load prices: {ex.Message}");
            }
        }
    }
}

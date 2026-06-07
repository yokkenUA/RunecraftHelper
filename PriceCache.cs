namespace RunecraftHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public enum PriceSyncStatus { Idle, Syncing, Ready, Error }

    // Fetches PoE2 prices from poe.ninja and exposes them keyed by in-game item name.
    //
    // poe.ninja's PoE2 economy API returns 3 top-level fields per overview type:
    //   core.rates.exalted        → 1 Divine in Exalted Orb units (the conversion rate)
    //   items[{id,name,...}]      → id → display-name lookup
    //   lines[{id,primaryValue}]  → id → price in Divine (the primary currency)
    //
    // We join lines+items on id, multiply primaryValue by the exalted rate, and keep the result
    // keyed by a normalized form of `name` (lowercase + alphanumerics only) so in-game names like
    // "Mystic Alloy" / "Orb of Alchemy" match regardless of spacing/case quirks.
    public sealed class PriceCache
    {
        private static readonly string[] OverviewTypes =
            { "Currency", "UncutGems", "Runes", "Idols", "Verisium", "Expedition" };

        private static readonly HttpClient http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("RunecraftHelper/1.0 (gamehelper2-fork plugin)");
            return c;
        }

        private readonly object gate = new();
        private Dictionary<string, double> pricesExalted = new(StringComparer.Ordinal);
        // Same prices keyed by the item's internal art id (icon filename, e.g. "CurrencyUpgradeToRare").
        // Language-independent — used to match items read off a non-English game client.
        private Dictionary<string, double> pricesByArt = new(StringComparer.Ordinal);
        // art-id → poe.ninja English display name. Lets the overlay show a readable English label for
        // items read off a non-English client (whose localized name the ImGui font may not render).
        private Dictionary<string, string> namesByArt = new(StringComparer.Ordinal);

        public PriceSyncStatus Status { get; private set; } = PriceSyncStatus.Idle;
        public DateTime LastSyncUtc { get; private set; } = DateTime.MinValue;
        public string LastError { get; private set; } = string.Empty;
        public double DivineToExaltedRate { get; private set; }

        public int PriceCount
        {
            get { lock (this.gate) return this.pricesExalted.Count; }
        }

        public bool TryGetExaltedPrice(string itemName, out double exaltedPrice)
        {
            exaltedPrice = 0;
            if (string.IsNullOrEmpty(itemName)) return false;
            var key = Normalize(itemName);
            if (key.Length == 0) return false;
            lock (this.gate)
            {
                return this.pricesExalted.TryGetValue(key, out exaltedPrice);
            }
        }

        // Match by internal art id (icon filename without extension), e.g. "CurrencyUpgradeToRare".
        // Works regardless of game-client language. Caller should try this first, then fall back
        // to TryGetExaltedPrice(displayName) for English clients / unmapped items.
        public bool TryGetPriceByArtId(string artId, out double exaltedPrice)
        {
            exaltedPrice = 0;
            if (string.IsNullOrEmpty(artId)) return false;
            var key = Normalize(artId);
            if (key.Length == 0) return false;
            lock (this.gate)
            {
                return this.pricesByArt.TryGetValue(key, out exaltedPrice);
            }
        }

        // poe.ninja English display name for an internal art id, e.g. "ColdRune" → "...". Used only to
        // show a readable label on non-English clients; matching/pricing is still by art id.
        public bool TryGetNameByArtId(string artId, out string name)
        {
            name = string.Empty;
            if (string.IsNullOrEmpty(artId)) return false;
            var key = Normalize(artId);
            if (key.Length == 0) return false;
            lock (this.gate)
            {
                return this.namesByArt.TryGetValue(key, out name!);
            }
        }

        // Load a previously-saved snapshot. Returns true if the file existed AND its data is
        // within the TTL — caller skips a network refresh in that case. A return of false with
        // a populated Status (Ready) means stale data is loaded and usable while a refresh runs.
        public bool TryLoadFromDisk(string filePath, int ttlMinutes)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var content = File.ReadAllText(filePath);
                var dto = JsonConvert.DeserializeObject<PriceCacheDto>(content);
                if (dto == null || dto.Prices == null) return false;

                lock (this.gate)
                {
                    this.pricesExalted = new Dictionary<string, double>(dto.Prices, StringComparer.Ordinal);
                    this.pricesByArt = dto.ArtPrices != null
                        ? new Dictionary<string, double>(dto.ArtPrices, StringComparer.Ordinal)
                        : new Dictionary<string, double>(StringComparer.Ordinal);
                    this.namesByArt = dto.ArtNames != null
                        ? new Dictionary<string, string>(dto.ArtNames, StringComparer.Ordinal)
                        : new Dictionary<string, string>(StringComparer.Ordinal);
                    this.DivineToExaltedRate = dto.DivineToExaltedRate;
                    this.LastSyncUtc = dto.LastSyncUtc;
                    this.Status = PriceSyncStatus.Ready;
                }

                var age = DateTime.UtcNow - this.LastSyncUtc;
                return age <= TimeSpan.FromMinutes(Math.Max(1, ttlMinutes));
            }
            catch (Exception ex)
            {
                lock (this.gate) this.LastError = $"load failed: {ex.Message}";
                return false;
            }
        }

        // Fire-and-forget. Status flips to Syncing → Ready / Error. Safe to spam-call:
        // a second call while one is in flight returns immediately.
        public void StartRefresh(string league, string filePath)
        {
            lock (this.gate)
            {
                if (this.Status == PriceSyncStatus.Syncing) return;
                this.Status = PriceSyncStatus.Syncing;
            }
            _ = Task.Run(() => this.RefreshAsync(league, filePath));
        }

        private async Task RefreshAsync(string league, string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(league))
                    throw new InvalidOperationException("League name is empty.");

                var aggregated = new Dictionary<string, double>(StringComparer.Ordinal);
                var aggregatedArt = new Dictionary<string, double>(StringComparer.Ordinal);
                var aggregatedArtNames = new Dictionary<string, string>(StringComparer.Ordinal);
                double divToEx = 0;

                var leagueParam = Uri.EscapeDataString(league.Trim()).Replace("%20", "+");

                // One bad overview type (404 / renamed slug) must not nuke every other price, so
                // each type is fetched independently and failures are collected, not thrown.
                var failedTypes = new List<string>();
                foreach (var type in OverviewTypes)
                {
                    try
                    {
                        var url = $"https://poe.ninja/poe2/api/economy/exchange/current/overview?league={leagueParam}&type={type}";
                        using var resp = await http.GetAsync(url).ConfigureAwait(false);
                        resp.EnsureSuccessStatusCode();
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                        var parsed = JObject.Parse(json);
                        var localRate = parsed["core"]?["rates"]?["exalted"]?.Value<double>() ?? 0;
                        if (localRate > 0) divToEx = localRate;

                        var nameById = new Dictionary<string, string>(StringComparer.Ordinal);
                        var artById = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (parsed["items"] is JArray itemsArr)
                        {
                            foreach (var it in itemsArr)
                            {
                                var id = it["id"]?.Value<string>();
                                if (string.IsNullOrEmpty(id)) continue;
                                var name = it["name"]?.Value<string>();
                                if (!string.IsNullOrEmpty(name)) nameById[id!] = name!;
                                // image filename (no extension) is the game's internal art id, e.g.
                                // ".../CurrencyUpgradeToRare.png" → "CurrencyUpgradeToRare".
                                var art = ExtractArtId(it["image"]?.Value<string>());
                                if (!string.IsNullOrEmpty(art)) artById[id!] = art!;
                                // art-id → English name, for ALL items (even unpriced ones), so the
                                // overlay can show a readable label regardless of client language.
                                if (!string.IsNullOrEmpty(art) && !string.IsNullOrEmpty(name))
                                {
                                    var ak = Normalize(art!);
                                    if (ak.Length > 0) aggregatedArtNames[ak] = name!;
                                }
                            }
                        }

                        if (parsed["lines"] is JArray lines)
                        {
                            foreach (var ln in lines)
                            {
                                var id = ln["id"]?.Value<string>();
                                var primary = ln["primaryValue"]?.Value<double?>() ?? 0;
                                if (string.IsNullOrEmpty(id) || primary <= 0) continue;
                                var price = primary * localRate;

                                if (nameById.TryGetValue(id!, out var name))
                                {
                                    var key = Normalize(name);
                                    if (key.Length > 0) aggregated[key] = price;
                                }
                                if (artById.TryGetValue(id!, out var art))
                                {
                                    var artKey = Normalize(art);
                                    if (artKey.Length > 0) aggregatedArt[artKey] = price;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failedTypes.Add($"{type}: {ex.Message}");
                    }
                }

                if (aggregated.Count == 0)
                    throw new InvalidOperationException(
                        $"no prices fetched — all overview types failed ({string.Join("; ", failedTypes)})");

                // Ensure the reference currencies themselves are queryable. lines[] for Currency
                // includes them (their primaryValue is their own price in Divine), but defending
                // against future API changes is cheap.
                if (divToEx > 0)
                {
                    aggregated[Normalize("Exalted Orb")] = 1.0;
                    aggregated[Normalize("Divine Orb")] = divToEx;
                }

                lock (this.gate)
                {
                    this.pricesExalted = aggregated;
                    this.pricesByArt = aggregatedArt;
                    this.namesByArt = aggregatedArtNames;
                    this.DivineToExaltedRate = divToEx;
                    this.LastSyncUtc = DateTime.UtcNow;
                    this.Status = PriceSyncStatus.Ready;
                    this.LastError = failedTypes.Count == 0
                        ? string.Empty
                        : $"partial — skipped {string.Join(", ", failedTypes)}";
                }

                var dto = new PriceCacheDto
                {
                    LastSyncUtc = this.LastSyncUtc,
                    DivineToExaltedRate = divToEx,
                    Prices = aggregated,
                    ArtPrices = aggregatedArt,
                    ArtNames = aggregatedArtNames,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.WriteAllText(filePath, JsonConvert.SerializeObject(dto, Formatting.Indented));
            }
            catch (Exception ex)
            {
                lock (this.gate)
                {
                    this.Status = PriceSyncStatus.Error;
                    this.LastError = ex.Message;
                }
            }
        }

        // Lowercase + drop everything that isn't a-z 0-9. Matches what the game UI shows
        // ("Mystic Alloy" / "Orb of Alchemy") against poe.ninja item names.
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
                else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.ToString();
        }

        // poe.ninja image URLs end with the item's art asset, e.g.
        //   "/gen/image/<base64>/<hash>/CurrencyUpgradeToRare.png"
        // The filename (without extension) is the game's language-independent internal art id.
        public static string ExtractArtId(string? imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl)) return string.Empty;
            var s = imageUrl;
            int q = s.IndexOf('?');
            if (q >= 0) s = s.Substring(0, q);
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            int dot = s.LastIndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            return s;
        }

        private sealed class PriceCacheDto
        {
            public DateTime LastSyncUtc { get; set; }
            public double DivineToExaltedRate { get; set; }
            public Dictionary<string, double> Prices { get; set; } = new();
            public Dictionary<string, double> ArtPrices { get; set; } = new();
            public Dictionary<string, string> ArtNames { get; set; } = new();
        }
    }
}

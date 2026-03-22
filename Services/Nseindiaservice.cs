using Newtonsoft.Json.Linq;
using System.Globalization;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// Fetches market context from NSE India free public API.
    /// Provides: India VIX, FII/DII data, Nifty trend, Sector performance.
    /// No API key needed. Cached every 15 minutes.
    /// </summary>
    public class NseIndiaService
    {
        private readonly ILogger<NseIndiaService> _logger;
        private readonly HttpClient _http;

        private MarketContext? _cachedContext;
        private DateTime _lastFetch = DateTime.MinValue;
        private const int CacheMinutes = 15;
        private const string NseBase = "https://www.nseindia.com";

        public NseIndiaService(
            ILogger<NseIndiaService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _http = httpClientFactory.CreateClient("News");
        }

        // ── Main entry point ──────────────────────────
        public async Task<MarketContext> GetMarketContextAsync()
        {
            if (_cachedContext != null &&
                (DateTime.Now - _lastFetch).TotalMinutes < CacheMinutes)
                return _cachedContext;

            var ctx = new MarketContext();

            // Run all in parallel for speed
            await Task.WhenAll(
                FetchVixAndNiftyAsync(ctx),
                FetchFiiDiiAsync(ctx),
                FetchSectorIndicesAsync(ctx));

            ctx.FetchedAt = DateTime.Now;
            _cachedContext = ctx;
            _lastFetch = DateTime.Now;

            _logger.LogInformation(
                "✅ Market context: VIX={vix:F1} " +
                "FII=₹{fii:N0}Cr Nifty={chg:F2}% Quality={q}/100",
                ctx.IndiaVix, ctx.FiiNetCrore,
                ctx.NiftyChange, ctx.MarketQualityScore);

            return ctx;
        }

        // ── VIX + Nifty from allIndices ───────────────
        private async Task FetchVixAndNiftyAsync(MarketContext ctx)
        {
            try
            {
                var token = await GetNseTokenAsync(
                    $"{NseBase}/api/allIndices");
                if (token == null) return;

                // NSE returns { "data": [...] }
                var dataToken = token["data"];
                if (dataToken == null) return;

                var items = dataToken as JArray
                         ?? new JArray(dataToken);

                foreach (var item in items)
                {
                    var name = item["index"]?.Value<string>() ?? "";

                    // ── VIX ───────────────────────────
                    if (name.Contains("VIX",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.IndiaVix = SafeDouble(item["last"]);
                        ctx.VixChange = SafeDouble(item["variation"]);
                        ctx.VixInterpretation = ctx.IndiaVix switch
                        {
                            < 12 => "Very Low Fear — Stable ✅",
                            < 16 => "Low Fear — Good for intraday ✅",
                            < 20 => "Moderate — Trade carefully ⚠️",
                            < 25 => "High Fear — Avoid aggressive trades ❌",
                            _ => "Very High Fear — Stay out 🚨"
                        };
                    }

                    // ── Nifty 50 ──────────────────────
                    if (name == "NIFTY 50")
                    {
                        ctx.NiftyLtp = SafeDouble(item["last"]);
                        ctx.NiftyChange = SafeDouble(item["percentChange"]);
                        ctx.NiftyHigh = SafeDouble(item["high"]);
                        ctx.NiftyLow = SafeDouble(item["low"]);

                        ctx.NiftyTrend = ctx.NiftyChange switch
                        {
                            > 1.0 => "STRONG BULLISH 📈 (>1% up)",
                            > 0.3 => "BULLISH 📈 (positive today)",
                            > -0.3 => "SIDEWAYS ↔️ (flat)",
                            > -1.0 => "BEARISH 📉 (negative today)",
                            _ => "STRONG BEARISH 📉 (>1% down)"
                        };

                        if (ctx.NiftyHigh > 0 && ctx.NiftyLow > 0)
                        {
                            var range = ctx.NiftyHigh - ctx.NiftyLow;
                            var pos = ctx.NiftyLtp - ctx.NiftyLow;
                            var pct = range > 0 ? (pos / range) * 100 : 50;
                            ctx.NiftyDayPosition = pct > 70
                                ? "Near day HIGH (overbought risk)"
                                : pct < 30
                                ? "Near day LOW (bounce possible)"
                                : "Mid-range (neutral)";
                        }
                    }
                }

                // Default VIX if not found
                if (ctx.IndiaVix == 0)
                {
                    ctx.IndiaVix = 15;
                    ctx.VixInterpretation = "Moderate (VIX data unavailable)";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ VIX/Nifty fetch failed: {m}", ex.Message);
                ctx.IndiaVix = 15;
                ctx.VixInterpretation = "Moderate (unavailable)";
                ctx.NiftyTrend = "Unknown";
            }
        }

        // ── FII / DII ─────────────────────────────────
        private async Task FetchFiiDiiAsync(MarketContext ctx)
        {
            try
            {
                var token = await GetNseTokenAsync(
                    $"{NseBase}/api/fiidiiTradeReact");
                if (token == null)
                {
                    SetFiiDefaults(ctx);
                    return;
                }

                // NSE can return array directly or { data: [...] }
                JToken? today = null;

                if (token is JArray rootArr && rootArr.Count > 0)
                {
                    today = rootArr[0];
                }
                else
                {
                    var dataNode = token["data"];
                    if (dataNode is JArray dataArr && dataArr.Count > 0)
                        today = dataArr[0];
                    else if (dataNode is JObject dataObj)
                        today = dataObj;
                    else if (token is JObject obj)
                        today = obj;
                }

                if (today == null)
                {
                    SetFiiDefaults(ctx);
                    return;
                }

                // Try multiple field name variations NSE uses
                var fiiNet = today["fiiNet"]?.Value<string>()
                          ?? today["FII_NET"]?.Value<string>()
                          ?? today["fiinet"]?.Value<string>()
                          ?? "0";

                var diiNet = today["diiNet"]?.Value<string>()
                          ?? today["DII_NET"]?.Value<string>()
                          ?? today["diinet"]?.Value<string>()
                          ?? "0";

                ctx.FiiNetCrore = ParseCrore(fiiNet);
                ctx.DiiNetCrore = ParseCrore(diiNet);
                ctx.FiiDiiDate = today["date"]?.Value<string>()
                               ?? DateTime.Now.ToString("dd-MMM-yyyy");

                ctx.FiiSentiment = ctx.FiiNetCrore switch
                {
                    > 2000 => "Massive FII Buying 📈 Very Bullish",
                    > 500 => "FII Buying ✅ Bullish",
                    > 0 => "Slight FII Buying — Mildly Bullish",
                    > -500 => "Slight FII Selling — Mildly Bearish",
                    > -2000 => "FII Selling ❌ Bearish",
                    _ => "Massive FII Selling 📉 Very Bearish"
                };

                ctx.DiiSentiment = ctx.DiiNetCrore > 0
                    ? $"DII Buying ₹{ctx.DiiNetCrore:N0}Cr (supporting)"
                    : $"DII Selling ₹{Math.Abs(ctx.DiiNetCrore):N0}Cr";
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ FII/DII fetch failed: {m}", ex.Message);
                SetFiiDefaults(ctx);
            }
        }

        private void SetFiiDefaults(MarketContext ctx)
        {
            ctx.FiiNetCrore = 0;
            ctx.FiiSentiment = "FII data unavailable";
            ctx.DiiSentiment = "DII data unavailable";
        }

        // ── Sector Indices ────────────────────────────
        private async Task FetchSectorIndicesAsync(MarketContext ctx)
        {
            try
            {
                var token = await GetNseTokenAsync(
                    $"{NseBase}/api/allIndices");
                if (token == null) return;

                var dataToken = token["data"];
                if (dataToken == null) return;

                var items = dataToken as JArray
                         ?? new JArray(dataToken);

                var sectorMap = new Dictionary<string, string>
                {
                    { "NIFTY BANK",               "Banking"  },
                    { "NIFTY IT",                 "IT"       },
                    { "NIFTY PHARMA",             "Pharma"   },
                    { "NIFTY ENERGY",             "Energy"   },
                    { "NIFTY METAL",              "Metals"   },
                    { "NIFTY AUTO",               "Auto"     },
                    { "NIFTY FMCG",               "FMCG"     },
                    { "NIFTY REALTY",             "Realty"   },
                    { "NIFTY INFRA",              "Infra"    },
                    { "NIFTY PSU BANK",           "PSU Banks"},
                    { "NIFTY FINANCIAL SERVICES", "Finance"  },
                };

                var sectors = new List<SectorPerformance>();

                foreach (var item in items)
                {
                    var name = item["index"]?.Value<string>() ?? "";
                    if (!sectorMap.TryGetValue(name, out var friendly))
                        continue;

                    double chg = SafeDouble(item["percentChange"]);
                    sectors.Add(new SectorPerformance
                    {
                        SectorName = friendly,
                        ChangePercent = chg,
                        IsPositive = chg > 0
                    });
                }

                ctx.Sectors = sectors.OrderByDescending(s =>
                    s.ChangePercent).ToList();

                ctx.TopSectors = sectors
                    .Where(s => s.ChangePercent > 0)
                    .OrderByDescending(s => s.ChangePercent)
                    .Take(3)
                    .Select(s => $"{s.SectorName} +{s.ChangePercent:F1}%")
                    .ToList();

                ctx.WeakSectors = sectors
                    .Where(s => s.ChangePercent < 0)
                    .OrderBy(s => s.ChangePercent)
                    .Take(3)
                    .Select(s => $"{s.SectorName} {s.ChangePercent:F1}%")
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Sector fetch failed: {m}", ex.Message);
            }
        }

        // ── NSE HTTP helper ───────────────────────────
        // NSE requires a cookie from the homepage first.
        // The "News" HttpClient has cookie handling enabled.
        private async Task<JToken?> GetNseTokenAsync(string url)
        {
            try
            {
                // Warm up cookies if needed
                try
                {
                    var warmup = new HttpRequestMessage(
                        HttpMethod.Get, NseBase);
                    warmup.Headers.Add("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                        "AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
                    await _http.SendAsync(warmup);
                }
                catch { /* ignore warmup errors */ }

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Referer", NseBase + "/");
                req.Headers.Add("Accept",
                    "application/json, text/plain, */*");
                req.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");

                var response = await _http.SendAsync(req);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("NSE {url}: HTTP {code}",
                        url, (int)response.StatusCode);
                    return null;
                }

                var raw = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(raw)) return null;

                raw = raw.Trim();
                if (!raw.StartsWith("{") && !raw.StartsWith("["))
                    return null;

                return JToken.Parse(raw);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("NSE fetch failed {url}: {m}",
                    url, ex.Message);
                return null;
            }
        }

        // ── Helpers ───────────────────────────────────
        private double SafeDouble(JToken? token)
        {
            if (token == null) return 0;
            return double.TryParse(
                token.Value<string>(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double v) ? v : 0;
        }

        private double ParseCrore(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            var clean = value
                .Replace(",", "")
                .Replace("₹", "")
                .Replace(" ", "")
                .Trim();
            return double.TryParse(clean,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double v) ? v : 0;
        }

        // ── Sector lookup for a stock symbol ──────────
        public string GetSectorForSymbol(string symbol)
        {
            var map = new Dictionary<string, string>
            {
                // PSU Banks
                {"SBIN","PSU Banks"},{"BANKBARODA","PSU Banks"},
                {"CANBK","PSU Banks"},{"PNB","PSU Banks"},
                {"UNIONBANK","PSU Banks"},{"IOB","PSU Banks"},
                {"INDIANB","PSU Banks"},{"CENTRALBK","PSU Banks"},
                {"MAHABANK","PSU Banks"},
                // IT
                {"TCS","IT"},{"INFY","IT"},{"WIPRO","IT"},
                {"HCLTECH","IT"},{"TECHM","IT"},{"LTIM","IT"},
                // Energy
                {"ONGC","Energy"},{"BPCL","Energy"},
                {"IOC","Energy"},{"GAIL","Energy"},
                {"COALINDIA","Energy"},{"NTPC","Energy"},
                // Metals
                {"TATASTEEL","Metals"},{"HINDALCO","Metals"},
                {"NMDC","Metals"},{"NATIONALUM","Metals"},
                {"JSWSTEEL","Metals"},
                // Pharma
                {"SUNPHARMA","Pharma"},{"CIPLA","Pharma"},
                {"DRREDDY","Pharma"},{"LUPIN","Pharma"},
                // Power
                {"NHPC","Power"},{"TATAPOWER","Power"},
                {"IREDA","Power"},{"ADANIPOWER","Power"},
                // Finance
                {"LICI","Finance"},{"MUTHOOTFIN","Finance"},
                {"LICHSGFIN","Finance"},{"PFC","Finance"},
                {"RECLTD","Finance"},{"IRFC","Finance"},
            };
            return map.TryGetValue(symbol.ToUpper(), out var s)
                ? s : "Diversified";
        }

        public SectorPerformance? GetSectorPerformance(
            string symbol, MarketContext ctx)
        {
            var sector = GetSectorForSymbol(symbol);
            return ctx.Sectors.FirstOrDefault(s =>
                s.SectorName.Equals(sector,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Models ────────────────────────────────────────
    public class MarketContext
    {
        public double IndiaVix { get; set; } = 15;
        public double VixChange { get; set; }
        public string VixInterpretation { get; set; } = "";

        public double FiiNetCrore { get; set; }
        public double DiiNetCrore { get; set; }
        public string FiiDiiDate { get; set; } = "";
        public string FiiSentiment { get; set; } = "";
        public string DiiSentiment { get; set; } = "";

        public double NiftyLtp { get; set; }
        public double NiftyChange { get; set; }
        public double NiftyHigh { get; set; }
        public double NiftyLow { get; set; }
        public string NiftyTrend { get; set; } = "";
        public string NiftyDayPosition { get; set; } = "";

        public List<SectorPerformance> Sectors { get; set; } = new();
        public List<string> TopSectors { get; set; } = new();
        public List<string> WeakSectors { get; set; } = new();

        public DateTime FetchedAt { get; set; }

        public int MarketQualityScore
        {
            get
            {
                int s = 50;
                // VIX
                if (IndiaVix < 12) s += 20;
                else if (IndiaVix < 16) s += 10;
                else if (IndiaVix > 20) s -= 15;
                else if (IndiaVix > 25) s -= 25;
                // FII
                if (FiiNetCrore > 2000) s += 20;
                else if (FiiNetCrore > 500) s += 10;
                else if (FiiNetCrore < -500) s -= 10;
                else if (FiiNetCrore < -2000) s -= 20;
                // Nifty
                if (NiftyChange > 1.0) s += 15;
                else if (NiftyChange > 0.3) s += 8;
                else if (NiftyChange < -1.0) s -= 15;
                else if (NiftyChange < -0.3) s -= 8;
                return Math.Max(0, Math.Min(100, s));
            }
        }

        public string MarketQualityLabel => MarketQualityScore switch
        {
            >= 75 => "EXCELLENT — Great day for intraday ✅",
            >= 60 => "GOOD — Favorable conditions ✅",
            >= 45 => "AVERAGE — Trade selectively ⚠️",
            >= 30 => "POOR — High risk day ❌",
            _ => "VERY POOR — Avoid trading today 🚨"
        };
    }

    public class SectorPerformance
    {
        public string SectorName { get; set; } = "";
        public double ChangePercent { get; set; }
        public bool IsPositive { get; set; }
    }
}
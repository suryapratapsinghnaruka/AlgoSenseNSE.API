using AlgoSenseNSE.API.Models;

namespace AlgoSenseNSE.API.Services
{
    // This is the brain — orchestrates everything
    // and implements the tiered scanning approach
    public class MarketScanService
    {
        private readonly AngelOneService _angel;
        private readonly TechnicalAnalysisService _technical;
        private readonly FundamentalService _fundamental;
        private readonly NewsService _news;
        private readonly ScoringEngine _scoring;
        private readonly ClaudeAiService _ai;
        private readonly ILogger<MarketScanService> _logger;
        private readonly IConfiguration _config;

        // In-memory state
        private Dictionary<string, string> _symbolTokenMap = new();
        private List<string> _tier1Symbols = new();
        private List<string> _tier2Symbols = new();
        private List<string> _allQualitySymbols = new();
        private Dictionary<string, LivePrice> _livePrices = new();
        private Dictionary<string, TechnicalResult> _techResults = new();
        private Dictionary<string, FundamentalResult> _fundResults = new();
        private Dictionary<string, CompositeScore> _scores = new();
        private List<Recommendation> _recommendations = new();
        private DateTime _lastFullScan = DateTime.MinValue;

        public MarketScanService(
            AngelOneService angel,
            TechnicalAnalysisService technical,
            FundamentalService fundamental,
            NewsService news,
            ScoringEngine scoring,
            ClaudeAiService ai,
            ILogger<MarketScanService> logger,
            IConfiguration config)
        {
            _angel = angel;
            _technical = technical;
            _fundamental = fundamental;
            _news = news;
            _scoring = scoring;
            _ai = ai;
            _logger = logger;
            _config = config;
        }

        // ── Initialize — runs once on startup ────────
        public async Task InitializeAsync()
        {
            _logger.LogInformation("🚀 Initializing market scan...");

            // Login to Angel One
            await _angel.LoginAsync();

            // Build symbol → token map for all NSE+BSE
            var allSymbols = await GetAllNseBseSymbolsAsync();
            _symbolTokenMap = await _angel
                .GetSymbolTokenMapAsync(allSymbols);

            // Run first full scan
            await RunFullDailyScanAsync();

            _logger.LogInformation(
                "✅ Initialization complete. " +
                "Tracking {t1} Tier1, {t2} Tier2 stocks",
                _tier1Symbols.Count, _tier2Symbols.Count);
        }

        // ── Full daily scan — runs at 6am ────────────
        public async Task RunFullDailyScanAsync()
        {
            _logger.LogInformation("🔍 Running full daily market scan...");

            try
            {
                // ── Step 1: Filter symbol map to ONLY real equity stocks ──
                // Remove indices (contain spaces), ETFs, bonds, F&O symbols
                var equitySymbols = _symbolTokenMap.Keys
                    .Where(s =>
                        !s.Contains(" ") &&        // No spaces = not an index
                        !s.Contains("-") &&        // No hyphens
                        !s.Contains("&") &&        // No special chars
                        !s.EndsWith("BE") &&       // Not book entry
                        !s.EndsWith("BL") &&       // Not block deal
                        !s.EndsWith("GS") &&       // Not govt securities
                        s.Length >= 2 &&
                        s.Length <= 20 &&
                        !s.StartsWith("Nifty") &&  // Not Nifty indices
                        !s.StartsWith("NIFTY") &&  // Not Nifty indices
                        !s.StartsWith("SENSEX") && // Not Sensex
                        !s.StartsWith("India") &&  // Not India VIX etc
                        char.IsLetter(s[0])        // Must start with letter
                    )
                    .ToList();

                _logger.LogInformation(
                    "📊 Filtered to {count} equity symbols " +
                    "from {total} total instruments",
                    equitySymbols.Count,
                    _symbolTokenMap.Count);

                // ── Step 2: Pre-screen with known quality stocks ──────────
                // Use a curated list of quality NSE stocks for fast startup
                // These are NSE 500 companies — all real, all on Screener.in
                var tier1KnownSymbols = new List<string>
        {
            // Nifty 50
            "RELIANCE", "TCS", "HDFCBANK", "INFY", "ICICIBANK",
            "HINDUNILVR", "BAJFINANCE", "SBIN", "BHARTIARTL",
            "KOTAKBANK", "AXISBANK", "LT", "MARUTI", "SUNPHARMA",
            "TITAN", "WIPRO", "DRREDDY", "TATASTEEL", "ONGC",
            "NTPC", "POWERGRID", "TECHM", "DIVISLAB", "NESTLEIND",
            "ULTRACEMCO", "ASIANPAINT", "COALINDIA", "BPCL",
            "IOC", "GRASIM", "ADANIPORTS", "TATACONSUM", "BAJAJFINSV",
            "HCLTECH", "INDUSINDBK", "JSWSTEEL", "LTIM", "BRITANNIA",
            "CIPLA", "EICHERMOT",
            // Nifty Next 50
            "ZOMATO", "ADANIENT", "ADANIGREEN", "ADANIPOWER",
            "APOLLOHOSP", "BANKBARODA", "BERGEPAINT", "BEL",
            "BHEL", "BOSCHLTD", "CANBK", "CHOLAFIN", "COLPAL",
            "DABUR", "DLF", "GAIL", "GODREJCP", "HAVELLS",
            "HEROMOTOCO", "HINDALCO", "ICICIGI", "ICICIPRULI",
            "IGL", "INDUSTOWER", "IRCTC", "LICI", "LODHA",
            "LUPIN", "M&M", "MCDOWELL-N", "MUTHOOTFIN",
            "NAUKRI", "OFSS", "PAGEIND", "PIDILITIND",
            "POLYCAB", "RECLTD", "SHRIRAMFIN", "SIEMENS",
            "SRF", "TATACOMM", "TORNTPHARM", "TRENT",
            "TVSMOTOR", "UBL", "UNIONBANK", "VBL",
            "VEDL", "ZYDUSLIFE",
            // Additional quality midcaps
            "ABCAPITAL", "ABFRL", "ACC", "AIAENG", "AJANTPHARM",
            "ALKEM", "AMBUJACEM", "APLAPOLLO", "ASTRAL",
            "ATUL", "AUBANK", "AUROPHARMA", "AVANTIFEED",
            "BAJAJ-AUTO", "BALKRISIND", "BANDHANBNK",
            "BATAINDIA", "BAYERCROP", "BIKAJI", "BLUEDART",
            "BLUESTARCO", "BSOFT", "CAMS", "CANFINHOME",
            "CASTROLIND", "CEATLTD", "CENTRALBK", "CLEAN",
            "COFORGE", "CONCOR", "COROMANDEL", "CROMPTON",
            "CUMMINSIND", "CYIENT", "DALBHARAT",
            "DEEPAKNTR", "DELHIVERY", "DEVYANI", "DIXON",
            "DMART", "DODLA", "DRHORTON", "ECLERX",
            "EIDPARRY", "EIHOTEL", "ELGIEQUIP",
            "EMAMILTD", "ENDURANCE", "ENGINERSIN",
            "EQUITASBNK", "ESCORTS", "EXIDEIND",
            "FEDERALBNK", "FINCABLES", "FINPIPE",
            "FLUOROCHEM", "FORTIS", "GICRE", "GILLETTE",
            "GLAXO", "GLENMARK", "GMRINFRA", "GNFC",
            "GODFRYPHLP", "GODREJIND", "GODREJPROP",
            "GRANULES", "GRINFRA", "GSPL", "GUJGASLTD",
            "HAPPSTMNDS", "HBLPOWER", "HDFCAMC",
            "HDFCLIFE", "HEXAWARE", "HINDCOPPER",
            "HINDZINC", "HONAUT", "HUDCO",
            "IBREALEST", "IDBI", "IDFCFIRSTB",
            "IEX", "IFBIND", "IGL", "IIFL",
            "INDIANB", "INDIGO", "INDOSTAR",
            "INOXWIND", "INTELLECT", "IOB",
            "IPCALAB", "IRB", "IREDA", "IRFC",
            "ITC", "JBCHEPHARM", "JKCEMENT",
            "JKLAKSHMI", "JKPAPER", "JKTYRE",
            "JUBLFOOD", "JUBILANT", "JUSTDIAL",
            "KAJARIACER", "KALPATPOWR", "KANSAINER",
            "KARURVYSYA", "KEC", "KESORAMIND",
            "KFINTECH", "KOTAKBANK", "KPIL",
            "KRBL", "KSCL", "LALPATHLAB",
            "LATENTVIEW", "LAURUSLABS", "LICHSGFIN",
            "LINDEINDIA", "LTTS", "LUXIND",
            "M&MFIN", "MAHABANK", "MAHINDCIE",
            "MANAPPURAM", "MARICO", "MAXHEALTH",
            "MFSL", "MIDHANI", "MINDTREE",
            "MMTC", "MOTHERSON", "MPHASIS",
            "MRF", "NATCOPHARM", "NATIONALUM",
            "NAVINFLUOR", "NBCC", "NCC",
            "NHPC", "NLCINDIA", "NMDC",
            "NSLNISP", "NYKAA", "OIL",
            "OLECTRA", "PAYTM", "PCBL",
            "PERSISTENT", "PETRONET", "PFC",
            "PFIZER", "PHOENIXLTD", "PIIND",
            "PNBHOUSING", "POLICYBZR", "PRAJ",
            "PRESTIGE", "PRINCEPIPE", "PRSMJOHNSN",
            "PSUBANK", "PVRINOX", "RADICO",
            "RAILTEL", "RAJESHEXPO", "RALLIS",
            "RAMCOCEM", "RATNAMANI", "RAYMOND",
            "RBLBANK", "REDINGTON", "RELAXO",
            "RITES", "RVNL", "SAFARI",
            "SAIL", "SANOFI", "SAPPHIRE",
            "SAREGAMA", "SCHAEFFLER", "SEQUENT",
            "SHREECEM", "SHREDIGCEM", "SHYAMMETL",
            "SKFINDIA", "SOBHA", "SOLARA",
            "SONACOMS", "SPANDANA", "SPARC",
            "STAR", "STERLITE", "STOVEKRAFT",
            "SUMICHEM", "SUNDARMFIN", "SUNDRMFAST",
            "SUNTV", "SUPRAJIT", "SUPREMEIND",
            "SUZLON", "SWANENERGY", "SYMPHONY",
            "TANLA", "TATACHEM", "TATACOFFEE",
            "TATAELXSI", "TATAINVEST", "TATAMTRDVR",
            "TATAPOWER", "TATVA", "TCNSBRANDS",
            "TEAMLEASE", "THERMAX", "THYROCARE",
            "TIMKEN", "TITAGARH", "TORNTPOWER",
            "TTML", "TUDIP", "TVSHLTD",
            "UCOBANK", "UJJIVANSFB", "ULTRACEMCO",
            "UNIPARTS", "UNITDSPR", "UPL",
            "UTIAMC", "VAIBHAVGBL", "VAKRANGEE",
            "VARROC", "VGUARD", "VINATIORGA",
            "VOLTAS", "VSTIND", "WELCORP",
            "WELSPUNIND", "WHIRLPOOL", "WOCKPHARMA",
            "WONDERLA", "XCHANGING", "YESBANK",
            "ZEEL", "ZEEMEDIA", "ZENTEC", "ZENSARTECH"
        };

                // Only keep symbols that exist in our token map
                var validSymbols = tier1KnownSymbols
                    .Where(s => _symbolTokenMap.ContainsKey(s))
                    .Distinct()
                    .ToList();

                _logger.LogInformation(
                    "📋 Valid quality symbols: {count}", validSymbols.Count);

                // ── Step 3: Fundamental screening ────────────────────────
                _logger.LogInformation(
                    "📋 Step 2: Running fundamental screen " +
                    "on {count} stocks...", validSymbols.Count);

                var qualitySymbols = new List<string>();
                var fundScores = new Dictionary<string, double>();
                int processed = 0;

                foreach (var symbol in validSymbols)
                {
                    try
                    {
                        var fund = await _fundamental
                            .GetFundamentalsAsync(symbol);
                        _fundResults[symbol] = fund;

                        // Accept stocks even with partial data
                        // Score > 0 means we got some data
                        qualitySymbols.Add(symbol);
                        fundScores[symbol] = fund.Score;

                        processed++;
                        if (processed % 10 == 0)
                            _logger.LogInformation(
                                "📊 Screened {done}/{total} stocks...",
                                processed, validSymbols.Count);

                        // Respectful delay — avoid 429 rate limit
                        // 1.5 seconds between requests
                        await Task.Delay(800);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            "⚠️ Fundamental fetch failed for {s}: {m}",
                            symbol, ex.Message);
                        // Still add to quality list with default score
                        qualitySymbols.Add(symbol);
                        fundScores[symbol] = 50;
                    }
                }

                _allQualitySymbols = qualitySymbols;

                // ── Step 4: Assign tiers by fundamental score ─────────────
                var sorted = qualitySymbols
                    .OrderByDescending(s =>
                        fundScores.GetValueOrDefault(s))
                    .ToList();

                _tier1Symbols = sorted.Take(100).ToList();
                _tier2Symbols = sorted.Skip(100).Take(400).ToList();
                _lastFullScan = DateTime.Now;

                _logger.LogInformation(
                    "✅ Full scan complete: {total} quality stocks, " +
                    "{t1} Tier1, {t2} Tier2",
                    qualitySymbols.Count,
                    _tier1Symbols.Count,
                    _tier2Symbols.Count);

                // ── Step 5: Technical analysis on Tier 1 ─────────────────
                await RefreshTechnicalAnalysisAsync(
                    _tier1Symbols.Take(30).ToList());

                // ── Step 6: Generate recommendations ─────────────────────
                await RefreshRecommendationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Full scan failed");
            }
        }

        // ── Update live prices every 5 seconds ───────
        public async Task UpdateLivePricesAsync(
            List<string> symbols)
        {
            var tasks = symbols.Select(async symbol =>
            {
                if (!_symbolTokenMap.TryGetValue(symbol, out var token))
                    return;
                var price = await _angel.GetLivePriceAsync(
                    symbol, token);
                if (price != null)
                    _livePrices[symbol] = price;
            });

            await Task.WhenAll(tasks);
        }

        // ── Refresh technical analysis ────────────────
        public async Task RefreshTechnicalAnalysisAsync(
            List<string> symbols)
        {
            foreach (var symbol in symbols)
            {
                try
                {
                    if (!_symbolTokenMap.TryGetValue(
                        symbol, out var token)) continue;

                    var candles = await _angel.GetOhlcvAsync(
                        symbol, token, "ONE_DAY", 200);

                    if (candles.Count < 50) continue;

                    var tech = _technical.Compute(symbol, candles);
                    _techResults[symbol] = tech;

                    // Update composite score
                    var newsSentiment = _news
                        .GetSymbolSentiment(symbol);
                    var fundScore = _fundResults
                        .TryGetValue(symbol, out var f) ? f.Score : 50;

                    var score = _scoring.Compute(
                        symbol, tech.Score, fundScore, newsSentiment);
                    _scores[symbol] = score;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "⚠️ Technical analysis failed for {symbol}: {msg}",
                        symbol, ex.Message);
                }

                await Task.Delay(200); // Rate limit friendly
            }
        }

        // ── Generate Top 3 recommendations ───────────
        public async Task RefreshRecommendationsAsync()
        {
            try
            {
                // To this — fall back to fundamental scores if no technical:
                var topStocks = _fundResults
                    .Where(f => f.Value.Score > 0)
                    .Select(f => new KeyValuePair<string, CompositeScore>(
                        f.Key,
                        _scores.TryGetValue(f.Key, out var sc) ? sc :
                        new CompositeScore
                        {
                            Symbol = f.Key,
                            FundamentalScore = f.Value.Score,
                            TechnicalScore = 50,
                            NewsScore = 50,
                            FinalScore = f.Value.Score * 0.5 + 25
                        }))
                    .OrderByDescending(s => s.Value.FinalScore)
                    .Take(10)
                    .ToList();

                var recommendations = new List<Recommendation>();
                int rank = 1;

                foreach (var item in topStocks.Take(3))
                {
                    var symbol = item.Key;
                    var score = item.Value;

                    // Get stock info
                    var price = _livePrices.GetValueOrDefault(symbol);
                    var stock = new StockInfo
                    {
                        Symbol = symbol,
                        LastPrice = price?.LTP ?? 0,
                        Change = price?.Change ?? 0,
                        ChangePercent = price?.ChangePercent ?? 0,
                        High = price?.High ?? 0,
                        Low = price?.Low ?? 0,
                        Volume = price?.Volume ?? 0
                    };

                    var tech = _techResults.GetValueOrDefault(symbol)
                        ?? new TechnicalResult { Symbol = symbol };
                    var fund = _fundResults.GetValueOrDefault(symbol)
                        ?? new FundamentalResult { Symbol = symbol };
                    var relatedNews = _news.GetNewsForSymbol(symbol);

                    // Get AI analysis
                    var ai = await _ai.AnalyzeStockAsync(
                        stock, tech, fund, relatedNews, score);

                    recommendations.Add(new Recommendation
                    {
                        Rank = rank++,
                        Stock = stock,
                        Technical = tech,
                        Fundamental = fund,
                        Score = score,
                        AiAnalysis = ai,
                        RelatedNews = relatedNews,
                        GeneratedAt = DateTime.Now
                    });
                }

                _recommendations = recommendations;
                _logger.LogInformation(
                        "✅ Recommendations updated: {symbols}",
                        string.Join(", ",
                            recommendations.Select(r => r.Stock.Symbol)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "❌ Failed to refresh recommendations");
            }
        }

        // ── Get all NSE+BSE symbols ───────────────────
        private async Task<List<string>> GetAllNseBseSymbolsAsync()
        {
            // Angel One provides a full instrument list
            // We just return the keys from token map
            // which is populated from Angel One master list
            return _symbolTokenMap.Keys.ToList();
        }

        // ── Public getters ────────────────────────────
        public List<Recommendation> GetRecommendations()
            => _recommendations;

        public List<LivePrice> GetLivePrices()
            => _livePrices.Values.ToList();

        public LivePrice? GetLivePrice(string symbol)
            => _livePrices.GetValueOrDefault(symbol);

        public TechnicalResult? GetTechnical(string symbol)
            => _techResults.GetValueOrDefault(symbol);

        public FundamentalResult? GetFundamental(string symbol)
            => _fundResults.GetValueOrDefault(symbol);

        public CompositeScore? GetScore(string symbol)
            => _scores.GetValueOrDefault(symbol);

        public List<string> GetTier1Symbols() => _tier1Symbols;
        public List<string> GetTier2Symbols() => _tier2Symbols;
        public List<string> GetAllSymbols()
            => _symbolTokenMap.Keys.ToList();

        public Dictionary<string, CompositeScore> GetAllScores()
            => _scores;
    }
}
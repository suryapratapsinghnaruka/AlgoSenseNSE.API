using AlgoSenseNSE.API.Models;

namespace AlgoSenseNSE.API.Services
{
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

            await _angel.LoginAsync();

            var allSymbols = new List<string>();
            _symbolTokenMap = await _angel.GetSymbolTokenMapAsync(allSymbols);

            await RunFullDailyScanAsync();

            _logger.LogInformation(
                "✅ Initialization complete. Tracking {t1} Tier1, {t2} Tier2 stocks",
                _tier1Symbols.Count, _tier2Symbols.Count);
        }

        // ── Full daily scan ───────────────────────────
        public async Task RunFullDailyScanAsync()
        {
            _logger.LogInformation("🔍 Running full daily market scan...");

            try
            {
                var equitySymbols = _symbolTokenMap.Keys
                    .Where(s =>
                        !s.Contains(" ") && !s.Contains("-") &&
                        !s.Contains("&") && !s.EndsWith("BE") &&
                        !s.EndsWith("BL") && !s.EndsWith("GS") &&
                        s.Length >= 2 && s.Length <= 20 &&
                        !s.StartsWith("Nifty") && !s.StartsWith("NIFTY") &&
                        !s.StartsWith("SENSEX") && !s.StartsWith("India") &&
                        char.IsLetter(s[0]))
                    .ToList();

                _logger.LogInformation(
                    "📊 Filtered to {count} equity symbols from {total} total instruments",
                    equitySymbols.Count, _symbolTokenMap.Count);

                var tier1KnownSymbols = new List<string>
                {
                    // Nifty 50
                    "RELIANCE","TCS","HDFCBANK","INFY","ICICIBANK",
                    "HINDUNILVR","BAJFINANCE","SBIN","BHARTIARTL",
                    "KOTAKBANK","AXISBANK","LT","MARUTI","SUNPHARMA",
                    "TITAN","WIPRO","DRREDDY","TATASTEEL","ONGC",
                    "NTPC","POWERGRID","TECHM","DIVISLAB","NESTLEIND",
                    "ULTRACEMCO","ASIANPAINT","COALINDIA","BPCL",
                    "IOC","GRASIM","ADANIPORTS","TATACONSUM","BAJAJFINSV",
                    "HCLTECH","INDUSINDBK","JSWSTEEL","BRITANNIA",
                    "CIPLA","EICHERMOT",
                    // Nifty Next 50
                    "ZOMATO","ADANIENT","ADANIGREEN","ADANIPOWER",
                    "APOLLOHOSP","BANKBARODA","BERGEPAINT","BEL",
                    "BHEL","BOSCHLTD","CANBK","CHOLAFIN","COLPAL",
                    "DABUR","DLF","GAIL","GODREJCP","HAVELLS",
                    "HEROMOTOCO","HINDALCO","ICICIGI","ICICIPRULI",
                    "IGL","INDUSTOWER","IRCTC","LICI","LODHA",
                    "LUPIN","M&M","MUTHOOTFIN",
                    "NAUKRI","OFSS","PAGEIND","PIDILITIND",
                    "POLYCAB","RECLTD","SHRIRAMFIN","SIEMENS",
                    "SRF","TATACOMM","TORNTPHARM","TRENT",
                    "TVSMOTOR","UBL","UNIONBANK","VBL",
                    "VEDL","ZYDUSLIFE",
                    // Quality midcaps
                    "ABCAPITAL","ABFRL","ACC","AIAENG","AJANTPHARM",
                    "ALKEM","AMBUJACEM","APLAPOLLO","ASTRAL",
                    "ATUL","AUBANK","AUROPHARMA","AVANTIFEED",
                    "BALKRISIND","BANDHANBNK","BATAINDIA","BAYERCROP",
                    "BIKAJI","BLUEDART","BLUESTARCO","BSOFT","CAMS",
                    "CANFINHOME","CASTROLIND","CEATLTD","CENTRALBK",
                    "CLEAN","COFORGE","CONCOR","COROMANDEL","CROMPTON",
                    "CUMMINSIND","CYIENT","DALBHARAT","DEEPAKNTR",
                    "DELHIVERY","DEVYANI","DIXON","DMART","DODLA",
                    "ECLERX","EIDPARRY","EIHOTEL","ELGIEQUIP",
                    "EMAMILTD","ENDURANCE","ENGINERSIN","EQUITASBNK",
                    "ESCORTS","EXIDEIND","FEDERALBNK","FINCABLES",
                    "FINPIPE","FLUOROCHEM","FORTIS","GICRE","GILLETTE",
                    "GLAXO","GLENMARK","GNFC","GODFRYPHLP","GODREJIND",
                    "GODREJPROP","GRANULES","GRINFRA","GSPL","GUJGASLTD",
                    "HAPPSTMNDS","HDFCAMC","HDFCLIFE","HINDCOPPER",
                    "HINDZINC","HONAUT","HUDCO","IDBI","IDFCFIRSTB",
                    "IEX","IFBIND","IIFL","INDIANB","INDIGO","INDOSTAR",
                    "INOXWIND","INTELLECT","IOB","IPCALAB","IRB",
                    "IREDA","IRFC","ITC","JBCHEPHARM","JKCEMENT",
                    "JKLAKSHMI","JKPAPER","JKTYRE","JUBLFOOD","JUSTDIAL",
                    "KAJARIACER","KANSAINER","KARURVYSYA","KEC",
                    "KESORAMIND","KFINTECH","KPIL","KRBL","KSCL",
                    "LALPATHLAB","LATENTVIEW","LAURUSLABS","LICHSGFIN",
                    "LINDEINDIA","LTTS","LUXIND","M&MFIN","MAHABANK",
                    "MANAPPURAM","MARICO","MAXHEALTH","MFSL","MIDHANI",
                    "MMTC","MOTHERSON","MPHASIS","MRF","NATCOPHARM",
                    "NATIONALUM","NAVINFLUOR","NBCC","NCC","NHPC",
                    "NLCINDIA","NMDC","NSLNISP","NYKAA","OIL",
                    "OLECTRA","PAYTM","PCBL","PERSISTENT","PETRONET",
                    "PFC","PFIZER","PHOENIXLTD","PIIND","PNBHOUSING",
                    "POLICYBZR","PRESTIGE","PRINCEPIPE","PRSMJOHNSN",
                    "PSUBANK","PVRINOX","RADICO","RAILTEL","RAJESHEXPO",
                    "RALLIS","RAMCOCEM","RATNAMANI","RAYMOND","RBLBANK",
                    "REDINGTON","RELAXO","RITES","RVNL","SAFARI",
                    "SAIL","SANOFI","SAPPHIRE","SAREGAMA","SCHAEFFLER",
                    "SHREECEM","SHREDIGCEM","SHYAMMETL","SKFINDIA",
                    "SOBHA","SOLARA","SONACOMS","SPANDANA","SPARC",
                    "STAR","STOVEKRAFT","SUMICHEM","SUNDARMFIN",
                    "SUNDRMFAST","SUNTV","SUPRAJIT","SUPREMEIND",
                    "SUZLON","SYMPHONY","TANLA","TATACHEM","TATAELXSI",
                    "TATAINVEST","TATAPOWER","TATVA","TEAMLEASE",
                    "THERMAX","THYROCARE","TIMKEN","TITAGARH","TORNTPOWER",
                    "TTML","TVSHLTD","UCOBANK","UJJIVANSFB","UNIPARTS",
                    "UNITDSPR","UPL","UTIAMC","VAIBHAVGBL","VAKRANGEE",
                    "VARROC","VGUARD","VINATIORGA","VOLTAS","VSTIND",
                    "WELCORP","WHIRLPOOL","WOCKPHARMA","WONDERLA",
                    "XCHANGING","YESBANK","ZEEL","ZEEMEDIA","ZENTEC","ZENSARTECH"
                };

                var validSymbols = tier1KnownSymbols
                    .Where(s => _symbolTokenMap.ContainsKey(s))
                    .Distinct()
                    .ToList();

                _logger.LogInformation("📋 Valid quality symbols: {count}", validSymbols.Count);

                // ── Fundamental screening ─────────────────
                _logger.LogInformation("📋 Step 2: Running fundamental screen on {count} stocks...",
                    validSymbols.Count);

                var qualitySymbols = new List<string>();
                var fundScores = new Dictionary<string, double>();
                int processed = 0;

                foreach (var symbol in validSymbols)
                {
                    try
                    {
                        var fund = await _fundamental.GetFundamentalsAsync(symbol);
                        _fundResults[symbol] = fund;
                        qualitySymbols.Add(symbol);
                        fundScores[symbol] = fund.Score;

                        processed++;
                        if (processed % 10 == 0)
                            _logger.LogInformation("📊 Screened {done}/{total} stocks...",
                                processed, validSymbols.Count);

                        await Task.Delay(800);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("⚠️ Fundamental fetch failed for {s}: {m}",
                            symbol, ex.Message);
                        qualitySymbols.Add(symbol);
                        fundScores[symbol] = 50;
                    }
                }

                _allQualitySymbols = qualitySymbols;

                var sorted = qualitySymbols
                    .OrderByDescending(s => fundScores.GetValueOrDefault(s))
                    .ToList();

                _tier1Symbols = sorted.Take(100).ToList();
                _tier2Symbols = sorted.Skip(100).Take(400).ToList();
                _lastFullScan = DateTime.Now;

                _logger.LogInformation(
                    "✅ Full scan complete: {total} quality stocks, {t1} Tier1, {t2} Tier2",
                    qualitySymbols.Count, _tier1Symbols.Count, _tier2Symbols.Count);

                // Technical analysis on top 30 by fundamental score
                await RefreshTechnicalAnalysisAsync(_tier1Symbols.Take(30).ToList());

                await RefreshRecommendationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Full scan failed");
            }
        }

        // ── Update live prices — SEQUENTIAL not parallel ──
        // Parallel was causing 100 concurrent API calls → rate limit storm
        public async Task UpdateLivePricesAsync(List<string> symbols)
        {
            // Only update in batches of 10 with throttling between each
            foreach (var symbol in symbols)
            {
                if (!_symbolTokenMap.TryGetValue(symbol, out var token)) continue;

                var price = await _angel.GetLivePriceAsync(symbol, token);
                if (price != null)
                    _livePrices[symbol] = price;

                // ThrottleAsync inside GetLivePriceAsync handles pacing
                // but add a tiny extra delay between symbols
                await Task.Delay(50);
            }
        }

        // ── Refresh technical analysis ────────────────
        public async Task RefreshTechnicalAnalysisAsync(List<string> symbols)
        {
            foreach (var symbol in symbols)
            {
                try
                {
                    if (!_symbolTokenMap.TryGetValue(symbol, out var token)) continue;

                    var candles = await _angel.GetOhlcvAsync(symbol, token, "ONE_DAY", 200);

                    if (candles.Count < 50)
                    {
                        _logger.LogDebug(
                            "⚠️ Not enough candles for {sym}: {count} — skipping technical",
                            symbol, candles.Count);

                        // Still compute score using fundamentals only
                        var fundScore = _fundResults.TryGetValue(symbol, out var f)
                            ? f.Score : 50;
                        var newsSentiment = _news.GetSymbolSentiment(symbol);
                        var score = _scoring.Compute(symbol, 50, fundScore, newsSentiment);
                        _scores[symbol] = score;
                        continue;
                    }

                    var tech = _technical.Compute(symbol, candles);
                    _techResults[symbol] = tech;

                    var newsSent = _news.GetSymbolSentiment(symbol);
                    var fundScr = _fundResults.TryGetValue(symbol, out var fr) ? fr.Score : 50;
                    var compositeScore = _scoring.Compute(symbol, tech.Score, fundScr, newsSent);
                    _scores[symbol] = compositeScore;

                    // Small delay between OHLCV fetches
                    await Task.Delay(600);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("⚠️ Technical analysis failed for {sym}: {msg}",
                        symbol, ex.Message);
                }
            }
        }

        // ── Generate Top 3 recommendations ───────────
        public async Task RefreshRecommendationsAsync()
        {
            try
            {
                // Merge fundamental-only scores for stocks without technical
                var allScored = _fundResults
                    .Where(f => f.Value.Score > 0)
                    .Select(f =>
                    {
                        if (_scores.TryGetValue(f.Key, out var sc)) return (f.Key, sc);
                        // No technical yet — use fundamental score at 70% weight
                        var newsSentiment = _news.GetSymbolSentiment(f.Key);
                        var ns = 50 + (newsSentiment * 40);
                        var fs = f.Value.Score * 0.7 + ns * 0.3;
                        return (f.Key, new CompositeScore
                        {
                            Symbol = f.Key,
                            FundamentalScore = f.Value.Score,
                            TechnicalScore = 50,
                            NewsScore = Math.Max(0, Math.Min(100, ns)),
                            FinalScore = Math.Round(fs, 1)
                        });
                    })
                    .OrderByDescending(x => x.Item2.FinalScore)
                    .Take(10)
                    .ToList();

                var recommendations = new List<Recommendation>();
                int rank = 1;

                foreach (var (symbol, score) in allScored.Take(3))
                {
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
                        ?? new TechnicalResult { Symbol = symbol, Score = 50 };
                    var fund = _fundResults.GetValueOrDefault(symbol)
                        ?? new FundamentalResult { Symbol = symbol };
                    var relatedNews = _news.GetNewsForSymbol(symbol);

                    var ai = await _ai.AnalyzeStockAsync(stock, tech, fund, relatedNews, score);

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
                _logger.LogInformation("✅ Recommendations updated: {symbols}",
                    string.Join(", ", recommendations.Select(r => r.Stock.Symbol)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to refresh recommendations");
            }
        }

        // ── Public getters ────────────────────────────
        public List<Recommendation> GetRecommendations() => _recommendations;
        public List<LivePrice> GetLivePrices() => _livePrices.Values.ToList();
        public LivePrice? GetLivePrice(string symbol) => _livePrices.GetValueOrDefault(symbol);
        public TechnicalResult? GetTechnical(string symbol) => _techResults.GetValueOrDefault(symbol);
        public FundamentalResult? GetFundamental(string symbol) => _fundResults.GetValueOrDefault(symbol);
        public CompositeScore? GetScore(string symbol) => _scores.GetValueOrDefault(symbol);
        public List<string> GetTier1Symbols() => _tier1Symbols;
        public List<string> GetTier2Symbols() => _tier2Symbols;
        public List<string> GetAllSymbols() => _symbolTokenMap.Keys.ToList();
        public Dictionary<string, CompositeScore> GetAllScores() => _scores;
    }
}
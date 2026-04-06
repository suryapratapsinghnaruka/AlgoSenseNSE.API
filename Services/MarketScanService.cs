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
        private readonly StockScreenerService _screener;
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

        // ── Fundamental cache ─────────────────────────
        // Prevents re-scraping Screener.in when new stocks
        // enter the dynamic universe mid-day
        private readonly Dictionary<string, FundamentalResult> _fundCache = new();
        private DateTime _fundCacheDate = DateTime.MinValue;

        public MarketScanService(
            AngelOneService angel,
            TechnicalAnalysisService technical,
            FundamentalService fundamental,
            NewsService news,
            ScoringEngine scoring,
            ClaudeAiService ai,
            StockScreenerService screener,
            ILogger<MarketScanService> logger,
            IConfiguration config)
        {
            _angel = angel; _technical = technical;
            _fundamental = fundamental; _news = news;
            _scoring = scoring; _ai = ai;
            _screener = screener;
            _logger = logger; _config = config;
        }

        // ── Initialize on startup ─────────────────────
        public async Task InitializeAsync()
        {
            _logger.LogInformation("🚀 Initializing AlgoSense Intraday...");
            await _angel.LoginAsync();
            _symbolTokenMap = await _angel
                .GetSymbolTokenMapAsync(new List<string>());
            await RunFullDailyScanAsync();
            _logger.LogInformation(
                "✅ Ready. Tracking {n} intraday stocks",
                _tier1Symbols.Count);
        }

        // ── Full daily scan ───────────────────────────
        public async Task RunFullDailyScanAsync()
        {
            _logger.LogInformation("🔍 Running intraday market scan...");
            try
            {
                // ── Dynamic universe ──────────────────
                // Get all stocks affordable with current capital.
                // Replaces hardcoded 53-stock list.
                // At ₹1500 → stocks up to ₹180 (afford 5+ shares)
                // At ₹5000 → stocks up to ₹600 (scales automatically)
                var screenerResults = _screener.Screen();

                List<string> symbolsToScan;

                if (screenerResults.Any())
                {
                    // Tier 1: top 80 by volume — get deep analysis
                    symbolsToScan = screenerResults
                        .Where(s => s.Tier == 1 &&
                               _symbolTokenMap.ContainsKey(s.Symbol))
                        .Select(s => s.Symbol)
                        .Take(80)
                        .ToList();

                    _logger.LogInformation(
                        "📋 Dynamic universe: {total} affordable stocks " +
                        "→ {t1} selected for deep analysis",
                        screenerResults.Count, symbolsToScan.Count);
                }
                else
                {
                    // Fallback: screener has no ticks yet (before market open)
                    // Filter out indices and ETFs — they have spaces,
                    // long names, or known keywords
                    var skipWords = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        "NIFTY","SENSEX","BANKNIFTY","FINNIFTY","VIX",
                        "BANKEX","LIQUIDBEES","JUNIORBEES","NIFTYBEES",
                        "SETFNIF","ICICIB","HDFCNIF"
                    };

                    symbolsToScan = _symbolTokenMap.Keys
                        .Where(s => !s.Contains(' '))       // indices have spaces
                        .Where(s => !s.Contains('.'))       // some ETFs have dots
                        .Where(s => s.Length <= 12)         // indices have long names
                        .Where(s => char.IsLetter(s[0]))    // skip 011NSETEST etc.
                        .Where(s => !s.Contains("TEST",
                            StringComparison.OrdinalIgnoreCase))
                        .Where(s => !skipWords.Any(w =>
                            s.StartsWith(w,
                            StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(s => s)
                        .Take(80)
                        .ToList();

                    _logger.LogInformation(
                        "📋 Pre-market fallback: {n} equity symbols queued",
                        symbolsToScan.Count);
                }

                // Reset fundamental cache daily
                if (_fundCacheDate.Date != DateTime.Today)
                {
                    _fundCache.Clear();
                    _fundCacheDate = DateTime.Today;
                    _logger.LogInformation("🔄 Fundamental cache reset for new day");
                }

                _logger.LogInformation(
                    "📋 Running fundamental screen on {n} stocks...",
                    symbolsToScan.Count);

                int done = 0;
                foreach (var sym in symbolsToScan)
                {
                    try
                    {
                        // Use cache if already scanned today
                        if (_fundCache.TryGetValue(sym, out var cached))
                        {
                            _fundResults[sym] = cached;
                            done++;
                            continue;
                        }

                        var fund = await _fundamental.GetFundamentalsAsync(sym);
                        _fundResults[sym] = fund;
                        _fundCache[sym]   = fund; // cache for today

                        done++;
                        if (done % 10 == 0)
                            _logger.LogInformation(
                                "📊 Screened {d}/{t}...",
                                done, symbolsToScan.Count);

                        await Task.Delay(800); // Screener.in rate limit
                    }
                    catch
                    {
                        var fallback = new FundamentalResult
                        { Symbol = sym, Score = 50 };
                        _fundResults[sym] = fallback;
                        _fundCache[sym]   = fallback;
                    }
                }

                // All go to tier1 for monitoring
                _tier1Symbols      = symbolsToScan.Take(50).ToList();
                _tier2Symbols      = symbolsToScan.Skip(50).ToList();
                _allQualitySymbols = symbolsToScan;

                _logger.LogInformation(
                    "✅ Scan complete: {t1} Tier1 | {t2} Tier2 stocks",
                    _tier1Symbols.Count, _tier2Symbols.Count);

                // First technical scan if market is open
                if (IsMarketOpen())
                    await RefreshIntradaySignalsAsync(
                        _tier1Symbols.Take(30).ToList());

                await RefreshRecommendationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Full scan failed");
            }
        }

        // ── 5-min candle technical refresh ───────────
        public async Task RefreshIntradaySignalsAsync(List<string> symbols)
        {
            var screenerTier1 = _screener.GetTier1Symbols(50);
            var effectiveSymbols = screenerTier1.Any()
                ? screenerTier1
                    .Where(s => _symbolTokenMap.ContainsKey(s))
                    .Take(30)
                    .ToList()
                : symbols;

            // ── MiroFish: Pre-scan crowd behavior detection ──
            // Identify stocks moving AGAINST the market trend (defensive plays)
            // These are the best opportunities in PANIC/bearish days
            var allPrices = _livePrices.Values.ToList();
            if (allPrices.Count > 10)
            {
                double avgChange = allPrices
                    .Where(p => p.ChangePercent != 0)
                    .Average(p => p.ChangePercent);
                var crowdOutliers = allPrices
                    .Where(p => p.ChangePercent > avgChange + 0.5) // beating the market
                    .Select(p => p.Symbol)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Boost priority for market-beating stocks — add them to front of list
                var boosted = effectiveSymbols
                    .OrderByDescending(s => crowdOutliers.Contains(s) ? 1 : 0)
                    .ToList();

                if (crowdOutliers.Count > 0)
                    _logger.LogInformation(
                        "🐟 MiroFish crowd outliers (beating market): {syms}",
                        string.Join(",", crowdOutliers.Take(5)));

                effectiveSymbols = boosted;
            }

            _logger.LogInformation(
                "📈 Refreshing 5-min signals for {n} stocks...",
                effectiveSymbols.Count);

            _ai.ClearAllCache();

            foreach (var sym in effectiveSymbols)
            {
                try
                {
                    if (!_symbolTokenMap.TryGetValue(sym, out var token))
                        continue;

                    // Fetch 5 days of 5-min candles (~375 bars)
                    var candles = await _angel.GetOhlcvAsync(
                        sym, token, "FIVE_MINUTE", 5);

                    if (candles.Count < 30)
                    {
                        var fs = _fundResults
                            .TryGetValue(sym, out var f) ? f.Score : 50;
                        var ns = _news.GetSymbolSentiment(sym);
                        var newsScore = Math.Max(0, Math.Min(100,
                            50 + ns * 40));

                        _scores[sym] = new CompositeScore
                        {
                            Symbol           = sym,
                            TechnicalScore   = 50,
                            FundamentalScore = fs,
                            NewsScore        = newsScore,
                            // v3 weights: Tech 75%, Fund 10%, News 15%
                            FinalScore       = Math.Round(
                                50   * 0.75 +
                                fs   * 0.10 +
                                newsScore * 0.15, 1),
                            CalculatedAt = DateTime.Now
                        };
                        continue;
                    }

                    // Compute indicators
                    var tech = _technical.Compute(sym, candles);
                    _techResults[sym] = tech;

                    // Composite score — v3 weights
                    var newsSent  = _news.GetSymbolSentiment(sym);
                    var fundScore = _fundResults
                        .TryGetValue(sym, out var fr) ? fr.Score : 50;
                    var nScore = Math.Max(0, Math.Min(100,
                        50 + newsSent * 40));

                    _scores[sym] = _scoring.Compute(
                        sym, tech.Score, fundScore, newsSent);

                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "⚠️ Signal failed for {sym}: {msg}",
                        sym, ex.Message);
                }
            }

            _logger.LogInformation(
                "✅ Intraday signals updated for {n} stocks",
                effectiveSymbols.Count);
        }

        // ── Legacy alias ──────────────────────────────
        public async Task RefreshTechnicalAnalysisAsync(List<string> symbols)
            => await RefreshIntradaySignalsAsync(symbols);

        // ── Live prices ───────────────────────────────
        public async Task UpdateLivePricesAsync(List<string> symbols)
        {
            foreach (var sym in symbols)
            {
                if (!_symbolTokenMap.TryGetValue(sym, out var token))
                    continue;
                var price = await _angel.GetLivePriceAsync(sym, token);
                if (price != null) _livePrices[sym] = price;
                await Task.Delay(50);
            }
        }

        // ── Top recommendations ───────────────────────
        public async Task RefreshRecommendationsAsync()
        {
            try
            {
                // ── MiroFish-inspired crowd behavior filter ────────────
                // Before ranking, detect sector clustering (PSU banks dominating every day).
                // Apply peer-group volume surge bonus: if multiple stocks in same sector
                // are surging together = institutional rotation = stronger signal.
                // Then enforce sector diversity: max 2 per sector in top 10.

                var sectorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    {"CANBK","PSUBank"},{"UNIONBANK","PSUBank"},{"MAHABANK","PSUBank"},
                    {"CENTRALBK","PSUBank"},{"BANKBARODA","PSUBank"},{"IOB","PSUBank"},
                    {"PNB","PSUBank"},{"INDIANB","PSUBank"},{"SBIN","PSUBank"},
                    {"INFY","IT"},{"TCS","IT"},{"WIPRO","IT"},{"HCLTECH","IT"},{"TECHM","IT"},
                    {"ONGC","Energy"},{"BPCL","Energy"},{"IOC","Energy"},{"GAIL","Energy"},
                    {"COALINDIA","Energy"},{"NTPC","Energy"},
                    {"TATASTEEL","Metals"},{"HINDALCO","Metals"},{"NMDC","Metals"},
                    {"NATIONALUM","Metals"},{"JSWSTEEL","Metals"},
                    {"SUNPHARMA","Pharma"},{"CIPLA","Pharma"},{"DRREDDY","Pharma"},
                    {"RECLTD","Finance"},{"PFC","Finance"},{"IRFC","Finance"},
                    {"SUZLON","Renewables"},{"IREDA","Renewables"},{"NHPC","Power"},
                };

                // Count sector volume surge (MiroFish crowd detection)
                var sectorVolumeSurge = new Dictionary<string, int>();
                foreach (var kv in _techResults)
                {
                    var sector = sectorMap.GetValueOrDefault(kv.Key, "Other");
                    var lp = _livePrices.GetValueOrDefault(kv.Key);
                    if (kv.Value.SupertrendBullish && lp?.LTP > 0)
                    {
                        sectorVolumeSurge.TryGetValue(sector, out int cnt);
                        sectorVolumeSurge[sector] = cnt + 1;
                    }
                }

                var ranked = _scores
                    .Where(s => s.Value.FinalScore > 0)
                    .Select(s =>
                    {
                        var tech   = _techResults.GetValueOrDefault(s.Key);
                        var lp     = _livePrices.GetValueOrDefault(s.Key);
                        var sector = sectorMap.GetValueOrDefault(s.Key, "Other");

                        double bonus = 0;
                        if (tech?.SupertrendBullish == true) bonus += 5;
                        if (tech != null && lp != null &&
                            lp.LTP > tech.VWAP && tech.VWAP > 0) bonus += 5;
                        if (tech?.ADX > 25) bonus += 3;
                        if (tech?.ADX > 40) bonus += 3; // very strong trend bonus

                        // MiroFish: sector rotation bonus — if 3+ stocks in sector surging
                        // = institutional crowd behavior = add bonus to ALL stocks in sector
                        if (sectorVolumeSurge.GetValueOrDefault(sector, 0) >= 3) bonus += 4;

                        // Sector diversity penalty: penalise PSU banks if already 2 in list
                        // (handled below during selection, not here)

                        return (Symbol: s.Key, Score: s.Value,
                            AdjScore: s.Value.FinalScore + bonus,
                            Sector: sector);
                    })
                    .OrderByDescending(x => x.AdjScore)
                    .Take(30) // take 30 before diversity filter
                    .ToList();

                // Apply sector diversity: max 2 per sector in final top 10
                var sectorCount = new Dictionary<string, int>();
                var diverseRanked = new List<(string Symbol, CompositeScore Score, double AdjScore, string Sector)>();
                foreach (var item in ranked)
                {
                    sectorCount.TryGetValue(item.Sector, out int sc);
                    int maxPerSector = item.Sector == "PSUBank" ? 2 : 3; // stricter for PSU banks
                    if (sc < maxPerSector)
                    {
                        diverseRanked.Add(item);
                        sectorCount[item.Sector] = sc + 1;
                        if (diverseRanked.Count >= 10) break;
                    }
                }

                _logger.LogInformation(
                    "📊 Sector diversity applied: {sectors}",
                    string.Join(", ", sectorCount.Select(kv => $"{kv.Key}:{kv.Value}")));

                var finalRanked = diverseRanked.Take(10).ToList();

                var recs = new List<Recommendation>();
                int rank = 1;

                foreach (var item in finalRanked.Take(5))
                {
                    var sym   = item.Symbol;
                    var score = item.Score;
                    var lp    = _livePrices.GetValueOrDefault(sym);

                    var stock = new StockInfo
                    {
                        Symbol        = sym,
                        LastPrice     = lp?.LTP           ?? 0,
                        Change        = lp?.Change        ?? 0,
                        ChangePercent = lp?.ChangePercent ?? 0,
                        High          = lp?.High          ?? 0,
                        Low           = lp?.Low           ?? 0,
                        Volume        = lp?.Volume        ?? 0
                    };

                    var tech = _techResults.GetValueOrDefault(sym)
                             ?? new TechnicalResult
                                { Symbol = sym, Score = 50 };
                    var fund = _fundResults.GetValueOrDefault(sym)
                             ?? new FundamentalResult { Symbol = sym };
                    var news = _news.GetNewsForSymbol(sym);
                    var ai   = await _ai.AnalyzeStockAsync(
                        stock, tech, fund, news, score);

                    recs.Add(new Recommendation
                    {
                        Rank        = rank++,
                        Stock       = stock,
                        Technical   = tech,
                        Fundamental = fund,
                        Score       = score,
                        AiAnalysis  = ai,
                        RelatedNews = news,
                        GeneratedAt = DateTime.Now
                    });
                }

                _recommendations = recs;
                _logger.LogInformation("✅ Picks: {syms}",
                    string.Join(", ", recs.Select(r =>
                        $"{r.Stock.Symbol}({r.AiAnalysis?.Recommendation})")));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ RefreshRecommendations failed");
            }
        }

        private bool IsMarketOpen()
        {
            try
            {
                var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById(
                        "India Standard Time"));
                if (now.DayOfWeek == DayOfWeek.Saturday ||
                    now.DayOfWeek == DayOfWeek.Sunday) return false;
                return now.TimeOfDay >= new TimeSpan(9, 15, 0) &&
                       now.TimeOfDay <= new TimeSpan(15, 30, 0);
            }
            catch
            {
                var now = DateTime.UtcNow.AddHours(5).AddMinutes(30);
                if (now.DayOfWeek == DayOfWeek.Saturday ||
                    now.DayOfWeek == DayOfWeek.Sunday) return false;
                return now.Hour >= 9 && now.Hour < 16;
            }
        }

        // ── Public getters ────────────────────────────
        public List<Recommendation> GetRecommendations() => _recommendations;
        public List<LivePrice> GetLivePrices()           => _livePrices.Values.ToList();
        public LivePrice? GetLivePrice(string s)         => _livePrices.GetValueOrDefault(s);
        public TechnicalResult? GetTechnical(string s)   => _techResults.GetValueOrDefault(s);
        public FundamentalResult? GetFundamental(string s)=> _fundResults.GetValueOrDefault(s);
        public CompositeScore? GetScore(string s)        => _scores.GetValueOrDefault(s);
        public List<string> GetTier1Symbols()            => _tier1Symbols;
        public List<string> GetTier2Symbols()            => _tier2Symbols;
        public List<string> GetAllSymbols()              => _symbolTokenMap.Keys.ToList();
        public Dictionary<string, CompositeScore> GetAllScores() => _scores;

        public bool PicksLockedToday                     => false;
        public DateTime LockedAt                         => DateTime.MinValue;
        public List<Recommendation> GetLockedPicks()     => _recommendations;
        public List<EntryTrigger> GetEntryTriggers()     => new List<EntryTrigger>();
    }
}

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

        // ── Best liquid intraday stocks ───────────────
        // Chosen for: high daily volume, ₹100-₹1000 price range,
        // tight spreads, clear trends on 5-min charts
        private readonly List<string> _intradayStocks = new()
        {
            // Nifty 50 blue chips — most liquid
            "SBIN", "BANKBARODA", "CANBK", "UNIONBANK", "MAHABANK",
            "CENTRALBK", "IOB", "INDIANB", "PNB",
            // PSU banks best for small capital intraday
            // (₹50-₹250 range → can buy 5-15 shares with ₹1500)

            // Energy & commodity — strong intraday movers
            "COALINDIA", "ONGC", "BPCL", "IOC", "GAIL",
            "NMDC", "NATIONALUM", "HINDALCO", "TATASTEEL",

            // Large cap IT — good gap plays
            "WIPRO", "HCLTECH", "TECHM", "INFY", "TCS",

            // Pharma momentum
            "SUNPHARMA", "CIPLA", "DRREDDY", "LUPIN",

            // Infra & power
            "NTPC", "POWERGRID", "NHPC", "IRFC", "RVNL",
            "RECLTD", "PFC", "IREDA",

            // High momentum midcaps
            "IRCTC", "IEX", "SUZLON", "ADANIPORTS",
            "ADANIPOWER", "TATAPOWER", "BEL",

            // FMCG
            "ITC", "EMAMILTD", "MARICO",

            // Finance
            "MUTHOOTFIN", "MANAPPURAM", "LICHSGFIN",

            // Misc liquid
            "LICI", "INDUSTOWER", "PETRONET",
            "COLPAL", "HINDUNILVR",
        };

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
            _angel = angel; _technical = technical;
            _fundamental = fundamental; _news = news;
            _scoring = scoring; _ai = ai;
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
                // Filter to stocks in token map
                var validStocks = _intradayStocks
                    .Where(s => _symbolTokenMap.ContainsKey(s))
                    .Distinct().ToList();

                _logger.LogInformation(
                    "📋 Valid intraday stocks: {n}", validStocks.Count);

                // Fundamental screen (for context)
                _logger.LogInformation(
                    "📋 Running fundamental screen on {n} stocks...",
                    validStocks.Count);

                int done = 0;
                foreach (var sym in validStocks)
                {
                    try
                    {
                        var fund = await _fundamental.GetFundamentalsAsync(sym);
                        _fundResults[sym] = fund;
                        done++;
                        if (done % 10 == 0)
                            _logger.LogInformation(
                                "📊 Screened {d}/{t}...", done, validStocks.Count);
                        await Task.Delay(800);
                    }
                    catch
                    {
                        _fundResults[sym] = new FundamentalResult
                        { Symbol = sym, Score = 50 };
                    }
                }

                // All go to tier1 for intraday (we want all 50 monitored)
                _tier1Symbols = validStocks.Take(50).ToList();
                _tier2Symbols = validStocks.Skip(50).ToList();
                _allQualitySymbols = validStocks;

                _logger.LogInformation(
                    "✅ Scan complete: {t1} Tier1 intraday stocks",
                    _tier1Symbols.Count);

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
            _logger.LogInformation(
                "📈 Refreshing 5-min signals for {n} stocks...",
                symbols.Count);
            _ai.ClearAllCache();

            foreach (var sym in symbols)
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
                        // Not enough data — use fundamental score only
                        var fs = _fundResults
                            .TryGetValue(sym, out var f) ? f.Score : 50;
                        var ns = _news.GetSymbolSentiment(sym);
                        _scores[sym] = new CompositeScore
                        {
                            Symbol = sym,
                            TechnicalScore = 50,
                            FundamentalScore = fs,
                            NewsScore = Math.Max(0, Math.Min(100,
                                50 + ns * 40)),
                            FinalScore = fs * 0.15 + 50 * 0.70 +
                                Math.Max(0, Math.Min(100, 50 + ns * 40)) * 0.15,
                            CalculatedAt = DateTime.Now
                        };
                        continue;
                    }

                    // Compute all 10 indicators
                    var tech = _technical.Compute(sym, candles);
                    _techResults[sym] = tech;

                    // Composite score (intraday weights)
                    var newsSent = _news.GetSymbolSentiment(sym);
                    var fundScore = _fundResults
                        .TryGetValue(sym, out var fr) ? fr.Score : 50;
                    var newsScore = Math.Max(0, Math.Min(100,
                        50 + newsSent * 40));

                    // Technical: 70%, Fundamental: 15%, News: 15%
                    _scores[sym] = new CompositeScore
                    {
                        Symbol = sym,
                        TechnicalScore = Math.Round(tech.Score, 1),
                        FundamentalScore = Math.Round(fundScore, 1),
                        NewsScore = Math.Round(newsScore, 1),
                        FinalScore = Math.Round(
                            tech.Score * 0.70 +
                            fundScore * 0.15 +
                            newsScore * 0.15, 1),
                        CalculatedAt = DateTime.Now
                    };

                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "⚠️ Signal failed for {sym}: {msg}", sym, ex.Message);
                }
            }

            _logger.LogInformation(
                "✅ Intraday signals updated for {n} stocks", symbols.Count);
        }

        // ── Legacy alias ──────────────────────────────
        public async Task RefreshTechnicalAnalysisAsync(List<string> symbols)
            => await RefreshIntradaySignalsAsync(symbols);

        // ── Live prices — sequential ──────────────────
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

        // ── Top 3 recommendations ─────────────────────
        public async Task RefreshRecommendationsAsync()
        {
            try
            {
                // Rank by score + bonus for strong signals
                var ranked = _scores
                    .Where(s => s.Value.FinalScore > 0)
                    .Select(s =>
                    {
                        var tech = _techResults.GetValueOrDefault(s.Key);
                        var lp = _livePrices.GetValueOrDefault(s.Key);
                        double bonus = 0;
                        if (tech?.SupertrendBullish == true) bonus += 5;
                        if (tech != null && lp != null &&
                            lp.LTP > tech.VWAP && tech.VWAP > 0) bonus += 5;
                        if (tech?.ADX > 25) bonus += 3;
                        return (Symbol: s.Key, Score: s.Value,
                            AdjScore: s.Value.FinalScore + bonus);
                    })
                    .OrderByDescending(x => x.AdjScore)
                    .Take(10)
                    .ToList();

                var recs = new List<Recommendation>();
                int rank = 1;

                foreach (var item in ranked.Take(3))
                {
                    var sym = item.Symbol;
                    var score = item.Score;
                    var lp = _livePrices.GetValueOrDefault(sym);

                    var stock = new StockInfo
                    {
                        Symbol = sym,
                        LastPrice = lp?.LTP ?? 0,
                        Change = lp?.Change ?? 0,
                        ChangePercent = lp?.ChangePercent ?? 0,
                        High = lp?.High ?? 0,
                        Low = lp?.Low ?? 0,
                        Volume = lp?.Volume ?? 0
                    };

                    var tech = _techResults.GetValueOrDefault(sym)
                             ?? new TechnicalResult { Symbol = sym, Score = 50 };
                    var fund = _fundResults.GetValueOrDefault(sym)
                             ?? new FundamentalResult { Symbol = sym };
                    var news = _news.GetNewsForSymbol(sym);
                    var ai = await _ai.AnalyzeStockAsync(
                        stock, tech, fund, news, score);

                    recs.Add(new Recommendation
                    {
                        Rank = rank++,
                        Stock = stock,
                        Technical = tech,
                        Fundamental = fund,
                        Score = score,
                        AiAnalysis = ai,
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
        public List<LivePrice> GetLivePrices() => _livePrices.Values.ToList();
        public LivePrice? GetLivePrice(string s) => _livePrices.GetValueOrDefault(s);
        public TechnicalResult? GetTechnical(string s) => _techResults.GetValueOrDefault(s);
        public FundamentalResult? GetFundamental(string s) => _fundResults.GetValueOrDefault(s);
        public CompositeScore? GetScore(string s) => _scores.GetValueOrDefault(s);
        public List<string> GetTier1Symbols() => _tier1Symbols;
        public List<string> GetTier2Symbols() => _tier2Symbols;
        public List<string> GetAllSymbols() => _symbolTokenMap.Keys.ToList();
        public Dictionary<string, CompositeScore> GetAllScores() => _scores;
    }
}
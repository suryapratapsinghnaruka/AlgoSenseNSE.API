using AlgoSenseNSE.API.Models;
using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoSenseNSE.API.Controllers
{
    [ApiController]
    [Route("api/stocks")]
    public class StocksController : ControllerBase
    {
        private readonly MarketScanService _scanner;
        private readonly AngelOneService _angel;
        private readonly TechnicalAnalysisService _technical;
        private readonly FundamentalService _fundamental;
        private readonly NewsService _news;
        private readonly ClaudeAiService _ai;
        private readonly ScoringEngine _scoring;
        private readonly NseIndiaService _nse;
        private readonly AngelOneWebSocketService _ws;
        private readonly StockScreenerService _screener;
        private readonly ILogger<StocksController> _logger;

        public StocksController(
            MarketScanService scanner,
            AngelOneService angel,
            TechnicalAnalysisService technical,
            FundamentalService fundamental,
            NewsService news,
            ClaudeAiService ai,
            ScoringEngine scoring,
            NseIndiaService nse,
            AngelOneWebSocketService ws,
            StockScreenerService screener,
            ILogger<StocksController> logger)
        {
            _scanner = scanner;
            _angel = angel;
            _technical = technical;
            _fundamental = fundamental;
            _news = news;
            _ai = ai;
            _scoring = scoring;
            _nse = nse;
            _ws = ws;
            _screener = screener;
            _logger = logger;
        }

        // ── GET /api/stocks ──────────────────────────
        // All tracked stocks with scores for the screener table
        [HttpGet]
        public IActionResult GetAllStocks()
        {
            var allScores = _scanner.GetAllScores();
            var result = allScores.Select(kv =>
            {
                var sym = kv.Key;
                var sc = kv.Value;
                var lp = _scanner.GetLivePrice(sym);
                var tech = _scanner.GetTechnical(sym);
                var fund = _scanner.GetFundamental(sym);

                return new
                {
                    symbol = sym,
                    price = lp?.LTP ?? 0,
                    changePercent = lp?.ChangePercent ?? 0,
                    volume = lp?.Volume ?? 0,
                    high = lp?.High ?? 0,
                    low = lp?.Low ?? 0,
                    //sector = fund?.Sector ?? "",
                    compositeScore = sc.FinalScore,
                    technicalScore = sc.TechnicalScore,
                    fundamentalScore = sc.FundamentalScore,
                    newsScore = sc.NewsScore,
                    recommendation = sc.FinalScore >= 68 ? "BUY"
                                   : sc.FinalScore >= 50 ? "HOLD" : "AVOID",
                    supertrend = tech?.SupertrendBullish == true ? "BUY" : "SELL",
                    rsi = tech?.RSI ?? 0,
                    vwap = tech?.VWAP ?? 0,
                    adx = tech?.ADX ?? 0
                };
            })
            .OrderByDescending(s => s.compositeScore)
            .ToList();

            return Ok(result);
        }

        // ── GET /api/stocks/indices ──────────────────
        // Nifty + Sensex for initial page load
        [HttpGet("indices")]
        public async Task<IActionResult> GetIndices()
        {
            var nifty = await _angel.GetLiveIndexAsync("NSE", "26000");
            var sensex = await _angel.GetLiveIndexAsync("BSE", "1");
            return Ok(new { nifty, sensex });
        }

        // ── GET /api/stocks/tiers ────────────────────
        // Tier1 + Tier2 counts for dashboard
        [HttpGet("tiers")]
        public IActionResult GetTiers()
        {
            var tier1 = _scanner.GetTier1Symbols();
            var allSym = _scanner.GetAllSymbols();
            return Ok(new
            {
                tier1Count = tier1.Count,
                totalTracked = allSym.Count,
                wsConnected = _ws.IsConnected,
                wsTicks = _ws.TickCount,
                screenerCount = _screener.CandidateCount
            });
        }

        // ── GET /api/stocks/{symbol} ─────────────────
        // Full stock detail for analysis page
        [HttpGet("{symbol}")]
        public async Task<IActionResult> GetStock(string symbol)
        {
            symbol = symbol.ToUpper();

            var lp = _scanner.GetLivePrice(symbol);
            var tech = _scanner.GetTechnical(symbol);
            var fund = _scanner.GetFundamental(symbol);
            var sc = _scanner.GetScore(symbol);
            var nws = _news.GetNewsForSymbol(symbol);

            return Ok(new
            {
                symbol,
                price = new
                {
                    ltp = lp?.LTP ?? 0,
                    changePercent = lp?.ChangePercent ?? 0,
                    high = lp?.High ?? 0,
                    low = lp?.Low ?? 0,
                    volume = lp?.Volume ?? 0,
                    updatedAt = lp?.UpdatedAt
                },
                technical = tech,
                fundamental = fund,
                score = sc ?? new CompositeScore { Symbol = symbol, FinalScore = 50 },
                news = nws.Take(10)
            });
        }

        // ── GET /api/stocks/{symbol}/ohlcv ───────────
        // 5-min candles for TradingView chart
        [HttpGet("{symbol}/ohlcv")]
        public async Task<IActionResult> GetOhlcv(
            string symbol,
            [FromQuery] int days = 5)
        {
            symbol = symbol.ToUpper();
            var tokens = await _angel.GetSymbolTokenMapAsync(new List<string>());

            if (!tokens.TryGetValue(symbol, out var token))
                return NotFound(new { message = $"Token not found for {symbol}" });

            var candles = await _angel.GetOhlcvAsync(
                symbol, token, "FIVE_MINUTE", days);

            return Ok(candles);
        }

        // ── POST /api/stocks/{symbol}/analyze ────────
        // Trigger fresh Claude AI analysis for a stock
        [HttpPost("{symbol}/analyze")]
        public async Task<IActionResult> AnalyzeStock(string symbol)
        {
            symbol = symbol.ToUpper();

            var lp = _scanner.GetLivePrice(symbol);
            var tech = _scanner.GetTechnical(symbol);
            var fund = _scanner.GetFundamental(symbol);
            var sc = _scanner.GetScore(symbol);
            var nws = _news.GetNewsForSymbol(symbol);

            if (lp == null || tech == null)
                return NotFound(new { message = $"No data for {symbol}. Run a scan first." });

            var stock = new StockInfo
            {
                Symbol = symbol,
                LastPrice = lp.LTP,
                Change = lp.Change,
                ChangePercent = lp.ChangePercent,
                High = lp.High,
                Low = lp.Low,
                Volume = lp.Volume
            };

            _ai.ClearCache(symbol);
            var analysis = await _ai.AnalyzeStockAsync(
                stock, tech,
                fund ?? new FundamentalResult { Symbol = symbol },
                nws,
                sc ?? new CompositeScore { Symbol = symbol, FinalScore = 50 });

            return Ok(analysis);
        }

        // ── GET /api/stocks/screener ─────────────────
        // Dynamic screener candidates from WebSocket
        [HttpGet("screener")]
        public IActionResult GetScreener(
            [FromQuery] int limit = 50)
        {
            var candidates = _screener.GetCandidates()
                .Take(limit)
                .Select(s => new
                {
                    s.Symbol,
                    s.Price,
                    s.ChangePercent,
                    s.Volume,
                    s.High,
                    s.Low,
                    s.MomentumScore,
                    s.AffordableShares
                });
            return Ok(candidates);
        }

        // ── GET /api/stocks/gainers ──────────────────
        // Top gainers from WebSocket screener
        [HttpGet("gainers")]
        public IActionResult GetGainers([FromQuery] int limit = 20)
        {
            var gainers = _screener.GetTopGainers(limit)
                .Select(s => new
                {
                    s.Symbol,
                    s.Price,
                    s.ChangePercent,
                    s.Volume,
                    s.MomentumScore
                });
            return Ok(gainers);
        }
    }
}
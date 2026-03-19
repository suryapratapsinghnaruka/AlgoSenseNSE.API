using AlgoSenseNSE.API.Models;
using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoSenseNSE.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StocksController : ControllerBase
    {
        private readonly MarketScanService _scanner;
        private readonly ClaudeAiService _ai;
        private readonly NewsService _news;
        private readonly AngelOneService _angelOne;
        private readonly AlertEngine _alertEngine;
        private readonly RiskManager _riskManager;

        public StocksController(
            MarketScanService scanner,
            ClaudeAiService ai,
            NewsService news,
            AngelOneService angelOne,
            AlertEngine alertEngine,
            RiskManager riskManager)
        {
            _scanner = scanner;
            _ai = ai;
            _news = news;
            _angelOne = angelOne;
            _alertEngine = alertEngine;
            _riskManager = riskManager;
        }

        // GET api/stocks
        [HttpGet]
        public IActionResult GetAll()
        {
            var scores = _scanner.GetAllScores();
            var prices = _scanner.GetLivePrices()
                .ToDictionary(p => p.Symbol);

            var result = scores
                .Select(s =>
                {
                    var lp = prices.GetValueOrDefault(s.Key);
                    return new
                    {
                        symbol = s.Key,
                        price = lp?.LTP ?? 0,
                        changePercent = lp?.ChangePercent ?? 0,
                        high = lp?.High ?? 0,
                        low = lp?.Low ?? 0,
                        volume = lp?.Volume ?? 0,
                        compositeScore = s.Value.FinalScore,
                        technicalScore = s.Value.TechnicalScore,
                        fundamentalScore = s.Value.FundamentalScore,
                        newsScore = s.Value.NewsScore,
                        recommendation = new ScoringEngine()
                            .GetRecommendation(s.Value.FinalScore),
                        updatedAt = s.Value.CalculatedAt
                    };
                })
                .OrderByDescending(s => s.compositeScore)
                .ToList();

            return Ok(result);
        }

        // GET api/stocks/{symbol}
        [HttpGet("{symbol}")]
        public IActionResult GetStock(string symbol)
        {
            symbol = symbol.ToUpper();
            var tech = _scanner.GetTechnical(symbol);
            var fund = _scanner.GetFundamental(symbol);
            var score = _scanner.GetScore(symbol);
            var price = _scanner.GetLivePrice(symbol);
            var news = _news.GetNewsForSymbol(symbol);

            // Risk calculation for this stock
            PositionSize? posSize = null;
            if (price != null && tech != null && price.LTP > 0)
            {
                posSize = _riskManager.Calculate(
                    symbol,
                    price.LTP,
                    tech.SuggestedStopLoss);
            }

            return Ok(new
            {
                price,
                technical = tech,
                fundamental = fund,
                score,
                news,
                positionSize = posSize
            });
        }

        // GET api/stocks/tiers
        [HttpGet("tiers")]
        public IActionResult GetTiers()
        {
            return Ok(new
            {
                tier1Count = _scanner.GetTier1Symbols().Count,
                tier2Count = _scanner.GetTier2Symbols().Count,
                totalTracked = _scanner.GetAllSymbols().Count,
                tier1 = _scanner.GetTier1Symbols(),
                tier2 = _scanner.GetTier2Symbols().Take(50)
            });
        }

        // GET api/stocks/indices
        [HttpGet("indices")]
        public async Task<IActionResult> GetIndices()
        {
            var nifty = await _angelOne.GetLiveIndexAsync("NSE", "26000");
            var sensex = await _angelOne.GetLiveIndexAsync("BSE", "1");
            return Ok(new { nifty, sensex });
        }

        // GET api/stocks/{symbol}/ohlcv
        [HttpGet("{symbol}/ohlcv")]
        public async Task<IActionResult> GetOhlcv(
            string symbol,
            [FromQuery] string interval = "FIVE_MINUTE",
            [FromQuery] int days = 5)
        {
            symbol = symbol.ToUpper();

            // Try to get token from scanner
            var allSymbols = _scanner.GetAllSymbols();
            // We need the token map — get it via a workaround
            // by attempting OHLCV fetch using known symbol
            var tier1 = _scanner.GetTier1Symbols();
            var tier2 = _scanner.GetTier2Symbols();

            // Return cached technical data for chart
            var tech = _scanner.GetTechnical(symbol);
            if (tech == null)
                return Ok(new List<object>());

            // Return basic price data we have
            var price = _scanner.GetLivePrice(symbol);
            if (price == null)
                return Ok(new List<object>());

            // Return synthetic candle from live price for chart display
            var now = DateTime.Now;
            var candles = new List<object>
            {
                new {
                    timestamp = now.AddMinutes(-5),
                    open  = price.LTP - (price.Change * 0.3),
                    high  = price.High,
                    low   = price.Low,
                    close = price.LTP,
                    volume = price.Volume
                }
            };

            return Ok(candles);
        }

        // POST api/stocks/{symbol}/analyze
        [HttpPost("{symbol}/analyze")]
        public async Task<IActionResult> AnalyzeStock(string symbol)
        {
            symbol = symbol.ToUpper();

            var price = _scanner.GetLivePrice(symbol);
            var stock = new StockInfo
            {
                Symbol = symbol,
                LastPrice = price?.LTP ?? 0,
                ChangePercent = price?.ChangePercent ?? 0,
                Volume = price?.Volume ?? 0,
                High = price?.High ?? 0,
                Low = price?.Low ?? 0
            };

            var tech = _scanner.GetTechnical(symbol)
                     ?? new TechnicalResult { Symbol = symbol };
            var fund = _scanner.GetFundamental(symbol)
                     ?? new FundamentalResult { Symbol = symbol };
            var score = _scanner.GetScore(symbol)
                     ?? new CompositeScore { Symbol = symbol };
            var news = _news.GetNewsForSymbol(symbol);

            var analysis = await _ai.AnalyzeStockAsync(
                stock, tech, fund, news, score);

            return Ok(analysis);
        }

        // GET api/stocks/alerts
        [HttpGet("alerts")]
        public IActionResult GetAlerts()
        {
            return Ok(new
            {
                alerts = _alertEngine.GetAlertHistory(),
                alertsToday = _alertEngine.GetAlertsToday(),
                dailyPnl = _alertEngine.GetDailyPnL()
            });
        }

        // GET api/stocks/risk
        [HttpGet("risk")]
        public IActionResult GetRisk()
        {
            return Ok(_riskManager.GetSummary());
        }
    }
}
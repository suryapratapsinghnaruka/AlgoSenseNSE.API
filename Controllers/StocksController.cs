using AlgoSenseNSE.API.Models;
using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.Mvc;
using System.Numerics;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

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

        public StocksController(
            MarketScanService scanner,
            ClaudeAiService ai,
            NewsService news,
            AngelOneService angelOne)
        {
            _scanner = scanner;
            _ai = ai;
            _news = news;
            _angelOne = angelOne;
        }

        // GET api/stocks
        [HttpGet]
        public IActionResult GetAll()
        {
            var scores = _scanner.GetAllScores();
            var prices = _scanner.GetLivePrices()
                .ToDictionary(p => p.Symbol);

            var result = scores
                .Select(s => new
                {
                    symbol = s.Key,
                    price = prices.GetValueOrDefault(s.Key)?.LTP ?? 0,
                    changePercent = prices
                        .GetValueOrDefault(s.Key)?.ChangePercent ?? 0,
                    compositeScore = s.Value.FinalScore,
                    technicalScore = s.Value.TechnicalScore,
                    fundamentalScore = s.Value.FundamentalScore,
                    newsScore = s.Value.NewsScore,
                    recommendation = new ScoringEngine()
                        .GetRecommendation(s.Value.FinalScore)
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
            return Ok(new
            {
                price = _scanner.GetLivePrice(symbol),
                technical = _scanner.GetTechnical(symbol),
                fundamental = _scanner.GetFundamental(symbol),
                score = _scanner.GetScore(symbol),
                news = _news.GetNewsForSymbol(symbol)
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

        // POST api/stocks/{symbol}/analyze
        [HttpPost("{symbol}/analyze")]
        public async Task<IActionResult> AnalyzeStock(string symbol)
        {
            symbol = symbol.ToUpper();
            var stock = new Models.StockInfo { Symbol = symbol };
            var price = _scanner.GetLivePrice(symbol);
            if (price != null)
            {
                stock.LastPrice = price.LTP;
                stock.ChangePercent = price.ChangePercent;
                stock.Volume = price.Volume;
            }

            var tech = _scanner.GetTechnical(symbol)
                ?? new Models.TechnicalResult { Symbol = symbol };
            var fund = _scanner.GetFundamental(symbol)
                ?? new Models.FundamentalResult { Symbol = symbol };
            var score = _scanner.GetScore(symbol)
                ?? new Models.CompositeScore { Symbol = symbol };
            var newsItems = _news.GetNewsForSymbol(symbol);

            var analysis = await _ai.AnalyzeStockAsync(
                stock, tech, fund, newsItems, score);

            return Ok(analysis);
        }

        // GET api/stocks/{symbol}/ohlcv
        [HttpGet("{symbol}/ohlcv")]
        public async Task<IActionResult> GetOhlcv(string symbol)
        {
            symbol = symbol.ToUpper();
            var scores = _scanner.GetAllScores();

            // Get token from symbol map via scanner
            var tier1 = _scanner.GetTier1Symbols();
            var tier2 = _scanner.GetTier2Symbols();

            // Return empty - chart will show unavailable message
            return Ok(new List<object>());
        }

        [HttpGet("indices")]
        public async Task<IActionResult> GetIndices()
        {
            var nifty = await _angelOne.GetLiveIndexAsync("NSE", "26000");   // Nifty 50 token
            var sensex = await _angelOne.GetLiveIndexAsync("BSE", "1");      // Sensex token
            return Ok(new { nifty, sensex });
        }
    }
}
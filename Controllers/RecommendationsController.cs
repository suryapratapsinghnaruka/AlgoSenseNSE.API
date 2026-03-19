using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoSenseNSE.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RecommendationsController : ControllerBase
    {
        private readonly MarketScanService _scanner;
        private readonly AlertEngine _alertEngine;
        private readonly RiskManager _riskManager;

        public RecommendationsController(
            MarketScanService scanner,
            AlertEngine alertEngine,
            RiskManager riskManager)
        {
            _scanner = scanner;
            _alertEngine = alertEngine;
            _riskManager = riskManager;
        }

        // GET api/recommendations
        [HttpGet]
        public IActionResult Get()
        {
            var recs = _scanner.GetRecommendations();
            var risk = _riskManager.GetSummary();
            var alerts = _alertEngine.GetAlertHistory();

            return Ok(new
            {
                generatedAt = DateTime.Now,
                count = recs.Count,
                recommendations = recs,
                riskSummary = risk,
                alertsToday = _alertEngine.GetAlertsToday(),
                recentAlerts = alerts.TakeLast(5)
            });
        }

        // POST api/recommendations/refresh
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            await _scanner.RefreshRecommendationsAsync();
            return Ok(new
            {
                recommendations = _scanner.GetRecommendations(),
                refreshedAt = DateTime.Now
            });
        }

        // GET api/recommendations/summary
        [HttpGet("summary")]
        public IActionResult GetSummary()
        {
            var recs = _scanner.GetRecommendations();
            var risk = _riskManager.GetSummary();

            return Ok(new
            {
                totalSignals = _alertEngine.GetAlertsToday(),
                dailyPnL = _alertEngine.GetDailyPnL(),
                capitalUsed = risk.TotalCapital - risk.AvailableCapital,
                capitalAvail = risk.AvailableCapital,
                tradingHalted = risk.TradingHalted,
                topPick = recs.FirstOrDefault()?.Stock.Symbol ?? "Scanning...",
                marketBias = recs.Count(r =>
                    r.AiAnalysis?.Recommendation == "BUY") > 1
                    ? "BULLISH" : "NEUTRAL"
            });
        }
    }
}
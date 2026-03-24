using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoSenseNSE.API.Controllers
{
    [ApiController]
    [Route("api/recommendations")]
    public class RecommendationsController : ControllerBase
    {
        private readonly MarketScanService _scanner;
        private readonly AlertEngine _alertEngine;
        private readonly RiskManager _risk;
        private readonly NseIndiaService _nse;
        private readonly ILogger<RecommendationsController> _logger;

        public RecommendationsController(
            MarketScanService scanner,
            AlertEngine alertEngine,
            RiskManager risk,
            NseIndiaService nse,
            ILogger<RecommendationsController> logger)
        {
            _scanner = scanner;
            _alertEngine = alertEngine;
            _risk = risk;
            _nse = nse;
            _logger = logger;
        }

        // ── GET /api/recommendations ─────────────────
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var recs = _scanner.GetRecommendations();
            var risk = _risk.GetSummary();

            // Try to get market context for display
            MarketContext? mktCtx = null;
            try { mktCtx = await _nse.GetMarketContextAsync(); } catch { }

            return Ok(new
            {
                generatedAt = DateTime.Now,
                count = recs.Count,
                recommendations = recs,
                riskSummary = risk,
                alertsToday = _alertEngine.GetAlertsToday(),
                recentAlerts = _alertEngine.GetAlertHistory()
                    .TakeLast(10).ToList(),
                marketContext = mktCtx != null ? new
                {
                    niftyLtp = mktCtx.NiftyLtp,
                    niftyChange = mktCtx.NiftyChange,
                    niftyTrend = mktCtx.NiftyTrend,
                    indiaVix = mktCtx.IndiaVix,
                    vixSignal = mktCtx.VixInterpretation,
                    fiiNet = mktCtx.FiiNetCrore,
                    fiiSentiment = mktCtx.FiiSentiment,
                    marketQuality = mktCtx.MarketQualityScore,
                    qualityLabel = mktCtx.MarketQualityLabel,
                    topSectors = mktCtx.TopSectors,
                    weakSectors = mktCtx.WeakSectors
                } : null,
                // Locked picks info
                //picksLocked = _scanner.PicksLockedToday,
                //lockedAt = _scanner.LockedAt
            });
        }

        // ── POST /api/recommendations/refresh ────────
        // Force refresh — useful for manual testing
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh()
        {
            _logger.LogInformation("🔄 Manual recommendation refresh...");

            var tier1 = _scanner.GetTier1Symbols();
            await _scanner.RefreshIntradaySignalsAsync(
                tier1.Take(30).ToList());
            await _scanner.RefreshRecommendationsAsync();

            var recs = _scanner.GetRecommendations();

            return Ok(new
            {
                message = $"✅ Refreshed {recs.Count} recommendations",
                recommendations = recs,
                refreshedAt = DateTime.Now
            });
        }

        // ── GET /api/recommendations/locked ──────────
        // Returns today's locked morning picks
        [HttpGet("locked")]
        public IActionResult GetLocked()
        {
            var locked = _scanner.GetLockedPicks();
            return Ok(new
            {
                locked = locked,
                lockedAt = _scanner.LockedAt,
                isLocked = _scanner.PicksLockedToday,
                count = locked.Count
            });
        }

        // ── GET /api/recommendations/triggers ────────
        // Returns entry triggers for locked picks
        [HttpGet("triggers")]
        public IActionResult GetTriggers()
        {
            var triggers = _scanner.GetEntryTriggers();
            return Ok(new
            {
                triggers,
                count = triggers.Count,
                checkedAt = DateTime.Now
            });
        }
    }
}
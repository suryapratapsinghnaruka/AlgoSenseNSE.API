using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoSenseNSE.API.Controllers
{
    /// <summary>
    /// Accuracy + performance stats endpoint.
    /// GET /api/accuracy → win rate, expectancy, drawdown
    /// </summary>
    [ApiController]
    [Route("api/accuracy")]
    public class AccuracyController : ControllerBase
    {
        private readonly SignalTrackingService  _signals;
        private readonly AlertEngine           _alerts;
        private readonly RejectedTradeTracker  _rejected;

        public AccuracyController(
            SignalTrackingService signals,
            AlertEngine alerts,
            RejectedTradeTracker rejected)
        {
            _signals  = signals;
            _alerts   = alerts;
            _rejected = rejected;
        }

        // ── GET /api/accuracy ────────────────────────
        // Full performance dashboard
        [HttpGet]
        public async Task<IActionResult> GetAccuracy(
            [FromQuery] int days = 30)
        {
            var stats = await _signals.GetAccuracyStatsAsync(days);

            // Expectancy = (WinRate × AvgWin) - (LossRate × AvgLoss)
            double winRate  = stats.TotalSignals > 0
                ? (double)stats.HitTarget / stats.TotalSignals : 0;
            double lossRate = 1 - winRate;
            double expectancy = stats.TotalSignals > 0
                ? (winRate  * Math.Abs(stats.AvgProfitPct))
                - (lossRate * Math.Abs(stats.AvgLossPct))
                : 0;

            // Grade the system
            string grade = expectancy switch
            {
                > 1.5  => "A+ — Excellent edge",
                > 1.0  => "A  — Strong edge",
                > 0.5  => "B  — Positive edge",
                > 0.0  => "C  — Marginal edge",
                _      => "D  — No edge yet (need more data)"
            };

            string advice = stats.TotalSignals < 20
                ? "⚠️ Need at least 20 signals for statistical significance. Keep running."
                : winRate > 0.6
                ? "✅ Win rate strong. Focus on maintaining R:R ≥ 1:2."
                : winRate > 0.45
                ? "⚠️ Win rate okay. Tighten entry conditions."
                : "❌ Win rate low. Review which indicators are failing.";

            return Ok(new
            {
                period       = $"Last {days} days",
                generatedAt  = DateTime.Now,

                // Core metrics
                totalSignals = stats.TotalSignals,
                hitTarget    = stats.HitTarget,
                hitSl        = stats.HitSl,
                expired      = stats.Expired,

                // Rates
                winRate      = Math.Round(winRate  * 100, 1),
                lossRate     = Math.Round(lossRate * 100, 1),

                // P&L
                avgWinPct    = Math.Round(stats.AvgProfitPct, 2),
                avgLossPct   = Math.Round(stats.AvgLossPct,   2),
                totalPnlRs   = Math.Round(stats.TotalPnlRs,   2),

                // Advanced
                expectancyPct = Math.Round(expectancy, 2),
                grade         = grade,
                advice        = advice,

                // Today
                alertsToday  = _alerts.GetAlertsToday(),
                dailyPnl     = _alerts.GetDailyPnL(),

                // Explanation
                howToRead = new
                {
                    expectancy  = "Expectancy > 0 means system has edge. " +
                                  "> 1.0% = strong.",
                    winRate     = "Win rate > 50% is good, but R:R matters more.",
                    grade       = "Based on expectancy per trade."
                }
            });
        }

        // ── GET /api/accuracy/history ─────────────────
        // Raw signal history for analysis
        [HttpGet("history")]
        public async Task<IActionResult> GetHistory(
            [FromQuery] int days = 30)
        {
            var training = await _signals.ExportTrainingDataAsync();
            return Ok(new
            {
                count   = training.Count,
                signals = training.Take(100)
            });
        }

        // ── GET /api/accuracy/rejections ─────────────
        // Rejected trades analysis — which gates are too strict?
        [HttpGet("rejections")]
        public async Task<IActionResult> GetRejections(
            [FromQuery] int days = 30)
        {
            var insights = await _rejected.GetInsightsAsync(days);
            return Ok(new
            {
                period         = $"Last {days} days",
                totalRejected  = insights.TotalRejected,
                wouldHaveWon   = insights.WouldHaveWon,
                wouldHaveWonPct = insights.WouldHaveWonPct,
                insight        = insights.Insight,
                byReason       = insights.ReasonBreakdown,
                howToRead      = new
                {
                    wouldHaveWonPct =
                        "> 60% means your gates may be too strict. " +
                        "Consider loosening the highest-count rejection reason.",
                    targetOptimization =
                        "After 50+ rejections: if top reason win rate > 60%, " +
                        "that gate is a false positive filter."
                },
                generatedAt = DateTime.Now
            });
        }

        // ── GET /api/accuracy/regime ──────────────────
        // Current market regime
        [HttpGet("regime")]
        public IActionResult GetRegime()
        {
            return Ok(new
            {
                message   = "Regime is computed live during signal processing.",
                regimes   = new[]
                {
                    new { name = "TREND",      description = "ADX > 25, clear direction. Use momentum indicators." },
                    new { name = "RANGE",      description = "ADX < 20, price oscillating. Higher confidence required." },
                    new { name = "PANIC",      description = "80%+ stocks bearish. No trades today." },
                    new { name = "TREND_DOWN", description = "ADX > 25 but bearish. Avoid longs." },
                },
                checkedAt = DateTime.Now
            });
        }
    }
}

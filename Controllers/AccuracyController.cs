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

            // Win rate based on filled outcomes only (not total signals)
            int outcomeFilled = stats.HitTarget + stats.HitSl + stats.Expired;
            double winRate      = outcomeFilled > 0 ? (double)stats.HitTarget / outcomeFilled : 0;
            double lossRate     = outcomeFilled > 0 ? (double)stats.HitSl     / outcomeFilled : 0;
            double expiredRate  = outcomeFilled > 0 ? (double)stats.Expired   / outcomeFilled : 0;
            double expectancy   = outcomeFilled > 0
                ? (winRate     * Math.Abs(stats.AvgProfitPct))
                - (lossRate    * Math.Abs(stats.AvgLossPct))
                - (expiredRate * 0.1)
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
                period        = $"Last {days} days",
                generatedAt   = DateTime.Now,

                // Total signals logged
                totalSignals  = stats.TotalSignals,
                buySignals    = stats.BuySignals,
                avoidSignals  = stats.AvoidSignals,
                outcomeFilled = stats.HitTarget + stats.HitSl + stats.Expired,
                pendingOutcomes = stats.TotalSignals - (stats.HitTarget + stats.HitSl + stats.Expired),

                // Outcomes
                hitTarget     = stats.HitTarget,
                hitSl         = stats.HitSl,
                expired       = stats.Expired,

                // Win rate calculated on filled outcomes only
                winRate       = Math.Round(winRate  * 100, 1),
                lossRate      = Math.Round(lossRate * 100, 1),
                expiredRate   = Math.Round(expiredRate * 100, 1),

                // P&L
                avgWinPercent  = Math.Round(stats.AvgProfitPct, 2),
                avgLossPercent = Math.Round(stats.AvgLossPct,   2),
                totalPnlRs     = Math.Round(stats.TotalPnlRs,   2),

                // Edge metrics
                expectancy     = Math.Round(expectancy, 2),
                grade          = grade,
                advice         = advice,

                // Today
                alertsToday    = _alerts.GetAlertsToday(),
                dailyPnl       = _alerts.GetDailyPnL(),

                howToRead = new
                {
                    totalSignals  = "BUY + AVOID combined. Both logged for accuracy measurement.",
                    buySignals    = "Signals where system said BUY. Win rate calculated on these.",
                    avoidSignals  = "Signals where system said AVOID. Check /api/accuracy/rejections to see if they would have won.",
                    expectancy    = "Expectancy > 0 = system has edge. > 1.0% = strong edge.",
                    winRate       = "Win rate > 50% is good. With R:R 1:2, even 45% is profitable.",
                    grade         = "A = strong edge, B = positive, C = marginal, D = need more data."
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

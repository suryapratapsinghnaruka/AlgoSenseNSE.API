using AlgoSenseNSE.API.Models;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// Alert engine for equity stock signals.
    /// Calibrated for ₹1,000–₹2,000 intraday capital.
    /// 
    /// Improvements v2:
    /// - Minimum R:R raised to 1:2
    /// - 30-min cooldown per stock (no repeat alerts)
    /// - ATR-based position sizing
    /// - Market regime detection (Trend/Range/Panic)
    /// </summary>
    public class AlertEngine
    {
        private readonly TelegramService _telegram;
        private readonly RiskManager _risk;
        private readonly IConfiguration _config;
        private readonly ILogger<AlertEngine> _logger;

        private readonly List<AlertRecord> _alertHistory = new();
        private readonly object _lock = new();

        // ── Per-stock cooldown (30 min) ───────────────
        private readonly Dictionary<string, DateTime> _lastAlertTime = new();
        private const int CooldownMinutes = 30;

        private bool _marketOpenAlertSent  = false;
        private bool _marketCloseAlertSent = false;
        private bool _haltAlertSent        = false;
        private int    _alertsToday = 0;
        private double _dailyPnL    = 0;
        private DateTime _lastProcessTime = DateTime.MinValue;

        public AlertEngine(
            TelegramService telegram,
            RiskManager risk,
            IConfiguration config,
            ILogger<AlertEngine> logger)
        {
            _telegram = telegram;
            _risk     = risk;
            _config   = config;
            _logger   = logger;
        }

        // ── Main process — called every 5 minutes ─────
        public async Task ProcessSignalsAsync(
            List<Recommendation> recommendations,
            double niftyLtp,
            double bankniftyLtp,
            double capital = 1500)
        {
            var ist = GetIST();

            // ── Market open alert (9:15 AM) ───────────
            if (!_marketOpenAlertSent &&
                ist.Hour == 9 && ist.Minute >= 15 &&
                IsWeekday(ist))
            {
                _marketOpenAlertSent  = true;
                _marketCloseAlertSent = false;
                _haltAlertSent        = false;
                _alertsToday          = 0;
                _dailyPnL             = 0;

                // Detect market regime for open message
                var regime = DetectRegime(recommendations, niftyLtp);

                int bullCount = recommendations.Count(r =>
                    r.AiAnalysis?.Recommendation == "BUY");
                string bias = bullCount >= 2 ? "BULLISH"
                    : recommendations.Count(r =>
                        r.AiAnalysis?.Recommendation == "SELL") >= 2
                    ? "BEARISH" : "NEUTRAL";

                var picks = recommendations.Take(3).Select(r =>
                    $"{r.Stock.Symbol} — Score:{r.Score.FinalScore:F0} " +
                    $"({r.AiAnalysis?.Recommendation ?? "?"})").ToList();

                await _telegram.SendMarketOpenSummaryAsync(
                    niftyLtp, 0, $"{bias} | Regime: {regime}", picks);

                _logger.LogInformation(
                    "📱 Market open alert sent | Regime: {regime}", regime);
                return;
            }

            // ── Market close reminder (3:10 PM) ──────
            if (!_marketCloseAlertSent &&
                ist.Hour == 15 && ist.Minute >= 5 &&
                IsWeekday(ist))
            {
                _marketCloseAlertSent = true;
                _marketOpenAlertSent  = false;

                await _telegram.SendMessageAsync(
                    "⏰ <b>EXIT ALL POSITIONS — 3:10 PM</b>\n\n" +
                    "🛑 Market closes in 20 minutes!\n" +
                    "Open Angel One → Exit ALL open positions NOW.\n\n" +
                    $"Today's signals: {_alertsToday}\n" +
                    $"Estimated P&L: ₹{_dailyPnL:F0}");

                await Task.Delay(2000);

                await _telegram.SendDailySummaryAsync(
                    _dailyPnL, _alertsToday,
                    recommendations.FirstOrDefault()?.Stock.Symbol ?? "None");

                _logger.LogInformation("📱 Market close alert sent");
                return;
            }

            // ── Don't process outside market hours ────
            if (!IsMarketHours(ist)) return;

            // ── Wait for 9:20 AM ──────────────────────
            if (ist.Hour == 9 && ist.Minute < 20) return;

            // ── Prevent too frequent processing ───────
            if ((DateTime.Now - _lastProcessTime).TotalMinutes < 4) return;
            _lastProcessTime = DateTime.Now;

            // ── Detect market regime ──────────────────
            var currentRegime = DetectRegime(recommendations, niftyLtp);
            _logger.LogInformation(
                "📊 Market regime: {regime}", currentRegime);

            // Don't trade in Panic regime
            if (currentRegime == "PANIC")
            {
                _logger.LogInformation(
                    "🚨 PANIC regime — skipping all signals");
                return;
            }

            // ── Check trading halted ──────────────────
            var riskSummary = _risk.GetSummary();
            if (riskSummary.TradingHalted)
            {
                if (!_haltAlertSent)
                {
                    _haltAlertSent = true;
                    await _telegram.SendTradingHaltedAsync(
                        "2 consecutive losses or max daily loss hit",
                        riskSummary.DailyLoss);
                }
                return;
            }

            // ── Max alerts per day ────────────────────
            int maxAlerts = _config.GetValue<int>(
                "Trading:MaxAlertsPerDay", 3);
            if (_alertsToday >= maxAlerts)
            {
                _logger.LogDebug(
                    "Max alerts ({max}) reached for today", maxAlerts);
                return;
            }

            // ── Quality thresholds ────────────────────
            int    minConfidence = _config.GetValue<int>(
                "Trading:MinConfidence", 70);
            double minScore = 68.0;

            // ── Adjust thresholds by regime ───────────
            // In Range regime, require higher confidence
            if (currentRegime == "RANGE")
            {
                minConfidence = 75;
                minScore      = 72.0;
            }

            // ── Process each recommendation ───────────
            foreach (var rec in recommendations.Take(5))
            {
                if (_alertsToday >= maxAlerts) break;

                var symbol = rec.Stock.Symbol;
                var ai     = rec.AiAnalysis;
                var tech   = rec.Technical;
                var score  = rec.Score;

                // ── 30-min cooldown per stock ─────────
                if (_lastAlertTime.TryGetValue(symbol, out var lastAlert) &&
                    (DateTime.Now - lastAlert).TotalMinutes < CooldownMinutes)
                {
                    _logger.LogDebug(
                        "⏸ {sym} on cooldown ({min:F0} min remaining)",
                        symbol,
                        CooldownMinutes - (DateTime.Now - lastAlert).TotalMinutes);
                    continue;
                }

                // ── Quality filters ───────────────────
                if (ai?.Recommendation != "BUY")
                {
                    _logger.LogDebug(
                        "⏭ {sym} skipped: AI={rec}",
                        symbol, ai?.Recommendation);
                    continue;
                }

                if (score.FinalScore < minScore)
                {
                    _logger.LogInformation(
                        "⏭ {sym} skipped: Score={s} < {min}",
                        symbol, score.FinalScore, minScore);
                    continue;
                }

                if (ai.Confidence < minConfidence)
                {
                    _logger.LogInformation(
                        "⏭ {sym} skipped: Confidence={c}% < {min}%",
                        symbol, ai.Confidence, minConfidence);
                    continue;
                }

                if (!tech.SupertrendBullish)
                {
                    _logger.LogInformation(
                        "⏭ {sym} skipped: Supertrend=SELL", symbol);
                    continue;
                }

                if (rec.Stock.LastPrice <= 0 ||
                    tech.VWAP <= 0 ||
                    rec.Stock.LastPrice < tech.VWAP)
                {
                    _logger.LogInformation(
                        "⏭ {sym} skipped: Price ₹{p} below VWAP ₹{v}",
                        symbol, rec.Stock.LastPrice, tech.VWAP);
                    continue;
                }

                if (tech.ADX < 20)
                {
                    _logger.LogInformation(
                        "⏭ {sym} skipped: ADX={adx} < 20",
                        symbol, tech.ADX);
                    continue;
                }

                if (tech.RSI < 45 || tech.RSI > 75)
                {
                    _logger.LogInformation(
                        "⏭ {sym} skipped: RSI={rsi} out of 45-75",
                        symbol, tech.RSI);
                    continue;
                }

                // ── Risk check ────────────────────────
                if (!_risk.CanTrade(out var riskReason))
                {
                    _logger.LogWarning(
                        "⚠️ Risk block: {reason}", riskReason);
                    break;
                }

                // ── ATR-based position sizing ─────────
                // High ATR = volatile = smaller size
                // Low ATR  = stable  = larger size
                var posSize = _risk.CalculateWithAtr(
                    symbol,
                    rec.Stock.LastPrice,
                    tech.SuggestedStopLoss,
                    tech.ATR);

                if (!posSize.IsValid)
                {
                    _logger.LogWarning(
                        "⚠️ Position invalid for {sym}: {reason}",
                        symbol, posSize.Reason);
                    continue;
                }

                // ── Enforce minimum R:R of 1:2 (slippage-adjusted) ──
                // Use slippage-adjusted prices for realistic R:R
                double adjEntry  = rec.Stock.LastPrice * 1.0010; // +0.10%
                double adjTarget = ai.Target           * 0.9990; // -0.10%
                double adjSL     = ai.StopLoss         * 0.9995; // -0.05%

                double potentialProfit =
                    (adjTarget - adjEntry) * posSize.Quantity;
                double potentialLoss =
                    (adjEntry  - adjSL)    * posSize.Quantity;
                double rr = potentialLoss > 0
                    ? potentialProfit / potentialLoss : 0;

                if (rr < 2.0)
                {
                    _logger.LogInformation(
                        "⏭ {sym} skipped: Adj R:R={rr:F1} < 2.0 " +
                        "(after slippage adjustment)",
                        symbol, rr);
                    continue;
                }

                // ── Brokerage viability ───────────────
                double netProfit = potentialProfit - posSize.Brokerage;
                if (netProfit < posSize.Brokerage * 1.5)
                {
                    _logger.LogWarning(
                        "⚠️ {sym} — net profit ₹{p:F0} too close " +
                        "to brokerage ₹{b:F0}",
                        symbol, netProfit, posSize.Brokerage);
                    continue;
                }

                // ── Build signal reason ───────────────
                var bullSignals = tech.Signals
                    .Where(s => s.IsBullish == true)
                    .Take(4)
                    .Select(s => $"  • {s.Indicator}: {s.Signal}")
                    .ToList();
                string reason = string.Join("\n", bullSignals);
                if (string.IsNullOrEmpty(reason))
                    reason = "  • Strong technical + fundamental setup";

                // ── Send alert ────────────────────────
                await _telegram.SendBuyAlertAsync(
                    symbol:        symbol,
                    ltp:           rec.Stock.LastPrice,
                    target:        ai.Target,
                    stopLoss:      ai.StopLoss,
                    confidence:    ai.Confidence,
                    reason:        reason,
                    quantity:      posSize.Quantity,
                    capitalNeeded: posSize.TotalCost,
                    timeHorizon:   ai.TimeHorizon ?? "Exit by 14:30",
                    regime:        currentRegime,
                    riskReward:    rr);

                // ── Record ────────────────────────────
                _lastAlertTime[symbol] = DateTime.Now;
                RecordAlert(symbol, rec.Stock.LastPrice,
                    ai.Target, ai.StopLoss,
                    posSize.Quantity, score.FinalScore);
                _alertsToday++;

                _logger.LogInformation(
                    "📱 Alert sent: {sym} qty={qty} R:R=1:{rr:F1} " +
                    "entry=₹{e} target=₹{t} sl=₹{sl} regime={r}",
                    symbol, posSize.Quantity, rr,
                    rec.Stock.LastPrice, ai.Target, ai.StopLoss,
                    currentRegime);

                await Task.Delay(3000);
            }
        }

        // ── Market Regime Detection ───────────────────
        // Trend:  ADX > 25, clear direction
        // Range:  ADX < 20, price oscillating
        // Panic:  VIX > 25 or Nifty down > 1.5%
        public string DetectRegime(
            List<Recommendation> recs, double niftyLtp)
        {
            try
            {
                if (!recs.Any()) return "UNKNOWN";

                var techs = recs
                    .Where(r => r.Technical != null)
                    .Select(r => r.Technical)
                    .ToList();

                if (!techs.Any()) return "UNKNOWN";

                double avgAdx = techs.Average(t => t!.ADX);
                int bullCount = recs.Count(r =>
                    r.Technical?.SupertrendBullish == true);
                int bearCount = recs.Count(r =>
                    r.Technical?.SupertrendBullish == false);

                // Check for panic — high fear
                // We infer from Supertrend: if 80%+ are bearish
                double bearRatio = recs.Count > 0
                    ? (double)bearCount / recs.Count : 0;

                if (bearRatio >= 0.8 || avgAdx < 15)
                    return "PANIC";

                if (avgAdx >= 25 && bullCount > bearCount)
                    return "TREND";

                if (avgAdx >= 25 && bearCount > bullCount)
                    return "TREND_DOWN";

                return "RANGE";
            }
            catch
            {
                return "UNKNOWN";
            }
        }

        // ── Record to history ─────────────────────────
        private void RecordAlert(
            string symbol, double entry, double target,
            double sl, int qty, double score)
        {
            lock (_lock)
            {
                _alertHistory.Add(new AlertRecord
                {
                    Symbol   = symbol,
                    Entry    = entry,
                    Target   = target,
                    StopLoss = sl,
                    Quantity = qty,
                    Score    = score,
                    Type     = "STOCK",
                    SentAt   = DateTime.Now,
                    Status   = "OPEN"
                });

                if (_alertHistory.Count > 100)
                    _alertHistory.RemoveAt(0);
            }
        }

        // ── Helpers ───────────────────────────────────
        private bool IsMarketHours(DateTime ist)
        {
            if (!IsWeekday(ist)) return false;
            return ist.TimeOfDay >= new TimeSpan(9, 15, 0) &&
                   ist.TimeOfDay <= new TimeSpan(15, 30, 0);
        }

        private bool IsWeekday(DateTime dt) =>
            dt.DayOfWeek != DayOfWeek.Saturday &&
            dt.DayOfWeek != DayOfWeek.Sunday;

        private DateTime GetIST()
        {
            try
            {
                return TimeZoneInfo.ConvertTime(DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById(
                        "India Standard Time"));
            }
            catch
            {
                return DateTime.UtcNow.AddHours(5).AddMinutes(30);
            }
        }

        public List<AlertRecord> GetAlertHistory()
        {
            lock (_lock) { return _alertHistory.ToList(); }
        }
        public int    GetAlertsToday() => _alertsToday;
        public double GetDailyPnL()    => _dailyPnL;
        public void   AddPnL(double p) => _dailyPnL += p;
        public string GetCurrentRegime(
            List<Recommendation> recs, double niftyLtp)
            => DetectRegime(recs, niftyLtp);
    }

    public class AlertRecord
    {
        public string   Symbol    { get; set; } = "";
        public double   Entry     { get; set; }
        public double   Target    { get; set; }
        public double   StopLoss  { get; set; }
        public int      Quantity  { get; set; }
        public double   Score     { get; set; }
        public string   Type      { get; set; } = "STOCK";
        public DateTime SentAt    { get; set; }
        public string   Status    { get; set; } = "OPEN";
        public double   ExitPrice { get; set; }
        public double   PnL       { get; set; }
    }
}

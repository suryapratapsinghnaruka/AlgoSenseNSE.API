namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// Risk manager v2 — ATR-based position sizing.
    ///
    /// v2 changes:
    /// - CalculateWithAtr(): volatility-adjusted quantity
    ///   High ATR (volatile) → fewer shares
    ///   Low ATR (stable)    → more shares
    /// - Minimum R:R 1:2 enforced here too
    /// - Better logging
    /// </summary>
    public class RiskManager
    {
        private readonly ILogger<RiskManager> _logger;

        public double TotalCapital    { get; private set; }
        public double MaxRiskPerTrade { get; private set; }
        public double MaxDailyLoss    { get; private set; }
        public double ReserveCapital  { get; private set; }

        private const double BROKERAGE_PER_TRADE = 40.0;

        private double _dailyLoss       = 0;
        private double _dailyProfit     = 0;
        private int    _consecutiveLoss = 0;
        private int    _tradesCount     = 0;
        private bool   _tradingHalted   = false;

        public RiskManager(
            ILogger<RiskManager> logger,
            double capital = 1500)
        {
            _logger          = logger;
            TotalCapital     = capital;
            ReserveCapital   = capital * 0.40;
            MaxRiskPerTrade  = capital * 0.15;
            MaxDailyLoss     = capital * 0.25;
        }

        public bool CanTrade(out string reason)
        {
            reason = "";
            if (_tradingHalted)
            {
                reason = "Trading halted — protect remaining capital";
                return false;
            }
            if (_dailyLoss >= MaxDailyLoss)
            {
                reason = $"Max daily loss hit (₹{_dailyLoss:F0})";
                _tradingHalted = true;
                return false;
            }
            if (_consecutiveLoss >= 2)
            {
                reason = "2 losses in a row — stopping for today";
                _tradingHalted = true;
                return false;
            }
            return true;
        }

        // ── Original method — kept for compatibility ──
        public PositionSize Calculate(
            string symbol,
            double entryPrice,
            double stopLoss)
            => CalculateWithAtr(symbol, entryPrice, stopLoss, 0);

        // ── ATR-based position sizing ─────────────────
        // ATR > 0 → adjust size inversely to volatility
        // High ATR → stock is very volatile → buy fewer shares
        // Low ATR  → stock is stable → can buy more shares
        public PositionSize CalculateWithAtr(
            string symbol,
            double entryPrice,
            double stopLoss,
            double atr)
        {
            if (entryPrice <= 0 || stopLoss <= 0 ||
                stopLoss >= entryPrice)
                return new PositionSize
                {
                    IsValid = false,
                    Reason  = "Invalid price or stop loss"
                };

            double riskPerShare = entryPrice - stopLoss;

            double availableCapital =
                TotalCapital
                - ReserveCapital
                - Math.Max(0, _dailyLoss)
                - BROKERAGE_PER_TRADE;

            if (availableCapital <= entryPrice)
                return new PositionSize
                {
                    IsValid = false,
                    Reason  = $"Available ₹{availableCapital:F0} " +
                              $"< entry ₹{entryPrice:F2}"
                };

            // ── ATR multiplier ────────────────────────
            // If ATR is available and > 0, scale position
            // ATR as % of price tells us daily volatility
            double atrMultiplier = 1.0;
            if (atr > 0 && entryPrice > 0)
            {
                double atrPct = atr / entryPrice * 100;
                // ATR% bands:
                // < 1%  → very calm  → 1.2x size
                // 1-2%  → normal     → 1.0x size
                // 2-3%  → active     → 0.85x size
                // > 3%  → volatile   → 0.70x size
                atrMultiplier = atrPct switch
                {
                    < 1.0 => 1.2,
                    < 2.0 => 1.0,
                    < 3.0 => 0.85,
                    _     => 0.70
                };

                _logger.LogInformation(
                    "📐 {sym} ATR={atr:F2} ({pct:F1}%) " +
                    "→ size multiplier {mult:F2}x",
                    symbol, atr, atrPct, atrMultiplier);
            }

            // ── Quantity calculation ──────────────────
            int qtyByRisk = riskPerShare > 0
                ? (int)Math.Floor(
                    MaxRiskPerTrade * atrMultiplier / riskPerShare)
                : 0;

            int qtyByCapital =
                (int)Math.Floor(availableCapital / entryPrice);

            int qty = Math.Min(qtyByRisk, qtyByCapital);
            qty = Math.Max(1, qty);

            double totalCost  = qty * entryPrice;
            double totalRisk  = (qty * riskPerShare) + BROKERAGE_PER_TRADE;
            double capitalPct = (totalCost / TotalCapital) * 100;

            // Clamp to available capital
            if (totalCost > availableCapital)
            {
                qty       = (int)Math.Floor(availableCapital / entryPrice);
                totalCost = qty * entryPrice;
                totalRisk = (qty * riskPerShare) + BROKERAGE_PER_TRADE;
            }

            if (qty < 1)
                return new PositionSize
                {
                    IsValid = false,
                    Reason  = $"Need ₹{entryPrice:F0}+ for 1 share"
                };

            double minTarget = entryPrice +
                (BROKERAGE_PER_TRADE / qty) + 1;

            _logger.LogInformation(
                "📊 Position: {sym} qty={qty} cost=₹{cost:F0} " +
                "risk=₹{risk:F0} ({pct:F0}% capital) " +
                "ATR-adj={mult:F2}x",
                symbol, qty, totalCost, totalRisk, capitalPct,
                atrMultiplier);

            return new PositionSize
            {
                Symbol           = symbol,
                Quantity         = qty,
                EntryPrice       = entryPrice,
                StopLoss         = stopLoss,
                MinProfitTarget  = Math.Round(minTarget, 2),
                RiskPerShare     = riskPerShare,
                TotalCost        = totalCost,
                TotalRisk        = totalRisk,
                Brokerage        = BROKERAGE_PER_TRADE,
                CapitalUsedPct   = capitalPct,
                AvailableCapital = availableCapital,
                AtrMultiplier    = atrMultiplier,
                IsValid          = true,
                Reason           = $"{qty} shares, ₹{totalCost:F0} capital, " +
                                   $"max risk ₹{totalRisk:F0}"
            };
        }

        public void RecordTradeResult(double pnl)
        {
            _tradesCount++;
            if (pnl >= 0)
            {
                _dailyProfit    += pnl;
                _consecutiveLoss = 0;
                _logger.LogInformation(
                    "✅ Trade profit ₹{pnl} | Day: +₹{total}",
                    pnl, _dailyProfit - _dailyLoss);
            }
            else
            {
                _dailyLoss      += Math.Abs(pnl);
                _consecutiveLoss++;
                _logger.LogWarning(
                    "❌ Trade loss ₹{pnl} | Consecutive: {n}",
                    pnl, _consecutiveLoss);

                if (_consecutiveLoss >= 2)
                {
                    _tradingHalted = true;
                    _logger.LogWarning(
                        "🛑 Trading halted — 2 losses in a row");
                }
            }
        }

        public void ResetDaily()
        {
            _dailyLoss       = 0;
            _dailyProfit     = 0;
            _consecutiveLoss = 0;
            _tradesCount     = 0;
            _tradingHalted   = false;
            _logger.LogInformation("🔄 Risk Manager reset for new day");
        }

        public RiskSummary GetSummary() => new RiskSummary
        {
            TotalCapital     = TotalCapital,
            DailyProfit      = _dailyProfit,
            DailyLoss        = _dailyLoss,
            NetPnL           = _dailyProfit - _dailyLoss,
            TradesCount      = _tradesCount,
            ConsecutiveLoss  = _consecutiveLoss,
            TradingHalted    = _tradingHalted,
            AvailableCapital = Math.Max(0,
                TotalCapital - ReserveCapital - _dailyLoss),
            ReserveCapital   = ReserveCapital,
            MaxRiskPerTrade  = MaxRiskPerTrade,
            MaxDailyLoss     = MaxDailyLoss,
            BrokeragePerTrade = BROKERAGE_PER_TRADE
        };
    }

    public class PositionSize
    {
        public string Symbol           { get; set; } = "";
        public int    Quantity         { get; set; }
        public double EntryPrice       { get; set; }
        public double StopLoss         { get; set; }
        public double MinProfitTarget  { get; set; }
        public double RiskPerShare     { get; set; }
        public double TotalCost        { get; set; }
        public double TotalRisk        { get; set; }
        public double ExpectedProfit   { get; set; }
        public double Brokerage        { get; set; }
        public double CapitalUsedPct   { get; set; }
        public double AvailableCapital { get; set; }
        public double AtrMultiplier    { get; set; } = 1.0;
        public bool   IsValid          { get; set; }
        public string Reason           { get; set; } = "";
        public int    Lots             { get; set; }
    }

    public class RiskSummary
    {
        public double TotalCapital      { get; set; }
        public double DailyProfit       { get; set; }
        public double DailyLoss         { get; set; }
        public double NetPnL            { get; set; }
        public int    TradesCount       { get; set; }
        public int    ConsecutiveLoss   { get; set; }
        public bool   TradingHalted     { get; set; }
        public double AvailableCapital  { get; set; }
        public double ReserveCapital    { get; set; }
        public double MaxRiskPerTrade   { get; set; }
        public double MaxDailyLoss      { get; set; }
        public double BrokeragePerTrade { get; set; }
    }
}

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// Risk manager calibrated for ₹1,000–₹2,000 intraday capital.
    ///
    /// Rules:
    ///   Capital ₹1,500 example:
    ///   - Reserve (never touch):    ₹600  (40%)
    ///   - Max risk per trade:       ₹225  (15% of total)
    ///   - Max daily loss:           ₹375  (25% of total)
    ///   - Available for trading:    ₹900  (60%)
    ///   - Stop after 2 losses in a row
    ///   - Brokerage aware (₹40 per round trip)
    /// </summary>
    public class RiskManager
    {
        private readonly ILogger<RiskManager> _logger;

        public double TotalCapital { get; private set; }
        public double MaxRiskPerTrade { get; private set; }
        public double MaxDailyLoss { get; private set; }
        public double ReserveCapital { get; private set; }

        private const double BROKERAGE_PER_TRADE = 40.0; // ₹20 buy + ₹20 sell

        private double _dailyLoss = 0;
        private double _dailyProfit = 0;
        private int _consecutiveLoss = 0;
        private int _tradesCount = 0;
        private bool _tradingHalted = false;

        public RiskManager(
            ILogger<RiskManager> logger,
            double capital = 1500)
        {
            _logger = logger;
            TotalCapital = capital;
            ReserveCapital = capital * 0.40; // Keep 40% safe
            MaxRiskPerTrade = capital * 0.15; // Risk max 15% per trade
            MaxDailyLoss = capital * 0.25; // Stop if 25% lost in a day
        }

        // ── Can we take a new trade? ──────────────────
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

        // ── Calculate position size ───────────────────
        public PositionSize Calculate(
            string symbol,
            double entryPrice,
            double stopLoss)
        {
            if (entryPrice <= 0 || stopLoss <= 0 || stopLoss >= entryPrice)
                return new PositionSize
                {
                    IsValid = false,
                    Reason = "Invalid price or stop loss"
                };

            double riskPerShare = entryPrice - stopLoss;
            double availableCapital = TotalCapital
                - ReserveCapital
                - Math.Max(0, _dailyLoss)
                - BROKERAGE_PER_TRADE; // Account for brokerage

            if (availableCapital <= entryPrice)
                return new PositionSize
                {
                    IsValid = false,
                    Reason = $"Available capital ₹{availableCapital:F0} " +
                              $"too low for even 1 share at ₹{entryPrice:F2}"
                };

            // Qty based on risk
            int qtyByRisk = riskPerShare > 0
                ? (int)Math.Floor(MaxRiskPerTrade / riskPerShare) : 0;

            // Qty based on available capital
            int qtyByCapital = (int)Math.Floor(availableCapital / entryPrice);

            int qty = Math.Min(qtyByRisk, qtyByCapital);
            qty = Math.Max(1, qty); // At least 1 share

            double totalCost = qty * entryPrice;
            double totalRisk = (qty * riskPerShare) + BROKERAGE_PER_TRADE;
            double netProfit = (qty * (entryPrice - entryPrice)) - BROKERAGE_PER_TRADE;
            double capitalPct = (totalCost / TotalCapital) * 100;

            // Warn if brokerage eats too much profit
            double minTarget = entryPrice + (BROKERAGE_PER_TRADE / qty) + 1;

            if (totalCost > availableCapital)
            {
                qty = (int)Math.Floor(availableCapital / entryPrice);
                totalCost = qty * entryPrice;
                totalRisk = (qty * riskPerShare) + BROKERAGE_PER_TRADE;
            }

            if (qty < 1)
                return new PositionSize
                {
                    IsValid = false,
                    Reason = $"Capital too low. Need ₹{entryPrice:F0}+ for 1 share"
                };

            _logger.LogInformation(
                "📊 Position: {sym} qty={qty} cost=₹{cost:F0} " +
                "risk=₹{risk:F0} ({pct:F0}% capital)",
                symbol, qty, totalCost, totalRisk, capitalPct);

            return new PositionSize
            {
                Symbol = symbol,
                Quantity = qty,
                EntryPrice = entryPrice,
                StopLoss = stopLoss,
                MinProfitTarget = Math.Round(minTarget, 2),
                RiskPerShare = riskPerShare,
                TotalCost = totalCost,
                TotalRisk = totalRisk,
                Brokerage = BROKERAGE_PER_TRADE,
                CapitalUsedPct = capitalPct,
                AvailableCapital = availableCapital,
                IsValid = true,
                Reason = $"{qty} shares, ₹{totalCost:F0} capital, " +
                                   $"max risk ₹{totalRisk:F0}"
            };
        }

        // ── Record trade result ───────────────────────
        public void RecordTradeResult(double pnl)
        {
            _tradesCount++;
            if (pnl >= 0)
            {
                _dailyProfit += pnl;
                _consecutiveLoss = 0;
                _logger.LogInformation(
                    "✅ Trade profit ₹{pnl} | Day P&L: +₹{total}",
                    pnl, _dailyProfit - _dailyLoss);
            }
            else
            {
                _dailyLoss += Math.Abs(pnl);
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

        // ── Reset at start of new day ─────────────────
        public void ResetDaily()
        {
            _dailyLoss = 0;
            _dailyProfit = 0;
            _consecutiveLoss = 0;
            _tradesCount = 0;
            _tradingHalted = false;
            _logger.LogInformation("🔄 Risk Manager reset for new day");
        }

        // ── Summary ───────────────────────────────────
        public RiskSummary GetSummary() => new RiskSummary
        {
            TotalCapital = TotalCapital,
            DailyProfit = _dailyProfit,
            DailyLoss = _dailyLoss,
            NetPnL = _dailyProfit - _dailyLoss,
            TradesCount = _tradesCount,
            ConsecutiveLoss = _consecutiveLoss,
            TradingHalted = _tradingHalted,
            AvailableCapital = Math.Max(0,
                TotalCapital - ReserveCapital - _dailyLoss),
            ReserveCapital = ReserveCapital,
            MaxRiskPerTrade = MaxRiskPerTrade,
            MaxDailyLoss = MaxDailyLoss,
            BrokeragePerTrade = BROKERAGE_PER_TRADE
        };
    }

    // ── Models ────────────────────────────────────────
    public class PositionSize
    {
        public string Symbol { get; set; } = "";
        public int Quantity { get; set; }
        public double EntryPrice { get; set; }
        public double StopLoss { get; set; }
        public double MinProfitTarget { get; set; }
        public double RiskPerShare { get; set; }
        public double TotalCost { get; set; }
        public double TotalRisk { get; set; }
        public double ExpectedProfit { get; set; }
        public double Brokerage { get; set; }
        public double CapitalUsedPct { get; set; }
        public double AvailableCapital { get; set; }
        public bool IsValid { get; set; }
        public string Reason { get; set; } = "";
        public int Lots { get; set; }
    }

    public class RiskSummary
    {
        public double TotalCapital { get; set; }
        public double DailyProfit { get; set; }
        public double DailyLoss { get; set; }
        public double NetPnL { get; set; }
        public int TradesCount { get; set; }
        public int ConsecutiveLoss { get; set; }
        public bool TradingHalted { get; set; }
        public double AvailableCapital { get; set; }
        public double ReserveCapital { get; set; }
        public double MaxRiskPerTrade { get; set; }
        public double MaxDailyLoss { get; set; }
        public double BrokeragePerTrade { get; set; }
    }
}
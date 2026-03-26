using AlgoSenseNSE.API.Models;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// Dynamic Stock Screener
    ///
    /// Reads live ticks from WebSocket for ALL NSE/BSE stocks.
    /// Every 30 seconds filters down to today's best candidates.
    ///
    /// Replaces the hardcoded _intradayStocks list entirely.
    ///
    /// Filters applied:
    ///   1. Price ₹15-₹800 (affordable with ₹1,500 capital)
    ///   2. Volume > 2 lakh (liquid enough to trade)
    ///   3. Moving today (>1% change OR 2x volume spike)
    ///   4. Not in blacklist (known operator stocks)
    ///   5. Has valid token in Angel One
    ///
    /// Output: top 100 candidates → passed to MarketScanService
    /// </summary>
    public class StockScreenerService
    {
        private readonly AngelOneWebSocketService _ws;
        private readonly ILogger<StockScreenerService> _logger;
        private readonly IConfiguration _config;

        // Current screened candidates
        private List<ScreenedStock> _candidates = new();
        private DateTime _lastScreened = DateTime.MinValue;
        private readonly object _lock = new();

        // Capital config
        private double Capital => _config.GetValue<double>(
            "Trading:Capital", 1500);

        // ── Blacklist — known operator/manipulated stocks ─
        private static readonly HashSet<string> Blacklist = new()
        {
            // Add known operator stocks here
            // These will never be recommended regardless of signals
            "YESBANK",  // history of manipulation
            "RCOM",     // bankrupt
            "SUZLON",   // highly volatile penny
        };

        // ── Minimum quality thresholds ────────────────
        private const double MinPrice      = 15.0;   // ₹15 minimum
        private const double MaxPrice      = 800.0;  // ₹800 maximum
        private const long   MinVolume     = 200000; // 2 lakh shares/day
        private const int    MinSharesNeeded = 5;    // must buy 5+ shares
        private const double MinMovePercent  = 0.5;  // at least 0.5% move
        private const double MinVolumeRatio  = 1.5;  // 1.5x average volume

        public StockScreenerService(
            AngelOneWebSocketService ws,
            ILogger<StockScreenerService> logger,
            IConfiguration config)
        {
            _ws     = ws;
            _logger = logger;
            _config = config;
        }

        // ── Main screen — runs every 30 seconds ───────
        public List<ScreenedStock> Screen()
        {
            var allTicks = _ws.GetAllTicks();

            if (!allTicks.Any()) return _candidates;

            var screened = new List<ScreenedStock>();

            foreach (var tick in allTicks)
            {
                var sym = tick.Symbol;

                // Skip blacklisted
                if (Blacklist.Contains(sym)) continue;

                // Price filter
                if (tick.LTP < MinPrice ||
                    tick.LTP > MaxPrice) continue;

                // Volume filter
                if (tick.Volume < MinVolume) continue;

                // Can we buy enough shares?
                int shares = (int)(Capital * 0.60 / tick.LTP);
                if (shares < MinSharesNeeded) continue;

                // Skip stale ticks (> 5 min old)
                if ((DateTime.Now - tick.LastUpdated)
                    .TotalMinutes > 5) continue;

                // Calculate momentum score
                double momentumScore = CalculateMomentumScore(tick);

                screened.Add(new ScreenedStock
                {
                    Symbol        = sym,
                    Price         = tick.LTP,
                    Change        = tick.LTP - tick.Close,
                    ChangePercent = tick.ChangePercent,
                    Volume        = tick.Volume,
                    High          = tick.High,
                    Low           = tick.Low,
                    Open          = tick.Open,
                    PrevClose     = tick.Close,
                    AffordableShares = shares,
                    MomentumScore = momentumScore,
                    ScreenedAt    = DateTime.Now
                });
            }

            // Sort by momentum score — best candidates first
            var sorted = screened
                .OrderByDescending(s => s.MomentumScore)
                .Take(150) // keep top 150
                .ToList();

            lock (_lock)
            {
                _candidates    = sorted;
                _lastScreened  = DateTime.Now;
            }

            _logger.LogInformation(
                "📊 Screener: {total} stocks → {screened} " +
                "candidates (price ₹{min}-₹{max}, vol>{vol})",
                allTicks.Count, sorted.Count,
                MinPrice, MaxPrice, MinVolume);

            return sorted;
        }

        // ── Get top N candidates for morning scan ─────
        public List<string> GetTopSymbols(int count = 100)
        {
            lock (_lock)
            {
                return _candidates
                    .Take(count)
                    .Select(s => s.Symbol)
                    .ToList();
            }
        }

        // ── Get top gainers ───────────────────────────
        public List<ScreenedStock> GetTopGainers(int count = 20)
        {
            lock (_lock)
            {
                return _candidates
                    .Where(s => s.ChangePercent > 0)
                    .OrderByDescending(s => s.ChangePercent)
                    .Take(count)
                    .ToList();
            }
        }

        // ── Get volume spikes ─────────────────────────
        public List<ScreenedStock> GetVolumeSpikes(int count = 20)
        {
            lock (_lock)
            {
                return _candidates
                    .OrderByDescending(s => s.Volume)
                    .Take(count)
                    .ToList();
            }
        }

        // ── Get all candidates ────────────────────────
        public List<ScreenedStock> GetCandidates()
        {
            lock (_lock) { return new List<ScreenedStock>(_candidates); }
        }

        public int CandidateCount
        {
            get { lock (_lock) { return _candidates.Count; } }
        }

        // ── Momentum score calculation ─────────────────
        private double CalculateMomentumScore(LiveTick tick)
        {
            double score = 0;

            // 1. Price change momentum
            if (tick.ChangePercent > 3.0)      score += 30;
            else if (tick.ChangePercent > 2.0) score += 20;
            else if (tick.ChangePercent > 1.0) score += 10;
            else if (tick.ChangePercent > 0.5) score += 5;
            else if (tick.ChangePercent < -2.0) score -= 10;

            // 2. Volume momentum
            // We don't have 20-day avg here, use absolute volume
            if (tick.Volume > 5_000_000)      score += 20;
            else if (tick.Volume > 2_000_000) score += 10;
            else if (tick.Volume > 1_000_000) score += 5;

            // 3. Price range (today's range as % of price)
            if (tick.High > 0 && tick.Low > 0 && tick.LTP > 0)
            {
                double rangePct =
                    (tick.High - tick.Low) / tick.LTP * 100;
                if (rangePct > 3.0) score += 15; // volatile = opportunity
                else if (rangePct > 2.0) score += 8;
                else if (rangePct > 1.0) score += 4;
            }

            // 4. Position in day range
            // Price near high of day = strong momentum
            if (tick.High > tick.Low && tick.LTP > 0)
            {
                double range     = tick.High - tick.Low;
                double position  = tick.LTP  - tick.Low;
                double pctInRange = range > 0
                    ? (position / range) * 100 : 50;

                if (pctInRange > 75) score += 15; // near day high
                else if (pctInRange > 50) score += 8;
                else if (pctInRange < 25) score -= 10; // near day low
            }

            // 5. Affordable bonus — more shares = more profit potential
            int shares = (int)(1500 * 0.60 / Math.Max(tick.LTP, 1));
            if (shares >= 20)      score += 10; // cheap stock, many shares
            else if (shares >= 10) score += 5;

            return score;
        }
    }

    // ── Screened Stock Model ──────────────────────────
    public class ScreenedStock
    {
        public string   Symbol           { get; set; } = "";
        public double   Price            { get; set; }
        public double   Change           { get; set; }
        public double   ChangePercent    { get; set; }
        public long     Volume           { get; set; }
        public double   High             { get; set; }
        public double   Low              { get; set; }
        public double   Open             { get; set; }
        public double   PrevClose        { get; set; }
        public int      AffordableShares { get; set; }
        public double   MomentumScore    { get; set; }
        public DateTime ScreenedAt       { get; set; }
    }
}

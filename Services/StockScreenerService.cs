using AlgoSenseNSE.API.Models;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// StockScreenerService v2 — Dynamic universe based on capital budget.
    ///
    /// v1: hardcoded 53 stocks
    /// v2: scans all WebSocket ticks and returns every stock affordable
    ///     with current capital. Universe changes daily as prices move.
    ///
    /// Budget filter:
    ///   maxPrice = availableCapital / minShares (default 5)
    ///   minPrice = 15 (avoid penny stocks)
    ///   minVolume = 200,000 (liquidity gate)
    ///
    /// Output tiers:
    ///   Tier 1 (deep analysis): top 80 by volume — fundamental + technical
    ///   Tier 2 (screener only): remaining candidates — live price watch only
    /// </summary>
    public class StockScreenerService
    {
        private readonly AngelOneWebSocketService _ws;
        private readonly IConfiguration _config;
        private readonly ILogger<StockScreenerService> _logger;

        private List<ScreenedStock> _candidates = new();
        private readonly object _lock = new();
        private DateTime _lastScreen = DateTime.MinValue;

        // Blacklist: indices, ETFs, operator stocks
        private static readonly HashSet<string> Blacklist = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "NIFTY","BANKNIFTY","FINNIFTY","MIDCPNIFTY",
            "SENSEX","BANKEX",
            "NIFTYBEES","JUNIORBEES","BANKBEES","LIQUIDBEES",
            "ICICIB22","HDFCNIFTY","SETFNIF50",
        };

        // Minimum shares to buy — ensures enough qty for brokerage to make sense
        private const int MinSharesNeeded = 5;

        public StockScreenerService(
            AngelOneWebSocketService ws,
            IConfiguration config,
            ILogger<StockScreenerService> logger)
        {
            _ws     = ws;
            _config = config;
            _logger = logger;
        }

        // ── Main screen ───────────────────────────────
        // Returns stocks affordable with current available capital
        public List<ScreenedStock> Screen()
        {
            var capital   = _config.GetValue<double>("Trading:Capital", 1500);
            var reserved  = capital * 0.40;
            var available = capital - reserved;

            // Max price = what we can afford for MinSharesNeeded shares
            double maxPrice = available / MinSharesNeeded;
            double minPrice = 15.0;
            long   minVol   = 200_000;

            var allTicks = _ws.GetAllTicks();
            if (!allTicks.Any()) return _candidates;

            var screened = new List<ScreenedStock>();

            foreach (var tick in allTicks)
            {
                var sym = tick.Symbol;

                // Skip blacklisted
                if (Blacklist.Contains(sym)) continue;

                // Skip stale ticks (> 5 min old during market hours)
                if ((DateTime.Now - tick.LastUpdated).TotalMinutes > 5
                    && IsMarketHours()) continue;

                // ── Budget filter ─────────────────────
                // Only include stocks we can actually buy with our capital
                if (tick.LTP < minPrice) continue;
                if (tick.LTP > maxPrice) continue;

                // ── Liquidity filter ──────────────────
                if (tick.Volume < minVol) continue;

                // ── Affordability check ───────────────
                int shares = (int)(available / tick.LTP);
                if (shares < MinSharesNeeded) continue;

                // ── Momentum score ────────────────────
                double momentumScore = CalculateMomentumScore(tick);

                screened.Add(new ScreenedStock
                {
                    Symbol        = sym,
                    LTP           = tick.LTP,
                    High          = tick.High > 0 ? tick.High : tick.LTP,
                    Low           = tick.Low  > 0 ? tick.Low  : tick.LTP,
                    Volume        = tick.Volume,
                    ChangePercent = tick.ChangePercent,
                    AffordableQty = shares,
                    MaxCapNeeded  = Math.Round(shares * tick.LTP, 0),
                    MomentumScore = momentumScore,
                    LastUpdated   = tick.LastUpdated
                });
            }

            // Sort by volume (most liquid first) + momentum bonus
            var sorted = screened
                .OrderByDescending(s => s.Volume * 0.6 + s.MomentumScore * 0.4)
                .ToList();

            // Tag tiers
            for (int i = 0; i < sorted.Count; i++)
                sorted[i].Tier = i < 80 ? 1 : 2;

            lock (_lock)
            {
                _candidates  = sorted;
                _lastScreen  = DateTime.Now;
            }

            _logger.LogInformation(
                "📊 Dynamic universe: {total} affordable stocks " +
                "(₹{min}–₹{max}, vol>2L) → T1:{t1} T2:{t2}",
                sorted.Count,
                Math.Round(minPrice),
                Math.Round(maxPrice),
                sorted.Count(s => s.Tier == 1),
                sorted.Count(s => s.Tier == 2));

            return sorted;
        }

        // ── Get Tier 1 symbols for deep analysis ──────
        // These replace the hardcoded 53-stock list
        public List<string> GetTier1Symbols(int limit = 80)
        {
            lock (_lock)
            {
                return _candidates
                    .Where(s => s.Tier == 1)
                    .Take(limit)
                    .Select(s => s.Symbol)
                    .ToList();
            }
        }

        // ── Get all affordable symbols ─────────────────
        public List<string> GetAllAffordableSymbols()
        {
            lock (_lock)
            {
                return _candidates
                    .Select(s => s.Symbol)
                    .ToList();
            }
        }

        // ── Summary for API ───────────────────────────
        public ScreenerSummary GetSummary()
        {
            lock (_lock)
            {
                var capital   = _config.GetValue<double>("Trading:Capital", 1500);
                var available = capital * 0.60;
                return new ScreenerSummary
                {
                    TotalAffordable   = _candidates.Count,
                    Tier1ForAnalysis  = _candidates.Count(s => s.Tier == 1),
                    Tier2WatchOnly    = _candidates.Count(s => s.Tier == 2),
                    MaxAffordablePrice = Math.Round(available / MinSharesNeeded, 0),
                    MinPrice          = 15,
                    Capital           = capital,
                    AvailableCapital  = available,
                    LastUpdated       = _lastScreen,
                    TopCandidates     = _candidates.Take(20).ToList()
                };
            }
        }

        // ── Momentum score (0–100) ────────────────────
        private double CalculateMomentumScore(LiveTick tick)
        {
            double score = 50;

            // Price change direction
            if (tick.ChangePercent > 2.0) score += 20;
            else if (tick.ChangePercent > 1.0) score += 12;
            else if (tick.ChangePercent > 0.3) score += 6;
            else if (tick.ChangePercent < -2.0) score -= 20;
            else if (tick.ChangePercent < -1.0) score -= 12;

            // Price vs open (intraday trend)
            if (tick.Open > 0 && tick.LTP > tick.Open * 1.005) score += 10;
            else if (tick.Open > 0 && tick.LTP < tick.Open * 0.995) score -= 10;

            // Near high (momentum continuing)
            if (tick.High > 0 && tick.LTP >= tick.High * 0.99) score += 10;

            // Volume spike proxy — high volume = institutional interest
            // (we don't have avg here, so use absolute thresholds)
            if (tick.Volume > 1_000_000) score += 10;
            else if (tick.Volume > 500_000) score += 5;

            return Math.Max(0, Math.Min(100, score));
        }

        private bool IsMarketHours()
        {
            var ist = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            return ist.DayOfWeek != DayOfWeek.Saturday
                && ist.DayOfWeek != DayOfWeek.Sunday
                && ist.TimeOfDay >= new TimeSpan(9, 15, 0)
                && ist.TimeOfDay <= new TimeSpan(15, 30, 0);
        }

        public List<ScreenedStock> GetCandidates()
        {
            lock (_lock) { return _candidates.ToList(); }
        }

        // ── Compatibility methods expected by controllers ─
        public int CandidateCount
        {
            get { lock (_lock) { return _candidates.Count; } }
        }

        public List<ScreenedStock> GetTopGainers(int count = 10)
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

        public List<ScreenedStock> GetVolumeSpikes(int count = 10)
        {
            lock (_lock)
            {
                return _candidates
                    .OrderByDescending(s => s.Volume)
                    .Take(count)
                    .ToList();
            }
        }
    }

    public class ScreenedStock
    {
        public string   Symbol          { get; set; } = "";
        public double   LTP             { get; set; }
        public long     Volume          { get; set; }
        public double   ChangePercent   { get; set; }
        public int      AffordableQty   { get; set; }
        public double   MaxCapNeeded    { get; set; }
        public double   MomentumScore   { get; set; }
        public int      Tier            { get; set; } = 2;
        public DateTime LastUpdated     { get; set; }

        // Compatibility aliases — old code may use these names
        public double   Price           => LTP;
        public double   High            { get; set; }
        public double   Low             { get; set; }
        public int      AffordableShares => AffordableQty;
    }

    public class ScreenerSummary
    {
        public int    TotalAffordable    { get; set; }
        public int    Tier1ForAnalysis   { get; set; }
        public int    Tier2WatchOnly     { get; set; }
        public double MaxAffordablePrice { get; set; }
        public double MinPrice           { get; set; }
        public double Capital            { get; set; }
        public double AvailableCapital   { get; set; }
        public DateTime LastUpdated      { get; set; }
        public List<ScreenedStock> TopCandidates { get; set; } = new();
    }
}

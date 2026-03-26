using AlgoSenseNSE.API.Models;
using Microsoft.Data.Sqlite;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// Tracks trades that almost passed but were rejected by 1-2 final gates.
    /// 
    /// Why this matters:
    /// After 30+ days you can query: "what would have happened if I had
    /// loosened R:R from 1:2 to 1:1.8?" or "was the ADX gate too strict?"
    /// This is how you tune filters with real data instead of guessing.
    ///
    /// Logged when a stock passes 5+ gates but fails 1-2 final conditions.
    /// Outcomes filled 2 hours later just like real signals.
    /// </summary>
    public class RejectedTradeTracker
    {
        private readonly ILogger<RejectedTradeTracker> _logger;
        private readonly string _dbPath;

        public RejectedTradeTracker(
            ILogger<RejectedTradeTracker> logger,
            IConfiguration config)
        {
            _logger = logger;
            var connStr = config["ConnectionStrings:DefaultConnection"]
                       ?? "Data Source=algosense.db";
            _dbPath = connStr.Replace("Data Source=", "").Trim();
            InitializeTable();
        }

        private void InitializeTable()
        {
            try
            {
                using var conn = new SqliteConnection(
                    $"Data Source={_dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS rejected_trades (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        reject_id       TEXT UNIQUE NOT NULL,
                        symbol          TEXT NOT NULL,
                        reject_date     TEXT NOT NULL,
                        reject_time     TEXT NOT NULL,
                        entry_price     REAL,
                        target_price    REAL,
                        stop_loss       REAL,
                        adj_rr          REAL,
                        reject_reason   TEXT,
                        gates_passed    INTEGER,
                        gates_total     INTEGER,
                        tech_score      REAL,
                        composite_score REAL,
                        rsi             REAL,
                        adx             REAL,
                        supertrend      TEXT,
                        vwap            REAL,
                        india_vix       REAL,
                        nifty_change    REAL,
                        time_window     TEXT,
                        outcome_price   REAL,
                        outcome_result  TEXT,
                        profit_loss_pct REAL,
                        outcome_filled  INTEGER DEFAULT 0,
                        created_at      TEXT DEFAULT CURRENT_TIMESTAMP
                    )";
                cmd.ExecuteNonQuery();
                _logger.LogInformation(
                    "✅ Rejected trade tracker ready");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "⚠️ RejectedTradeTracker init: {m}", ex.Message);
            }
        }

        // ── Log a rejected trade ──────────────────────
        public async Task LogRejectionAsync(
            string symbol,
            double entryPrice,
            double target,
            double stopLoss,
            double adjRR,
            string rejectReason,
            int    gatesPassed,
            int    gatesTotal,
            TechnicalResult? tech,
            CompositeScore?  score,
            MarketContext?   mkt)
        {
            try
            {
                var ist      = GetIST();
                var rejectId = $"REJ_{symbol}_{ist:yyyyMMdd_HHmm}";

                using var conn = new SqliteConnection(
                    $"Data Source={_dbPath}");
                await conn.OpenAsync();

                // Dedup — only one rejection per stock per minute
                using var chk = conn.CreateCommand();
                chk.CommandText =
                    "SELECT COUNT(*) FROM rejected_trades " +
                    "WHERE reject_id=$id";
                chk.Parameters.AddWithValue("$id", rejectId);
                if (Convert.ToInt32(
                    await chk.ExecuteScalarAsync()) > 0) return;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO rejected_trades (
                        reject_id, symbol, reject_date, reject_time,
                        entry_price, target_price, stop_loss, adj_rr,
                        reject_reason, gates_passed, gates_total,
                        tech_score, composite_score, rsi, adx,
                        supertrend, vwap, india_vix, nifty_change,
                        time_window
                    ) VALUES (
                        $id,$sym,$date,$time,
                        $entry,$target,$sl,$rr,
                        $reason,$gp,$gt,
                        $tscore,$cscore,$rsi,$adx,
                        $st,$vwap,$vix,$nchg,
                        $tw
                    )";

                var ist2 = GetIST();
                string timeWindow = ist2.TimeOfDay switch
                {
                    var t when t < new TimeSpan(9, 45, 0)  => "OPEN_VOLATILE",
                    var t when t < new TimeSpan(10, 0, 0)  => "SETTLING",
                    var t when t < new TimeSpan(12, 30, 0) => "BEST_WINDOW",
                    var t when t < new TimeSpan(14, 30, 0) => "MIDDAY",
                    var t when t < new TimeSpan(15, 0, 0)  => "AFTERNOON",
                    _                                       => "CLOSE_VOLATILE"
                };

                cmd.Parameters.AddWithValue("$id",     rejectId);
                cmd.Parameters.AddWithValue("$sym",    symbol);
                cmd.Parameters.AddWithValue("$date",   ist.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$time",   ist.ToString("HH:mm:ss"));
                cmd.Parameters.AddWithValue("$entry",  entryPrice);
                cmd.Parameters.AddWithValue("$target", target);
                cmd.Parameters.AddWithValue("$sl",     stopLoss);
                cmd.Parameters.AddWithValue("$rr",     adjRR);
                cmd.Parameters.AddWithValue("$reason", rejectReason);
                cmd.Parameters.AddWithValue("$gp",     gatesPassed);
                cmd.Parameters.AddWithValue("$gt",     gatesTotal);
                cmd.Parameters.AddWithValue("$tscore", tech?.Score      ?? 0);
                cmd.Parameters.AddWithValue("$cscore", score?.FinalScore ?? 0);
                cmd.Parameters.AddWithValue("$rsi",    tech?.RSI        ?? 0);
                cmd.Parameters.AddWithValue("$adx",    tech?.ADX        ?? 0);
                cmd.Parameters.AddWithValue("$st",
                    tech?.SupertrendBullish == true ? "BUY" : "SELL");
                cmd.Parameters.AddWithValue("$vwap",   tech?.VWAP       ?? 0);
                cmd.Parameters.AddWithValue("$vix",    mkt?.IndiaVix    ?? 0);
                cmd.Parameters.AddWithValue("$nchg",   mkt?.NiftyChange ?? 0);
                cmd.Parameters.AddWithValue("$tw",     timeWindow);

                await cmd.ExecuteNonQueryAsync();

                _logger.LogInformation(
                    "📋 Rejected: {sym} reason='{reason}' " +
                    "gates={gp}/{gt} adjRR={rr:F2}",
                    symbol, rejectReason, gatesPassed, gatesTotal, adjRR);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "⚠️ LogRejection {sym}: {m}", symbol, ex.Message);
            }
        }

        // ── Fill outcomes for rejected trades ─────────
        public async Task FillRejectedOutcomesAsync(
            Func<string, Task<double>> getPriceFunc)
        {
            try
            {
                var ist    = GetIST();
                var cutoff = ist.AddHours(-2).ToString("HH:mm:ss");
                var today  = ist.ToString("yyyy-MM-dd");

                using var conn = new SqliteConnection(
                    $"Data Source={_dbPath}");
                await conn.OpenAsync();

                var pending = new List<(int id, string sym,
                    double entry, double tgt, double sl)>();

                using var sel = conn.CreateCommand();
                sel.CommandText = @"
                    SELECT id, symbol, entry_price, target_price, stop_loss
                    FROM rejected_trades
                    WHERE outcome_filled=0
                    AND reject_date=$today
                    AND reject_time<=$cutoff";
                sel.Parameters.AddWithValue("$today",  today);
                sel.Parameters.AddWithValue("$cutoff", cutoff);

                using var rd = await sel.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    pending.Add((
                        rd.GetInt32(0), rd.GetString(1),
                        rd.IsDBNull(2) ? 0 : rd.GetDouble(2),
                        rd.IsDBNull(3) ? 0 : rd.GetDouble(3),
                        rd.IsDBNull(4) ? 0 : rd.GetDouble(4)));

                foreach (var (id, sym, entry, tgt, sl) in pending)
                {
                    try
                    {
                        var price = await getPriceFunc(sym);
                        if (price <= 0) continue;

                        string result;
                        double plPct;
                        if (tgt > 0 && price >= tgt)
                        { result = "WOULD_HIT_TARGET"; plPct = (tgt - entry) / entry * 100; }
                        else if (sl > 0 && price <= sl)
                        { result = "WOULD_HIT_SL"; plPct = (sl - entry) / entry * 100; }
                        else
                        { result = "WOULD_EXPIRE"; plPct = (price - entry) / entry * 100; }

                        using var upd = conn.CreateCommand();
                        upd.CommandText = @"
                            UPDATE rejected_trades SET
                                outcome_price=$p,
                                outcome_result=$r,
                                profit_loss_pct=$pp,
                                outcome_filled=1
                            WHERE id=$id";
                        upd.Parameters.AddWithValue("$p",  price);
                        upd.Parameters.AddWithValue("$r",  result);
                        upd.Parameters.AddWithValue("$pp", Math.Round(plPct, 2));
                        upd.Parameters.AddWithValue("$id", id);
                        await upd.ExecuteNonQueryAsync();
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "⚠️ FillRejectedOutcomes: {m}", ex.Message);
            }
        }

        // ── Query insights ────────────────────────────
        public async Task<RejectionInsights> GetInsightsAsync(int days = 30)
        {
            var insights = new RejectionInsights();
            try
            {
                var cutoff = DateTime.Now.AddDays(-days)
                    .ToString("yyyy-MM-dd");

                using var conn = new SqliteConnection(
                    $"Data Source={_dbPath}");
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT
                        COUNT(*) as total,
                        SUM(CASE WHEN outcome_result='WOULD_HIT_TARGET'
                            THEN 1 ELSE 0 END) as would_win,
                        AVG(adj_rr) as avg_rr,
                        reject_reason,
                        COUNT(*) as reason_count
                    FROM rejected_trades
                    WHERE reject_date>=$cutoff
                    AND outcome_filled=1
                    GROUP BY reject_reason
                    ORDER BY reason_count DESC";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);

                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    insights.TotalRejected += r.IsDBNull(0) ? 0 : r.GetInt32(0);
                    insights.WouldHaveWon  += r.IsDBNull(1) ? 0 : r.GetInt32(1);
                    insights.ReasonBreakdown.Add(new RejectionReason
                    {
                        Reason      = r.IsDBNull(3) ? "" : r.GetString(3),
                        Count       = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                        WinRate     = r.IsDBNull(0) || r.GetInt32(0) == 0 ? 0
                            : Math.Round(
                                (double)(r.IsDBNull(1) ? 0 : r.GetInt32(1))
                                / r.GetInt32(0) * 100, 1),
                        AvgAdjRR    = r.IsDBNull(2) ? 0
                            : Math.Round(r.GetDouble(2), 2)
                    });
                }

                insights.WouldHaveWonPct = insights.TotalRejected > 0
                    ? Math.Round(
                        (double)insights.WouldHaveWon
                        / insights.TotalRejected * 100, 1)
                    : 0;

                insights.Insight = insights.WouldHaveWonPct > 60
                    ? "⚠️ Many rejected trades would have won — " +
                      "consider loosening gates slightly"
                    : insights.WouldHaveWonPct > 45
                    ? "✅ Rejection rate looks reasonable"
                    : "✅ Gates are working — rejected trades mostly losers";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "⚠️ GetInsights: {m}", ex.Message);
            }
            return insights;
        }

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
    }

    public class RejectionInsights
    {
        public int    TotalRejected     { get; set; }
        public int    WouldHaveWon      { get; set; }
        public double WouldHaveWonPct   { get; set; }
        public string Insight           { get; set; } = "";
        public List<RejectionReason> ReasonBreakdown { get; set; } = new();
    }

    public class RejectionReason
    {
        public string Reason   { get; set; } = "";
        public int    Count    { get; set; }
        public double WinRate  { get; set; }
        public double AvgAdjRR { get; set; }
    }
}

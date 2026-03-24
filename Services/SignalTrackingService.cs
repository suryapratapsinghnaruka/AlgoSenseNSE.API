using Microsoft.Data.Sqlite;
using AlgoSenseNSE.API.Models;

namespace AlgoSenseNSE.API.Services
{
    public class SignalTrackingService
    {
        private readonly ILogger<SignalTrackingService> _logger;
        private readonly string _dbPath;

        public SignalTrackingService(
            ILogger<SignalTrackingService> logger,
            IConfiguration config)
        {
            _logger = logger;
            var connStr = config["ConnectionStrings:DefaultConnection"]
                       ?? "Data Source=algosense.db";
            _dbPath = connStr.Replace("Data Source=", "").Trim();
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS signals (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        signal_id       TEXT UNIQUE NOT NULL,
                        symbol          TEXT NOT NULL,
                        signal_date     TEXT NOT NULL,
                        signal_time     TEXT NOT NULL,
                        entry_price     REAL,
                        target_price    REAL,
                        stop_loss       REAL,
                        risk_reward     TEXT,
                        confidence      INTEGER,
                        ai_summary      TEXT,
                        rsi             REAL,
                        macd_hist       REAL,
                        supertrend      TEXT,
                        adx             REAL,
                        vwap            REAL,
                        price_vs_vwap   TEXT,
                        tech_score      REAL,
                        nifty_change    REAL,
                        india_vix       REAL,
                        fii_net         REAL,
                        market_quality  INTEGER,
                        fund_score      REAL,
                        outcome_price   REAL,
                        outcome_result  TEXT,
                        profit_loss_pct REAL,
                        profit_loss_rs  REAL,
                        outcome_filled  INTEGER DEFAULT 0,
                        created_at      TEXT DEFAULT CURRENT_TIMESTAMP
                    )";
                cmd.ExecuteNonQuery();
                _logger.LogInformation("✅ Signal tracking DB ready: {p}", _dbPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Signal DB init failed: {m}", ex.Message);
            }
        }

        // ── Record BUY signal ─────────────────────────
        public async Task RecordSignalAsync(
            Recommendation rec,
            MarketContext? mktCtx = null)
        {
            if (rec?.AiAnalysis?.Recommendation != "BUY") return;
            try
            {
                var ist = GetIST();
                var signalId = $"{rec.Stock.Symbol}_{ist:yyyyMMdd_HHmm}";

                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                await conn.OpenAsync();

                // Duplicate check
                using var chk = conn.CreateCommand();
                chk.CommandText = "SELECT COUNT(*) FROM signals WHERE signal_id=$id";
                chk.Parameters.AddWithValue("$id", signalId);
                if (Convert.ToInt32(await chk.ExecuteScalarAsync()) > 0) return;

                var ai = rec.AiAnalysis;
                var tech = rec.Technical;
                var s = rec.Stock;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO signals (
                        signal_id, symbol, signal_date, signal_time,
                        entry_price, target_price, stop_loss,
                        risk_reward, confidence, ai_summary,
                        rsi, macd_hist, supertrend, adx, vwap,
                        price_vs_vwap, tech_score,
                        nifty_change, india_vix, fii_net,
                        market_quality, fund_score
                    ) VALUES (
                        $sid,$sym,$date,$time,
                        $entry,$target,$sl,
                        $rr,$conf,$summary,
                        $rsi,$macd,$st,$adx,$vwap,
                        $pvwap,$tscore,
                        $nchg,$vix,$fii,
                        $mq,$fscore
                    )";

                cmd.Parameters.AddWithValue("$sid", signalId);
                cmd.Parameters.AddWithValue("$sym", s.Symbol);
                cmd.Parameters.AddWithValue("$date", ist.ToString("yyyy-MM-dd"));
                cmd.Parameters.AddWithValue("$time", ist.ToString("HH:mm:ss"));
                cmd.Parameters.AddWithValue("$entry", ai?.Entry ?? s.LastPrice);
                cmd.Parameters.AddWithValue("$target", ai?.Target ?? 0);
                cmd.Parameters.AddWithValue("$sl", ai?.StopLoss ?? 0);
                cmd.Parameters.AddWithValue("$rr", ai?.RiskReward ?? "N/A");
                cmd.Parameters.AddWithValue("$conf", ai?.Confidence ?? 0);
                cmd.Parameters.AddWithValue("$summary", ai?.Summary ?? "");
                cmd.Parameters.AddWithValue("$rsi", tech?.RSI ?? 0);
                cmd.Parameters.AddWithValue("$macd", tech?.MACDHistogram ?? 0);
                cmd.Parameters.AddWithValue("$st", tech?.SupertrendBullish == true ? "BUY" : "SELL");
                cmd.Parameters.AddWithValue("$adx", tech?.ADX ?? 0);
                cmd.Parameters.AddWithValue("$vwap", tech?.VWAP ?? 0);
                cmd.Parameters.AddWithValue("$pvwap",
                    s.LastPrice > (tech?.VWAP ?? 0) ? "ABOVE" : "BELOW");
                cmd.Parameters.AddWithValue("$tscore", tech?.Score ?? 50);
                cmd.Parameters.AddWithValue("$nchg", mktCtx?.NiftyChange ?? 0);
                cmd.Parameters.AddWithValue("$vix", mktCtx?.IndiaVix ?? 0);
                cmd.Parameters.AddWithValue("$fii", mktCtx?.FiiNetCrore ?? 0);
                cmd.Parameters.AddWithValue("$mq", mktCtx?.MarketQualityScore ?? 0);
                cmd.Parameters.AddWithValue("$fscore", rec.Fundamental?.Score ?? 0);

                await cmd.ExecuteNonQueryAsync();
                _logger.LogInformation(
                    "📝 Signal recorded: {sym} @ ₹{e:F1} T:₹{t:F1} SL:₹{sl:F1}",
                    s.Symbol, ai?.Entry ?? s.LastPrice,
                    ai?.Target ?? 0, ai?.StopLoss ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ RecordSignal {sym}: {m}",
                    rec.Stock?.Symbol, ex.Message);
            }
        }

        // ── Fill outcomes for signals 2+ hrs old ─────
        public async Task FillOutcomesAsync(
            Func<string, Task<double>> getPriceFunc)
        {
            try
            {
                var ist = GetIST();
                var cutoff = ist.AddHours(-2).ToString("HH:mm:ss");
                var today = ist.ToString("yyyy-MM-dd");

                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                await conn.OpenAsync();

                // Read unfilled signals older than 2 hrs
                var pending = new List<(int id, string sym, double entry, double tgt, double sl)>();
                using var sel = conn.CreateCommand();
                sel.CommandText = @"
                    SELECT id, symbol, entry_price, target_price, stop_loss
                    FROM signals
                    WHERE outcome_filled=0
                    AND signal_date=$today
                    AND signal_time<=$cutoff";
                sel.Parameters.AddWithValue("$today", today);
                sel.Parameters.AddWithValue("$cutoff", cutoff);
                using var rd = await sel.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                    pending.Add((rd.GetInt32(0), rd.GetString(1),
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
                        { result = "HIT_TARGET"; plPct = (tgt - entry) / entry * 100; }
                        else if (sl > 0 && price <= sl)
                        { result = "HIT_SL"; plPct = (sl - entry) / entry * 100; }
                        else
                        { result = "EXPIRED"; plPct = (price - entry) / entry * 100; }

                        double qty = entry > 0 ? Math.Floor(1500 * 0.6 / entry) : 1;
                        double plRs = plPct / 100 * entry * Math.Max(1, qty);

                        using var upd = conn.CreateCommand();
                        upd.CommandText = @"
                            UPDATE signals SET
                                outcome_price=$p, outcome_result=$r,
                                profit_loss_pct=$pp, profit_loss_rs=$pr,
                                outcome_filled=1
                            WHERE id=$id";
                        upd.Parameters.AddWithValue("$p", price);
                        upd.Parameters.AddWithValue("$r", result);
                        upd.Parameters.AddWithValue("$pp", Math.Round(plPct, 2));
                        upd.Parameters.AddWithValue("$pr", Math.Round(plRs, 2));
                        upd.Parameters.AddWithValue("$id", id);
                        await upd.ExecuteNonQueryAsync();

                        _logger.LogInformation(
                            "📊 Outcome: {sym} {r} {pct:F2}% ₹{rs:F0}",
                            sym, result, plPct, plRs);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ FillOutcomes: {m}", ex.Message);
            }
        }

        // ── Accuracy stats ────────────────────────────
        public async Task<AccuracyStats> GetAccuracyStatsAsync(int days = 30)
        {
            var stats = new AccuracyStats();
            try
            {
                var cutoff = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd");
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT
                        COUNT(*),
                        SUM(CASE WHEN outcome_result='HIT_TARGET' THEN 1 ELSE 0 END),
                        SUM(CASE WHEN outcome_result='HIT_SL'     THEN 1 ELSE 0 END),
                        SUM(CASE WHEN outcome_result='EXPIRED'    THEN 1 ELSE 0 END),
                        AVG(CASE WHEN profit_loss_pct>0 THEN profit_loss_pct END),
                        AVG(CASE WHEN profit_loss_pct<0 THEN profit_loss_pct END),
                        SUM(profit_loss_rs)
                    FROM signals
                    WHERE signal_date>=$cutoff AND outcome_filled=1";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    stats.TotalSignals = r.IsDBNull(0) ? 0 : r.GetInt32(0);
                    stats.HitTarget = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                    stats.HitSl = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                    stats.Expired = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                    stats.AvgProfitPct = r.IsDBNull(4) ? 0 : r.GetDouble(4);
                    stats.AvgLossPct = r.IsDBNull(5) ? 0 : r.GetDouble(5);
                    stats.TotalPnlRs = r.IsDBNull(6) ? 0 : r.GetDouble(6);
                    stats.AccuracyPct = stats.TotalSignals > 0
                        ? Math.Round((double)stats.HitTarget / stats.TotalSignals * 100, 1)
                        : 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ GetAccuracyStats: {m}", ex.Message);
            }
            return stats;
        }

        // ── Export training data ──────────────────────
        public async Task<List<TrainingExample>> ExportTrainingDataAsync()
        {
            var examples = new List<TrainingExample>();
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT symbol, price_vs_vwap, rsi, supertrend,
                           adx, tech_score, india_vix, nifty_change,
                           fii_net, market_quality, outcome_result,
                           profit_loss_pct, ai_summary
                    FROM signals WHERE outcome_filled=1
                    ORDER BY signal_date DESC LIMIT 2000";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var outcome = r.IsDBNull(10) ? "" : r.GetString(10);
                    var plPct = r.IsDBNull(11) ? 0d : r.GetDouble(11);
                    examples.Add(new TrainingExample
                    {
                        Instruction = "NSE intraday trader. Given signals, predict BUY or AVOID.",
                        Input = $"{(r.IsDBNull(0) ? "" : r.GetString(0))} | " +
                                $"VWAP:{(r.IsDBNull(1) ? "" : r.GetString(1))} | " +
                                $"RSI:{(r.IsDBNull(2) ? 0 : r.GetDouble(2)):F1} | " +
                                $"ST:{(r.IsDBNull(3) ? "" : r.GetString(3))} | " +
                                $"ADX:{(r.IsDBNull(4) ? 0 : r.GetDouble(4)):F0} | " +
                                $"VIX:{(r.IsDBNull(6) ? 0 : r.GetDouble(6)):F1} | " +
                                $"Nifty:{(r.IsDBNull(7) ? 0 : r.GetDouble(7)):F1}% | " +
                                $"MQ:{(r.IsDBNull(9) ? 0 : r.GetInt32(9))}/100",
                        Output = outcome == "HIT_TARGET"
                            ? $"BUY | Profitable +{plPct:F2}%"
                            : $"AVOID | {outcome} {plPct:F2}%"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ ExportTrainingData: {m}", ex.Message);
            }
            return examples;
        }

        private DateTime GetIST()
        {
            try
            {
                return TimeZoneInfo.ConvertTime(DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"));
            }
            catch { return DateTime.UtcNow.AddHours(5).AddMinutes(30); }
        }
    }

    public class AccuracyStats
    {
        public int TotalSignals { get; set; }
        public int HitTarget { get; set; }
        public int HitSl { get; set; }
        public int Expired { get; set; }
        public double AccuracyPct { get; set; }
        public double AvgProfitPct { get; set; }
        public double AvgLossPct { get; set; }
        public double TotalPnlRs { get; set; }
    }

    public class TrainingExample
    {
        public string Instruction { get; set; } = "";
        public string Input { get; set; } = "";
        public string Output { get; set; } = "";
    }
}
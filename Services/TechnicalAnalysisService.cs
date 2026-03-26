using AlgoSenseNSE.API.Models;
using Skender.Stock.Indicators;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// TechnicalAnalysisService v3
    ///
    /// Changes from v2:
    /// 1. Removed EMA 9/21 crossover + EMA 50 — redundant with Supertrend
    /// 2. Time-of-day modifier:
    ///    9:15–9:45 → -15 score (fake breakouts)
    ///    10:00–12:30 → +5 score (best window)
    ///    15:00–15:30 → -15 score (erratic close)
    /// 3. Slippage-adjusted target/SL:
    ///    Entry = LTP + 0.1% (market order fills worse)
    ///    Target = target - 0.1%
    ///    SL = SL - 0.05%
    ///
    /// Core indicators (7 total, down from 10):
    ///   VWAP, RSI, MACD, Supertrend, Bollinger, ADX, Volume + ATR
    ///
    /// Removed (redundant with Supertrend):
    ///   EMA 9/21 crossover
    ///   EMA 50 trend
    /// </summary>
    public class TechnicalAnalysisService
    {
        private readonly ILogger<TechnicalAnalysisService> _logger;

        // Price-sensitive slippage (NSE market reality)
        // Low-priced stocks have wider bid-ask spreads
        // High-priced stocks have tighter spreads
        private static (double entry, double target, double sl)
            GetSlippage(double price)
        {
            if (price < 100)
                return (0.0020, 0.0020, 0.0010); // ₹20–₹99: 0.20%
            if (price < 300)
                return (0.0010, 0.0010, 0.0005); // ₹100–₹299: 0.10%
            return (0.0005, 0.0005, 0.0003);     // ₹300+: 0.05%
        }

        public TechnicalAnalysisService(
            ILogger<TechnicalAnalysisService> logger)
        {
            _logger = logger;
        }

        private List<Quote> ToQuotes(List<OhlcvCandle> candles) =>
            candles.OrderBy(c => c.Timestamp)
                .Select(c => new Quote
                {
                    Date   = c.Timestamp,
                    Open   = (decimal)c.Open,
                    High   = (decimal)c.High,
                    Low    = (decimal)c.Low,
                    Close  = (decimal)c.Close,
                    Volume = c.Volume
                }).ToList();

        // ── Time-of-day score modifier ─────────────────
        private (double modifier, string label) GetTimeModifier()
        {
            var ist = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            var t   = ist.TimeOfDay;

            if (t < new TimeSpan(9, 45, 0))
                return (-15, "9:15–9:45 OPEN — fake breakout zone, extra caution ⚠️");
            if (t < new TimeSpan(10, 0, 0))
                return (-5,  "9:45–10:00 SETTLING — wait for direction ⚠️");
            if (t < new TimeSpan(12, 30, 0))
                return (5,   "10:00–12:30 BEST WINDOW — strongest trends ✅");
            if (t < new TimeSpan(14, 30, 0))
                return (0,   "12:30–14:30 MIDDAY — neutral");
            if (t < new TimeSpan(15, 0, 0))
                return (-5,  "14:30–15:00 AFTERNOON — reversals possible ⚠️");
            return (-15,     "15:00–15:30 CLOSE — erratic, avoid entry ⚠️");
        }

        // ── Main compute ───────────────────────────────
        public TechnicalResult Compute(
            string symbol, List<OhlcvCandle> candles)
        {
            var result = new TechnicalResult { Symbol = symbol };

            if (candles.Count < 30)
            {
                _logger.LogWarning(
                    "⚠️ Not enough candles for {sym}: {cnt}",
                    symbol, candles.Count);
                result.Score = 50;
                return result;
            }

            var quotes    = ToQuotes(candles);
            var lastClose = candles.Last().Close;
            var lastVol   = candles.Last().Volume;
            var avgVol    = candles.TakeLast(20)
                .Average(c => (double)c.Volume);

            double score       = 50;
            int    bullSignals = 0;
            int    bearSignals = 0;

            // ── Time-of-day adjustment ─────────────────
            var (timeMod, timeLabel) = GetTimeModifier();
            score += timeMod;
            result.Signals.Add(new TechnicalSignal
            {
                Indicator = "Time Window",
                Value     = timeLabel,
                Signal    = timeLabel,
                IsBullish = timeMod > 0 ? true
                          : timeMod < 0 ? false
                          : null
            });

            // ━━━ 1. VWAP ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
            var vwapResults = quotes.GetVwap().ToList();
            var lastVwap    = vwapResults.LastOrDefault(v => v.Vwap != null);
            if (lastVwap != null)
            {
                result.VWAP = (double)(lastVwap.Vwap ?? 0);
                double vwapDiff = result.VWAP > 0
                    ? ((lastClose - result.VWAP) / result.VWAP) * 100 : 0;

                if (lastClose > result.VWAP)
                {
                    score += 22; bullSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "VWAP",
                        Value     = $"₹{result.VWAP:F2} (+{vwapDiff:F2}%)",
                        Signal    = "Price above VWAP — Bullish bias",
                        IsBullish = true
                    });
                }
                else
                {
                    score -= 22; bearSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "VWAP",
                        Value     = $"₹{result.VWAP:F2} ({vwapDiff:F2}%)",
                        Signal    = "Price below VWAP — Bearish bias",
                        IsBullish = false
                    });
                }
            }

            // ━━━ 2. RSI (14) — gate only, not scorer ━━━
            // RSI is enforced as hard gate in RuleEngine (45–75 required).
            // Adding it to score was double-counting the same signal.
            // Kept here for display and Claude context only.
            var rsiResults = quotes.GetRsi(14).ToList();
            var lastRsi    = rsiResults.LastOrDefault(r => r.Rsi != null);
            if (lastRsi?.Rsi != null)
            {
                result.RSI = (double)lastRsi.Rsi;

                // Report RSI for display only — no score impact
                string rsiSignal;
                bool?  rsiBullish;
                if (result.RSI >= 55 && result.RSI <= 72)
                { rsiSignal = "RSI in buy zone (55-72) ✅"; rsiBullish = true; }
                else if (result.RSI > 72)
                { rsiSignal = "RSI overbought (>72) ❌"; rsiBullish = false; }
                else if (result.RSI >= 45 && result.RSI < 55)
                { rsiSignal = "RSI neutral (45-55)"; rsiBullish = null; }
                else
                { rsiSignal = $"RSI {result.RSI:F1} — outside buy zone"; rsiBullish = false; }

                result.Signals.Add(new TechnicalSignal
                {
                    Indicator = "RSI (gate)",
                    Value     = result.RSI.ToString("F1"),
                    Signal    = rsiSignal + " — gate only, not scored",
                    IsBullish = rsiBullish
                });
                // NOTE: score not modified — RSI is a gate in RuleEngine
            }

            // ━━━ 3. MACD (12,26,9) ━━━━━━━━━━━━━━━━━━━
            var macdResults = quotes.GetMacd(12, 26, 9).ToList();
            var lastMacd    = macdResults.LastOrDefault(
                m => m.Macd != null && m.Signal != null);
            if (lastMacd != null)
            {
                result.MACD          = (double)(lastMacd.Macd      ?? 0);
                result.MACDSignal    = (double)(lastMacd.Signal    ?? 0);
                result.MACDHistogram = (double)(lastMacd.Histogram ?? 0);

                var prevMacd  = macdResults
                    .Where(m => m.Date < lastMacd.Date && m.Histogram != null)
                    .LastOrDefault();
                bool histRising = prevMacd != null &&
                    lastMacd.Histogram > prevMacd.Histogram;

                if (result.MACDHistogram > 0 && histRising)
                {
                    score += 16; bullSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "MACD",
                        Value     = $"Hist:{result.MACDHistogram:F3} ↑",
                        Signal    = "MACD bullish + momentum building",
                        IsBullish = true
                    });
                }
                else if (result.MACDHistogram > 0)
                {
                    score += 9;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "MACD",
                        Value     = $"Hist:{result.MACDHistogram:F3}",
                        Signal    = "MACD bullish crossover",
                        IsBullish = true
                    });
                }
                else
                {
                    score -= (histRising ? 5 : 14); bearSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "MACD",
                        Value     = $"Hist:{result.MACDHistogram:F3}",
                        Signal    = "MACD bearish — avoid",
                        IsBullish = false
                    });
                }
            }

            // ━━━ 4. Supertrend (7,3) ━━━━━━━━━━━━━━━━━
            var stResults = quotes.GetSuperTrend(7, 3).ToList();
            var lastSt    = stResults.LastOrDefault(s => s.SuperTrend != null);
            if (lastSt != null)
            {
                result.Supertrend      = (double)(lastSt.SuperTrend ?? 0);
                result.SupertrendBullish = lastSt.UpperBand == null;

                if (result.SupertrendBullish)
                {
                    score += 20; bullSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Supertrend",
                        Value     = $"₹{result.Supertrend:F1}",
                        Signal    = "Supertrend BUY — Strong uptrend ✅",
                        IsBullish = true
                    });
                }
                else
                {
                    score -= 20; bearSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Supertrend",
                        Value     = $"₹{result.Supertrend:F1}",
                        Signal    = "Supertrend SELL — Downtrend ❌",
                        IsBullish = false
                    });
                }
            }

            // ━━━ 5. Bollinger Bands (20,2) ━━━━━━━━━━━━
            // Kept specifically for RANGE regime mean-reversion
            var bbResults = quotes.GetBollingerBands(20, 2).ToList();
            var lastBb    = bbResults.LastOrDefault(b => b.UpperBand != null);
            if (lastBb != null)
            {
                result.BollingerUpper  = (double)(lastBb.UpperBand ?? 0);
                result.BollingerMiddle = (double)(lastBb.Sma       ?? 0);
                result.BollingerLower  = (double)(lastBb.LowerBand ?? 0);
                result.BollingerPctB   = (double)(lastBb.PercentB  ?? 0);

                if (lastClose >= result.BollingerUpper)
                {
                    score -= 8;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value     = $"%B:{result.BollingerPctB:F2}",
                        Signal    = "At upper band — overextended, caution",
                        IsBullish = false
                    });
                }
                else if (lastClose <= result.BollingerLower)
                {
                    score += 8;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value     = $"%B:{result.BollingerPctB:F2}",
                        Signal    = "At lower band — potential bounce",
                        IsBullish = true
                    });
                }
                else if (lastClose > result.BollingerMiddle)
                {
                    score += 5;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value     = $"%B:{result.BollingerPctB:F2}",
                        Signal    = "Upper half — bullish momentum",
                        IsBullish = true
                    });
                }
                else
                {
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value     = $"%B:{result.BollingerPctB:F2}",
                        Signal    = "Lower half — bearish momentum",
                        IsBullish = false
                    });
                }
            }

            // ━━━ 6. ADX (14) ━━━━━━━━━━━━━━━━━━━━━━━━━
            var adxResults = quotes.GetAdx(14).ToList();
            var lastAdx    = adxResults.LastOrDefault(a => a.Adx != null);
            if (lastAdx != null)
            {
                result.ADX     = (double)(lastAdx.Adx ?? 0);
                result.PlusDI  = (double)(lastAdx.Pdi ?? 0);
                result.MinusDI = (double)(lastAdx.Mdi ?? 0);

                if (result.ADX > 25)
                {
                    bool trendUp = result.PlusDI > result.MinusDI;
                    score += trendUp ? 14 : -14;
                    if (trendUp) bullSignals++; else bearSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "ADX",
                        Value     = $"ADX:{result.ADX:F0} +DI:{result.PlusDI:F0} -DI:{result.MinusDI:F0}",
                        Signal    = $"Strong {(trendUp ? "uptrend" : "downtrend")} (ADX={result.ADX:F0})",
                        IsBullish = trendUp
                    });
                }
                else
                {
                    score -= 5;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "ADX",
                        Value     = $"ADX:{result.ADX:F0}",
                        Signal    = "Weak trend (ADX<25) — choppy market",
                        IsBullish = null
                    });
                }
            }

            // ━━━ 7. ATR (14) — for sizing, not scoring ━
            var atrResults = quotes.GetAtr(14).ToList();
            var lastAtr    = atrResults.LastOrDefault(a => a.Atr != null);
            result.ATR     = (double)(lastAtr?.Atr ?? 0);

            // ━━━ 8. Volume ━━━━━━━━━━━━━━━━━━━━━━━━━━━
            if (avgVol > 0)
            {
                double volRatio = lastVol / avgVol;
                if (volRatio > 2.0)
                {
                    score += 14; bullSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Volume",
                        Value     = $"{volRatio:F1}x avg",
                        Signal    = $"Huge volume {volRatio:F1}x — institutional move",
                        IsBullish = true
                    });
                }
                else if (volRatio > 1.5)
                {
                    score += 7;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Volume",
                        Value     = $"{volRatio:F1}x avg",
                        Signal    = $"Above avg volume {volRatio:F1}x — confirms move",
                        IsBullish = true
                    });
                }
                else if (volRatio < 0.5)
                {
                    score -= 8;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Volume",
                        Value     = $"{volRatio:F1}x avg",
                        Signal    = "Low volume — weak move, don't trust",
                        IsBullish = false
                    });
                }
            }

            // ━━━ Slippage-adjusted target and SL ━━━━━━
            // Realistic prices after market order execution
            double adjEntry, adjTarget, adjSL;
            var (slipEntry, slipTarget, slipSL) =
                GetSlippage(lastClose);

            if (result.ATR > 0)
            {
                double rawTarget = lastClose + (result.ATR * 2.0);
                double rawSL     = lastClose - (result.ATR * 0.75);

                adjEntry  = lastClose * (1 + slipEntry);
                adjTarget = rawTarget * (1 - slipTarget);
                adjSL     = rawSL    * (1 - slipSL);
            }
            else
            {
                adjEntry  = lastClose * (1 + slipEntry);
                adjTarget = lastClose * 1.005  * (1 - slipTarget);
                adjSL     = lastClose * 0.9975 * (1 - slipSL);
            }

            result.SuggestedTarget   = Math.Round(adjTarget, 2);
            result.SuggestedStopLoss = Math.Round(adjSL,     2);

            // Real R:R after slippage
            double realRR = (adjTarget - adjEntry) > 0 &&
                            (adjEntry - adjSL) > 0
                ? (adjTarget - adjEntry) / (adjEntry - adjSL)
                : 0;

            result.Signals.Add(new TechnicalSignal
            {
                Indicator = "Slippage-Adj R:R",
                Value     = $"1:{realRR:F2}",
                Signal    = realRR >= 2.0
                    ? $"Real R:R 1:{realRR:F1} after slippage ✅"
                    : $"Real R:R 1:{realRR:F1} after slippage ⚠️ (target 1:2)",
                IsBullish = realRR >= 2.0
            });

            // ━━━ Consensus adjustment ━━━━━━━━━━━━━━━━━
            if (bullSignals >= 4) score = Math.Max(score, 72);
            else if (bearSignals >= 4) score = Math.Min(score, 28);

            result.Score       = Math.Max(0, Math.Min(100, score));
            result.CalculatedAt = DateTime.Now;

            _logger.LogInformation(
                "✅ {sym}: Score={score:F0} VWAP={vwap:F1} RSI={rsi:F1} " +
                "ST={st} ADX={adx:F0} Time={time} RealRR=1:{rr:F1}",
                symbol, result.Score, result.VWAP, result.RSI,
                result.SupertrendBullish ? "BUY" : "SELL",
                result.ADX, timeLabel.Split('—')[0].Trim(), realRR);

            return result;
        }

        // EMA properties kept in model for backward compat
        // but no longer computed — set to 0
    }
}

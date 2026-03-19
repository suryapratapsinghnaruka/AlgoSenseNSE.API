using AlgoSenseNSE.API.Models;
using Skender.Stock.Indicators;

namespace AlgoSenseNSE.API.Services
{
    public class TechnicalAnalysisService
    {
        private readonly ILogger<TechnicalAnalysisService> _logger;

        public TechnicalAnalysisService(
            ILogger<TechnicalAnalysisService> logger)
        {
            _logger = logger;
        }

        private List<Quote> ToQuotes(List<OhlcvCandle> candles)
        {
            return candles
                .OrderBy(c => c.Timestamp)
                .Select(c => new Quote
                {
                    Date = c.Timestamp,
                    Open = (decimal)c.Open,
                    High = (decimal)c.High,
                    Low = (decimal)c.Low,
                    Close = (decimal)c.Close,
                    Volume = c.Volume
                }).ToList();
        }

        // ── Main compute — all 10 indicators ─────────
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

            var quotes = ToQuotes(candles);
            var lastClose = candles.Last().Close;
            var lastVol = candles.Last().Volume;
            var avgVol = candles.TakeLast(20).Average(c => (double)c.Volume);

            double score = 50;
            int bullSignals = 0;
            int bearSignals = 0;

            // ━━━ 1. VWAP ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
            // Most important intraday indicator
            // Price above VWAP = institutions bullish
            var vwapResults = quotes.GetVwap().ToList();
            var lastVwap = vwapResults.LastOrDefault(v => v.Vwap != null);
            if (lastVwap != null)
            {
                result.VWAP = (double)(lastVwap.Vwap ?? 0);
                double vwapDiff = result.VWAP > 0
                    ? ((lastClose - result.VWAP) / result.VWAP) * 100 : 0;

                if (lastClose > result.VWAP)
                {
                    score += 20; bullSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "VWAP",
                        Value = $"₹{result.VWAP:F2} (+{vwapDiff:F2}%)",
                        Signal = "Price above VWAP — Bullish bias",
                        IsBullish = true
                    });
                }
                else
                {
                    score -= 20; bearSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "VWAP",
                        Value = $"₹{result.VWAP:F2} ({vwapDiff:F2}%)",
                        Signal = "Price below VWAP — Bearish bias",
                        IsBullish = false
                    });
                }
            }

            // ━━━ 2. EMA 9 / 21 Crossover ━━━━━━━━━━━━━━
            // Standard intraday EMA combo
            var ema9 = quotes.GetEma(9).ToList();
            var ema21 = quotes.GetEma(21).ToList();
            result.EMA20 = (double)(ema9.LastOrDefault(e => e.Ema != null)?.Ema ?? 0);
            result.EMA50 = (double)(ema21.LastOrDefault(e => e.Ema != null)?.Ema ?? 0);

            if (result.EMA20 > result.EMA50 && lastClose > result.EMA20)
            {
                score += 15; bullSignals++;
                result.Signals.Add(new TechnicalSignal
                {
                    Indicator = "EMA 9/21",
                    Value = $"9:{result.EMA20:F1} 21:{result.EMA50:F1}",
                    Signal = "EMA9 > EMA21 — Bullish crossover",
                    IsBullish = true
                });
            }
            else if (result.EMA20 < result.EMA50 && lastClose < result.EMA20)
            {
                score -= 15; bearSignals++;
                result.Signals.Add(new TechnicalSignal
                {
                    Indicator = "EMA 9/21",
                    Value = $"9:{result.EMA20:F1} 21:{result.EMA50:F1}",
                    Signal = "EMA9 < EMA21 — Bearish crossover",
                    IsBullish = false
                });
            }
            else
            {
                result.Signals.Add(new TechnicalSignal
                {
                    Indicator = "EMA 9/21",
                    Value = $"9:{result.EMA20:F1} 21:{result.EMA50:F1}",
                    Signal = "Mixed EMA — wait for crossover",
                    IsBullish = null
                });
            }

            // ━━━ 3. EMA 50 (Intraday trend) ━━━━━━━━━━━
            var ema50 = quotes.GetEma(50).ToList();
            result.EMA200 = (double)(ema50.LastOrDefault(e => e.Ema != null)?.Ema ?? 0);
            if (lastClose > result.EMA200)
            {
                score += 8;
                result.Signals.Add(new TechnicalSignal
                {
                    Indicator = "EMA 50",
                    Value = $"₹{result.EMA200:F1}",
                    Signal = "Price above EMA50 — Uptrend confirmed",
                    IsBullish = true
                });
            }
            else
            {
                score -= 8;
                result.Signals.Add(new TechnicalSignal
                {
                    Indicator = "EMA 50",
                    Value = $"₹{result.EMA200:F1}",
                    Signal = "Price below EMA50 — Downtrend",
                    IsBullish = false
                });
            }

            // ━━━ 4. RSI (14) ━━━━━━━━━━━━━━━━━━━━━━━━━
            var rsiResults = quotes.GetRsi(14).ToList();
            var lastRsi = rsiResults.LastOrDefault(r => r.Rsi != null);
            if (lastRsi?.Rsi != null)
            {
                result.RSI = (double)lastRsi.Rsi;

                if (result.RSI >= 55 && result.RSI <= 72)
                {
                    score += 15; bullSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "RSI",
                        Value = result.RSI.ToString("F1"),
                        Signal = "RSI in bullish zone (55-72) — momentum buy",
                        IsBullish = true
                    });
                }
                else if (result.RSI > 72)
                {
                    score -= 12; bearSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "RSI",
                        Value = result.RSI.ToString("F1"),
                        Signal = "RSI overbought (>72) — avoid buying",
                        IsBullish = false
                    });
                }
                else if (result.RSI >= 35 && result.RSI < 55)
                {
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "RSI",
                        Value = result.RSI.ToString("F1"),
                        Signal = "RSI neutral — no strong signal",
                        IsBullish = null
                    });
                }
                else
                {
                    score -= 10; bearSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "RSI",
                        Value = result.RSI.ToString("F1"),
                        Signal = "RSI oversold/weak (<35) — bearish",
                        IsBullish = false
                    });
                }
            }

            // ━━━ 5. MACD (12,26,9) ━━━━━━━━━━━━━━━━━━━
            var macdResults = quotes.GetMacd(12, 26, 9).ToList();
            var lastMacd = macdResults.LastOrDefault(
                m => m.Macd != null && m.Signal != null);
            if (lastMacd != null)
            {
                result.MACD = (double)(lastMacd.Macd ?? 0);
                result.MACDSignal = (double)(lastMacd.Signal ?? 0);
                result.MACDHistogram = (double)(lastMacd.Histogram ?? 0);

                // Check if momentum is building (histogram increasing)
                var prevMacd = macdResults
                    .Where(m => m.Date < lastMacd.Date && m.Histogram != null)
                    .LastOrDefault();
                bool histRising = prevMacd != null &&
                    lastMacd.Histogram > prevMacd.Histogram;

                if (result.MACDHistogram > 0 && histRising)
                {
                    score += 15; bullSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "MACD",
                        Value = $"Hist: {result.MACDHistogram:F3} ↑",
                        Signal = "MACD bullish + momentum building",
                        IsBullish = true
                    });
                }
                else if (result.MACDHistogram > 0)
                {
                    score += 8;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "MACD",
                        Value = $"Hist: {result.MACDHistogram:F3}",
                        Signal = "MACD bullish crossover",
                        IsBullish = true
                    });
                }
                else
                {
                    score -= (histRising ? 5 : 12);
                    bearSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "MACD",
                        Value = $"Hist: {result.MACDHistogram:F3}",
                        Signal = "MACD bearish — avoid",
                        IsBullish = false
                    });
                }
            }

            // ━━━ 6. Supertrend (7,3) ━━━━━━━━━━━━━━━━━
            // Best intraday trend indicator
            var stResults = quotes.GetSuperTrend(7, 3).ToList();
            var lastSt = stResults.LastOrDefault(s => s.SuperTrend != null);
            if (lastSt != null)
            {
                result.Supertrend = (double)(lastSt.SuperTrend ?? 0);
                result.SupertrendBullish = lastSt.UpperBand == null;

                if (result.SupertrendBullish)
                {
                    score += 18; bullSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Supertrend",
                        Value = $"₹{result.Supertrend:F1}",
                        Signal = "Supertrend BUY — Strong uptrend ✅",
                        IsBullish = true
                    });
                }
                else
                {
                    score -= 18; bearSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Supertrend",
                        Value = $"₹{result.Supertrend:F1}",
                        Signal = "Supertrend SELL — Downtrend ❌",
                        IsBullish = false
                    });
                }
            }

            // ━━━ 7. Bollinger Bands (20,2) ━━━━━━━━━━━━
            var bbResults = quotes.GetBollingerBands(20, 2).ToList();
            var lastBb = bbResults.LastOrDefault(b => b.UpperBand != null);
            if (lastBb != null)
            {
                result.BollingerUpper = (double)(lastBb.UpperBand ?? 0);
                result.BollingerMiddle = (double)(lastBb.Sma ?? 0);
                result.BollingerLower = (double)(lastBb.LowerBand ?? 0);
                result.BollingerPctB = (double)(lastBb.PercentB ?? 0);

                if (lastClose >= result.BollingerUpper)
                {
                    score -= 8;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value = $"%B:{result.BollingerPctB:F2}",
                        Signal = "At upper band — overextended, caution",
                        IsBullish = false
                    });
                }
                else if (lastClose <= result.BollingerLower)
                {
                    score += 8;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value = $"%B:{result.BollingerPctB:F2}",
                        Signal = "At lower band — potential bounce",
                        IsBullish = true
                    });
                }
                else if (lastClose > result.BollingerMiddle)
                {
                    score += 5;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value = $"%B:{result.BollingerPctB:F2}",
                        Signal = "Upper half — bullish momentum",
                        IsBullish = true
                    });
                }
                else
                {
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value = $"%B:{result.BollingerPctB:F2}",
                        Signal = "Lower half — bearish momentum",
                        IsBullish = false
                    });
                }
            }

            // ━━━ 8. ADX (14) — Trend Strength ━━━━━━━━━
            var adxResults = quotes.GetAdx(14).ToList();
            var lastAdx = adxResults.LastOrDefault(a => a.Adx != null);
            if (lastAdx != null)
            {
                result.ADX = (double)(lastAdx.Adx ?? 0);
                result.PlusDI = (double)(lastAdx.Pdi ?? 0);
                result.MinusDI = (double)(lastAdx.Mdi ?? 0);

                if (result.ADX > 25)
                {
                    bool trendUp = result.PlusDI > result.MinusDI;
                    score += trendUp ? 12 : -12;
                    if (trendUp) bullSignals++; else bearSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "ADX",
                        Value = $"ADX:{result.ADX:F0} +DI:{result.PlusDI:F0} -DI:{result.MinusDI:F0}",
                        Signal = $"Strong {(trendUp ? "uptrend" : "downtrend")} " +
                                    $"(ADX={result.ADX:F0})",
                        IsBullish = trendUp
                    });
                }
                else
                {
                    score -= 5;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "ADX",
                        Value = $"ADX:{result.ADX:F0}",
                        Signal = "Weak trend (ADX<25) — choppy market",
                        IsBullish = null
                    });
                }
            }

            // ━━━ 9. ATR (14) — Volatility/SL ━━━━━━━━━━
            var atrResults = quotes.GetAtr(14).ToList();
            var lastAtr = atrResults.LastOrDefault(a => a.Atr != null);
            result.ATR = (double)(lastAtr?.Atr ?? 0);

            // ━━━ 10. Volume Spike ━━━━━━━━━━━━━━━━━━━━━
            if (avgVol > 0)
            {
                double volRatio = lastVol / avgVol;
                if (volRatio > 2.0)
                {
                    score += 12; bullSignals++;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Volume",
                        Value = $"{volRatio:F1}x avg",
                        Signal = $"Huge volume spike {volRatio:F1}x — institutional move",
                        IsBullish = true
                    });
                }
                else if (volRatio > 1.5)
                {
                    score += 6;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Volume",
                        Value = $"{volRatio:F1}x avg",
                        Signal = $"Above avg volume {volRatio:F1}x — confirms move",
                        IsBullish = true
                    });
                }
                else if (volRatio < 0.5)
                {
                    score -= 8;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Volume",
                        Value = $"{volRatio:F1}x avg",
                        Signal = "Low volume — weak move, don't trust",
                        IsBullish = false
                    });
                }
            }

            // ━━━ Target & Stop Loss (ATR-based) ━━━━━━━
            if (result.ATR > 0)
            {
                // Intraday: 1.5x ATR target, 0.75x ATR stop
                result.SuggestedTarget = lastClose + (result.ATR * 1.5);
                result.SuggestedStopLoss = lastClose - (result.ATR * 0.75);
            }
            else
            {
                result.SuggestedTarget = lastClose * 1.005;  // +0.5%
                result.SuggestedStopLoss = lastClose * 0.9975; // -0.25%
            }

            // ━━━ Consensus Adjustment ━━━━━━━━━━━━━━━━━
            // Strong consensus overrides individual scores
            if (bullSignals >= 4) score = Math.Max(score, 72);
            else if (bearSignals >= 4) score = Math.Min(score, 28);
            else if (bullSignals == bearSignals && bullSignals >= 2)
                score = 48; // Mixed — lean avoid

            result.Score = Math.Max(0, Math.Min(100, score));
            result.CalculatedAt = DateTime.Now;

            _logger.LogInformation(
                "✅ {sym}: Score={score:F0} " +
                "VWAP={vwap:F1} RSI={rsi:F1} ST={st} " +
                "ADX={adx:F0} Bull={bull} Bear={bear}",
                symbol, result.Score, result.VWAP, result.RSI,
                result.SupertrendBullish ? "BUY" : "SELL",
                result.ADX, bullSignals, bearSignals);

            return result;
        }
    }
}
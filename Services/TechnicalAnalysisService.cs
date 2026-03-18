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

        // ── Convert our candles to Skender quotes ───
        private List<Quote> ToQuotes(List<OhlcvCandle> candles)
        {
            return candles.Select(c => new Quote
            {
                Date = c.Timestamp,
                Open = (decimal)c.Open,
                High = (decimal)c.High,
                Low = (decimal)c.Low,
                Close = (decimal)c.Close,
                Volume = c.Volume
            }).ToList();
        }

        // ── Main compute method ─────────────────────
        public TechnicalResult Compute(
            string symbol, List<OhlcvCandle> candles)
        {
            var result = new TechnicalResult { Symbol = symbol };

            if (candles.Count < 50)
            {
                _logger.LogWarning(
                    "⚠️ Not enough candles for {symbol}: {count}",
                    symbol, candles.Count);
                result.Score = 50;
                return result;
            }

            var quotes = ToQuotes(candles);
            double score = 50;

            // ── RSI ─────────────────────────────────
            var rsiResults = quotes.GetRsi(14).ToList();
            var lastRsi = rsiResults.LastOrDefault(r => r.Rsi != null);
            if (lastRsi?.Rsi != null)
            {
                result.RSI = (double)lastRsi.Rsi;
                if (result.RSI < 30)
                {
                    score += 15;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "RSI",
                        Value = result.RSI.ToString("F1"),
                        Signal = "Oversold — Strong BUY signal",
                        IsBullish = true
                    });
                }
                else if (result.RSI > 70)
                {
                    score -= 15;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "RSI",
                        Value = result.RSI.ToString("F1"),
                        Signal = "Overbought — SELL signal",
                        IsBullish = false
                    });
                }
                else if (result.RSI > 50)
                {
                    score += 5;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "RSI",
                        Value = result.RSI.ToString("F1"),
                        Signal = "Bullish zone",
                        IsBullish = true
                    });
                }
                else
                {
                    score -= 5;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "RSI",
                        Value = result.RSI.ToString("F1"),
                        Signal = "Bearish zone",
                        IsBullish = false
                    });
                }
            }

            // ── MACD ────────────────────────────────
            var macdResults = quotes.GetMacd(12, 26, 9).ToList();
            var lastMacd = macdResults.LastOrDefault(
                m => m.Macd != null && m.Signal != null);
            if (lastMacd != null)
            {
                result.MACD = (double)(lastMacd.Macd ?? 0);
                result.MACDSignal = (double)(lastMacd.Signal ?? 0);
                result.MACDHistogram = (double)(lastMacd.Histogram ?? 0);

                if (result.MACDHistogram > 0)
                {
                    score += 12;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "MACD",
                        Value = result.MACDHistogram.ToString("F3"),
                        Signal = "Bullish crossover",
                        IsBullish = true
                    });
                }
                else
                {
                    score -= 8;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "MACD",
                        Value = result.MACDHistogram.ToString("F3"),
                        Signal = "Bearish crossover",
                        IsBullish = false
                    });
                }
            }

            // ── Bollinger Bands ──────────────────────
            var bbResults = quotes.GetBollingerBands(20, 2).ToList();
            var lastBb = bbResults.LastOrDefault(
                b => b.UpperBand != null && b.LowerBand != null);
            var lastClose = candles.Last().Close;

            if (lastBb != null)
            {
                result.BollingerUpper = (double)(lastBb.UpperBand ?? 0);
                result.BollingerMiddle = (double)(lastBb.Sma ?? 0);
                result.BollingerLower = (double)(lastBb.LowerBand ?? 0);
                result.BollingerPctB = (double)(lastBb.PercentB ?? 0);

                if (lastClose < result.BollingerLower)
                {
                    score += 10;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value = $"%B: {result.BollingerPctB:F2}",
                        Signal = "Below lower band — oversold",
                        IsBullish = true
                    });
                }
                else if (lastClose > result.BollingerUpper)
                {
                    score -= 10;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value = $"%B: {result.BollingerPctB:F2}",
                        Signal = "Above upper band — extended",
                        IsBullish = false
                    });
                }
                else
                {
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Bollinger Bands",
                        Value = $"%B: {result.BollingerPctB:F2}",
                        Signal = "Within bands — neutral",
                        IsBullish = null
                    });
                }
            }

            // ── EMA 20 / 50 / 200 ───────────────────
            var ema20 = quotes.GetEma(20).ToList();
            var ema50 = quotes.GetEma(50).ToList();
            var ema200 = quotes.GetEma(200).ToList();

            result.EMA20 = (double)(ema20.LastOrDefault(
                e => e.Ema != null)?.Ema ?? 0);
            result.EMA50 = (double)(ema50.LastOrDefault(
                e => e.Ema != null)?.Ema ?? 0);
            result.EMA200 = (double)(ema200.LastOrDefault(
                e => e.Ema != null)?.Ema ?? 0);

            // Price vs EMA trend
            if (lastClose > result.EMA20 &&
                result.EMA20 > result.EMA50 &&
                result.EMA50 > result.EMA200)
            {
                score += 15;
                result.Signals.Add(new TechnicalSignal
                {
                    Indicator = "EMA Trend",
                    Value = $"20:{result.EMA20:F0} 50:{result.EMA50:F0}",
                    Signal = "Price > EMA20 > EMA50 > EMA200 ✓",
                    IsBullish = true
                });
            }
            else if (lastClose < result.EMA20 &&
                     result.EMA20 < result.EMA50)
            {
                score -= 12;
                result.Signals.Add(new TechnicalSignal
                {
                    Indicator = "EMA Trend",
                    Value = $"20:{result.EMA20:F0} 50:{result.EMA50:F0}",
                    Signal = "Price < EMA20 < EMA50 ✗",
                    IsBullish = false
                });
            }
            else
            {
                result.Signals.Add(new TechnicalSignal
                {
                    Indicator = "EMA Trend",
                    Value = $"20:{result.EMA20:F0} 50:{result.EMA50:F0}",
                    Signal = "Mixed EMA signals",
                    IsBullish = null
                });
            }

            // ── ATR ─────────────────────────────────
            var atrResults = quotes.GetAtr(14).ToList();
            var lastAtr = atrResults.LastOrDefault(a => a.Atr != null);
            result.ATR = (double)(lastAtr?.Atr ?? 0);

            // ── ADX ─────────────────────────────────
            var adxResults = quotes.GetAdx(14).ToList();
            var lastAdx = adxResults.LastOrDefault(a => a.Adx != null);
            if (lastAdx != null)
            {
                result.ADX = (double)(lastAdx.Adx ?? 0);
                result.PlusDI = (double)(lastAdx.Pdi ?? 0);
                result.MinusDI = (double)(lastAdx.Mdi ?? 0);

                if (result.ADX > 25)
                {
                    bool trendBull = result.PlusDI > result.MinusDI;
                    score += trendBull ? 10 : -10;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "ADX",
                        Value = result.ADX.ToString("F1"),
                        Signal = $"Strong {(trendBull ? "uptrend" : "downtrend")} " +
                                 $"(ADX={result.ADX:F0})",
                        IsBullish = trendBull
                    });
                }
            }

            // ── VWAP ────────────────────────────────
            var vwapResults = quotes.GetVwap().ToList();
            var lastVwap = vwapResults.LastOrDefault(v => v.Vwap != null);
            if (lastVwap != null)
            {
                result.VWAP = (double)(lastVwap.Vwap ?? 0);
                if (lastClose > result.VWAP)
                {
                    score += 5;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "VWAP",
                        Value = $"₹{result.VWAP:F2}",
                        Signal = "Price above VWAP — bullish",
                        IsBullish = true
                    });
                }
                else
                {
                    score -= 5;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "VWAP",
                        Value = $"₹{result.VWAP:F2}",
                        Signal = "Price below VWAP — bearish",
                        IsBullish = false
                    });
                }
            }

            // ── Supertrend ──────────────────────────
            var stResults = quotes.GetSuperTrend(7, 3).ToList();
            var lastSt = stResults.LastOrDefault(
                s => s.SuperTrend != null);
            if (lastSt != null)
            {
                result.Supertrend = (double)(lastSt.SuperTrend ?? 0);
                result.SupertrendBullish = lastSt.UpperBand == null;

                if (result.SupertrendBullish)
                {
                    score += 8;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Supertrend",
                        Value = $"₹{result.Supertrend:F0}",
                        Signal = "Supertrend BUY signal ✓",
                        IsBullish = true
                    });
                }
                else
                {
                    score -= 8;
                    result.Signals.Add(new TechnicalSignal
                    {
                        Indicator = "Supertrend",
                        Value = $"₹{result.Supertrend:F0}",
                        Signal = "Supertrend SELL signal ✗",
                        IsBullish = false
                    });
                }
            }

            // ── Target & Stop Loss using ATR ────────
            result.SuggestedTarget = lastClose + (result.ATR * 2.5);
            result.SuggestedStopLoss = lastClose - (result.ATR * 1.5);

            // ── Final Score ──────────────────────────
            result.Score = Math.Max(0, Math.Min(100, score));
            result.CalculatedAt = DateTime.Now;

            _logger.LogInformation(
                "✅ Technical score for {symbol}: {score:F1}",
                symbol, result.Score);

            return result;
        }
    }
}
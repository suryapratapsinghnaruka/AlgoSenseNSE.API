using AlgoSenseNSE.API.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace AlgoSenseNSE.API.Services
{
    public class ClaudeAiService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<ClaudeAiService> _logger;
        private readonly HttpClient _http;
        private readonly NseIndiaService _nse;

        private readonly Dictionary<string, AiAnalysis> _cache = new();

        public ClaudeAiService(
            IConfiguration config,
            ILogger<ClaudeAiService> logger,
            IHttpClientFactory httpClientFactory,
            NseIndiaService nse)
        {
            _config = config;
            _logger = logger;
            _http = httpClientFactory.CreateClient("Claude");
            _nse = nse;
        }

        public async Task<AiAnalysis> AnalyzeStockAsync(
            StockInfo stock,
            TechnicalResult tech,
            FundamentalResult fund,
            List<NewsItem> news,
            CompositeScore score)
        {
            // Cache 10 minutes for intraday
            if (_cache.TryGetValue(stock.Symbol, out var cached) &&
                (DateTime.Now - cached.GeneratedAt).TotalMinutes < 10)
                return cached;

            try
            {
                var capital = _config.GetValue<double>("Trading:Capital", 1500);
                var ist = GetIST();
                var minsLeft = (int)(new DateTime(ist.Year, ist.Month,
                    ist.Day, 15, 30, 0) - ist).TotalMinutes;
                var timeCtx = minsLeft > 0
                    ? $"Market closes in {minsLeft} min ({ist:HH:mm} IST)"
                    : "Market closed — pre-market analysis";

                int shares = stock.LastPrice > 0
                    ? (int)(capital * 0.60 / stock.LastPrice) : 0;

                double brokerage = 40.0;
                double minProfitNeeded = shares > 0
                    ? brokerage / shares : 999;

                // ── Fetch live market context ──────────
                var mktCtx = await _nse.GetMarketContextAsync();
                var sector = _nse.GetSectorForSymbol(stock.Symbol);
                var sectorPerf = _nse.GetSectorPerformance(
                    stock.Symbol, mktCtx);
                string sectorText = sectorPerf != null
                    ? $"{sector}: {(sectorPerf.ChangePercent >= 0 ? "+" : "")}{sectorPerf.ChangePercent:F2}% today"
                    : $"{sector}: Data unavailable";

                // ── Signal details ─────────────────────
                int bullCount = tech.Signals.Count(s => s.IsBullish == true);
                int bearCount = tech.Signals.Count(s => s.IsBullish == false);
                string consensus = bullCount > bearCount
                    ? $"BULLISH ({bullCount} bull vs {bearCount} bear signals)"
                    : bearCount > bullCount
                    ? $"BEARISH ({bearCount} bear vs {bullCount} bull signals)"
                    : "MIXED — equal bull/bear";

                // All bullish signals as text
                var bullSignals = tech.Signals
                    .Where(s => s.IsBullish == true)
                    .Select(s => $"  ✅ {s.Indicator}: {s.Signal}")
                    .ToList();
                var bearSignals = tech.Signals
                    .Where(s => s.IsBullish == false)
                    .Select(s => $"  ❌ {s.Indicator}: {s.Signal}")
                    .ToList();

                var newsText = news.Any()
                    ? string.Join("\n", news.Take(3)
                        .Select(n => $"  • {n.Headline} [{n.SentimentLabel}]"))
                    : "  No specific news today";

                // ── Top/weak sectors ───────────────────
                var topSectors = mktCtx.TopSectors.Any()
                    ? string.Join(", ", mktCtx.TopSectors)
                    : "Data unavailable";
                var weakSectors = mktCtx.WeakSectors.Any()
                    ? string.Join(", ", mktCtx.WeakSectors)
                    : "Data unavailable";

                // ── Build improved multi-layer prompt ──
                var prompt = $@"You are an expert NSE intraday trader with 15 years experience.
Help a beginner make ₹100-₹200 profit today with ₹{capital:N0} capital.
{timeCtx}

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
LAYER 1: MARKET CONTEXT (most important)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Nifty 50:      ₹{mktCtx.NiftyLtp:N0} ({(mktCtx.NiftyChange >= 0 ? "+" : "")}{mktCtx.NiftyChange:F2}%)
Nifty Trend:   {mktCtx.NiftyTrend}
Nifty Position:{mktCtx.NiftyDayPosition}

India VIX:     {mktCtx.IndiaVix:F1} ({(mktCtx.VixChange >= 0 ? "+" : "")}{mktCtx.VixChange:F2})
VIX Signal:    {mktCtx.VixInterpretation}

FII Activity:  ₹{mktCtx.FiiNetCrore:N0} Cr → {mktCtx.FiiSentiment}
DII Activity:  {mktCtx.DiiSentiment}

Market Quality:{mktCtx.MarketQualityScore}/100 — {mktCtx.MarketQualityLabel}

Strong Sectors:  {topSectors}
Weak Sectors:    {weakSectors}
This Stock's Sector: {sectorText}

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
LAYER 2: STOCK TECHNICAL (5-min candles)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Stock:    {stock.Symbol} | CMP: ₹{stock.LastPrice:F2}
Score:    {score.FinalScore:F0}/100

VWAP:     ₹{tech.VWAP:F2} → Price {(stock.LastPrice > tech.VWAP ? "ABOVE VWAP ✅ (bullish)" : "BELOW VWAP ❌ (bearish)")}
EMA 9:    ₹{tech.EMA20:F2} | EMA 21: ₹{tech.EMA50:F2} → {(tech.EMA20 > tech.EMA50 ? "EMA9 > EMA21 ✅ Bullish" : "EMA9 < EMA21 ❌ Bearish")}
EMA 50:   ₹{tech.EMA200:F2} → Price {(stock.LastPrice > tech.EMA200 ? "above ✅" : "below ❌")} medium-term trend
RSI(14):  {tech.RSI:F1} → {(tech.RSI >= 55 && tech.RSI <= 72 ? "✅ Bullish zone (55-72)" : tech.RSI > 72 ? "❌ Overbought (>72)" : tech.RSI < 40 ? "❌ Oversold (<40)" : "⚠️ Neutral")}
MACD:     Histogram={tech.MACDHistogram:F3} → {(tech.MACDHistogram > 0 ? "✅ Bullish momentum" : "❌ Bearish momentum")}
Supertrend: {(tech.SupertrendBullish ? "✅ BUY signal (most reliable)" : "❌ SELL signal")}
ADX:      {tech.ADX:F0} → {(tech.ADX > 25 ? "✅ Strong trend present" : tech.ADX > 15 ? "⚠️ Moderate trend" : "❌ No trend (choppy)")}
ATR:      ₹{tech.ATR:F2} (volatility measure)
BB %B:    {tech.BollingerPctB:F2} → {(tech.BollingerPctB > 0.8 ? "Near upper band (overbought)" : tech.BollingerPctB < 0.2 ? "Near lower band (oversold)" : "Middle of bands")}

INDICATOR CONSENSUS: {consensus}
Bull Signals:
{(bullSignals.Any() ? string.Join("\n", bullSignals) : "  None")}
Bear Signals:
{(bearSignals.Any() ? string.Join("\n", bearSignals) : "  None")}

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
LAYER 3: FUNDAMENTAL QUALITY
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
PE Ratio:  {fund.PE:F1} | ROE: {fund.ROE:F1}% | ROCE: {fund.ROCE:F1}%
Promoter:  {fund.PromoterHolding:F1}% holding (>{60}% = good)
Fund Score:{fund.Score}/100

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
LAYER 4: NEWS SENTIMENT
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
{newsText}

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
CAPITAL & TRADE SETUP
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Capital:         ₹{capital:N0}
Max shares:      {shares} (using 60% capital)
Suggested entry: ₹{stock.LastPrice:F2}
Suggested target:₹{tech.SuggestedTarget:F2} (+{((tech.SuggestedTarget - stock.LastPrice) / stock.LastPrice * 100):F2}%)
Suggested SL:    ₹{tech.SuggestedStopLoss:F2} (-{((stock.LastPrice - tech.SuggestedStopLoss) / stock.LastPrice * 100):F2}%)
Brokerage:       ₹{brokerage:F0} round trip
Min profit/share needed: ₹{minProfitNeeded:F2}

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
DECISION RULES (follow strictly)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
ALWAYS AVOID if:
• Market Quality < 40 (bad market day)
• VIX > 22 (too fearful)
• FII sold > ₹2000Cr (institutions exiting)
• Nifty down > 1% (strong bear day)
• Stock's sector is in weak sectors list
• Supertrend = SELL
• Price below VWAP
• ADX < 15 (no trend)
• Less than 60 min to market close
• Shares affordable = 0 (too expensive)
• Expected profit < brokerage × 1.5

STRONG BUY if ALL true:
• Market Quality > 60
• VIX < 18
• FII net positive or DII supporting
• Supertrend = BUY ✅
• Price above VWAP ✅
• RSI 55-72 ✅
• ADX > 20 ✅
• Stock's sector is strong today ✅
• 4+ bullish signals ✅

Respond ONLY in this exact JSON. No markdown, no extra text:
{{
  ""recommendation"": ""BUY"" or ""AVOID"",
  ""confidence"": <50-95>,
  ""entry"": <exact entry price>,
  ""target"": <realistic intraday target — achievable in 1-2 hrs>,
  ""stopLoss"": <max 0.75% below entry>,
  ""riskReward"": ""1:X.X"",
  ""expectedProfit"": ""₹XX on {shares} shares after ₹{brokerage:F0} brokerage"",
  ""timeHorizon"": ""Exit by HH:MM"",
  ""summary"": ""One sentence: market context + stock signal + decision"",
  ""keyDrivers"": [""driver 1"", ""driver 2"", ""driver 3""],
  ""risks"": [""risk 1"", ""risk 2""],
  ""marketView"": ""Bullish"" or ""Bearish"" or ""Neutral"",
  ""technicalView"": ""Bullish"" or ""Bearish"" or ""Neutral"",
  ""fundamentalView"": ""Strong"" or ""Average"" or ""Weak"",
  ""newsView"": ""Positive"" or ""Negative"" or ""Neutral"",
  ""sectorView"": ""Strong"" or ""Weak"" or ""Neutral""
}}";

                var payload = new
                {
                    model = "claude-sonnet-4-20250514",
                    max_tokens = 1000,
                    messages = new[]
                    {
                        new { role = "user", content = prompt }
                    }
                };

                var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://api.anthropic.com/v1/messages");
                req.Headers.Add("x-api-key", _config["Claude:ApiKey"]);
                req.Headers.Add("anthropic-version", "2023-06-01");
                req.Content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(req);
                var json = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);
                var text = result["content"]?[0]?["text"]
                    ?.Value<string>() ?? "";

                var clean = text
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();

                var analysis = JsonConvert
                    .DeserializeObject<AiAnalysis>(clean)
                    ?? FallbackAnalysis(stock, tech, score, shares, mktCtx);

                analysis.Symbol = stock.Symbol;
                analysis.GeneratedAt = DateTime.Now;

                _cache[stock.Symbol] = analysis;

                _logger.LogInformation(
                    "✅ AI {sym}: {rec} conf:{conf}% | " +
                    "VIX:{vix:F1} FII:₹{fii:N0}Cr Nifty:{trend} | {summary}",
                    stock.Symbol, analysis.Recommendation,
                    analysis.Confidence,
                    mktCtx.IndiaVix, mktCtx.FiiNetCrore,
                    mktCtx.NiftyChange >= 0
                        ? $"+{mktCtx.NiftyChange:F1}%"
                        : $"{mktCtx.NiftyChange:F1}%",
                    analysis.Summary?.Length > 80
                        ? analysis.Summary[..80] + "..."
                        : analysis.Summary);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Claude AI failed for {sym}", stock.Symbol);
                int shares = stock.LastPrice > 0
                    ? (int)(_config.GetValue<double>("Trading:Capital", 1500)
                        * 0.60 / stock.LastPrice) : 0;
                return FallbackAnalysis(stock, tech, score, shares, null);
            }
        }

        // ── Fallback when Claude API fails ────────────
        private AiAnalysis FallbackAnalysis(
            StockInfo stock, TechnicalResult tech,
            CompositeScore score, int shares,
            MarketContext? mkt)
        {
            // Conservative: require ALL 3 main signals
            bool marketOk = mkt == null || mkt.MarketQualityScore >= 45;
            bool vixOk = mkt == null || mkt.IndiaVix < 20;

            bool strongBuy = marketOk && vixOk
                && tech.SupertrendBullish
                && stock.LastPrice > tech.VWAP
                && tech.RSI > 55 && tech.RSI < 72
                && tech.ADX > 18
                && tech.Score >= 65;

            string rec = strongBuy ? "BUY" : "AVOID";
            double brokerage = 40.0;
            double grossProfit = shares * (tech.SuggestedTarget - stock.LastPrice);
            double netProfit = grossProfit - brokerage;

            string marketNote = mkt != null
                ? $" | Market:{mkt.MarketQualityScore}/100 VIX:{mkt.IndiaVix:F1}"
                : "";

            return new AiAnalysis
            {
                Symbol = stock.Symbol,
                Recommendation = rec,
                Confidence = strongBuy ? 68 : 55,
                Entry = stock.LastPrice,
                Target = tech.SuggestedTarget > stock.LastPrice
                    ? tech.SuggestedTarget
                    : stock.LastPrice * 1.003,
                StopLoss = tech.SuggestedStopLoss < stock.LastPrice
                    ? tech.SuggestedStopLoss
                    : stock.LastPrice * 0.9975,
                RiskReward = "1:1.5",
                ExpectedProfit = $"₹{netProfit:F0} on {shares} shares",
                TimeHorizon = "Exit by 14:00",
                Summary = rec == "BUY"
                    ? $"{stock.Symbol}: Technical + market aligned{marketNote}"
                    : $"{stock.Symbol}: Weak signals or bad market{marketNote}",
                KeyDrivers = tech.Signals
                    .Where(s => s.IsBullish == true)
                    .Take(3).Select(s => s.Signal).ToList(),
                Risks = tech.Signals
                    .Where(s => s.IsBullish == false)
                    .Take(2).Select(s => s.Signal).ToList(),
                TechnicalView = tech.Score > 65 ? "Bullish"
                                : tech.Score > 45 ? "Neutral" : "Bearish",
                FundamentalView = "Average",
                NewsView = score.NewsScore > 60 ? "Positive"
                                : score.NewsScore > 40 ? "Neutral" : "Negative",
                GeneratedAt = DateTime.Now
            };
        }

        public void ClearCache(string symbol) => _cache.Remove(symbol);
        public void ClearAllCache() => _cache.Clear();

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
}
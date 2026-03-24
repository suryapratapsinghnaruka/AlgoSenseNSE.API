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
                double broker = 40.0;
                double minProfit = shares > 0 ? broker / shares : 999;

                // Market context
                var mktCtx = await _nse.GetMarketContextAsync();
                var sector = _nse.GetSectorForSymbol(stock.Symbol);
                var sectorPerf = _nse.GetSectorPerformance(stock.Symbol, mktCtx);
                string sectorText = sectorPerf != null
                    ? $"{sector}: {(sectorPerf.ChangePercent >= 0 ? "+" : "")}{sectorPerf.ChangePercent:F2}% today"
                    : $"{sector}: data unavailable";

                int bullCount = tech.Signals.Count(s => s.IsBullish == true);
                int bearCount = tech.Signals.Count(s => s.IsBullish == false);
                string consensus = bullCount > bearCount
                    ? $"BULLISH ({bullCount} bull vs {bearCount} bear)"
                    : bearCount > bullCount
                    ? $"BEARISH ({bearCount} bear vs {bullCount} bull)"
                    : "MIXED";

                var bullSigs = tech.Signals
                    .Where(s => s.IsBullish == true)
                    .Select(s => $"  ✅ {s.Indicator}: {s.Signal}").ToList();
                var bearSigs = tech.Signals
                    .Where(s => s.IsBullish == false)
                    .Select(s => $"  ❌ {s.Indicator}: {s.Signal}").ToList();

                var newsText = news.Any()
                    ? string.Join("\n", news.Take(3)
                        .Select(n => $"  • {n.Headline} [{n.SentimentLabel}]"))
                    : "  No specific news today";

                var topSec = mktCtx.TopSectors.Any()
                    ? string.Join(", ", mktCtx.TopSectors) : "Unavailable";
                var weakSec = mktCtx.WeakSectors.Any()
                    ? string.Join(", ", mktCtx.WeakSectors) : "Unavailable";

                var prompt = $@"You are an expert NSE intraday trader.
Help a beginner make ₹100-₹200 profit today with ₹{capital:N0} capital.
{timeCtx}

━━━ LAYER 1: MARKET CONTEXT ━━━
Nifty 50:       ₹{mktCtx.NiftyLtp:N0} ({(mktCtx.NiftyChange >= 0 ? "+" : "")}{mktCtx.NiftyChange:F2}%)
Nifty Trend:    {mktCtx.NiftyTrend}
India VIX:      {mktCtx.IndiaVix:F1} — {mktCtx.VixInterpretation}
FII Activity:   ₹{mktCtx.FiiNetCrore:N0}Cr → {mktCtx.FiiSentiment}
Market Quality: {mktCtx.MarketQualityScore}/100 — {mktCtx.MarketQualityLabel}
Strong Sectors: {topSec}
Weak Sectors:   {weakSec}
This Stock:     {sectorText}

━━━ LAYER 2: TECHNICAL (5-min candles) ━━━
Stock:      {stock.Symbol} | CMP: ₹{stock.LastPrice:F2}
Score:      {score.FinalScore:F0}/100
VWAP:       ₹{tech.VWAP:F2} → {(stock.LastPrice > tech.VWAP ? "ABOVE ✅" : "BELOW ❌")}
EMA 9/21:   {(tech.EMA20 > tech.EMA50 ? "Bullish ✅" : "Bearish ❌")} ({tech.EMA20:F1}/{tech.EMA50:F1})
RSI(14):    {tech.RSI:F1} → {(tech.RSI >= 55 && tech.RSI <= 72 ? "✅ Buy zone" : tech.RSI > 72 ? "❌ Overbought" : tech.RSI < 40 ? "❌ Weak" : "⚠️ Neutral")}
MACD:       {tech.MACDHistogram:F3} {(tech.MACDHistogram > 0 ? "✅" : "❌")}
Supertrend: {(tech.SupertrendBullish ? "BUY ✅" : "SELL ❌")}
ADX:        {tech.ADX:F0} {(tech.ADX > 25 ? "✅ Strong" : "⚠️ Weak")}
ATR:        ₹{tech.ATR:F2}
CONSENSUS:  {consensus}
Bull signals: {string.Join("; ", bullSigs.Take(3))}
Bear signals: {string.Join("; ", bearSigs.Take(2))}

━━━ LAYER 3: FUNDAMENTALS ━━━
PE: {fund.PE:F1} | ROE: {fund.ROE:F1}% | ROCE: {fund.ROCE:F1}%
Promoter: {fund.PromoterHolding:F1}% | Fund Score: {fund.Score}/100

━━━ LAYER 4: NEWS ━━━
{newsText}

━━━ CAPITAL MATH ━━━
Shares: {shares} (60% of ₹{capital:N0})
Entry: ₹{stock.LastPrice:F2} | Target: ₹{tech.SuggestedTarget:F2} | SL: ₹{tech.SuggestedStopLoss:F2}
Brokerage: ₹{broker:F0} | Min profit/share needed: ₹{minProfit:F2}

RULES — ALWAYS AVOID IF:
• Market Quality < 40
• VIX > 22
• FII sold > ₹2000Cr
• Nifty down > 1%
• Supertrend = SELL
• Price below VWAP
• ADX < 15
• Shares = 0 (too expensive)
• < 60 min to close

Respond ONLY in this JSON. No markdown:
{{
  ""recommendation"": ""BUY"" or ""AVOID"",
  ""confidence"": <50-95>,
  ""entry"": <price or 0 if AVOID>,
  ""target"": <price or 0 if AVOID>,
  ""stopLoss"": <price or 0 if AVOID>,
  ""riskReward"": ""1:X.X"" or ""N/A"",
  ""expectedProfit"": ""₹XX on {shares} shares"",
  ""timeHorizon"": ""Exit by HH:MM"",
  ""summary"": ""Market context + signal + decision in one sentence"",
  ""keyDrivers"": [""reason 1"", ""reason 2"", ""reason 3""],
  ""risks"": [""risk 1"", ""risk 2""],
  ""marketView"": ""Bullish"" or ""Bearish"" or ""Neutral"",
  ""technicalView"": ""Bullish"" or ""Bearish"" or ""Neutral"",
  ""fundamentalView"": ""Strong"" or ""Average"" or ""Weak"",
  ""newsView"": ""Positive"" or ""Negative"" or ""Neutral"",
  ""sectorView"": ""Strong"" or ""Weak"" or ""Neutral""
}}";

                var payload = new
                {
                    model = "claude-haiku-4-5-20251001",
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

                // Fix AVOID — zero out trade levels
                if (analysis.Recommendation == "AVOID")
                {
                    analysis.Entry = stock.LastPrice;
                    analysis.Target = stock.LastPrice;
                    analysis.StopLoss = stock.LastPrice;
                    analysis.RiskReward = "N/A";
                }
                else
                {
                    if (analysis.Target <= analysis.Entry)
                        analysis.Target = analysis.Entry * 1.005;
                    if (analysis.StopLoss >= analysis.Entry)
                        analysis.StopLoss = analysis.Entry * 0.9975;
                }

                analysis.Symbol = stock.Symbol;
                analysis.GeneratedAt = DateTime.Now;
                _cache[stock.Symbol] = analysis;

                _logger.LogInformation(
                    "✅ AI {sym}: {rec} conf:{conf}% | " +
                    "VIX:{vix:F1} FII:₹{fii:N0}Cr Nifty:{chg} | {summary}",
                    stock.Symbol, analysis.Recommendation, analysis.Confidence,
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
                int sh = stock.LastPrice > 0
                    ? (int)(_config.GetValue<double>("Trading:Capital", 1500)
                        * 0.60 / stock.LastPrice) : 0;
                return FallbackAnalysis(stock, tech, score, sh, null);
            }
        }

        private AiAnalysis FallbackAnalysis(
            StockInfo stock, TechnicalResult tech,
            CompositeScore score, int shares,
            MarketContext? mkt)
        {
            bool marketOk = mkt == null || mkt.MarketQualityScore >= 45;
            bool vixOk = mkt == null || mkt.IndiaVix < 20;

            bool strongBuy = marketOk && vixOk
                && tech.SupertrendBullish
                && stock.LastPrice > tech.VWAP
                && tech.RSI > 55 && tech.RSI < 72
                && tech.ADX > 18 && tech.Score >= 65;

            string rec = strongBuy ? "BUY" : "AVOID";
            double broker = 40.0;
            double gross = shares * (tech.SuggestedTarget - stock.LastPrice);
            double net = gross - broker;

            return new AiAnalysis
            {
                Symbol = stock.Symbol,
                Recommendation = rec,
                Confidence = strongBuy ? 68 : 55,
                Entry = stock.LastPrice,
                Target = rec == "BUY"
                    ? (tech.SuggestedTarget > stock.LastPrice
                        ? tech.SuggestedTarget
                        : stock.LastPrice * 1.005)
                    : stock.LastPrice,
                StopLoss = rec == "BUY"
                    ? (tech.SuggestedStopLoss < stock.LastPrice
                        ? tech.SuggestedStopLoss
                        : stock.LastPrice * 0.9975)
                    : stock.LastPrice,
                RiskReward = rec == "BUY" ? "1:1.5" : "N/A",
                ExpectedProfit = $"₹{net:F0} on {shares} shares",
                TimeHorizon = "Exit by 14:00",
                Summary = rec == "BUY"
                    ? $"{stock.Symbol}: Technical signals aligned"
                    : $"{stock.Symbol}: Poor market conditions or weak signals",
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
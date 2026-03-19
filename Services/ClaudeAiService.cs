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
        private readonly Dictionary<string, AiAnalysis> _cache = new();

        public ClaudeAiService(
            IConfiguration config,
            ILogger<ClaudeAiService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _logger = logger;
            _http = httpClientFactory.CreateClient("Claude");
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
                var minsLeft = (int)(new DateTime(ist.Year, ist.Month, ist.Day,
                    15, 30, 0) - ist).TotalMinutes;
                var timeCtx = minsLeft > 0
                    ? $"Market closes in {minsLeft} min"
                    : "Market closed — pre-market analysis";

                // Shares based on capital
                int shares = stock.LastPrice > 0
                    ? (int)(capital * 0.60 / stock.LastPrice) : 0;

                // Bull/bear signal count
                int bullCount = tech.Signals.Count(s => s.IsBullish == true);
                int bearCount = tech.Signals.Count(s => s.IsBullish == false);
                string consensus = bullCount > bearCount
                    ? $"BULLISH ({bullCount} bull vs {bearCount} bear)"
                    : bearCount > bullCount
                    ? $"BEARISH ({bearCount} bear vs {bullCount} bull)"
                    : "MIXED — signals equal";

                // Brokerage impact
                double brokerage = 40.0;
                double minProfitNeeded = brokerage / Math.Max(shares, 1);

                var newsText = news.Any()
                    ? string.Join("\n", news.Take(3)
                        .Select(n => $"  - {n.Headline} [{n.SentimentLabel}]"))
                    : "  No specific news today";

                var prompt = $@"You are an expert NSE intraday trader.
Help a beginner make ₹100-₹200 profit today with ₹{capital:N0} capital.

STOCK: {stock.Symbol} | CMP: ₹{stock.LastPrice:F2}
Time: {ist:HH:mm} IST | {timeCtx}

━━━ 5-MIN CANDLE INDICATORS ━━━
Score:        {score.FinalScore:F0}/100
VWAP:         ₹{tech.VWAP:F2} → Price is {(stock.LastPrice > tech.VWAP ? "ABOVE ✅" : "BELOW ❌")}
EMA 9:        ₹{tech.EMA20:F2}
EMA 21:       ₹{tech.EMA50:F2} → {(tech.EMA20 > tech.EMA50 ? "Bullish ✅" : "Bearish ❌")}
RSI (14):     {tech.RSI:F1} → {(tech.RSI > 60 ? "Strong ✅" : tech.RSI < 40 ? "Weak ❌" : "Neutral")}
MACD Hist:    {tech.MACDHistogram:F3} {(tech.MACDHistogram > 0 ? "✅" : "❌")}
Supertrend:   {(tech.SupertrendBullish ? "BUY ✅" : "SELL ❌")}
ADX:          {tech.ADX:F0} {(tech.ADX > 25 ? "(Strong trend)" : "(Weak trend ⚠️)")}
ATR:          ₹{tech.ATR:F2}

CONSENSUS: {consensus}

━━━ TRADE SETUP ━━━
Suggested Entry: ₹{stock.LastPrice:F2}
Suggested Target: ₹{tech.SuggestedTarget:F2}
Suggested SL:    ₹{tech.SuggestedStopLoss:F2}

━━━ CAPITAL MATH (₹{capital:N0}) ━━━
Shares affordable: {shares}
Min profit needed to beat brokerage: ₹{minProfitNeeded:F2}/share
Brokerage cost: ₹{brokerage:F0} (buy+sell Angel One)

━━━ FUNDAMENTALS ━━━
PE: {fund.PE:F1} | ROE: {fund.ROE:F1}% | Promoter: {fund.PromoterHolding:F1}%

━━━ NEWS ━━━
{newsText}

STRICT RULES YOU MUST FOLLOW:
1. If ADX < 20 → AVOID (no trend, choppy)
2. If MACD and Supertrend conflict → AVOID
3. If RSI > 75 → AVOID (overbought)
4. If consensus is MIXED → AVOID
5. Stop loss must be within 0.75% of entry
6. Target must give at least ₹{brokerage * 2:F0} profit after brokerage
7. Target must be achievable within 1-2 hours
8. If less than 60 min to market close → AVOID

Respond ONLY in this exact JSON. No markdown, no extra text:
{{
  ""recommendation"": ""BUY"" or ""AVOID"",
  ""confidence"": <55-92>,
  ""entry"": <price>,
  ""target"": <realistic intraday target>,
  ""stopLoss"": <strict — max 0.75% below entry>,
  ""riskReward"": ""1:X.X"",
  ""expectedProfit"": ""₹XX on {shares} shares after brokerage"",
  ""timeHorizon"": ""Exit by HH:MM"",
  ""summary"": ""One clear sentence why to trade or avoid"",
  ""keyDrivers"": [""reason 1"", ""reason 2"", ""reason 3""],
  ""risks"": [""risk 1"", ""risk 2""],
  ""technicalView"": ""Bullish"" or ""Bearish"" or ""Neutral"",
  ""fundamentalView"": ""Strong"" or ""Average"" or ""Weak"",
  ""newsView"": ""Positive"" or ""Negative"" or ""Neutral""
}}";

                var payload = new
                {
                    model = "claude-sonnet-4-20250514",
                    max_tokens = 800,
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
                    ?? FallbackAnalysis(stock, tech, score, shares);

                analysis.Symbol = stock.Symbol;
                analysis.GeneratedAt = DateTime.Now;

                _cache[stock.Symbol] = analysis;

                _logger.LogInformation(
                    "✅ AI {sym}: {rec} conf:{conf}% — {summary}",
                    stock.Symbol,
                    analysis.Recommendation,
                    analysis.Confidence,
                    analysis.Summary?.Length > 60
                        ? analysis.Summary[..60] + "..."
                        : analysis.Summary);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Claude AI failed for {sym}", stock.Symbol);
                int shares = stock.LastPrice > 0
                    ? (int)(_config.GetValue<double>("Trading:Capital", 1500)
                        * 0.60 / stock.LastPrice) : 0;
                return FallbackAnalysis(stock, tech, score, shares);
            }
        }

        private AiAnalysis FallbackAnalysis(
            StockInfo stock,
            TechnicalResult tech,
            CompositeScore score,
            int shares)
        {
            // Conservative fallback — only BUY if all 3 main signals align
            bool strongBuy = tech.SupertrendBullish
                && stock.LastPrice > tech.VWAP
                && tech.RSI > 55 && tech.RSI < 75
                && tech.ADX > 22
                && tech.Score >= 65;

            string rec = strongBuy ? "BUY" : "AVOID";

            double brokerage = 40.0;
            double grossProfit = shares * (tech.SuggestedTarget - stock.LastPrice);
            double netProfit = grossProfit - brokerage;

            return new AiAnalysis
            {
                Symbol = stock.Symbol,
                Recommendation = rec,
                Confidence = strongBuy ? 68 : 55,
                Entry = stock.LastPrice,
                Target = tech.SuggestedTarget,
                StopLoss = tech.SuggestedStopLoss,
                RiskReward = tech.SuggestedStopLoss > 0 &&
                    (stock.LastPrice - tech.SuggestedStopLoss) > 0
                    ? $"1:{((tech.SuggestedTarget - stock.LastPrice) / (stock.LastPrice - tech.SuggestedStopLoss)):F1}"
                    : "N/A",
                ExpectedProfit = $"₹{netProfit:F0} on {shares} shares (after ₹{brokerage:F0} brokerage)",
                TimeHorizon = "Exit by 14:00",
                Summary = rec == "BUY"
                    ? $"{stock.Symbol}: VWAP+Supertrend+RSI all bullish — momentum trade"
                    : $"{stock.Symbol}: Mixed signals — skip today",
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
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

        // ── Main analysis method ─────────────────────
        public async Task<AiAnalysis> AnalyzeStockAsync(
            StockInfo stock,
            TechnicalResult tech,
            FundamentalResult fund,
            List<NewsItem> news,
            CompositeScore score)
        {
            // Return cache if analyzed in last 30 mins
            if (_cache.TryGetValue(stock.Symbol, out var cached) &&
                (DateTime.Now - cached.GeneratedAt).TotalMinutes < 30)
                return cached;

            try
            {
                var newsHeadlines = news.Any()
                    ? string.Join("\n", news.Take(5)
                        .Select(n =>
                            $"- {n.Headline} " +
                            $"[{n.SentimentLabel}] ({n.Source})"))
                    : "No specific news today";

                var prompt = $@"You are an expert Indian stock market analyst 
specializing in NSE/BSE stocks. Analyze this stock comprehensively and give 
a precise trading recommendation.

═══════════════════════════════════════
STOCK: {stock.Symbol} | CMP: ₹{stock.LastPrice:F2}
Day Change: {stock.ChangePercent:F2}% | 
Volume: {stock.Volume:N0}
═══════════════════════════════════════

TECHNICAL ANALYSIS (Score: {tech.Score:F0}/100):
- RSI (14):        {tech.RSI:F1}
- MACD Histogram:  {tech.MACDHistogram:F3}
- Bollinger %B:    {tech.BollingerPctB:F2}
- EMA 20:          ₹{tech.EMA20:F2}
- EMA 50:          ₹{tech.EMA50:F2}  
- EMA 200:         ₹{tech.EMA200:F2}
- ADX:             {tech.ADX:F1}
- +DI / -DI:       {tech.PlusDI:F1} / {tech.MinusDI:F1}
- VWAP:            ₹{tech.VWAP:F2}
- ATR (14):        ₹{tech.ATR:F2}
- Supertrend:      {(tech.SupertrendBullish ? "BUY ✓" : "SELL ✗")}
- Suggested Target:   ₹{tech.SuggestedTarget:F2}
- Suggested Stop Loss:₹{tech.SuggestedStopLoss:F2}

FUNDAMENTAL ANALYSIS (Score: {fund.Score:F0}/100):
- P/E Ratio:       {fund.PE:F1}
- P/B Ratio:       {fund.PB:F2}
- ROE:             {fund.ROE:F1}%
- ROCE:            {fund.ROCE:F1}%
- Debt/Equity:     {fund.DebtToEquity:F2}
- Revenue Growth:  {fund.RevenueGrowthYoY:F1}% YoY
- EPS Growth:      {fund.EPSGrowth:F1}%
- Promoter Hold:   {fund.PromoterHolding:F1}%
- FII Holding:     {fund.FIIHolding:F1}%
- Market Cap:      {fund.MarketCap}

RECENT NEWS (Sentiment score: {score.NewsScore:F0}/100):
{newsHeadlines}

COMPOSITE SCORE: {score.FinalScore:F1}/100
Technical: {score.TechnicalScore:F0} | 
Fundamental: {score.FundamentalScore:F0} | 
News: {score.NewsScore:F0}

Based on ALL the above data, provide your analysis.
Respond ONLY in this exact JSON format, no markdown, 
no extra text, no explanation outside JSON:

{{
  ""recommendation"": ""BUY"" or ""HOLD"" or ""SELL"",
  ""confidence"": <integer 55-95>,
  ""entry"": <number: suggested entry price>,
  ""target"": <number: 4-8 week price target>,
  ""stopLoss"": <number: strict stop loss>,
  ""riskReward"": ""1:X.X"",
  ""timeHorizon"": ""X-X weeks"",
  ""summary"": ""2-3 sentences: key catalyst + risk + overall view"",
  ""keyDrivers"": [
    ""driver 1"",
    ""driver 2"",
    ""driver 3""
  ],
  ""risks"": [
    ""risk 1"",
    ""risk 2""
  ],
  ""technicalView"": ""Bullish"" or ""Bearish"" or ""Neutral"",
  ""fundamentalView"": ""Strong"" or ""Average"" or ""Weak"",
  ""newsView"": ""Positive"" or ""Negative"" or ""Neutral""
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
                req.Headers.Add("x-api-key",
                    _config["Claude:ApiKey"]);
                req.Headers.Add("anthropic-version", "2023-06-01");
                req.Content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(req);
                var json = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);

                var text = result["content"]?[0]?["text"]
                    ?.Value<string>() ?? "";

                // Clean and parse JSON response
                var clean = text
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();

                var analysis = JsonConvert
                    .DeserializeObject<AiAnalysis>(clean)
                    ?? FallbackAnalysis(stock, tech, score);

                analysis.Symbol = stock.Symbol;
                analysis.GeneratedAt = DateTime.Now;

                _cache[stock.Symbol] = analysis;

                _logger.LogInformation(
                    "✅ AI analysis for {symbol}: {rec} " +
                    "(confidence: {conf}%)",
                    stock.Symbol,
                    analysis.Recommendation,
                    analysis.Confidence);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "❌ Claude AI analysis failed for {symbol}",
                    stock.Symbol);
                return FallbackAnalysis(stock, tech, score);
            }
        }

        // ── Fallback if Claude API fails ─────────────
        private AiAnalysis FallbackAnalysis(
            StockInfo stock,
            TechnicalResult tech,
            CompositeScore score)
        {
            var rec = score.FinalScore >= 65 ? "BUY"
                    : score.FinalScore >= 45 ? "HOLD"
                    : "SELL";

            return new AiAnalysis
            {
                Symbol = stock.Symbol,
                Recommendation = rec,
                Confidence = (int)(score.FinalScore * 0.8 + 15),
                Entry = stock.LastPrice,
                Target = tech.SuggestedTarget,
                StopLoss = tech.SuggestedStopLoss,
                RiskReward = $"1:{((tech.SuggestedTarget - stock.LastPrice) / (stock.LastPrice - tech.SuggestedStopLoss)):F1}",
                TimeHorizon = "4-6 weeks",
                Summary = $"{stock.Symbol} has a composite score of " +
                          $"{score.FinalScore:F0}/100. Technical score " +
                          $"{score.TechnicalScore:F0}, Fundamental " +
                          $"{score.FundamentalScore:F0}, News sentiment " +
                          $"{score.NewsScore:F0}.",
                KeyDrivers = tech.Signals
                    .Where(s => s.IsBullish == true)
                    .Take(3)
                    .Select(s => s.Signal)
                    .ToList(),
                Risks = tech.Signals
                    .Where(s => s.IsBullish == false)
                    .Take(2)
                    .Select(s => s.Signal)
                    .ToList(),
                TechnicalView = tech.Score > 60 ? "Bullish"
                    : tech.Score > 40 ? "Neutral" : "Bearish",
                FundamentalView = "Average",
                NewsView = score.NewsScore > 60 ? "Positive"
                    : score.NewsScore > 40 ? "Neutral" : "Negative",
                GeneratedAt = DateTime.Now
            };
        }

        public void ClearCache(string symbol)
            => _cache.Remove(symbol);
    }
}
using AlgoSenseNSE.API.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// ClaudeAiService v3 — Claude is EXPLAINER only, not decision maker.
    ///
    /// AVOID signals now compute real ATR-based entry/target/SL
    /// so SignalTrackingService can measure accuracy on all signals,
    /// not just BUY. This enables /api/accuracy to answer:
    /// "Was AVOID the right call? Would the trade have worked?"
    /// </summary>
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
            _http   = httpClientFactory.CreateClient("Claude");
            _nse    = nse;
        }

        public async Task<AiAnalysis> AnalyzeStockAsync(
            StockInfo stock,
            TechnicalResult tech,
            FundamentalResult fund,
            List<NewsItem> news,
            CompositeScore score)
        {
            // ── Step 1: Rule engine decides BUY/AVOID ──
            var mktCtx = await _nse.GetMarketContextAsync();
            var ruleDecision = RuleEngine.Decide(
                stock, tech, fund, score, mktCtx);

            // ── Step 2: Check cache ─────────────────────
            if (_cache.TryGetValue(stock.Symbol, out var cached) &&
                (DateTime.Now - cached.GeneratedAt).TotalMinutes < 15 &&
                cached.Recommendation == ruleDecision.Recommendation)
                return cached;

            // ── Step 3: AVOID → fast local explanation ──
            // No Claude API call — but we DO compute real entry/target/SL
            if (ruleDecision.Recommendation == "AVOID")
            {
                var avoidAnalysis = BuildAvoidAnalysis(
                    stock, tech, fund, score, mktCtx, ruleDecision);
                _cache[stock.Symbol] = avoidAnalysis;

                _logger.LogInformation(
                    "⚡ Rule engine AVOID {sym} | {reason} | " +
                    "Entry:{entry:F2} Target:{target:F2} SL:{sl:F2}",
                    stock.Symbol,
                    ruleDecision.BlockReason,
                    avoidAnalysis.Entry,
                    avoidAnalysis.Target,
                    avoidAnalysis.StopLoss);

                return avoidAnalysis;
            }

            // ── Step 4: BUY → Claude writes explanation ─
            try
            {
                var capital   = _config.GetValue<double>("Trading:Capital", 1500);
                var ist       = GetIST();
                int shares    = stock.LastPrice > 0
                    ? (int)(capital * 0.60 / stock.LastPrice) : 0;
                double broker = 40.0;

                var sector     = _nse.GetSectorForSymbol(stock.Symbol);
                var sectorPerf = _nse.GetSectorPerformance(stock.Symbol, mktCtx);
                string sectorText = sectorPerf != null
                    ? $"{sector}: {(sectorPerf.ChangePercent >= 0 ? "+" : "")}{sectorPerf.ChangePercent:F2}% today"
                    : $"{sector}: data unavailable";

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

                var prompt = $@"You are explaining a BUY signal to a beginner NSE intraday trader.
The rule engine has ALREADY decided this is a BUY. Your job is ONLY to write a clear, 
confident explanation of WHY this is a good trade right now.

Stock: {stock.Symbol} at ₹{stock.LastPrice:F2}
Entry: ₹{ruleDecision.Entry:F2} | Target: ₹{ruleDecision.Target:F2} | SL: ₹{ruleDecision.StopLoss:F2}
Composite Score: {score.FinalScore:F0}/100

Market Context:
- Nifty: {mktCtx.NiftyChange:+0.00;-0.00}% | VIX: {mktCtx.IndiaVix:F1} | Quality: {mktCtx.MarketQualityScore}/100
- FII: ₹{mktCtx.FiiNetCrore:N0}Cr | Sector: {sectorText}

Technical Signals:
{string.Join("\n", bullSigs.Take(4))}
{string.Join("\n", bearSigs.Take(2))}

Fundamentals: PE={fund.PE:F1} ROE={fund.ROE:F1}% Score={fund.Score:F0}/100

News:
{newsText}

Capital math: {shares} shares, need ₹{shares * stock.LastPrice:F0}, 
min profit/share: ₹{broker / Math.Max(shares, 1):F2}

Respond ONLY in this JSON (no markdown, no preamble):
{{
  ""confidence"": <70-92>,
  ""summary"": ""One sentence: market context + key signal + why now"",
  ""keyDrivers"": [""driver 1"", ""driver 2"", ""driver 3""],
  ""risks"": [""risk 1"", ""risk 2""],
  ""timeHorizon"": ""Exit by HH:MM"",
  ""expectedProfit"": ""₹XX on {shares} shares"",
  ""marketView"": ""Bullish"" or ""Neutral"",
  ""sectorView"": ""Strong"" or ""Weak"" or ""Neutral""
}}";

                var payload = new
                {
                    model      = "claude-haiku-4-5-20251001",
                    max_tokens = 600,
                    messages   = new[]
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
                var json     = await response.Content.ReadAsStringAsync();
                var result   = JObject.Parse(json);
                var text     = result["content"]?[0]?["text"]
                    ?.Value<string>() ?? "";

                var clean = text
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();

                var narrative = JsonConvert
                    .DeserializeObject<ClaudeNarrative>(clean);

                var analysis = new AiAnalysis
                {
                    Symbol         = stock.Symbol,
                    Recommendation = ruleDecision.Recommendation,
                    Confidence     = narrative?.Confidence
                        ?? ruleDecision.Confidence,
                    Entry          = ruleDecision.Entry,
                    Target         = ruleDecision.Target,
                    StopLoss       = ruleDecision.StopLoss,
                    RiskReward     = ruleDecision.RiskReward,
                    ExpectedProfit = narrative?.ExpectedProfit
                        ?? $"₹? on {shares} shares",
                    TimeHorizon    = narrative?.TimeHorizon
                        ?? "Exit by 14:00",
                    Summary        = narrative?.Summary
                        ?? ruleDecision.BlockReason,
                    KeyDrivers     = narrative?.KeyDrivers
                        ?? tech.Signals
                            .Where(s => s.IsBullish == true)
                            .Take(3).Select(s => s.Signal).ToList(),
                    Risks          = narrative?.Risks
                        ?? tech.Signals
                            .Where(s => s.IsBullish == false)
                            .Take(2).Select(s => s.Signal).ToList(),
                    MarketView     = narrative?.MarketView  ?? "Bullish",
                    SectorView     = narrative?.SectorView  ?? "Neutral",
                    GeneratedAt    = DateTime.Now
                };

                _cache[stock.Symbol] = analysis;

                _logger.LogInformation(
                    "✅ AI {sym}: {rec} conf:{conf}% | {summary}",
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
                _logger.LogError(ex,
                    "❌ Claude narrative failed for {sym}, using rule decision",
                    stock.Symbol);

                var fallback = BuildAvoidAnalysis(
                    stock, tech, fund, score, mktCtx, ruleDecision);
                fallback.Recommendation = ruleDecision.Recommendation;
                return fallback;
            }
        }

        // ─────────────────────────────────────────────
        // BuildAvoidAnalysis — computes REAL entry/target/SL
        // even for AVOID signals so accuracy can be tracked.
        //
        // Before: Entry = LastPrice, Target = LastPrice, SL = LastPrice (useless)
        // After:  ATR-based realistic prices with slippage applied
        //
        // /api/accuracy uses these to fill outcomes 2hrs later and answer:
        // "This signal was AVOID — would it actually have hit target?"
        // ─────────────────────────────────────────────
        private AiAnalysis BuildAvoidAnalysis(
            StockInfo stock,
            TechnicalResult tech,
            FundamentalResult fund,
            CompositeScore score,
            MarketContext mkt,
            RuleDecision rule)
        {
            var capital = _config.GetValue<double>("Trading:Capital", 1500);
            int shares  = stock.LastPrice > 0
                ? (int)(capital * 0.60 / stock.LastPrice) : 0;

            double entry = 0, target = 0, sl = 0;
            string rrStr = "N/A";

            if (stock.LastPrice > 0)
            {
                // Same price-sensitive slippage as BUY path
                double rawEntry = stock.LastPrice;
                double slipE  = rawEntry < 100 ? 0.0020 : rawEntry < 300 ? 0.0010 : 0.0005;
                double slipT  = rawEntry < 100 ? 0.0020 : rawEntry < 300 ? 0.0010 : 0.0005;
                double slipSL = rawEntry < 100 ? 0.0010 : rawEntry < 300 ? 0.0005 : 0.0003;

                entry = Math.Round(rawEntry * (1 + slipE), 2);

                if (tech.ATR > 0)
                {
                    // ATR-based levels — same formula as RuleEngine BUY path
                    double rawTarget = rawEntry + (tech.ATR * 2.0);
                    double rawSL     = rawEntry - (tech.ATR * 0.75);
                    target = Math.Round(rawTarget * (1 - slipT),  2);
                    sl     = Math.Round(rawSL     * (1 - slipSL), 2);
                }
                else
                {
                    // Fallback fixed % when ATR not yet computed
                    target = Math.Round(rawEntry * 1.015 * (1 - slipT),  2);
                    sl     = Math.Round(rawEntry * 0.990 * (1 - slipSL), 2);
                }

                double profit = target - entry;
                double risk   = entry  - sl;
                if (risk > 0)
                    rrStr = $"1:{profit / risk:F1}";
            }

            return new AiAnalysis
            {
                Symbol         = stock.Symbol,
                Recommendation = "AVOID",
                Confidence     = 55,
                Entry          = entry,
                Target         = target,
                StopLoss       = sl,
                RiskReward     = rrStr,
                // Mark clearly as hypothetical — do not trade this
                ExpectedProfit = shares > 0 && target > entry
                    ? $"~₹{(target - entry) * shares:F0} hypothetical (AVOID)"
                    : "AVOID — not recommended",
                TimeHorizon    = "AVOID — wait for better setup",
                Summary        = $"{stock.Symbol}: {rule.BlockReason}",
                KeyDrivers     = new List<string> { rule.BlockReason },
                Risks          = tech.Signals
                    .Where(s => s.IsBullish == false)
                    .Take(2).Select(s => s.Signal).ToList(),
                TechnicalView  = tech.Score > 65 ? "Bullish"
                    : tech.Score > 45 ? "Neutral" : "Bearish",
                FundamentalView = fund.Score > 70 ? "Strong"
                    : fund.Score > 45 ? "Average" : "Weak",
                NewsView       = score.NewsScore > 60 ? "Positive"
                    : score.NewsScore > 40 ? "Neutral" : "Negative",
                GeneratedAt    = DateTime.Now
            };
        }

        public void ClearCache(string symbol) => _cache.Remove(symbol);
        public void ClearAllCache()           => _cache.Clear();

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

    // ── Claude narrative model (BUY only) ────────────
    public class ClaudeNarrative
    {
        public int          Confidence     { get; set; }
        public string       Summary        { get; set; } = "";
        public List<string> KeyDrivers     { get; set; } = new();
        public List<string> Risks          { get; set; } = new();
        public string       TimeHorizon    { get; set; } = "";
        public string       ExpectedProfit { get; set; } = "";
        public string       MarketView     { get; set; } = "";
        public string       SectorView     { get; set; } = "";
    }

    // ── Rule Engine — deterministic BUY/AVOID ────────
    public static class RuleEngine
    {
        public static RuleDecision Decide(
            StockInfo         stock,
            TechnicalResult   tech,
            FundamentalResult fund,
            CompositeScore    score,
            MarketContext     mkt)
        {
            if (mkt.IndiaVix > 22)
                return Avoid($"VIX={mkt.IndiaVix:F1} > 22 — high fear");

            if (mkt.MarketQualityScore < 40)
                return Avoid($"Market quality {mkt.MarketQualityScore}/100 — poor conditions");

            if (mkt.NiftyChange < -1.0)
                return Avoid($"Nifty down {mkt.NiftyChange:F1}% — broad market weak");

            if (!tech.SupertrendBullish)
                return Avoid("Supertrend = SELL — downtrend active");

            if (stock.LastPrice <= 0 || tech.VWAP <= 0 ||
                stock.LastPrice < tech.VWAP)
                return Avoid($"Price ₹{stock.LastPrice:F2} below VWAP ₹{tech.VWAP:F2}");

            if (tech.ADX < 20)
                return Avoid($"ADX={tech.ADX:F0} < 20 — no trend strength");

            if (tech.RSI < 45 || tech.RSI > 75)
                return Avoid($"RSI={tech.RSI:F1} outside buy zone 45-75");

            if (score.FinalScore < 65)
                return Avoid($"Composite score {score.FinalScore:F0} < 65");

            if (fund.Score < 45)
                return Avoid($"Fundamental score {fund.Score:F0} < 45");

            var ist = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            var minsToClose = (int)(new TimeSpan(15, 30, 0) -
                ist.TimeOfDay).TotalMinutes;
            if (minsToClose < 60)
                return Avoid($"Only {minsToClose} min to close — too late");

            // All rules passed → BUY
            double rawEntry = stock.LastPrice;
            var slipTuple = rawEntry switch
            {
                < 100 => (E: 0.0020, T: 0.0020, SL: 0.0010),
                < 300 => (E: 0.0010, T: 0.0010, SL: 0.0005),
                _     => (E: 0.0005, T: 0.0005, SL: 0.0003)
            };
            double slipE  = slipTuple.E;
            double slipT  = slipTuple.T;
            double slipSL = slipTuple.SL;
            double entry  = rawEntry * (1 + slipE);

            double sl, target;
            if (tech.ATR > 0)
            {
                sl     = (rawEntry - tech.ATR * 0.75) * (1 - slipSL);
                target = (rawEntry + tech.ATR * 2.0)  * (1 - slipT);
            }
            else
            {
                sl     = entry * 0.9970;
                target = entry * 1.0040;
            }

            double rr = sl > 0 && sl < entry
                ? (target - entry) / (entry - sl) : 0;

            return new RuleDecision
            {
                Recommendation = "BUY",
                Confidence     = ComputeConfidence(tech, fund, score, mkt),
                Entry          = Math.Round(entry,  2),
                Target         = Math.Round(target, 2),
                StopLoss       = Math.Round(sl,     2),
                RiskReward     = $"1:{rr:F1}",
                BlockReason    = BuildBuyReason(tech, mkt)
            };
        }

        private static RuleDecision Avoid(string reason) =>
            new RuleDecision
            {
                Recommendation = "AVOID",
                Confidence     = 55,
                BlockReason    = reason
            };

        private static int ComputeConfidence(
            TechnicalResult   tech,
            FundamentalResult fund,
            CompositeScore    score,
            MarketContext     mkt)
        {
            int conf = 65;
            if (tech.RSI    >= 55 && tech.RSI    <= 68) conf += 5;
            if (tech.ADX    >  30)                      conf += 5;
            if (tech.Score  >  75)                      conf += 5;
            if (fund.Score  >  80)                      conf += 5;
            if (mkt.IndiaVix < 16)                      conf += 5;
            if (mkt.FiiNetCrore > 500)                  conf += 5;
            if (mkt.MarketQualityScore > 65)            conf += 5;
            if (score.FinalScore > 75)                  conf += 3;
            return Math.Min(conf, 92);
        }

        private static string BuildBuyReason(
            TechnicalResult tech, MarketContext mkt)
        {
            var reasons = new List<string>();
            if (tech.SupertrendBullish)          reasons.Add("Supertrend BUY");
            if (tech.RSI >= 55 && tech.RSI <= 70)reasons.Add($"RSI {tech.RSI:F0} in buy zone");
            if (tech.ADX > 25)                   reasons.Add($"ADX {tech.ADX:F0} strong trend");
            if (mkt.NiftyChange > 0.5)           reasons.Add($"Nifty +{mkt.NiftyChange:F1}%");
            return reasons.Any()
                ? string.Join(", ", reasons)
                : "All technical filters passed";
        }
    }

    // ── Rule decision output model ────────────────────
    public class RuleDecision
    {
        public string Recommendation { get; set; } = "AVOID";
        public int    Confidence     { get; set; } = 55;
        public double Entry          { get; set; }
        public double Target         { get; set; }
        public double StopLoss       { get; set; }
        public string RiskReward     { get; set; } = "N/A";
        public string BlockReason    { get; set; } = "";
    }
}


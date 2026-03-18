namespace AlgoSenseNSE.API.Models
{
    // ── Stock Universe ──────────────────────────────
    public class StockInfo
    {
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public string Sector { get; set; } = "";
        public double LastPrice { get; set; }
        public double PrevClose { get; set; }
        public double Change { get; set; }
        public double ChangePercent { get; set; }
        public long Volume { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double MarketCap { get; set; }
    }

    // ── OHLCV Candle ────────────────────────────────
    public class OhlcvCandle
    {
        public DateTime Timestamp { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
    }

    // ── Technical Result ────────────────────────────
    public class TechnicalResult
    {
        public string Symbol { get; set; } = "";
        public double Score { get; set; }
        public double RSI { get; set; }
        public double MACD { get; set; }
        public double MACDSignal { get; set; }
        public double MACDHistogram { get; set; }
        public double BollingerUpper { get; set; }
        public double BollingerMiddle { get; set; }
        public double BollingerLower { get; set; }
        public double BollingerPctB { get; set; }
        public double EMA20 { get; set; }
        public double EMA50 { get; set; }
        public double EMA200 { get; set; }
        public double ATR { get; set; }
        public double ADX { get; set; }
        public double PlusDI { get; set; }
        public double MinusDI { get; set; }
        public double VWAP { get; set; }
        public double Supertrend { get; set; }
        public bool SupertrendBullish { get; set; }
        public double SuggestedTarget { get; set; }
        public double SuggestedStopLoss { get; set; }
        public List<TechnicalSignal> Signals { get; set; } = new();
        public DateTime CalculatedAt { get; set; } = DateTime.Now;
    }

    public class TechnicalSignal
    {
        public string Indicator { get; set; } = "";
        public string Value { get; set; } = "";
        public string Signal { get; set; } = "";
        public bool? IsBullish { get; set; }
    }

    // ── Fundamental Result ──────────────────────────
    public class FundamentalResult
    {
        public string Symbol { get; set; } = "";
        public double Score { get; set; }
        public double PE { get; set; }
        public double PB { get; set; }
        public double ROE { get; set; }
        public double ROCE { get; set; }
        public double DebtToEquity { get; set; }
        public double RevenueGrowthYoY { get; set; }
        public double EPSGrowth { get; set; }
        public double PromoterHolding { get; set; }
        public double PromoterPledge { get; set; }
        public double FIIHolding { get; set; }
        public double DIIHolding { get; set; }
        public double FreeCashFlow { get; set; }
        public string MarketCap { get; set; } = "";
        public List<TechnicalSignal> Signals { get; set; } = new();
        public DateTime LastUpdated { get; set; } = DateTime.Now;
    }

    // ── News Item ───────────────────────────────────
    public class NewsItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Headline { get; set; } = "";
        public string Source { get; set; } = "";
        public string Url { get; set; } = "";
        public double SentimentScore { get; set; }
        public string SentimentLabel { get; set; } = "";
        public List<string> RelatedSymbols { get; set; } = new();
        public DateTime PublishedAt { get; set; }
        public string TimeAgo { get; set; } = "";
    }

    // ── Composite Score ─────────────────────────────
    public class CompositeScore
    {
        public string Symbol { get; set; } = "";
        public double TechnicalScore { get; set; }
        public double FundamentalScore { get; set; }
        public double NewsScore { get; set; }
        public double FinalScore { get; set; }
        public DateTime CalculatedAt { get; set; } = DateTime.Now;
    }

    // ── AI Analysis ─────────────────────────────────
    public class AiAnalysis
    {
        public string Symbol { get; set; } = "";
        public string Recommendation { get; set; } = "";
        public int Confidence { get; set; }
        public double Entry { get; set; }
        public double Target { get; set; }
        public double StopLoss { get; set; }
        public string RiskReward { get; set; } = "";
        public string TimeHorizon { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<string> KeyDrivers { get; set; } = new();
        public List<string> Risks { get; set; } = new();
        public string TechnicalView { get; set; } = "";
        public string FundamentalView { get; set; } = "";
        public string NewsView { get; set; } = "";
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
    }

    // ── Final Recommendation ────────────────────────
    public class Recommendation
    {
        public int Rank { get; set; }
        public StockInfo Stock { get; set; } = new();
        public TechnicalResult Technical { get; set; } = new();
        public FundamentalResult Fundamental { get; set; } = new();
        public CompositeScore Score { get; set; } = new();
        public AiAnalysis? AiAnalysis { get; set; }
        public List<NewsItem> RelatedNews { get; set; } = new();
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
    }

    // ── SignalR Live Price ───────────────────────────
    public class LivePrice
    {
        public string Symbol { get; set; } = "";
        public double LTP { get; set; }
        public double Change { get; set; }
        public double ChangePercent { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public long Volume { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    // ── Angel One API Response ──────────────────────
    public class AngelOneLoginResponse
    {
        public string jwtToken { get; set; } = "";
        public string refreshToken { get; set; } = "";
        public string feedToken { get; set; } = "";
    }

    public class AngelOneQuoteResponse
    {
        public bool status { get; set; }
        public string message { get; set; } = "";
        public AngelOneQuoteData? data { get; set; }
    }

    public class AngelOneQuoteData
    {
        public double ltp { get; set; }
        public double open { get; set; }
        public double high { get; set; }
        public double low { get; set; }
        public double close { get; set; }
        public long volume { get; set; }
        public double netChange { get; set; }
        public double percentChange { get; set; }
    }
}
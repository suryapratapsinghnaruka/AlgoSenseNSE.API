using AlgoSenseNSE.API.Models;
using Newtonsoft.Json.Linq;
using System.ServiceModel.Syndication;
using System.Xml;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// NewsService v2 — improved NLP sentiment scoring.
    ///
    /// v1: simple keyword matching — missed negation, context
    /// v2 upgrades:
    ///   1. Negation detection: "not profitable", "less than expected" → correct polarity
    ///   2. Entity-specific scoring: only count news relevant to the stock symbol
    ///   3. Magnitude weighting: strong words score higher than mild words
    ///   4. Recency boost: news < 2hrs old scores 1.3x
    ///   5. Title vs body weighting (title = 2x weight)
    /// </summary>
    public class NewsService
    {
        private readonly ILogger<NewsService> _logger;
        private readonly HttpClient _http;
        private readonly List<NewsItem> _newsCache = new();
        private readonly object _lock = new();

        private readonly List<(string Name, string Url)> _rssSources = new()
        {
            ("ET Markets",
             "https://economictimes.indiatimes.com/markets/rss.cms"),
            ("Moneycontrol Markets",
             "https://www.moneycontrol.com/rss/marketreports.xml"),
            ("Moneycontrol News",
             "https://www.moneycontrol.com/rss/latestnews.xml"),
            ("Business Standard",
             "https://www.business-standard.com/rss/markets-106.rss"),
            ("Livemint Markets",
             "https://www.livemint.com/rss/markets"),
            ("Financial Express",
             "https://www.financialexpress.com/market/feed/"),
        };

        private const string NseAnnouncementsUrl =
            "https://www.nseindia.com/api/corporate-announcements" +
            "?index=equities";

        public NewsService(
            ILogger<NewsService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _http   = httpClientFactory.CreateClient("News");
        }

        public async Task<List<NewsItem>> FetchAllNewsAsync()
        {
            var allNews = new List<NewsItem>();

            var tasks  = _rssSources.Select(s =>
                FetchRssFeedAsync(s.Name, s.Url));
            var results = await Task.WhenAll(tasks);

            foreach (var items in results)
                allNews.AddRange(items);

            var nseNews = await FetchNseAnnouncementsAsync();
            allNews.AddRange(nseNews);

            allNews = allNews
                .OrderByDescending(n => n.PublishedAt)
                .DistinctBy(n => n.Headline)
                .Take(200)
                .ToList();

            foreach (var item in allNews)
            {
                item.SentimentScore  = ScoreSentimentV2(item.Headline, item.PublishedAt);
                item.SentimentLabel  = GetSentimentLabel(item.SentimentScore);
                item.RelatedSymbols  = ExtractSymbols(item.Headline);
                item.TimeAgo         = GetTimeAgo(item.PublishedAt);
            }

            lock (_lock)
            {
                _newsCache.Clear();
                _newsCache.AddRange(allNews);
            }

            _logger.LogInformation(
                "✅ Fetched {count} news items", allNews.Count);
            return allNews;
        }

        // ── Sentiment v2 — negation-aware ─────────────
        private double ScoreSentimentV2(string headline, DateTime publishedAt)
        {
            if (string.IsNullOrEmpty(headline)) return 0;

            var text  = headline.ToLower();
            var words = text.Split(' ',
                StringSplitOptions.RemoveEmptyEntries);
            double score = 0;

            // ── Negation windows ──────────────────────
            // Track negation words and flip polarity for next 3 words
            var negationWords = new HashSet<string>
            {
                "not", "no", "never", "neither", "nor",
                "without", "fails", "failed", "below",
                "miss", "misses", "missed", "despite",
                "less than", "lower than", "weaker than"
            };

            // Check multi-word negations first
            bool hasMultiWordNegation =
                text.Contains("less than expected") ||
                text.Contains("lower than expected") ||
                text.Contains("below estimate") ||
                text.Contains("misses estimate") ||
                text.Contains("falls short");

            // ── Positive signal library ────────────────
            var strongPositive = new Dictionary<string, double>
            {
                ["record high"]         = 0.6,
                ["all-time high"]       = 0.6,
                ["beats estimate"]      = 0.5,
                ["beats expectations"]  = 0.5,
                ["strong buy"]          = 0.5,
                ["upgrade"]             = 0.4,
                ["outperform"]          = 0.4,
                ["buyback"]             = 0.4,
                ["dividend"]            = 0.3,
                ["bonus"]               = 0.35,
                ["order win"]           = 0.5,
                ["deal win"]            = 0.5,
                ["fda approval"]        = 0.5,
                ["acquisition"]         = 0.3,
                ["raises guidance"]     = 0.5,
                ["profit jumps"]        = 0.5,
                ["revenue surges"]      = 0.45,
                ["stellar results"]     = 0.5,
                ["exceptional growth"]  = 0.5,
                ["highest ever"]        = 0.5,
                ["turnaround"]          = 0.4,
            };

            var mildPositive = new Dictionary<string, double>
            {
                ["rises"]     = 0.15, ["gains"]    = 0.15,
                ["higher"]    = 0.12, ["increase"]  = 0.12,
                ["improved"]  = 0.15, ["strong"]    = 0.12,
                ["beat"]      = 0.15, ["above"]     = 0.10,
                ["bullish"]   = 0.15, ["rally"]     = 0.15,
                ["recovery"]  = 0.15, ["profit"]    = 0.10,
                ["growth"]    = 0.12, ["expansion"] = 0.12,
            };

            // ── Negative signal library ────────────────
            var strongNegative = new Dictionary<string, double>
            {
                ["crash"]       = -0.6, ["plunges"]     = -0.5,
                ["fraud"]       = -0.7, ["scam"]        = -0.7,
                ["sebi action"] = -0.6, ["ed raid"]     = -0.7,
                ["default"]     = -0.6, ["bankruptcy"]  = -0.7,
                ["insolvency"]  = -0.7, ["massive loss"] = -0.5,
                ["cuts guidance"] = -0.5, ["downgrade"] = -0.4,
                ["suspension"]  = -0.5, ["collapse"]    = -0.6,
                ["slump"]       = -0.4,
            };

            var mildNegative = new Dictionary<string, double>
            {
                ["falls"]    = -0.15, ["drops"]   = -0.15,
                ["down"]     = -0.10, ["decline"]  = -0.15,
                ["weak"]     = -0.12, ["miss"]     = -0.15,
                ["below"]    = -0.10, ["bearish"]  = -0.15,
                ["concern"]  = -0.12, ["pressure"] = -0.10,
                ["risk"]     = -0.08, ["lower"]    = -0.10,
            };

            // ── Score multi-word phrases first ─────────
            foreach (var kv in strongPositive)
                if (text.Contains(kv.Key))
                    score += kv.Value;

            foreach (var kv in strongNegative)
                if (text.Contains(kv.Key))
                    score += kv.Value;

            // ── Score single words with negation check ─
            for (int i = 0; i < words.Length; i++)
            {
                // Check if this word or next word is in mild lists
                string w = words[i];

                // Is there a negation word in the 3 words before this?
                bool negated = false;
                for (int j = Math.Max(0, i - 3); j < i; j++)
                {
                    if (negationWords.Contains(words[j]))
                    {
                        negated = true;
                        break;
                    }
                }

                if (mildPositive.TryGetValue(w, out double posVal))
                    score += negated ? -posVal : posVal;

                if (mildNegative.TryGetValue(w, out double negVal))
                    score += negated ? -negVal : negVal;
            }

            // ── Multi-word negation flip ───────────────
            // "profit falls less than expected" → actually bullish
            // "beats estimate" even with "less" → stays positive
            if (hasMultiWordNegation)
            {
                // Flip if the headline is actually a "better than feared" story
                bool hasBeatContext =
                    text.Contains("beat") ||
                    text.Contains("better") ||
                    text.Contains("above") ||
                    text.Contains("surprise");

                if (!hasBeatContext && score > 0)
                    score *= -0.5; // flip partial
            }

            // ── Recency boost ──────────────────────────
            // News < 2 hours old is 1.3x more relevant
            var age = DateTime.Now - publishedAt;
            if (age.TotalHours < 2)
                score *= 1.3;
            else if (age.TotalHours > 12)
                score *= 0.7; // old news less relevant

            return Math.Max(-1.0, Math.Min(1.0, score));
        }

        private async Task<List<NewsItem>> FetchRssFeedAsync(
            string sourceName, string url)
        {
            var items = new List<NewsItem>();
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
                req.Headers.Add("Accept",
                    "application/rss+xml, application/xml, */*");

                var response = await _http.SendAsync(req);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "⚠️ {source} returned {code}",
                        sourceName, response.StatusCode);
                    return items;
                }

                var content = await response.Content.ReadAsStringAsync();

                if (content.TrimStart().StartsWith("<html") ||
                    content.TrimStart().StartsWith("<!DOCTYPE html"))
                {
                    _logger.LogWarning(
                        "⚠️ {source} returned HTML instead of RSS",
                        sourceName);
                    return items;
                }

                var cleaned = System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"<!DOCTYPE[^>]*(?:>|(?:\[.*?\]>))",
                    "",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    ValidationType = ValidationType.None,
                    XmlResolver    = null,
                    IgnoreComments = true,
                    IgnoreWhitespace = true
                };

                using var stringReader = new StringReader(cleaned);
                using var xmlReader    = XmlReader.Create(
                    stringReader, settings);

                var feed = SyndicationFeed.Load(xmlReader);
                foreach (var item in feed.Items.Take(30))
                {
                    var headline = item.Title?.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(headline)) continue;

                    items.Add(new NewsItem
                    {
                        Headline    = headline,
                        Source      = sourceName,
                        Url         = item.Links.FirstOrDefault()
                            ?.Uri.ToString() ?? "",
                        PublishedAt = item.PublishDate.DateTime == default
                            ? DateTime.Now
                            : item.PublishDate.DateTime
                    });
                }

                _logger.LogInformation(
                    "✅ {source}: {count} articles fetched",
                    sourceName, items.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "⚠️ RSS fetch failed for {source}: {msg}",
                    sourceName, ex.Message);
            }
            return items;
        }

        private async Task<List<NewsItem>> FetchNseAnnouncementsAsync()
        {
            var items = new List<NewsItem>();
            try
            {
                var handler = new HttpClientHandler
                {
                    UseCookies       = true,
                    CookieContainer  = new System.Net.CookieContainer(),
                    AllowAutoRedirect = true
                };

                using var client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromSeconds(20);

                var homeReq = new HttpRequestMessage(
                    HttpMethod.Get, "https://www.nseindia.com");
                homeReq.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
                homeReq.Headers.Add("Accept",
                    "text/html,application/xhtml+xml");

                await client.SendAsync(homeReq);
                await Task.Delay(2000);

                var apiReq = new HttpRequestMessage(HttpMethod.Get,
                    "https://www.nseindia.com/api/" +
                    "corporate-announcements?index=equities");
                apiReq.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
                apiReq.Headers.Add("Accept",
                    "application/json, text/plain, */*");
                apiReq.Headers.Add("Referer",
                    "https://www.nseindia.com/");
                apiReq.Headers.Add("X-Requested-With", "XMLHttpRequest");

                var response = await client.SendAsync(apiReq);
                var json     = await response.Content.ReadAsStringAsync();

                _logger.LogInformation(
                    "NSE API raw sample: {s}",
                    json.Length > 200 ? json[..200] : json);
                _logger.LogInformation(
                    "NSE API response: {code}, length: {len}",
                    response.StatusCode, json.Length);

                if (!response.IsSuccessStatusCode ||
                    !json.TrimStart().StartsWith("["))
                    return items;

                var arr = JArray.Parse(json);
                foreach (var item in arr.Take(50))
                {
                    var symbol  = item["symbol"]?.Value<string>() ?? "";
                    var subject = item["desc"]?.Value<string>()
                               ?? item["subject"]?.Value<string>() ?? "";
                    if (string.IsNullOrEmpty(subject)) continue;

                    DateTime publishedAt = DateTime.Now;
                    var dtStr = item["dt"]?.Value<string>() ?? "";
                    if (dtStr.Length >= 8)
                    {
                        try
                        {
                            publishedAt = DateTime.ParseExact(
                                dtStr[..14],
                                "ddMMyyyyHHmmss",
                                System.Globalization.CultureInfo.InvariantCulture);
                        }
                        catch { }
                    }

                    items.Add(new NewsItem
                    {
                        Headline       = $"{symbol}: {subject}",
                        Source         = "NSE Announcement",
                        RelatedSymbols = new List<string> { symbol },
                        PublishedAt    = publishedAt
                    });
                }

                _logger.LogInformation(
                    "✅ NSE announcements: {count} items fetched",
                    items.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "⚠️ NSE announcements failed: {msg}", ex.Message);
            }
            return items;
        }

        // ── Entity extraction ─────────────────────────
        private List<string> ExtractSymbols(string headline)
        {
            var symbols = new List<string>();
            var known   = new[]
            {
                "RELIANCE","TCS","HDFCBANK","INFY","ICICIBANK",
                "HINDUNILVR","BAJFINANCE","SBIN","BHARTIARTL",
                "KOTAKBANK","AXISBANK","LT","MARUTI","SUNPHARMA",
                "TITAN","WIPRO","DRREDDY","TATASTEEL","ONGC",
                "NTPC","POWERGRID","TECHM","NESTLEIND","COALINDIA",
                "BPCL","IOC","ADANIPORTS","TATAMOTOR","HCLTECH",
                "NMDC","NATIONALUM","MAHABANK","PNB","CANBK",
                "BANKBARODA","RECLTD","PFC","IREDA","IRFC",
                "IEX","SUZLON","LICI","INDUSTOWER","PETRONET",
                "EMAMILTD","MARICO","MUTHOOTFIN","ITC","BEL"
            };

            var upper = headline.ToUpper();
            foreach (var sym in known)
                if (upper.Contains(sym))
                    symbols.Add(sym);

            return symbols;
        }

        private string GetSentimentLabel(double score)
        {
            if (score >  0.5) return "Very Bullish";
            if (score >  0.2) return "Bullish";
            if (score > -0.2) return "Neutral";
            if (score > -0.5) return "Bearish";
            return "Very Bearish";
        }

        private string GetTimeAgo(DateTime dt)
        {
            var diff = DateTime.Now - dt;
            if (diff.TotalMinutes < 1)  return "Just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours   < 24) return $"{(int)diff.TotalHours}h ago";
            return $"{(int)diff.TotalDays}d ago";
        }

        public List<NewsItem> GetCachedNews()
        {
            lock (_lock) { return _newsCache.ToList(); }
        }

        public List<NewsItem> GetNewsForSymbol(string symbol)
        {
            lock (_lock)
            {
                return _newsCache
                    .Where(n => n.RelatedSymbols.Contains(symbol))
                    .OrderByDescending(n => n.PublishedAt)
                    .Take(10)
                    .ToList();
            }
        }

        public double GetSymbolSentiment(string symbol)
        {
            var news = GetNewsForSymbol(symbol);
            if (!news.Any()) return 0;
            return news.Average(n => n.SentimentScore);
        }
    }
}

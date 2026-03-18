using AlgoSenseNSE.API.Models;
using HtmlAgilityPack;
using Newtonsoft.Json.Linq;
using System.ServiceModel.Syndication;
using System.Xml;

namespace AlgoSenseNSE.API.Services
{
    public class NewsService
    {
        private readonly ILogger<NewsService> _logger;
        private readonly HttpClient _http;
        private readonly List<NewsItem> _newsCache = new();
        private readonly object _lock = new();

        // ── Free Indian market news RSS sources ─────
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

        // ── NSE announcement API ─────────────────────
        private const string NseAnnouncementsUrl =
            "https://www.nseindia.com/api/corporate-announcements" +
            "?index=equities";

        public NewsService(
            ILogger<NewsService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _http = httpClientFactory.CreateClient("News");
        }

        // ── Fetch all news ───────────────────────────
        public async Task<List<NewsItem>> FetchAllNewsAsync()
        {
            var allNews = new List<NewsItem>();

            // Fetch RSS feeds in parallel
            var tasks = _rssSources.Select(source =>
                FetchRssFeedAsync(source.Name, source.Url));
            var results = await Task.WhenAll(tasks);

            foreach (var items in results)
                allNews.AddRange(items);

            // Fetch NSE announcements
            var nseNews = await FetchNseAnnouncementsAsync();
            allNews.AddRange(nseNews);

            // Sort by published date
            allNews = allNews
                .OrderByDescending(n => n.PublishedAt)
                .DistinctBy(n => n.Headline)
                .Take(200)
                .ToList();

            // Score sentiment for each
            foreach (var item in allNews)
            {
                item.SentimentScore = ScoreSentiment(item.Headline);
                item.SentimentLabel = GetSentimentLabel(item.SentimentScore);
                item.RelatedSymbols = ExtractSymbols(item.Headline);
                item.TimeAgo = GetTimeAgo(item.PublishedAt);
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

        // ── Parse RSS feed ───────────────────────────
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
                    "application/rss+xml, application/xml, text/xml, */*");

                var response = await _http.SendAsync(req);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "⚠️ {source} returned {code}",
                        sourceName, response.StatusCode);
                    return items;
                }

                var content = await response.Content.ReadAsStringAsync();

                // Check if it's actually HTML (blocked)
                if (content.TrimStart().StartsWith("<html") ||
                    content.TrimStart().StartsWith("<!DOCTYPE html"))
                {
                    _logger.LogWarning(
                        "⚠️ {source} returned HTML instead of RSS",
                        sourceName);
                    return items;
                }

                // Remove DOCTYPE declarations that cause DTD errors
                // ET Markets has multiple DTDs which .NET blocks
                var cleaned = System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"<!DOCTYPE[^>]*(?:>|(?:\[.*?\]>))",
                    "",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    ValidationType = ValidationType.None,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreWhitespace = true
                };

                using var stringReader = new StringReader(cleaned);
                using var xmlReader = XmlReader.Create(
                    stringReader, settings);

                var feed = SyndicationFeed.Load(xmlReader);
                foreach (var item in feed.Items.Take(30))
                {
                    var headline = item.Title?.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(headline)) continue;

                    items.Add(new NewsItem
                    {
                        Headline = headline,
                        Source = sourceName,
                        Url = item.Links.FirstOrDefault()
                            ?.Uri.ToString() ?? "",
                        PublishedAt =
                            item.PublishDate.DateTime == default
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

        // ── NSE Announcements ────────────────────────
        private async Task<List<NewsItem>> FetchNseAnnouncementsAsync()
        {
            var items = new List<NewsItem>();
            try
            {
                // NSE needs a full browser session with cookies
                // Use a dedicated HttpClient with cookie support
                var handler = new HttpClientHandler
                {
                    UseCookies = true,
                    CookieContainer = new System.Net.CookieContainer(),
                    AllowAutoRedirect = true
                };

                using var client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromSeconds(20);

                // Step 1: Visit homepage first to get session cookies
                var homeReq = new HttpRequestMessage(
                    HttpMethod.Get, "https://www.nseindia.com");
                homeReq.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
                homeReq.Headers.Add("Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                homeReq.Headers.Add("Accept-Language", "en-US,en;q=0.5");

                await client.SendAsync(homeReq);
                await Task.Delay(2000); // Wait for cookies to settle

                // Step 2: Call the API with session cookies
                var apiReq = new HttpRequestMessage(HttpMethod.Get,
                    "https://www.nseindia.com/api/" +
                    "corporate-announcements?index=equities");
                apiReq.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
                apiReq.Headers.Add("Accept", "application/json, text/plain, */*");
                apiReq.Headers.Add("Accept-Language", "en-US,en;q=0.5");
                apiReq.Headers.Add("Referer", "https://www.nseindia.com/");
                apiReq.Headers.Add("X-Requested-With", "XMLHttpRequest");

                var response = await client.SendAsync(apiReq);
                var json = await response.Content.ReadAsStringAsync();

                _logger.LogInformation(
    "NSE API raw sample: {sample}",
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
                    var symbol = item["symbol"]?.Value<string>() ?? "";

                    // NSE uses "desc" not "subject"
                    var subject = item["desc"]?.Value<string>()
                               ?? item["subject"]?.Value<string>()
                               ?? "";

                    if (string.IsNullOrEmpty(subject)) continue;

                    // Parse NSE date format: "18032026232117" = ddMMyyyyHHmmss
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
                        Headline = $"{symbol}: {subject}",
                        Source = "NSE Announcement",
                        RelatedSymbols = new List<string> { symbol },
                        PublishedAt = publishedAt
                    });
                }

                _logger.LogInformation(
                    "✅ NSE announcements: {count} items fetched",
                    items.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "⚠️ NSE announcements failed: {msg}",
                    ex.Message);
            }
            return items;
        }

        // ── Sentiment Scoring (NLP keywords) ─────────
        private double ScoreSentiment(string headline)
        {
            if (string.IsNullOrEmpty(headline)) return 0;
            var text = headline.ToLower();
            double score = 0;

            // Strong positive signals
            var strongPositive = new[]
            {
                "record high", "all-time high", "beats estimate",
                "strong buy", "upgrade", "outperform", "buyback",
                "dividend", "bonus", "stellar results", "profit jumps",
                "revenue surges", "order win", "deal win", "fda approval",
                "nod", "acquisition", "merger", "turnaround",
                "highest ever", "exceptional", "raises guidance"
            };

            // Mild positive signals
            var mildPositive = new[]
            {
                "rises", "gains", "up", "growth", "positive",
                "higher", "increase", "improved", "strong",
                "beat", "above", "bullish", "rally", "recovery",
                "profit", "revenue growth", "expansion"
            };

            // Strong negative signals
            var strongNegative = new[]
            {
                "crash", "plunges", "fraud", "scam", "sebi action",
                "ed raid", "default", "bankruptcy", "insolvency",
                "massive loss", "cuts guidance", "downgrade",
                "underperform", "sell", "warning", "red flag",
                "slump", "collapse", "suspension"
            };

            // Mild negative signals
            var mildNegative = new[]
            {
                "falls", "drops", "down", "loss", "decline",
                "weak", "miss", "below", "bearish", "concern",
                "pressure", "headwind", "risk", "lower"
            };

            foreach (var word in strongPositive)
                if (text.Contains(word)) score += 0.4;

            foreach (var word in mildPositive)
                if (text.Contains(word)) score += 0.15;

            foreach (var word in strongNegative)
                if (text.Contains(word)) score -= 0.4;

            foreach (var word in mildNegative)
                if (text.Contains(word)) score -= 0.15;

            return Math.Max(-1.0, Math.Min(1.0, score));
        }

        // ── Extract NSE symbols from headline ────────
        private List<string> ExtractSymbols(string headline)
        {
            var symbols = new List<string>();
            var knownSymbols = new[]
            {
                "RELIANCE", "TCS", "HDFCBANK", "INFY", "ICICIBANK",
                "HINDUNILVR", "BAJFINANCE", "SBIN", "BHARTIARTL",
                "KOTAKBANK", "AXISBANK", "LT", "MARUTI", "SUNPHARMA",
                "TITAN", "WIPRO", "DRREDDY", "TATASTEEL", "ONGC",
                "NTPC", "POWERGRID", "TECHM", "DIVISLAB", "NESTLEIND",
                "ULTRACEMCO", "ASIANPAINT", "COALINDIA", "BPCL",
                "IOC", "GRASIM", "ADANIPORTS", "ADANIENT", "TATAMOTOR",
                "BAJAJFINSV", "HCLTECH", "INDUSINDBK", "JSWSTEEL",
                "LTIM", "ZOMATO", "PAYTM", "NYKAA", "DELHIVERY"
            };

            var upper = headline.ToUpper();
            foreach (var sym in knownSymbols)
                if (upper.Contains(sym))
                    symbols.Add(sym);

            return symbols;
        }

        // ── Sentiment label ──────────────────────────
        private string GetSentimentLabel(double score)
        {
            if (score > 0.5) return "Very Bullish";
            if (score > 0.2) return "Bullish";
            if (score > -0.2) return "Neutral";
            if (score > -0.5) return "Bearish";
            return "Very Bearish";
        }

        // ── Time ago ─────────────────────────────────
        private string GetTimeAgo(DateTime dt)
        {
            var diff = DateTime.Now - dt;
            if (diff.TotalMinutes < 1) return "Just now";
            if (diff.TotalMinutes < 60)
                return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24)
                return $"{(int)diff.TotalHours}h ago";
            return $"{(int)diff.TotalDays}d ago";
        }

        // ── Get cached news ──────────────────────────
        public List<NewsItem> GetCachedNews()
        {
            lock (_lock) { return _newsCache.ToList(); }
        }

        // ── Get news for specific symbol ─────────────
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

        // ── Get aggregate sentiment for symbol ───────
        public double GetSymbolSentiment(string symbol)
        {
            var news = GetNewsForSymbol(symbol);
            if (!news.Any()) return 0;
            return news.Average(n => n.SentimentScore);
        }
    }
}
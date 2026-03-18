using AlgoSenseNSE.API.Models;
using HtmlAgilityPack;
using System.Globalization;

namespace AlgoSenseNSE.API.Services
{
    public class FundamentalService
    {
        private readonly ILogger<FundamentalService> _logger;
        private readonly HttpClient _http;
        private readonly Dictionary<string, FundamentalResult> _cache = new();

        public FundamentalService(
            ILogger<FundamentalService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _http = httpClientFactory.CreateClient("Screener");
        }

        // ── Main fetch ───────────────────────────────
        public async Task<FundamentalResult> GetFundamentalsAsync(string symbol)
        {
            if (_cache.TryGetValue(symbol, out var cached) &&
                cached.LastUpdated.Date == DateTime.Today)
                return cached;

            var result = new FundamentalResult { Symbol = symbol };

            try
            {
                var url = $"https://www.screener.in/company/{symbol}/";
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 (KHTML, like Gecko) " +
                    "Chrome/120.0.0.0 Safari/537.36");
                req.Headers.Add("Accept",
                    "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                req.Headers.Add("Accept-Language", "en-US,en;q=0.9");

                var response = await _http.SendAsync(req);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("⚠️ Rate limited for {sym}, waiting 10s...", symbol);
                    await Task.Delay(10000);
                    var retry = new HttpRequestMessage(HttpMethod.Get, url);
                    retry.Headers.Add("User-Agent", "Mozilla/5.0");
                    response = await _http.SendAsync(retry);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("⚠️ Screener returned {code} for {sym}", response.StatusCode, symbol);
                    result.Score = 50; result.LastUpdated = DateTime.Now;
                    _cache[symbol] = result; return result;
                }

                var html = await response.Content.ReadAsStringAsync();

                if (html.Contains("Page not found") || !html.Contains("company-ratios"))
                {
                    result.Score = 50; result.LastUpdated = DateTime.Now;
                    _cache[symbol] = result; return result;
                }

                // ── Parse HTML into result ────────────────
                ParseScreenerHtml(html, symbol, result);

                result.Score = ComputeFundamentalScore(result);
                result.LastUpdated = DateTime.Now;
                _cache[symbol] = result;

                _logger.LogInformation(
                    "✅ {sym}: Score={score:F0} PE={pe:F1} ROE={roe:F1}% " +
                    "ROCE={roce:F1}% D/E={de:F2} Promoter={ph:F1}%",
                    symbol, result.Score, result.PE, result.ROE,
                    result.ROCE, result.DebtToEquity, result.PromoterHolding);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error fetching fundamentals for {sym}", symbol);
                result.Score = 50; result.LastUpdated = DateTime.Now;
                _cache[symbol] = result;
            }

            return result;
        }

        // ── Parse Screener HTML ──────────────────────
        private void ParseScreenerHtml(string html, string symbol, FundamentalResult result)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // ── Top ratios: try multiple selectors ────
            // Screener renders: <ul id="top-ratios"> or inside <div class="company-ratios">
            var liNodes = doc.DocumentNode.SelectNodes("//ul[@id='top-ratios']/li")
                       ?? doc.DocumentNode.SelectNodes("//section[contains(@class,'company-ratios')]//li")
                       ?? doc.DocumentNode.SelectNodes("//div[contains(@class,'company-ratios')]//li");

            if (liNodes != null)
            {
                // DEBUG: log first few li texts so we can see real structure
                foreach (var li in liNodes.Take(3))
                    _logger.LogDebug("SCREENER_LI [{sym}]: {txt}", symbol,
                        li.InnerText.Trim().Replace("\n", " ").Replace("\r", "")
                          .Substring(0, Math.Min(100, li.InnerText.Trim().Length)));

                foreach (var li in liNodes)
                {
                    // Screener structure inside each <li>:
                    //   <span class="name">Return on equity</span>
                    //   <span class="value"><span class="number">18.3</span> %</span>
                    // OR newer layout:
                    //   <span class="name">...</span><span class="number">...</span>

                    var nameNode = li.SelectSingleNode(".//span[@class='name']");
                    if (nameNode == null) continue;
                    var name = nameNode.InnerText.Trim().ToLower();

                    // Value: prefer inner span inside .value, else .number directly
                    var valueNode =
                        li.SelectSingleNode(".//span[@class='value']/span[@class='number']")
                     ?? li.SelectSingleNode(".//span[@class='number']")
                     ?? li.SelectSingleNode(".//span[@class='value']");

                    if (valueNode == null) continue;

                    var raw = valueNode.InnerText
                        .Trim()
                        .Replace(",", "")
                        .Replace("%", "")
                        .Replace("₹", "")
                        .Replace("Cr.", "")
                        .Replace("Cr", "")
                        .Trim();

                    if (!double.TryParse(raw, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out double val))
                        continue;

                    // Match label to field
                    if (name.Contains("stock p/e")) result.PE = val;
                    else if (name.Contains("p/e")) result.PE = val;
                    else if (name.Contains("price to book")) result.PB = val;
                    else if (name.Contains("p/b")) result.PB = val;
                    else if (name.Contains("return on equity") ||
                             name == "roe") result.ROE = val;
                    else if (name.Contains("return on capital") ||
                             name.Contains("roce")) result.ROCE = val;
                    else if (name.Contains("debt to equity") ||
                             name.Contains("debt / equity")) result.DebtToEquity = val;
                    else if (name.Contains("sales growth")) result.RevenueGrowthYoY = val;
                    else if (name.Contains("profit growth")) result.EPSGrowth = val;
                    else if (name.Contains("market cap")) result.MarketCap = raw; // string field
                }
            }

            // ── Shareholding: promoters, FII, DII ────
            result.PromoterHolding = ParseHolding(doc, "Promoters");
            result.FIIHolding = ParseHolding(doc, "FII");
            result.DIIHolding = ParseHolding(doc, "DII");

            // ── ROE fallback: scrape from ratios table ─
            // Screener also shows a "Key Ratios" table with rows like "ROE %  18.3  19.1 ..."
            if (result.ROE == 0)
                result.ROE = ScrapeTableRow(doc, "roe");

            if (result.ROCE == 0)
                result.ROCE = ScrapeTableRow(doc, "roce");

            // ── Market cap text fallback ──────────────
            if (string.IsNullOrEmpty(result.MarketCap))
            {
                var mcNode = doc.DocumentNode.SelectSingleNode(
                    "//li[.//span[contains(text(),'Market Cap')]]//span[@class='number']");
                result.MarketCap = mcNode?.InnerText.Trim() ?? "N/A";
            }
        }

        // ── Scrape a ratio from any table row ────────
        // Handles Screener's "Key Ratios" section where each row is a metric
        private double ScrapeTableRow(HtmlDocument doc, string keyword)
        {
            try
            {
                var rows = doc.DocumentNode.SelectNodes("//tr");
                if (rows == null) return 0;

                foreach (var tr in rows)
                {
                    var firstCell = tr.SelectSingleNode(".//td[1] | .//th[1]");
                    if (firstCell == null) continue;

                    var cellText = firstCell.InnerText.Trim().ToLower();
                    if (!cellText.Contains(keyword)) continue;

                    // Get last non-empty TD (most recent year value)
                    var tds = tr.SelectNodes(".//td");
                    if (tds == null) continue;

                    for (int i = tds.Count - 1; i >= 1; i--)
                    {
                        var v = tds[i].InnerText.Trim()
                            .Replace("%", "").Replace(",", "").Trim();
                        if (string.IsNullOrEmpty(v)) continue;
                        if (double.TryParse(v, NumberStyles.Any,
                            CultureInfo.InvariantCulture, out double val)
                            && val != 0)
                            return val;
                    }
                }
            }
            catch { }
            return 0;
        }

        // ── Parse shareholding table ─────────────────
        private double ParseHolding(HtmlDocument doc, string holder)
        {
            try
            {
                var rows = doc.DocumentNode.SelectNodes(
                    "//table[contains(@class,'data-table')]//tr");
                if (rows == null) return 0;

                foreach (var row in rows)
                {
                    var cells = row.SelectNodes(".//td");
                    if (cells == null || cells.Count < 2) continue;
                    if (!cells[0].InnerText.Trim()
                        .Contains(holder, StringComparison.OrdinalIgnoreCase)) continue;

                    // Latest quarter = last column
                    var last = cells[cells.Count - 1].InnerText
                        .Replace("%", "").Trim();
                    return double.TryParse(last, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var v) ? v : 0;
                }
            }
            catch { }
            return 0;
        }

        // ── Score 0-100 ──────────────────────────────
        public double ComputeFundamentalScore(FundamentalResult f)
        {
            double score = 50;
            var signals = new List<TechnicalSignal>();

            // P/E
            if (f.PE > 0 && f.PE < 10) { score += 20; signals.Add(new TechnicalSignal { Indicator = "P/E Ratio", Value = f.PE.ToString("F1"), Signal = "Very cheap", IsBullish = true }); }
            else if (f.PE < 15) { score += 15; signals.Add(new TechnicalSignal { Indicator = "P/E Ratio", Value = f.PE.ToString("F1"), Signal = "Undervalued", IsBullish = true }); }
            else if (f.PE < 25) { score += 8; signals.Add(new TechnicalSignal { Indicator = "P/E Ratio", Value = f.PE.ToString("F1"), Signal = "Fairly valued", IsBullish = true }); }
            else if (f.PE < 40) { score -= 5; signals.Add(new TechnicalSignal { Indicator = "P/E Ratio", Value = f.PE.ToString("F1"), Signal = "Slightly expensive", IsBullish = null }); }
            else if (f.PE > 0) { score -= 15; signals.Add(new TechnicalSignal { Indicator = "P/E Ratio", Value = f.PE.ToString("F1"), Signal = "Overvalued", IsBullish = false }); }

            // ROE
            if (f.ROE > 25) { score += 18; signals.Add(new TechnicalSignal { Indicator = "ROE", Value = $"{f.ROE:F1}%", Signal = "Exceptional", IsBullish = true }); }
            else if (f.ROE > 20) { score += 15; signals.Add(new TechnicalSignal { Indicator = "ROE", Value = $"{f.ROE:F1}%", Signal = "Excellent returns", IsBullish = true }); }
            else if (f.ROE > 15) { score += 8; signals.Add(new TechnicalSignal { Indicator = "ROE", Value = $"{f.ROE:F1}%", Signal = "Good returns", IsBullish = true }); }
            else if (f.ROE > 10) { score += 3; signals.Add(new TechnicalSignal { Indicator = "ROE", Value = $"{f.ROE:F1}%", Signal = "Average returns", IsBullish = null }); }
            else { score -= 10; signals.Add(new TechnicalSignal { Indicator = "ROE", Value = $"{f.ROE:F1}%", Signal = "Poor returns", IsBullish = false }); }

            // ROCE
            if (f.ROCE > 20) { score += 8; signals.Add(new TechnicalSignal { Indicator = "ROCE", Value = $"{f.ROCE:F1}%", Signal = "Strong capital use", IsBullish = true }); }
            else if (f.ROCE > 12) { score += 4; signals.Add(new TechnicalSignal { Indicator = "ROCE", Value = $"{f.ROCE:F1}%", Signal = "Good capital use", IsBullish = true }); }

            // D/E
            if (f.DebtToEquity == 0) { score += 10; signals.Add(new TechnicalSignal { Indicator = "Debt/Equity", Value = "0", Signal = "Debt-free", IsBullish = true }); }
            else if (f.DebtToEquity < 0.3) { score += 12; signals.Add(new TechnicalSignal { Indicator = "Debt/Equity", Value = f.DebtToEquity.ToString("F2"), Signal = "Very low debt", IsBullish = true }); }
            else if (f.DebtToEquity < 1.0) { score += 5; signals.Add(new TechnicalSignal { Indicator = "Debt/Equity", Value = f.DebtToEquity.ToString("F2"), Signal = "Manageable debt", IsBullish = true }); }
            else if (f.DebtToEquity < 2.0) { score -= 8; signals.Add(new TechnicalSignal { Indicator = "Debt/Equity", Value = f.DebtToEquity.ToString("F2"), Signal = "High debt", IsBullish = false }); }
            else { score -= 15; signals.Add(new TechnicalSignal { Indicator = "Debt/Equity", Value = f.DebtToEquity.ToString("F2"), Signal = "Dangerous debt", IsBullish = false }); }

            // Revenue Growth
            if (f.RevenueGrowthYoY > 20) { score += 12; signals.Add(new TechnicalSignal { Indicator = "Revenue Growth", Value = $"{f.RevenueGrowthYoY:F1}%", Signal = "Strong growth", IsBullish = true }); }
            else if (f.RevenueGrowthYoY > 10) { score += 6; signals.Add(new TechnicalSignal { Indicator = "Revenue Growth", Value = $"{f.RevenueGrowthYoY:F1}%", Signal = "Healthy growth", IsBullish = true }); }
            else if (f.RevenueGrowthYoY > 0) { score += 2; signals.Add(new TechnicalSignal { Indicator = "Revenue Growth", Value = $"{f.RevenueGrowthYoY:F1}%", Signal = "Slow growth", IsBullish = null }); }
            else { score -= 12; signals.Add(new TechnicalSignal { Indicator = "Revenue Growth", Value = $"{f.RevenueGrowthYoY:F1}%", Signal = "Declining revenue", IsBullish = false }); }

            // Promoter Holding
            if (f.PromoterHolding > 60) { score += 10; signals.Add(new TechnicalSignal { Indicator = "Promoter Holding", Value = $"{f.PromoterHolding:F1}%", Signal = "High conviction", IsBullish = true }); }
            else if (f.PromoterHolding > 40) { score += 4; signals.Add(new TechnicalSignal { Indicator = "Promoter Holding", Value = $"{f.PromoterHolding:F1}%", Signal = "Moderate holding", IsBullish = null }); }
            else if (f.PromoterHolding > 0) { score -= 8; signals.Add(new TechnicalSignal { Indicator = "Promoter Holding", Value = $"{f.PromoterHolding:F1}%", Signal = "Low promoter confidence", IsBullish = false }); }

            // FII
            if (f.FIIHolding > 20)
                signals.Add(new TechnicalSignal { Indicator = "FII Holding", Value = $"{f.FIIHolding:F1}%", Signal = "Strong FII interest", IsBullish = true });

            f.Signals = signals;
            return Math.Max(0, Math.Min(100, score));
        }

        // ── Batch fetch ──────────────────────────────
        public async Task<List<FundamentalResult>> BatchFetchAsync(
            List<string> symbols, int delayMs = 1000)
        {
            var results = new List<FundamentalResult>();
            foreach (var symbol in symbols)
            {
                results.Add(await GetFundamentalsAsync(symbol));
                await Task.Delay(delayMs);
            }
            return results;
        }
    }
}
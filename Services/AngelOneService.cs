using AlgoSenseNSE.API.Models;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OtpNet;
using SQLitePCL;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection.PortableExecutable;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using static System.Net.WebRequestMethods;

namespace AlgoSenseNSE.API.Services
{
    public class AngelOneService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<AngelOneService> _logger;
        private readonly HttpClient _http;

        private string _jwtToken = "";
        private string _apiKey = "ORyQFhHc";
        private string _feedToken = "";
        private string _refreshToken = "";
        private DateTime _tokenExpiry = DateTime.MinValue;

        private const string BaseUrl = "https://apiconnect.angelone.in";

        public AngelOneService(IConfiguration config,
            ILogger<AngelOneService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _logger = logger;
            _http = httpClientFactory.CreateClient("AngelOne");
        }

        // ── Generate TOTP ───────────────────────────
        private string GenerateTotp()
        {
            var secret = _config["AngelOne:TotpSecret"]!;
            var bytes = Base32Encoding.ToBytes(secret);
            var totp = new Totp(bytes);
            return totp.ComputeTotp();
        }

        // ── Login ───────────────────────────────────
        public async Task<bool> LoginAsync()
        {
            try
            {
                var totp = GenerateTotp();
                var payload = new
                {
                    clientcode = _config["AngelOne:ClientId"],
                    password = _config["AngelOne:Password"],
                    totp = totp
                };

                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{BaseUrl}/rest/auth/angelbroking/user/v1/loginByPassword");

                // Angel One requires these exact headers
                request.Headers.Add("X-API-KEY",
                    _config["AngelOne:ApiKey"]);
                request.Headers.Add("X-ClientLocalIP", "127.0.0.1");
                request.Headers.Add("X-ClientPublicIP", "127.0.0.1");
                request.Headers.Add("X-MACAddress", "00:00:00:00:00:00");
                request.Headers.Add("X-PrivateKey",
                    _config["AngelOne:ApiKey"]);
                request.Headers.Add("X-UserType", "USER");
                request.Headers.Add("X-SourceID", "WEB");
                request.Headers.Add("Accept", "application/json");

                var json = Newtonsoft.Json.JsonConvert
                    .SerializeObject(payload);
                request.Content = new StringContent(
                    json, Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(request);

                // Read raw response first for debugging
                var rawResponse = await response.Content
                    .ReadAsStringAsync();

                _logger.LogInformation(
                    "Angel One login response [{code}]: {body}",
                    (int)response.StatusCode,
                    rawResponse.Length > 200
                        ? rawResponse[..200]
                        : rawResponse);

                if (string.IsNullOrWhiteSpace(rawResponse))
                {
                    _logger.LogError(
                        "❌ Empty response from Angel One login");
                    return false;
                }

                var result = JObject.Parse(rawResponse);

                if (result["status"]?.Value<bool>() == true)
                {
                    _jwtToken = result["data"]?["jwtToken"]
                        ?.Value<string>() ?? "";
                    _feedToken = result["data"]?["feedToken"]
                        ?.Value<string>() ?? "";
                    _refreshToken = result["data"]?["refreshToken"]
                        ?.Value<string>() ?? "";
                    _tokenExpiry = DateTime.Now.AddHours(6);
                    _logger.LogInformation(
                        "✅ Angel One login successful");
                    return true;
                }

                _logger.LogError(
                    "❌ Angel One login failed: {msg}",
                    result["message"]?.Value<string>());
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "❌ Angel One login exception");
                return false;
            }
        }

        // ── Ensure token is valid ───────────────────
        private async Task EnsureLoggedInAsync()
        {
            if (string.IsNullOrEmpty(_jwtToken) ||
                DateTime.Now >= _tokenExpiry)
            {
                await LoginAsync();
            }
        }

        // ── Get headers ─────────────────────────────
        private HttpRequestMessage CreateRequest(
            HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _jwtToken);
            req.Headers.Add("X-API-KEY", _config["AngelOne:ApiKey"]);
            req.Headers.Add("X-ClientLocalIP", "127.0.0.1");
            req.Headers.Add("X-ClientPublicIP", "127.0.0.1");
            req.Headers.Add("X-MACAddress", "00:00:00:00:00:00");
            req.Headers.Add("X-PrivateKey", _config["AngelOne:ApiKey"]);
            req.Headers.Add("Accept", "application/json");
            return req;
        }

        // ── Get Live Quote ──────────────────────────
        public async Task<LivePrice?> GetLivePriceAsync(
            string symbol, string token)
        {
            try
            {
                await EnsureLoggedInAsync();

                var payload = new
                {
                    mode = "FULL",
                    exchangeTokens = new Dictionary<string, string[]>
                    {
                        { "NSE", new[] { token } }
                    }
                };

                var req = CreateRequest(HttpMethod.Post,
                    $"{BaseUrl}/rest/secure/angelbroking/market/v1/quote/");
                req.Content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(req);
                var json = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);

                var fetched = result["data"]?["fetched"]?[0];
                if (fetched == null) return null;

                return new LivePrice
                {
                    Symbol = symbol,
                    LTP = fetched["ltp"]?.Value<double>() ?? 0,
                    Change = fetched["netChange"]?.Value<double>() ?? 0,
                    ChangePercent = fetched["percentChange"]?.Value<double>() ?? 0,
                    High = fetched["high"]?.Value<double>() ?? 0,
                    Low = fetched["low"]?.Value<double>() ?? 0,
                    Volume = fetched["tradeVolume"]?.Value<long>() ?? 0,
                    UpdatedAt = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "❌ Error fetching live price for {symbol}", symbol);
                return null;
            }
        }

        // ── Get OHLCV Candles ───────────────────────
        public async Task<List<OhlcvCandle>> GetOhlcvAsync(string symbol, string token, string interval = "ONE_DAY", int days = 60, string exchange = "NSE")
        {
            try
            {
                if (string.IsNullOrEmpty(_jwtToken)) await LoginAsync();

                // Angel One historical API needs dates in "yyyy-MM-dd HH:mm" format
                // and only allows up to 60 days of daily data on free tier
                var toDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                var fromDate = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd HH:mm");

                var payload = new
                {
                    exchange = exchange,
                    symboltoken = token,
                    interval = interval,
                    fromdate = fromDate,
                    todate = toDate
                };

                var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://apiconnect.angelone.in/rest/secure/angelbroking/historical/v1/getCandleData");

                // Historical endpoint needs BOTH Authorization Bearer AND X-PrivateKey
                request.Headers.Add("Authorization", $"Bearer {_jwtToken}");
                request.Headers.Add("X-PrivateKey", _apiKey);
                request.Headers.Add("X-UserType", "USER");
                request.Headers.Add("X-SourceID", "WEB");
                request.Headers.Add("X-ClientLocalIP", "127.0.0.1");
                request.Headers.Add("X-ClientPublicIP", "127.0.0.1");
                request.Headers.Add("X-MACAddress", "00:00:00:00:00:00");

                request.Content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(request);
                var raw = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    // Try BSE if NSE fails
                    if (exchange == "NSE")
                        return await GetOhlcvAsync(symbol, token, interval, days, "BSE");

                    _logger.LogWarning("⚠️ OHLCV {code} for {sym} (token:{tok})",
                        (int)response.StatusCode, symbol, token);
                    return new List<OhlcvCandle>();
                }

                var obj = JsonConvert.DeserializeObject<JObject>(raw);
                if (obj?["status"]?.Value<bool>() != true)
                {
                    _logger.LogWarning("⚠️ OHLCV bad status for {sym}: {msg}",
                        symbol, obj?["message"]);
                    return new List<OhlcvCandle>();
                }

                var data = obj["data"] as JArray;
                if (data == null || data.Count == 0) return new List<OhlcvCandle>();

                // Angel One returns: [timestamp, open, high, low, close, volume]
                var bars = new List<OhlcvCandle>();
                foreach (var item in data)
                {
                    var arr = item as JArray;
                    if (arr == null || arr.Count < 6) continue;

                    bars.Add(new OhlcvCandle
                    {
                        Timestamp = DateTime.Parse(arr[0].Value<string>()!),
                        Open = arr[1].Value<double>(),
                        High = arr[2].Value<double>(),
                        Low = arr[3].Value<double>(),
                        Close = arr[4].Value<double>(),
                        Volume = arr[5].Value<long>()
                    });
                }

                _logger.LogInformation("✅ OHLCV {sym}: {count} bars", symbol, bars.Count);
                return bars;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ OHLCV exception for {sym}: {msg}", symbol, ex.Message);
                return new List<OhlcvCandle>();
            }
        }

        // ── Get Symbol Token Map ────────────────────
        // Angel One needs a "token" number for each symbol
        // This fetches the master list
        public async Task<Dictionary<string, string>> GetSymbolTokenMapAsync(
    List<string> symbols)
        {
            var map = new Dictionary<string, string>();
            try
            {
                await EnsureLoggedInAsync();

                // Angel One provides instruments as a direct JSON download
                // This URL does not require auth headers
                var urls = new[]
                {
            "https://margincalculator.angelbroking.com/OpenAPI_File/files/OpenAPIScripMaster.json",
            "https://apiconnect.angelone.in/rest/secure/angelbroking/market/v1/allInstruments"
        };

                string raw = "";
                foreach (var url in urls)
                {
                    try
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.Add("User-Agent",
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                        // Only add auth for the secure endpoint
                        if (url.Contains("apiconnect"))
                        {
                            req.Headers.Authorization =
                                new AuthenticationHeaderValue(
                                    "Bearer", _jwtToken);
                            req.Headers.Add("X-API-KEY",
                                _config["AngelOne:ApiKey"]);
                            req.Headers.Add("X-PrivateKey",
                                _config["AngelOne:ApiKey"]);
                            req.Headers.Add("X-UserType", "USER");
                            req.Headers.Add("X-SourceID", "WEB");
                        }

                        var response = await _http.SendAsync(req);
                        raw = await response.Content.ReadAsStringAsync();

                        _logger.LogInformation(
                            "Instruments from {url}: length={len}, " +
                            "starts={start}",
                            url, raw.Length,
                            raw.Length > 80 ? raw[..80] : raw);

                        // Check if it's valid JSON array
                        if (raw.TrimStart().StartsWith("["))
                            break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            "⚠️ Instruments URL failed {url}: {msg}",
                            url, ex.Message);
                    }
                }

                if (!raw.TrimStart().StartsWith("["))
                {
                    _logger.LogError(
                        "❌ Could not get instrument list. " +
                        "Using fallback symbol list.");
                    return GetFallbackSymbolMap();
                }

                var instruments = JArray.Parse(raw);
                foreach (var inst in instruments)
                {
                    var sym = inst["symbol"]?.Value<string>()
                              ?? inst["tradingsymbol"]?.Value<string>()
                              ?? "";
                    var token = inst["token"]?.Value<string>()
                              ?? inst["symboltoken"]?.Value<string>()
                              ?? "";
                    var exch = inst["exch_seg"]?.Value<string>()
                              ?? inst["exchange"]?.Value<string>()
                              ?? "";
                    var instrType = inst["instrumenttype"]
                        ?.Value<string>() ?? "";

                    // Only equity, skip FNO/commodities
                    if ((exch == "NSE" || exch == "BSE")
                        && !string.IsNullOrEmpty(sym)
                        && !string.IsNullOrEmpty(token)
                        && (instrType == "EQ" ||
                            instrType == "" ||
                            instrType == "AMXIDX")
                        && !sym.Contains("-")
                        && !map.ContainsKey(sym))
                    {
                        map[sym] = token;
                    }
                }

                _logger.LogInformation(
                    "✅ Symbol token map: {count} symbols loaded",
                    map.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "❌ Error building symbol token map");
                return GetFallbackSymbolMap();
            }
            return map;
        }

        // Fallback with top 50 NSE stocks if API fails
        private Dictionary<string, string> GetFallbackSymbolMap()
        {
            _logger.LogWarning(
                "⚠️ Using fallback symbol map (top 50 NSE stocks)");

            // token numbers for top NSE stocks
            // format: symbol → Angel One token
            return new Dictionary<string, string>
    {
        {"RELIANCE", "2885"}, {"TCS", "11536"},
        {"HDFCBANK", "1333"}, {"INFY", "1594"},
        {"ICICIBANK", "4963"}, {"HINDUNILVR", "1394"},
        {"BAJFINANCE", "317"}, {"SBIN", "3045"},
        {"BHARTIARTL", "10604"}, {"KOTAKBANK", "1922"},
        {"AXISBANK", "5900"}, {"LT", "11483"},
        {"MARUTI", "10999"}, {"SUNPHARMA", "3351"},
        {"TITAN", "3506"}, {"WIPRO", "3787"},
        {"DRREDDY", "881"}, {"TATASTEEL", "3499"},
        {"ONGC", "2475"}, {"NTPC", "11630"},
        {"POWERGRID", "14977"}, {"TECHM", "13538"},
        {"DIVISLAB", "10940"}, {"NESTLEIND", "17963"},
        {"ULTRACEMCO", "11532"}, {"ASIANPAINT", "236"},
        {"COALINDIA", "20374"}, {"BPCL", "526"},
        {"IOC", "1624"}, {"GRASIM", "1232"},
        {"ADANIPORTS", "15083"}, {"TATAMOTOR", "3456"},
        {"BAJAJFINSV", "16675"}, {"HCLTECH", "7229"},
        {"INDUSINDBK", "5258"}, {"JSWSTEEL", "11723"},
        {"LTIM", "17818"}, {"ZOMATO", "5097"},
        {"CIPLA", "694"}, {"EICHERMOT", "910"},
        {"HEROMOTOCO", "1348"}, {"APOLLOHOSP", "157"},
        {"BRITANNIA", "547"}, {"PIDILITIND", "2664"},
        {"SIEMENS", "3150"}, {"ABB", "13"},
        {"HAVELLS", "1215"}, {"MUTHOOTFIN", "17261"},
        {"BANKBARODA", "4668"}, {"PNB", "2730"}
    };
        }

        public async Task<object?> GetLiveIndexAsync(string exchange, string token)
        {
            try
            {
                if (string.IsNullOrEmpty(_jwtToken)) await LoginAsync();

                var payload = new
                {
                    mode = "LTP",
                    exchangeTokens = new Dictionary<string, string[]>
            {
                { exchange, new[] { token } }
            }
                };

                var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://apiconnect.angelone.in/rest/secure/angelbroking/market/v1/quote/");

                // ← This is the correct auth for market data endpoints
                request.Headers.Add("Authorization", $"Bearer {_jwtToken}");
                request.Headers.Add("X-PrivateKey", _apiKey);        // ← KEY FIX
                request.Headers.Add("X-UserType", "USER");
                request.Headers.Add("X-SourceID", "WEB");
                request.Headers.Add("X-ClientLocalIP", "127.0.0.1");
                request.Headers.Add("X-ClientPublicIP", "127.0.0.1");
                request.Headers.Add("X-MACAddress", "00:00:00:00:00:00");
                request.Content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(request);
                var raw = await response.Content.ReadAsStringAsync();

                _logger.LogInformation("Index API raw [{exchange}/{token}]: {raw}",
                    exchange, token, raw.Length > 400 ? raw[..400] : raw);

                var obj = JsonConvert.DeserializeObject<JObject>(raw);
                if (obj?["success"]?.Value<bool>() != true) return null;

                // Response: {"data":{"fetched":[{"token":"26000","ltp":23450.5,"percentChange":0.42,...}]}}
                var fetched = obj["data"]?["fetched"]?.FirstOrDefault();
                if (fetched == null) return null;

                double ltp = 0, chg = 0;
                foreach (var f in new[] { "ltp", "LTP", "close", "lastTradedPrice" })
                    if (fetched[f] != null &&
                        double.TryParse(fetched[f]!.ToString(), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out double v) && v > 0)
                    { ltp = v; break; }

                foreach (var f in new[] { "percentChange", "pChange", "netChangePercentage" })
                    if (fetched[f] != null &&
                        double.TryParse(fetched[f]!.ToString(), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out double v))
                    { chg = v; break; }

                return ltp > 0 ? new { ltp, changePercent = chg } : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Index fetch failed: {msg}", ex.Message);
                return null;
            }
        }           

        // ── Get Stock Info ────────────────────────── 
        public async Task<StockInfo?> GetStockInfoAsync (
            string symbol, string token)
        {
            var price = await GetLivePriceAsync(symbol, token);
            if (price == null) return null;

            return new StockInfo
            {
                Symbol = symbol,
                LastPrice = price.LTP,
                Change = price.Change,
                ChangePercent = price.ChangePercent,
                High = price.High,
                Low = price.Low,
                Volume = price.Volume,
                Open = price.LTP - price.Change
            };
        }

        public string GetFeedToken() => _feedToken;
        public string GetJwtToken() => _jwtToken;
    }
}
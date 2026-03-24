using AlgoSenseNSE.API.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OtpNet;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace AlgoSenseNSE.API.Services
{
    public class AngelOneService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<AngelOneService> _logger;
        private readonly HttpClient _http;
        private readonly IHttpClientFactory _httpClientFactory;

        private string _jwtToken = "";
        private string _apiKey = "";
        private string _feedToken = "";
        private string _clientId = "";
        private string _refreshToken = "";
        private DateTime _tokenExpiry = DateTime.MinValue;
        private AngelOneWebSocketService? _wsService;

        private const string BaseUrl = "https://apiconnect.angelone.in";

        // Rate limiter — Angel One free = ~3 req/sec
        private readonly SemaphoreSlim _rateLimiter = new(1, 1);
        private DateTime _lastApiCall = DateTime.MinValue;
        private const int MinMsBetweenCalls = 400;

        public AngelOneService(
            IConfiguration config,
            ILogger<AngelOneService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _http = httpClientFactory.CreateClient("AngelOne");
            _apiKey = _config["AngelOne:ApiKey"] ?? "";
            _clientId = _config["AngelOne:ClientId"] ?? "";
        }
        public void SetWebSocketService(AngelOneWebSocketService ws) => _wsService = ws;

        // ── Rate throttle ─────────────────────────────
        private async Task ThrottleAsync()
        {
            await _rateLimiter.WaitAsync();
            try
            {
                var elapsed = (DateTime.UtcNow - _lastApiCall).TotalMilliseconds;
                if (elapsed < MinMsBetweenCalls)
                    await Task.Delay((int)(MinMsBetweenCalls - elapsed));
                _lastApiCall = DateTime.UtcNow;
            }
            finally { _rateLimiter.Release(); }
        }

        // ── Generate TOTP ─────────────────────────────
        private string GenerateTotp()
        {
            var secret = _config["AngelOne:TotpSecret"]!;
            var bytes = Base32Encoding.ToBytes(secret);
            var totp = new Totp(bytes);
            return totp.ComputeTotp();
        }

        // ── Login ─────────────────────────────────────
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

                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{BaseUrl}/rest/auth/angelbroking/user/v1/loginByPassword");

                request.Headers.Add("X-API-KEY", _apiKey);
                request.Headers.Add("X-ClientLocalIP", "127.0.0.1");
                request.Headers.Add("X-ClientPublicIP", "127.0.0.1");
                request.Headers.Add("X-MACAddress", "00:00:00:00:00:00");
                request.Headers.Add("X-PrivateKey", _apiKey);
                request.Headers.Add("X-UserType", "USER");
                request.Headers.Add("X-SourceID", "WEB");
                request.Headers.Add("Accept", "application/json");
                request.Content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(request);
                var rawResponse = await response.Content.ReadAsStringAsync();

                _logger.LogInformation(
                    "Angel One login response [{code}]: {body}",
                    (int)response.StatusCode,
                    rawResponse.Length > 200 ? rawResponse[..200] : rawResponse);

                var result = JObject.Parse(rawResponse);
                if (result["status"]?.Value<bool>() == true)
                {
                    _jwtToken = result["data"]?["jwtToken"]?.Value<string>() ?? "";
                    _feedToken = result["data"]?["feedToken"]?.Value<string>() ?? "";
                    _refreshToken = result["data"]?["refreshToken"]?.Value<string>() ?? "";
                    _tokenExpiry = DateTime.Now.AddHours(6);
                    _logger.LogInformation("✅ Angel One login successful");

                    if (_wsService != null && !string.IsNullOrEmpty(_feedToken))
                        _wsService.SetCredentials(_jwtToken, _feedToken, _clientId);
                    return true;
                }

                _logger.LogError("❌ Angel One login failed: {msg}",
                    result["message"]?.Value<string>());
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Angel One login exception");
                return false;
            }
        }

        // ── Ensure token valid ────────────────────────
        private async Task EnsureLoggedInAsync()
        {
            if (string.IsNullOrEmpty(_jwtToken) || DateTime.Now >= _tokenExpiry)
                await LoginAsync();
        }

        // ── Authenticated request builder ─────────────
        private HttpRequestMessage CreateAuthRequest(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _jwtToken);
            req.Headers.Add("X-PrivateKey", _apiKey);
            req.Headers.Add("X-UserType", "USER");
            req.Headers.Add("X-SourceID", "WEB");
            req.Headers.Add("X-ClientLocalIP", "127.0.0.1");
            req.Headers.Add("X-ClientPublicIP", "127.0.0.1");
            req.Headers.Add("X-MACAddress", "00:00:00:00:00:00");
            req.Headers.Add("Accept", "application/json");
            return req;
        }

        // ── Get Live Price ────────────────────────────
        public async Task<LivePrice?> GetLivePriceAsync(string symbol, string token)
        {
            try
            {
                await ThrottleAsync();
                await EnsureLoggedInAsync();

                var payload = new
                {
                    mode = "FULL",
                    exchangeTokens = new Dictionary<string, string[]>
                    {
                        { "NSE", new[] { token } }
                    }
                };

                var req = CreateAuthRequest(HttpMethod.Post,
                    $"{BaseUrl}/rest/secure/angelbroking/market/v1/quote/");
                req.Content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(req);
                var json = await response.Content.ReadAsStringAsync();

                if (json.Contains("exceeding access rate")) return null;

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
                _logger.LogDebug("Live price failed {sym}: {msg}", symbol, ex.Message);
                return null;
            }
        }

        // ── Get OHLCV Candles ─────────────────────────
        public async Task<List<OhlcvCandle>> GetOhlcvAsync(
            string symbol, string token,
            string interval = "ONE_DAY",
            int days = 60,
            string exchange = "NSE")
        {
            try
            {
                await ThrottleAsync();
                await EnsureLoggedInAsync();

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

                var req = CreateAuthRequest(HttpMethod.Post,
                    $"{BaseUrl}/rest/secure/angelbroking/historical/v1/getCandleData");
                req.Content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(req);
                var raw = await response.Content.ReadAsStringAsync();

                if (raw.Contains("exceeding access rate") ||
                    raw.Contains("Invalid API Key"))
                {
                    _logger.LogWarning("⚠️ OHLCV rate limited for {sym}", symbol);
                    await Task.Delay(2000);
                    return new List<OhlcvCandle>();
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (exchange == "NSE")
                    {
                        await Task.Delay(400);
                        return await GetOhlcvAsync(
                            symbol, token, interval, days, "BSE");
                    }
                    return new List<OhlcvCandle>();
                }

                var obj = JsonConvert.DeserializeObject<JObject>(raw);
                if (obj?["status"]?.Value<bool>() != true)
                {
                    var msg = obj?["message"]?.Value<string>() ?? "";
                    if (exchange == "NSE" &&
                        (msg.Contains("Invalid") || msg.Contains("token")))
                    {
                        await Task.Delay(400);
                        return await GetOhlcvAsync(
                            symbol, token, interval, days, "BSE");
                    }
                    return new List<OhlcvCandle>();
                }

                var data = obj["data"] as JArray;
                if (data == null || data.Count == 0)
                    return new List<OhlcvCandle>();

                var bars = new List<OhlcvCandle>();
                foreach (var item in data)
                {
                    var arr = item as JArray;
                    if (arr == null || arr.Count < 6) continue;
                    try
                    {
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
                    catch { }
                }

                if (bars.Count > 0)
                    _logger.LogInformation(
                        "✅ OHLCV {sym}: {count} bars ({exch})",
                        symbol, bars.Count, exchange);

                return bars;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ OHLCV exception {sym}: {msg}",
                    symbol, ex.Message);
                return new List<OhlcvCandle>();
            }
        }

        // ── Get Live Index (Nifty / Sensex) ──────────
        public async Task<object?> GetLiveIndexAsync(string exchange, string token)
        {
            try
            {
                await ThrottleAsync();
                await EnsureLoggedInAsync();

                var payload = new
                {
                    mode = "LTP",
                    exchangeTokens = new Dictionary<string, string[]>
                    {
                        { exchange, new[] { token } }
                    }
                };

                var req = CreateAuthRequest(HttpMethod.Post,
                    $"{BaseUrl}/rest/secure/angelbroking/market/v1/quote/");
                req.Content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");

                var response = await _http.SendAsync(req);
                var raw = await response.Content.ReadAsStringAsync();

                if (!raw.TrimStart().StartsWith("{"))
                {
                    _logger.LogWarning(
                        "⚠️ Index non-JSON [{exch}]: {raw}",
                        exchange, raw.Length > 80 ? raw[..80] : raw);
                    return null;
                }

                _logger.LogInformation(
                    "Index API raw [{exch}/{tok}]: {raw}",
                    exchange, token,
                    raw.Length > 200 ? raw[..200] : raw);

                var obj = JObject.Parse(raw);
                if (obj["status"]?.Value<bool>() != true) return null;

                var fetched = obj["data"]?["fetched"]?.FirstOrDefault();
                if (fetched == null) return null;

                double ltp = 0, chg = 0;
                foreach (var f in new[]
                    { "ltp", "LTP", "close", "lastTradedPrice" })
                {
                    if (fetched[f] != null &&
                        double.TryParse(fetched[f]!.ToString(),
                            NumberStyles.Any,
                            CultureInfo.InvariantCulture, out double v)
                        && v > 0)
                    { ltp = v; break; }
                }

                foreach (var f in new[]
                    { "percentChange", "pChange", "netChangePercentage" })
                {
                    if (fetched[f] != null &&
                        double.TryParse(fetched[f]!.ToString(),
                            NumberStyles.Any,
                            CultureInfo.InvariantCulture, out double v))
                    { chg = v; break; }
                }

                return ltp > 0 ? new { ltp, changePercent = chg } : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Index fetch failed: {msg}", ex.Message);
                return null;
            }
        }

        // ── Symbol Token Map — STREAMING (no OOM) ────
        // Uses streaming JSON reader to avoid loading
        // the entire 40MB file into memory at once.
        // Falls back to hardcoded map if OOM or error.
        public async Task<Dictionary<string, string>> GetSymbolTokenMapAsync(
            List<string> symbols)
        {
            var map = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                var url = "https://margincalculator.angelbroking.com" +
                          "/OpenAPI_File/files/OpenAPIScripMaster.json";

                // Stream response — never loads full 40MB into RAM
                using var response = await _http.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "⚠️ Instrument master returned {code}",
                        (int)response.StatusCode);
                    return GetFallbackSymbolMap();
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                using var jsonReader = new Newtonsoft.Json.JsonTextReader(reader);

                var serializer = new Newtonsoft.Json.JsonSerializer();
                var instruments = serializer
                    .Deserialize<List<InstrumentToken>>(jsonReader);

                if (instruments == null || instruments.Count == 0)
                {
                    _logger.LogWarning("⚠️ No instruments returned");
                    return GetFallbackSymbolMap();
                }

                _logger.LogInformation(
                    "Instruments loaded: {count}", instruments.Count);

                foreach (var inst in instruments)
                {
                    if (string.IsNullOrEmpty(inst.token) ||
                        string.IsNullOrEmpty(inst.symbol) ||
                        string.IsNullOrEmpty(inst.exch_seg))
                        continue;

                    // Only NSE equity stocks
                    if (inst.exch_seg != "NSE") continue;

                    // Accept symbols ending in -EQ or no suffix
                    var sym = inst.symbol.Trim().ToUpper();

                    if (sym.EndsWith("-EQ"))
                        sym = sym[..^3]; // Remove -EQ suffix

                    // Skip futures/options/indices (contain digits or special chars)
                    if (sym.Contains("-") || sym.Contains("&")) continue;
                    if (string.IsNullOrEmpty(sym)) continue;

                    // TryAdd — silently skips duplicates
                    map.TryAdd(sym, inst.token);
                }

                _logger.LogInformation(
                    "✅ Symbol token map: {count} symbols loaded", map.Count);

                return map.Count > 0 ? map : GetFallbackSymbolMap();
            }
            catch (OutOfMemoryException)
            {
                _logger.LogWarning(
                    "⚠️ OutOfMemory reading instruments — using fallback map");
                return GetFallbackSymbolMap();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error building symbol token map");
                return GetFallbackSymbolMap();
            }
        }

        // ── Fallback map — hardcoded correct tokens ───
        // Used when instrument master can't be loaded
        // (low memory server like Railway free tier)
        private Dictionary<string, string> GetFallbackSymbolMap()
        {
            _logger.LogWarning(
                "⚠️ Using fallback symbol map (50 intraday stocks)");

            var tokens = new[]
            {
                ("SBIN",       "3045"),
                ("BANKBARODA", "4668"),
                ("CANBK",      "10794"),
                ("UNIONBANK",  "2752"),
                ("MAHABANK",   "4032"),
                ("CENTRALBK",  "20374"),
                ("IOB",        "4574"),
                ("INDIANB",    "10999"),
                ("PNB",        "14428"),
                ("COALINDIA",  "1660"),
                ("ONGC",       "2475"),
                ("BPCL",       "526"),
                ("IOC",        "1624"),
                ("GAIL",       "910"),
                ("NMDC",       "15332"),
                ("NATIONALUM", "15355"),
                ("HINDALCO",   "1363"),
                ("TATASTEEL",  "3432"),
                ("WIPRO",      "3787"),
                ("HCLTECH",    "7229"),
                ("TECHM",      "13538"),
                ("INFY",       "1594"),
                ("TCS",        "11536"),
                ("SUNPHARMA",  "3351"),
                ("CIPLA",      "694"),
                ("DRREDDY",    "881"),
                ("LUPIN",      "10440"),
                ("NTPC",       "11630"),
                ("POWERGRID",  "14977"),
                ("NHPC",       "13675"),
                ("IRFC",       "49071"),
                ("RVNL",       "20320"),
                ("RECLTD",     "13611"),
                ("PFC",        "14299"),
                ("IREDA",      "54214"),
                ("IRCTC",      "16752"),
                ("IEX",        "17163"),
                ("SUZLON",     "12018"),
                ("ADANIPORTS", "15083"),
                ("ADANIPOWER", "533096"),
                ("TATAPOWER",  "3426"),
                ("BEL",        "383"),
                ("ITC",        "1660"),
                ("EMAMILTD",   "317"),
                ("MARICO",     "4067"),
                ("MUTHOOTFIN", "18143"),
                ("MANAPPURAM", "19061"),
                ("LICHSGFIN",  "1023"),
                ("LICI",       "543526"),
                ("INDUSTOWER", "14181"),
                ("PETRONET",   "11351"),
                ("COLPAL",     "1623"),
                ("HINDUNILVR", "1394"),
            };

            var map = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var (sym, tok) in tokens)
                map.TryAdd(sym, tok);

            return map;
        }

        // ── Get Stock Info ────────────────────────────
        public async Task<StockInfo?> GetStockInfoAsync(
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
        public string GetClientId() => _clientId;
    }

    // ── Instrument token model for streaming parse ──
    public class InstrumentToken
    {
        public string token { get; set; } = "";
        public string symbol { get; set; } = "";
        public string name { get; set; } = "";
        public string exch_seg { get; set; } = "";
        public string expiry { get; set; } = "";    
        public string instrumenttype { get; set; } = "";
    }
}
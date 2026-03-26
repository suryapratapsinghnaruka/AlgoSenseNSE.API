using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlgoSenseNSE.API.Models;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// Angel One SmartAPI WebSocket V2
    /// Per official Python SDK:
    /// github.com/angel-one/smartapi-python/blob/main/SmartApi/smartWebSocketV2.py
    ///
    /// Auth: 4 HTTP headers at handshake (no Bearer prefix on Authorization)
    /// Heartbeat: send "ping" every 10s, server responds "pong"
    /// Binary: little-endian, token at bytes 2-27, LTP at 43-51 / 100
    /// Max 1000 tokens per session, max 3 concurrent connections
    /// </summary>
    public class AngelOneWebSocketService : IDisposable
    {
        private readonly ILogger<AngelOneWebSocketService> _logger;
        private readonly IConfiguration _config;

        private ClientWebSocket? _ws;
        private CancellationTokenSource _cts = new();
        private bool _isConnected  = false;
        private bool _disposed     = false;
        private bool _reconnecting = false;

        private string _jwtToken  = "";
        private string _feedToken = "";
        private string _clientId  = "";
        private string _apiKey    = "";

        private readonly Dictionary<string, string> _symbolToToken = new();
        private readonly Dictionary<string, string> _tokenToSymbol = new();
        private readonly HashSet<string> _nseTokens = new();
        private readonly HashSet<string> _bseTokens = new();

        private readonly Dictionary<string, LiveTick> _ticks = new();
        private readonly object _lock = new();

        public event Action<string, LiveTick>? OnTickUpdate;
        public event Action?                   OnConnected;
        public event Action<string>?           OnDisconnected;

        // ✅ CORRECT URL — angelone.in not angelbroking.com
        private const string WsUrl            = "wss://smartapisocket.angelone.in/smart-stream";
        private const string HeartBeatMessage = "ping";
        private const int    HeartBeatInterval = 10; // seconds per official SDK

        private const int LTP_MODE   = 1;
        private const int QUOTE      = 2;
        private const int SNAP_QUOTE = 3;
        private const int NSE_CM     = 1;
        private const int BSE_CM     = 3;
        private const int SUBSCRIBE_ACTION   = 1;
        private const int UNSUBSCRIBE_ACTION = 0;

        public bool IsConnected => _isConnected;
        public int  TickCount   => _ticks.Count;

        public AngelOneWebSocketService(
            ILogger<AngelOneWebSocketService> logger,
            IConfiguration config)
        {
            _logger   = logger;
            _config   = config;
            _apiKey   = config["AngelOne:ApiKey"]   ?? "";
            _clientId = config["AngelOne:ClientId"] ?? "";
        }

        public void SetCredentials(
            string jwtToken, string feedToken, string clientId = "")
        {
            _jwtToken  = jwtToken;
            _feedToken = feedToken;
            if (!string.IsNullOrEmpty(clientId)) _clientId = clientId;
            _logger.LogInformation("✅ WebSocket credentials set");
        }

        public void RegisterTokens(
            Dictionary<string, string> symbolToToken, bool isNse = true)
        {
            lock (_lock)
            {
                foreach (var kv in symbolToToken)
                {
                    _symbolToToken[kv.Key]   = kv.Value;
                    _tokenToSymbol[kv.Value] = kv.Key;
                    if (isNse) _nseTokens.Add(kv.Value);
                    else       _bseTokens.Add(kv.Value);
                }
            }
            _logger.LogInformation(
                "📋 Registered {n} {ex} tokens for WebSocket",
                symbolToToken.Count, isNse ? "NSE" : "BSE");
        }

        // ✅ THE FIX: clears old data and reloads from complete map
        public void RegisterTokensFromMap(
            Dictionary<string, string> symbolToToken)
        {
            lock (_lock)
            {
                _nseTokens.Clear();
                _symbolToToken.Clear();
                _tokenToSymbol.Clear();

                foreach (var kv in symbolToToken)
                {
                    _symbolToToken[kv.Key]   = kv.Value;
                    _tokenToSymbol[kv.Value] = kv.Key;
                    _nseTokens.Add(kv.Value);
                }
            }
            _logger.LogInformation(
                "📋 Token map loaded into WebSocket: {n} symbols",
                symbolToToken.Count);
        }

        public async Task ResubscribeAsync()
        {
            if (!_isConnected) return;
            _logger.LogInformation("📡 Re-subscribing all tokens...");
            await SubscribeAllAsync();
        }

        // ── Connect ───────────────────────────────────
        public async Task ConnectAsync()
        {
            if (string.IsNullOrEmpty(_jwtToken) ||
                string.IsNullOrEmpty(_feedToken))
            {
                _logger.LogWarning("⚠️ WS: missing credentials");
                return;
            }

            try
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _ws?.Dispose();
                _ws = new ClientWebSocket();

                // ✅ CORRECT HEADERS per official Python SDK:
                // Authorization = raw JWT (NO "Bearer" prefix)
                // "Bearer" prefix invalidates the REST token
                _ws.Options.SetRequestHeader("Authorization", _jwtToken);
                _ws.Options.SetRequestHeader("x-api-key",     _apiKey);
                _ws.Options.SetRequestHeader("x-client-code", _clientId);
                _ws.Options.SetRequestHeader("x-feed-token",  _feedToken);

                _logger.LogInformation("🔌 Connecting Angel One WebSocket...");

                await _ws.ConnectAsync(new Uri(WsUrl), _cts.Token);

                _isConnected  = true;
                _reconnecting = false;

                _logger.LogInformation("✅ Angel One WebSocket connected");
                OnConnected?.Invoke();

                await SubscribeAllAsync();

                _ = Task.Run(ReceiveLoopAsync, _cts.Token);
                _ = Task.Run(HeartbeatLoopAsync, _cts.Token);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _logger.LogError(
                    "❌ WebSocket connect failed: {msg}", ex.Message);
                _ = Task.Run(() => ReconnectAsync());
            }
        }

        private async Task SubscribeAllAsync()
        {
            List<string> nseList, bseList;
            lock (_lock)
            {
                nseList = _nseTokens.Take(800).ToList();
                bseList = _bseTokens.Take(200).ToList();
            }

            if (nseList.Any())
            {
                _logger.LogInformation(
                    "📡 Subscribing {n} NSE tokens...", nseList.Count);
                await SubscribeBatchAsync(nseList, NSE_CM);
            }

            if (bseList.Any())
            {
                _logger.LogInformation(
                    "📡 Subscribing {n} BSE tokens...", bseList.Count);
                await SubscribeBatchAsync(bseList, BSE_CM);
            }

            _logger.LogInformation(
                "✅ Subscribed: NSE={nse} BSE={bse} tokens",
                nseList.Count, bseList.Count);
        }

        private async Task SubscribeBatchAsync(
            List<string> tokens, int exchangeType)
        {
            const int batchSize = 50;
            for (int i = 0; i < tokens.Count; i += batchSize)
            {
                var batch   = tokens.Skip(i).Take(batchSize).ToList();
                var request = new
                {
                    correlationID = $"sub_{exchangeType}_{i}",
                    action        = SUBSCRIBE_ACTION,
                    @params       = new
                    {
                        mode      = QUOTE, // mode 2 = LTP + OHLC + Volume
                        tokenList = new[]
                        {
                            new { exchangeType, tokens = batch }
                        }
                    }
                };
                await SendTextAsync(JsonSerializer.Serialize(request));
                await Task.Delay(50, _cts.Token);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[4096];
            try
            {
                while (_isConnected &&
                       !_cts.Token.IsCancellationRequested)
                {
                    var result = await _ws!.ReceiveAsync(
                        new ArraySegment<byte>(buffer), _cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("⚠️ WebSocket closed by server");
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary)
                        ParseBinaryData(buffer.Take(result.Count).ToArray());
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (text.Trim() != "pong")
                            _logger.LogDebug("WS text: {t}", text);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ WS receive error: {msg}", ex.Message);
            }
            finally
            {
                _isConnected = false;
                OnDisconnected?.Invoke("Receive loop ended");
                _ = Task.Run(() => ReconnectAsync());
            }
        }

        // ── Binary parser per official SDK byte layout ─
        // _parse_binary_data() in smartWebSocketV2.py
        private void ParseBinaryData(byte[] data)
        {
            try
            {
                if (data.Length < 51) return;

                byte mode         = data[0];  // subscription_mode
                byte exchangeType = data[1];  // exchange_type

                // bytes 2-27: token ASCII null-terminated (25 bytes)
                var tokenStr = "";
                for (int i = 2; i < 27 && i < data.Length; i++)
                {
                    if (data[i] == 0) break;
                    tokenStr += (char)data[i];
                }
                tokenStr = tokenStr.Trim();
                if (string.IsNullOrEmpty(tokenStr)) return;

                // bytes 43-51: LTP int64 little-endian / 100
                long   ltpRaw = BitConverter.ToInt64(data, 43);
                double ltp    = ltpRaw / 100.0;
                if (ltp <= 0) return;

                string symbol;
                lock (_lock)
                {
                    if (!_tokenToSymbol.TryGetValue(tokenStr, out symbol!))
                        symbol = tokenStr;
                }

                double open = 0, high = 0, low = 0, close = 0;
                long   vol  = 0;

                // QUOTE (2) or SNAP_QUOTE (3): extra fields
                if ((mode == QUOTE || mode == SNAP_QUOTE) &&
                    data.Length >= 123)
                {
                    vol   = BitConverter.ToInt64(data,  67);        // volume
                    open  = BitConverter.ToInt64(data,  91) / 100.0; // open
                    high  = BitConverter.ToInt64(data,  99) / 100.0; // high
                    low   = BitConverter.ToInt64(data, 107) / 100.0; // low
                    close = BitConverter.ToInt64(data, 115) / 100.0; // prev close
                }

                var tick = new LiveTick
                {
                    Symbol        = symbol,
                    Token         = tokenStr,
                    LTP           = ltp,
                    Open          = open  > 0 ? open  : ltp,
                    High          = high  > 0 ? high  : ltp,
                    Low           = low   > 0 ? low   : ltp,
                    Close         = close > 0 ? close : ltp,
                    Volume        = vol,
                    ChangePercent = close > 0
                        ? Math.Round((ltp - close) / close * 100, 2) : 0,
                    LastUpdated   = DateTime.Now
                };

                lock (_lock) { _ticks[symbol] = tick; }
                OnTickUpdate?.Invoke(symbol, tick);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("⚠️ Binary parse error: {msg}", ex.Message);
            }
        }

        // Send "ping" every 10s per official SDK
        private async Task HeartbeatLoopAsync()
        {
            try
            {
                while (_isConnected &&
                       !_cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(HeartBeatInterval * 1000, _cts.Token);
                    if (_isConnected)
                        await SendTextAsync(HeartBeatMessage);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ Heartbeat error: {m}", ex.Message);
            }
        }

        // Pause retries outside market hours
        private async Task ReconnectAsync()
        {
            if (_reconnecting || _disposed) return;
            _reconnecting = true;

            int[] delays = { 5, 10, 20, 40, 60, 60, 60 };
            int   idx    = 0;

            while (!_disposed && !_isConnected)
            {
                var now      = DateTime.UtcNow.AddHours(5).AddMinutes(30);
                bool weekday = now.DayOfWeek != DayOfWeek.Saturday
                            && now.DayOfWeek != DayOfWeek.Sunday;
                bool mktTime = now.TimeOfDay >= new TimeSpan(9, 0, 0)
                            && now.TimeOfDay <= new TimeSpan(15, 35, 0);

                if (!weekday || !mktTime)
                {
                    _logger.LogInformation(
                        "⏸️ WS retry paused — market closed. Auto-retry at 9:00 AM IST.");
                    await Task.Delay(TimeSpan.FromMinutes(10));
                    continue;
                }

                int wait = delays[Math.Min(idx++, delays.Length - 1)];
                _logger.LogInformation(
                    "🔄 WebSocket reconnecting in {s}s...", wait);
                await Task.Delay(wait * 1000);

                if (!_disposed)
                    await ConnectAsync();
            }
            _reconnecting = false;
        }

        private async Task SendTextAsync(string message)
        {
            try
            {
                if (_ws?.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(message);
                await _ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ WS send error: {m}", ex.Message);
            }
        }

        public LiveTick? GetTick(string symbol)
        {
            lock (_lock)
            {
                return _ticks.TryGetValue(symbol, out var t) ? t : null;
            }
        }

        public List<LiveTick> GetAllTicks()
        {
            lock (_lock) { return _ticks.Values.ToList(); }
        }

        public async Task DisconnectAsync()
        {
            _isConnected = false;
            _cts.Cancel();
            try
            {
                if (_ws?.State == WebSocketState.Open)
                    await _ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Shutdown", CancellationToken.None);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _ws?.Dispose();
            _cts.Dispose();
        }
    }

    public class LiveTick
    {
        public string   Symbol        { get; set; } = "";
        public string   Token         { get; set; } = "";
        public double   LTP           { get; set; }
        public double   Open          { get; set; }
        public double   High          { get; set; }
        public double   Low           { get; set; }
        public double   Close         { get; set; }
        public long     Volume        { get; set; }
        public double   ChangePercent { get; set; }
        public DateTime LastUpdated   { get; set; } = DateTime.Now;
    }
}

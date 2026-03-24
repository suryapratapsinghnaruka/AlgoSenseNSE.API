using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlgoSenseNSE.API.Models;

namespace AlgoSenseNSE.API.Services
{
    /// <summary>
    /// Angel One SmartAPI WebSocket v2
    ///
    /// Streams live tick data for ALL subscribed tokens simultaneously.
    /// No rate limits. No polling. One connection handles everything.
    ///
    /// Docs: https://smartapi.angelbroking.com/docs/WebSocket2
    ///
    /// Flow:
    ///   1. AngelOneService.Login() → saves jwtToken + feedToken
    ///   2. This service connects with those credentials
    ///   3. Subscribe all NSE/BSE tokens in batches of 50
    ///   4. Exchange pushes ticks → OnPriceUpdate fires
    ///   5. StockScreenerService reads live prices → filters candidates
    /// </summary>
    public class AngelOneWebSocketService : IDisposable
    {
        private readonly ILogger<AngelOneWebSocketService> _logger;
        private readonly IConfiguration _config;

        private ClientWebSocket? _ws;
        private CancellationTokenSource _cts = new();
        private bool _isConnected = false;
        private bool _disposed = false;
        private bool _reconnecting = false;

        // Auth credentials
        private string _jwtToken = "";
        private string _feedToken = "";
        private string _clientId = "";
        private string _apiKey = "";

        // Token management
        private readonly HashSet<string> _nseTokens = new();
        private readonly HashSet<string> _bseTokens = new();

        // Live price store — token → price data
        private readonly Dictionary<string, LiveTick> _ticks = new();
        private readonly Dictionary<string, string> _tokenToSymbol = new();
        private readonly Dictionary<string, string> _symbolToToken = new();
        private readonly object _lock = new();

        // Events
        public event Action<string, LiveTick>? OnTickUpdate;
        public event Action? OnConnected;
        public event Action<string>? OnDisconnected;

        // Angel One WebSocket URL
        private const string WsUrl =
            "wss://smartapisocket.angelbroking.com/smart-stream";

        // Exchange type codes
        private const int NSE_CM = 1; // NSE Cash Market
        private const int BSE_CM = 3; // BSE Cash Market

        public bool IsConnected => _isConnected;
        public int TickCount => _ticks.Count;

        public AngelOneWebSocketService(
            ILogger<AngelOneWebSocketService> logger,
            IConfiguration config)
        {
            _logger = logger;
            _config = config;
            _apiKey = config["AngelOne:ApiKey"] ?? "";
            _clientId = config["AngelOne:ClientId"] ?? "";
        }

        // ── Set credentials after login ───────────────
        public void SetCredentials(
            string jwtToken,
            string feedToken,
            string clientId = "")
        {
            _jwtToken = jwtToken;
            _feedToken = feedToken;
            if (!string.IsNullOrEmpty(clientId))
                _clientId = clientId;

            _logger.LogInformation(
                "✅ WebSocket credentials set");
        }

        // ── Register symbol ↔ token mapping ───────────
        public void RegisterTokens(
            Dictionary<string, string> symbolToToken,
            bool isNse = true)
        {
            lock (_lock)
            {
                foreach (var kv in symbolToToken)
                {
                    _symbolToToken[kv.Key] = kv.Value;
                    _tokenToSymbol[kv.Value] = kv.Key;

                    if (isNse) _nseTokens.Add(kv.Value);
                    else _bseTokens.Add(kv.Value);
                }
            }
            _logger.LogInformation(
                "📋 Registered {n} tokens for WebSocket",
                symbolToToken.Count);
        }

        // ── Connect ───────────────────────────────────
        public async Task ConnectAsync()
        {
            if (string.IsNullOrEmpty(_jwtToken) ||
                string.IsNullOrEmpty(_feedToken))
            {
                _logger.LogWarning(
                    "⚠️ WebSocket: credentials not set");
                return;
            }

            try
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _ws?.Dispose();
                _ws = new ClientWebSocket();

                // Angel One auth headers
                _ws.Options.SetRequestHeader(
                    "Authorization", $"Bearer {_jwtToken}");
                _ws.Options.SetRequestHeader(
                    "x-api-key", _apiKey);
                _ws.Options.SetRequestHeader(
                    "x-client-code", _clientId);
                _ws.Options.SetRequestHeader(
                    "x-feed-token", _feedToken);

                _logger.LogInformation(
                    "🔌 Connecting Angel One WebSocket...");

                await _ws.ConnectAsync(
                    new Uri(WsUrl), _cts.Token);

                _isConnected = true;
                _reconnecting = false;

                _logger.LogInformation(
                    "✅ Angel One WebSocket connected");

                OnConnected?.Invoke();

                // Subscribe all registered tokens
                await SubscribeAllAsync();

                // Start receive + heartbeat loops
                _ = Task.Run(ReceiveLoopAsync);
                _ = Task.Run(HeartbeatLoopAsync);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _logger.LogError(
                    "❌ WebSocket connect failed: {msg}", ex.Message);
                _ = Task.Run(() => ReconnectAsync());
            }
        }

        // ── Subscribe all tokens ──────────────────────
        private async Task SubscribeAllAsync()
        {
            List<string> nseList, bseList;
            lock (_lock)
            {
                nseList = _nseTokens.ToList();
                bseList = _bseTokens.ToList();
            }

            if (nseList.Any())
                await SubscribeBatchAsync(nseList, NSE_CM);

            if (bseList.Any())
                await SubscribeBatchAsync(bseList, BSE_CM);

            _logger.LogInformation(
                "✅ Subscribed: NSE={nse} BSE={bse} tokens",
                nseList.Count, bseList.Count);
        }

        // ── Subscribe batch of tokens ─────────────────
        // Angel One accepts max 50 tokens per subscribe message
        private async Task SubscribeBatchAsync(
            List<string> tokens, int exchangeType)
        {
            const int batchSize = 50;

            for (int i = 0; i < tokens.Count; i += batchSize)
            {
                var batch = tokens
                    .Skip(i).Take(batchSize).ToList();

                var msg = new
                {
                    correlationID = Guid.NewGuid()
                        .ToString()[..8],
                    action = 1, // 1=subscribe, 0=unsubscribe
                    @params = new
                    {
                        mode = 3, // 3=SNAP_QUOTE (full data)
                        tokenList = new[]
                        {
                            new
                            {
                                exchangeType,
                                tokens = batch
                            }
                        }
                    }
                };

                await SendAsync(JsonSerializer.Serialize(msg));
                await Task.Delay(100); // small delay between batches
            }
        }

        // ── Receive loop ──────────────────────────────
        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[4096];

            try
            {
                while (_isConnected &&
                       !_cts.Token.IsCancellationRequested)
                {
                    var result = await _ws!.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        _cts.Token);

                    if (result.MessageType ==
                        WebSocketMessageType.Close)
                    {
                        _logger.LogWarning(
                            "⚠️ WebSocket closed by server");
                        break;
                    }

                    if (result.MessageType ==
                        WebSocketMessageType.Binary)
                    {
                        // Angel One sends binary data
                        var data = buffer
                            .Take(result.Count).ToArray();
                        ParseBinaryTick(data);
                    }
                    else if (result.MessageType ==
                             WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(
                            buffer, 0, result.Count);
                        ParseTextMessage(text);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "⚠️ WebSocket receive error: {msg}",
                    ex.Message);
            }
            finally
            {
                _isConnected = false;
                OnDisconnected?.Invoke("Receive loop ended");
                _ = Task.Run(() => ReconnectAsync());
            }
        }

        // ── Parse binary tick data ────────────────────
        // Angel One WebSocket v2 binary format:
        // Byte 0:    Subscribe mode
        // Byte 1-2:  Exchange type
        // Byte 3-27: Token (padded)
        // Byte 27-35: Sequence number
        // Byte 35-43: Exchange timestamp
        // Byte 43-51: LTP (last traded price × 100)
        // Byte 51-59: LTQ (last traded qty)
        // ... more fields follow for SNAP_QUOTE mode
        private void ParseBinaryTick(byte[] data)
        {
            try
            {
                if (data.Length < 51) return;

                // Extract subscription mode (byte 0)
                byte mode = data[0];

                // Extract exchange type (bytes 1-2)
                short exchangeType = BitConverter
                    .ToInt16(data, 1);

                // Extract token (bytes 3-27, null-terminated)
                var tokenBytes = data.Skip(3).Take(24).ToArray();
                var tokenStr = Encoding.ASCII
                    .GetString(tokenBytes)
                    .TrimEnd('\0').Trim();

                if (string.IsNullOrEmpty(tokenStr)) return;

                // LTP at bytes 43-51 (int64, divide by 100)
                long ltpRaw = BitConverter.ToInt64(data, 43);
                double ltp = ltpRaw / 100.0;

                if (ltp <= 0) return;

                // Get symbol for this token
                string symbol;
                lock (_lock)
                {
                    if (!_tokenToSymbol.TryGetValue(
                        tokenStr, out symbol!))
                        symbol = tokenStr; // use token as fallback
                }

                // For SNAP_QUOTE mode (3) — more fields available
                var tick = new LiveTick
                {
                    Symbol = symbol,
                    Token = tokenStr,
                    LTP = ltp,
                    ExchangeType = exchangeType,
                    LastUpdated = DateTime.Now
                };

                if (data.Length >= 67)
                {
                    // Additional fields in SNAP_QUOTE
                    long open = BitConverter.ToInt64(data, 51);
                    long high = BitConverter.ToInt64(data, 59);
                    long low = BitConverter.ToInt64(data, 67);

                    if (data.Length >= 83)
                    {
                        long close = BitConverter.ToInt64(data, 75);
                        long vol = BitConverter.ToInt64(data, 83);

                        tick.Open = open / 100.0;
                        tick.High = high / 100.0;
                        tick.Low = low / 100.0;
                        tick.Close = close / 100.0;
                        tick.Volume = (long)(vol);

                        if (tick.Close > 0)
                            tick.ChangePercent =
                                ((ltp - tick.Close) / tick.Close) * 100;
                    }
                }

                // Store tick
                lock (_lock)
                {
                    _ticks[symbol] = tick;
                }

                // Fire event
                OnTickUpdate?.Invoke(symbol, tick);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Tick parse error: {msg}", ex.Message);
            }
        }

        // ── Parse text messages (errors/acks) ─────────
        private void ParseTextMessage(string text)
        {
            try
            {
                var json = JsonDocument.Parse(text);
                var root = json.RootElement;

                if (root.TryGetProperty("type", out var type))
                {
                    var typeStr = type.GetString();
                    if (typeStr == "error")
                    {
                        _logger.LogWarning(
                            "⚠️ WebSocket error: {msg}", text);
                    }
                    else if (typeStr == "pong")
                    {
                        // Heartbeat acknowledged
                    }
                    else
                    {
                        _logger.LogDebug(
                            "WS message: {msg}", text);
                    }
                }
            }
            catch { }
        }

        // ── Heartbeat loop ────────────────────────────
        private async Task HeartbeatLoopAsync()
        {
            while (_isConnected &&
                   !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(30), _cts.Token);
                    if (_isConnected)
                        await SendAsync(
                            JsonSerializer.Serialize(
                                new { action = "ping" }));
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        // ── Reconnect with backoff ────────────────────
        private async Task ReconnectAsync()
        {
            if (_reconnecting || _disposed) return;
            _reconnecting = true;

            int delay = 5;
            while (!_disposed && !_isConnected)
            {
                _logger.LogInformation(
                    "🔄 WebSocket reconnecting in {d}s...", delay);
                await Task.Delay(
                    TimeSpan.FromSeconds(delay));

                try { await ConnectAsync(); }
                catch { }

                delay = Math.Min(delay * 2, 60); // max 60s
            }
        }

        // ── Send message ──────────────────────────────
        private async Task SendAsync(string message)
        {
            if (_ws?.State != WebSocketState.Open) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await _ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "WS send error: {msg}", ex.Message);
            }
        }

        // ── Public getters ────────────────────────────
        public LiveTick? GetTick(string symbol)
        {
            lock (_lock)
            {
                return _ticks.GetValueOrDefault(symbol);
            }
        }

        public Dictionary<string, LiveTick> GetAllTicks()
        {
            lock (_lock)
            {
                return new Dictionary<string, LiveTick>(_ticks);
            }
        }

        public LivePrice? GetLivePrice(string symbol)
        {
            var tick = GetTick(symbol);
            if (tick == null) return null;

            return new LivePrice
            {
                Symbol = tick.Symbol,
                LTP = tick.LTP,
                Open = tick.Open,
                High = tick.High,
                Low = tick.Low,
                Change = tick.LTP - tick.Close,
                ChangePercent = tick.ChangePercent,
                Volume = tick.Volume
            };
        }

        // ── Disconnect ────────────────────────────────
        public async Task DisconnectAsync()
        {
            _isConnected = false;
            _cts.Cancel();

            if (_ws?.State == WebSocketState.Open)
            {
                try
                {
                    await _ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Disconnecting",
                        CancellationToken.None);
                }
                catch { }
            }
            _logger.LogInformation(
                "🔌 WebSocket disconnected");
        }

        public void Dispose()
        {
            _disposed = true;
            _isConnected = false;
            _cts.Cancel();
            _ws?.Dispose();
            _cts.Dispose();
        }
    }

    // ── Live Tick Model ───────────────────────────────
    public class LiveTick
    {
        public string Symbol { get; set; } = "";
        public string Token { get; set; } = "";
        public double LTP { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; } // prev close
        public double ChangePercent { get; set; }
        public long Volume { get; set; }
        public short ExchangeType { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
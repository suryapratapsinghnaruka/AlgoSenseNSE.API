using System.Text;
using Newtonsoft.Json;

namespace AlgoSenseNSE.API.Services
{
    public class TelegramService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<TelegramService> _logger;
        private readonly HttpClient _http;

        private readonly Dictionary<string, DateTime> _lastAlertTime = new();
        private const int MinMinutesBetweenAlerts = 15;

        public TelegramService(
            IConfiguration config,
            ILogger<TelegramService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _logger = logger;
            _http = httpClientFactory.CreateClient("Telegram");
        }

        // ── Core send ─────────────────────────────────
        public async Task SendMessageAsync(string message)
        {
            try
            {
                var botToken = _config["Telegram:BotToken"];
                var chatId = _config["Telegram:ChatId"];

                if (string.IsNullOrEmpty(botToken) ||
                    string.IsNullOrEmpty(chatId))
                {
                    _logger.LogWarning("⚠️ Telegram not configured");
                    return;
                }

                var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
                var payload = new
                {
                    chat_id = chatId,
                    text = message,
                    parse_mode = "HTML"
                };

                var content = new StringContent(
                    JsonConvert.SerializeObject(payload),
                    Encoding.UTF8, "application/json");

                var response = await _http.PostAsync(url, content);

                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("📱 Telegram alert sent");
                else
                {
                    var err = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("⚠️ Telegram failed: {e}", err);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Telegram error");
            }
        }

        // ── BUY signal alert ──────────────────────────
        // Calibrated for ₹1000-₹2000 capital
        public async Task SendBuyAlertAsync(
            string symbol,
            double ltp,
            double target,
            double stopLoss,
            double confidence,
            string reason,
            int quantity,
            double capitalNeeded,
            string timeHorizon = "Exit by 14:30")
        {
            // Spam guard
            if (_lastAlertTime.TryGetValue(symbol, out var last) &&
                (DateTime.Now - last).TotalMinutes < MinMinutesBetweenAlerts)
                return;

            double potentialProfit = (target - ltp) * quantity;
            double potentialLoss = (ltp - stopLoss) * quantity;
            double rr = potentialLoss > 0
                ? potentialProfit / potentialLoss : 0;
            double pctMove = ltp > 0
                ? ((target - ltp) / ltp) * 100 : 0;
            double slPct = ltp > 0
                ? ((ltp - stopLoss) / ltp) * 100 : 0;

            // Brokerage estimate (Angel One flat ₹20 per order)
            double brokerage = 40; // buy + sell
            double netProfit = potentialProfit - brokerage;

            var msg = $@"🚨 <b>BUY SIGNAL — {symbol}</b>
⏰ {DateTime.Now:HH:mm} IST

💰 <b>Entry Price:</b> ₹{ltp:F2}
🎯 <b>Target:</b> ₹{target:F2} (+{pctMove:F1}%)
🛑 <b>Stop Loss:</b> ₹{stopLoss:F2} (-{slPct:F1}%)
⚖️ <b>Risk:Reward:</b> 1:{rr:F1}

📦 <b>Buy:</b> {quantity} shares
💼 <b>Capital needed:</b> ₹{capitalNeeded:F0}
✅ <b>Gross profit:</b> ₹{potentialProfit:F0}
🏦 <b>Brokerage:</b> ~₹{brokerage:F0}
💵 <b>Net profit:</b> ₹{netProfit:F0}
❌ <b>Max loss:</b> ₹{potentialLoss:F0}

🤖 <b>AI Confidence:</b> {confidence:F0}%
⏰ <b>Exit by:</b> {timeHorizon}

📊 <b>Why this signal:</b>
{reason}

👆 <b>Action:</b> Open Angel One app
→ Search <b>{symbol}</b>
→ Buy <b>{quantity} shares</b> at market price
→ Set SL at ₹{stopLoss:F2}";

            await SendMessageAsync(msg);
            _lastAlertTime[symbol] = DateTime.Now;
        }

        // ── Exit reminder ─────────────────────────────
        public async Task SendExitAlertAsync(
            string symbol,
            double buyPrice,
            double currentPrice,
            string reason)
        {
            double pnl = currentPrice - buyPrice;
            double pnlPct = buyPrice > 0 ? (pnl / buyPrice) * 100 : 0;
            string emoji = pnl >= 0 ? "✅" : "❌";

            var msg = $@"{emoji} <b>EXIT ALERT — {symbol}</b>

📊 Bought at: ₹{buyPrice:F2}
📊 Exit at:   ₹{currentPrice:F2}
💰 P&L:       ₹{pnl:F2} ({pnlPct:F1}%)

⚠️ Reason: {reason}

👆 Open Angel One → Exit {symbol} NOW";

            await SendMessageAsync(msg);
        }

        // ── Market open summary ───────────────────────
        public async Task SendMarketOpenSummaryAsync(
            double niftyLtp,
            double niftyChange,
            string marketBias,
            List<string> topPicks)
        {
            string biasEmoji = marketBias == "BULLISH" ? "📈"
                             : marketBias == "BEARISH" ? "📉" : "↔️";

            var picksText = topPicks.Any()
                ? string.Join("\n", topPicks
                    .Select((p, i) => $"  {i + 1}. {p}"))
                : "  Scanning market...";

            var msg = $@"🌅 <b>MARKET OPEN — AlgoSense</b>
{DateTime.Now:dd MMM yyyy} | {DateTime.Now:HH:mm} IST

📊 <b>Nifty 50:</b> {niftyLtp:N0} ({(niftyChange >= 0 ? "+" : "")}{niftyChange:F2}%)
{biasEmoji} <b>Market Bias:</b> {marketBias}

🎯 <b>Today's Watchlist:</b>
{picksText}

⚠️ <b>Rules for today:</b>
  • Max {_config.GetValue<int>("Trading:MaxAlertsPerDay", 3)} signals
  • Capital: ₹{_config.GetValue<double>("Trading:Capital", 1500):N0}
  • Exit ALL by 3:10 PM

🤖 First signal after 9:20 AM...";

            await SendMessageAsync(msg);
        }

        // ── Daily P&L summary ─────────────────────────
        public async Task SendDailySummaryAsync(
            double totalPnl,
            int tradesSignalled,
            string bestSignal)
        {
            string emoji = totalPnl >= 0 ? "🟢" : "🔴";
            string status = totalPnl >= 0 ? "PROFIT DAY ✅" : "LOSS DAY ❌";

            var msg = $@"{emoji} <b>DAILY SUMMARY — {DateTime.Now:dd MMM}</b>

📊 <b>Result:</b> {status}
💰 <b>Net P&L:</b> ₹{totalPnl:F0}
📡 <b>Signals sent:</b> {tradesSignalled}
⭐ <b>Best signal:</b> {bestSignal}

Tomorrow's scan: 8:45 AM IST
🤖 AlgoSense — See you tomorrow!";

            await SendMessageAsync(msg);
        }

        // ── Trading halted alert ──────────────────────
        public async Task SendTradingHaltedAsync(
            string reason, double dailyLoss)
        {
            await SendMessageAsync(
                $"🛑 <b>TRADING HALTED</b>\n\n" +
                $"Reason: {reason}\n" +
                $"Daily loss so far: ₹{dailyLoss:F0}\n\n" +
                $"No more signals today.\n" +
                $"Capital is protected. Resume tomorrow.");
        }

        // ── Test alert ────────────────────────────────
        public async Task SendTestAlertAsync()
        {
            var capital = _config.GetValue<double>("Trading:Capital", 1500);
            await SendMessageAsync(
                "✅ <b>AlgoSense Connected!</b>\n\n" +
                $"📱 Telegram alerts working!\n" +
                $"💰 Capital configured: ₹{capital:N0}\n" +
                $"🕐 Time: {DateTime.Now:HH:mm:ss} IST\n\n" +
                "You will receive:\n" +
                "• Market open summary at 9:15 AM\n" +
                "• BUY signals during 9:20 AM–2:30 PM\n" +
                "• Exit reminder at 3:10 PM\n" +
                "• Daily summary at 3:35 PM\n\n" +
                "🤖 AlgoSense is ready!");
        }
    }
}
using System.Text;
using Newtonsoft.Json;

namespace AlgoSenseNSE.API.Services
{
    public class TelegramService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<TelegramService> _logger;
        private readonly HttpClient _http;

        public TelegramService(
            IConfiguration config,
            ILogger<TelegramService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _logger = logger;
            _http   = httpClientFactory.CreateClient("Telegram");
        }

        // ── Core send ─────────────────────────────────
        public async Task SendMessageAsync(string message)
        {
            try
            {
                var botToken = _config["Telegram:BotToken"];
                var chatId   = _config["Telegram:ChatId"];

                if (string.IsNullOrEmpty(botToken) ||
                    string.IsNullOrEmpty(chatId))
                {
                    _logger.LogWarning("⚠️ Telegram not configured");
                    return;
                }

                var url     = $"https://api.telegram.org/bot{botToken}/sendMessage";
                var payload = new
                {
                    chat_id    = chatId,
                    text       = message,
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

        // ── BUY signal alert v2 ───────────────────────
        // Now includes regime, actual R:R, ATR context
        public async Task SendBuyAlertAsync(
            string symbol,
            double ltp,
            double target,
            double stopLoss,
            double confidence,
            string reason,
            int    quantity,
            double capitalNeeded,
            string timeHorizon  = "Exit by 14:30",
            string regime       = "TREND",
            double riskReward   = 0)
        {
            double potentialProfit = (target   - ltp) * quantity;
            double potentialLoss   = (ltp - stopLoss) * quantity;
            double rr = riskReward > 0 ? riskReward
                : (potentialLoss > 0 ? potentialProfit / potentialLoss : 0);

            double pctMove = ltp > 0
                ? ((target   - ltp) / ltp) * 100 : 0;
            double slPct   = ltp > 0
                ? ((ltp - stopLoss) / ltp) * 100 : 0;

            double brokerage  = 40;
            double netProfit  = potentialProfit - brokerage;

            string regimeEmoji = regime switch
            {
                "TREND"      => "📈 TREND day",
                "TREND_DOWN" => "📉 TREND (bearish)",
                "RANGE"      => "↔️ RANGE day",
                "PANIC"      => "🚨 PANIC",
                _            => "📊 MARKET"
            };

            var msg = $@"🚨 <b>BUY SIGNAL — {symbol}</b>
⏰ {DateTime.Now:HH:mm} IST | {regimeEmoji}

💰 <b>Entry:</b>    ₹{ltp:F2}
🎯 <b>Target:</b>   ₹{target:F2} (+{pctMove:F1}%)
🛑 <b>Stop Loss:</b> ₹{stopLoss:F2} (-{slPct:F1}%)
⚖️ <b>R:R:</b>      1:{rr:F1} ✅

📦 <b>Qty:</b>       {quantity} shares
💼 <b>Capital:</b>   ₹{capitalNeeded:F0}
✅ <b>Gross profit:</b> ₹{potentialProfit:F0}
🏦 <b>Brokerage:</b> ~₹{brokerage:F0}
💵 <b>Net profit:</b>  ₹{netProfit:F0}
❌ <b>Max loss:</b>    ₹{potentialLoss:F0}

🤖 <b>AI Confidence:</b> {confidence:F0}%
⏰ <b>Exit by:</b> {timeHorizon}

📊 <b>Why:</b>
{reason}

👆 <b>Action:</b> Open Angel One
→ Search <b>{symbol}</b>
→ Buy <b>{quantity} shares</b> at market
→ Set SL at ₹{stopLoss:F2}";

            await SendMessageAsync(msg);
        }

        // ── Exit reminder ─────────────────────────────
        public async Task SendExitAlertAsync(
            string symbol,
            double buyPrice,
            double currentPrice,
            string reason)
        {
            double pnl    = currentPrice - buyPrice;
            double pnlPct = buyPrice > 0 ? (pnl / buyPrice) * 100 : 0;
            string emoji  = pnl >= 0 ? "✅" : "❌";

            await SendMessageAsync(
                $"{emoji} <b>EXIT ALERT — {symbol}</b>\n\n" +
                $"📊 Bought: ₹{buyPrice:F2}\n" +
                $"📊 Exit:   ₹{currentPrice:F2}\n" +
                $"💰 P&L:    ₹{pnl:F2} ({pnlPct:F1}%)\n\n" +
                $"⚠️ {reason}\n\n" +
                $"👆 Open Angel One → Exit {symbol} NOW");
        }

        // ── Market open summary ───────────────────────
        public async Task SendMarketOpenSummaryAsync(
            double niftyLtp,
            double niftyChange,
            string marketBias,
            List<string> topPicks)
        {
            string biasEmoji = marketBias.Contains("BULLISH") ? "📈"
                             : marketBias.Contains("BEARISH") ? "📉" : "↔️";

            var picksText = topPicks.Any()
                ? string.Join("\n", topPicks
                    .Select((p, i) => $"  {i + 1}. {p}"))
                : "  Scanning market...";

            var msg =
                $"🌅 <b>MARKET OPEN — AlgoSense v2</b>\n" +
                $"{DateTime.Now:dd MMM yyyy} | {DateTime.Now:HH:mm} IST\n\n" +
                $"📊 <b>Nifty 50:</b> {niftyLtp:N0}\n" +
                $"{biasEmoji} <b>Bias:</b> {marketBias}\n\n" +
                $"🎯 <b>Today's Watchlist:</b>\n{picksText}\n\n" +
                $"⚠️ <b>Rules:</b>\n" +
                $"  • Min R:R = 1:2 (v2 upgrade)\n" +
                $"  • 30-min cooldown per stock\n" +
                $"  • ATR-based position sizing\n" +
                $"  • Max {_config.GetValue<int>("Trading:MaxAlertsPerDay", 3)} signals\n\n" +
                $"🤖 First signal after 9:20 AM...";

            await SendMessageAsync(msg);
        }

        // ── Daily summary ─────────────────────────────
        public async Task SendDailySummaryAsync(
            double totalPnl,
            int    tradesSignalled,
            string bestSignal)
        {
            string emoji  = totalPnl >= 0 ? "🟢" : "🔴";
            string status = totalPnl >= 0 ? "PROFIT DAY ✅" : "LOSS DAY ❌";

            await SendMessageAsync(
                $"{emoji} <b>DAILY SUMMARY — {DateTime.Now:dd MMM}</b>\n\n" +
                $"📊 <b>Result:</b> {status}\n" +
                $"💰 <b>Net P&L:</b> ₹{totalPnl:F0}\n" +
                $"📡 <b>Signals:</b> {tradesSignalled}\n" +
                $"⭐ <b>Best:</b> {bestSignal}\n\n" +
                $"Tomorrow: 8:45 AM pre-market scan\n" +
                $"🤖 AlgoSense v2 — See you tomorrow!");
        }

        // ── Trading halted ────────────────────────────
        public async Task SendTradingHaltedAsync(
            string reason, double dailyLoss)
        {
            await SendMessageAsync(
                $"🛑 <b>TRADING HALTED</b>\n\n" +
                $"Reason: {reason}\n" +
                $"Daily loss: ₹{dailyLoss:F0}\n\n" +
                $"No more signals today.\n" +
                $"Capital protected. Resume tomorrow.");
        }

        // ── Test alert ────────────────────────────────
        public async Task SendTestAlertAsync()
        {
            var capital = _config.GetValue<double>(
                "Trading:Capital", 1500);
            await SendMessageAsync(
                "✅ <b>AlgoSense v2 Connected!</b>\n\n" +
                $"💰 Capital: ₹{capital:N0}\n" +
                $"🕐 Time: {DateTime.Now:HH:mm:ss} IST\n\n" +
                "✅ Upgrades active:\n" +
                "  • Min R:R = 1:2\n" +
                "  • ATR position sizing\n" +
                "  • 30-min stock cooldown\n" +
                "  • Regime detection\n\n" +
                "🤖 AlgoSense v2 is ready!");
        }
    }
}

using AlgoSenseNSE.API.Hubs;
using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json.Linq;

namespace AlgoSenseNSE.API.BackgroundServices
{
    public class PriceFeedWorker : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<PriceFeedWorker> _logger;

        public PriceFeedWorker(
            IServiceProvider services,
            ILogger<PriceFeedWorker> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await Task.Delay(5000, ct);

            using var scope = _services.CreateScope();
            var sp = scope.ServiceProvider;

            var scanner = sp.GetRequiredService<MarketScanService>();
            var angelOne = sp.GetRequiredService<AngelOneService>();
            var hub = sp.GetRequiredService<IHubContext<MarketHub>>();
            var config = sp.GetRequiredService<IConfiguration>();
            var alertEngine = sp.GetRequiredService<AlertEngine>();
            var riskManager = sp.GetRequiredService<RiskManager>();
            var telegram = sp.GetRequiredService<TelegramService>();
            var wsService = sp.GetRequiredService<AngelOneWebSocketService>();
            var screener = sp.GetRequiredService<StockScreenerService>();
            var signalTracker = sp.GetRequiredService<SignalTrackingService>();
            var nse = sp.GetRequiredService<NseIndiaService>();

            var capital = config.GetValue<double>("Trading:Capital", 1500);

            await scanner.InitializeAsync();

            // Connect WebSocket after login
            if (!string.IsNullOrEmpty(angelOne.GetFeedToken()))
            {
                _logger.LogInformation("🔌 Starting Angel One WebSocket...");
                await wsService.ConnectAsync();
                _logger.LogInformation("✅ WebSocket started");
            }
            else
            {
                _logger.LogWarning("⚠️ feedToken missing — REST fallback");
            }

            await telegram.SendMessageAsync(
                "🤖 <b>AlgoSense Started</b>\n\n" +
                $"Capital: ₹{capital:N0}\n" +
                $"Stocks tracked: {scanner.GetTier1Symbols().Count}\n" +
                $"WebSocket: {(wsService.IsConnected ? "✅ Live" : "⚠️ REST fallback")}\n" +
                $"Time: {GetIST():HH:mm} IST\n\n" +
                "Signals will start after 9:20 AM.");

            int loopCount = 0;
            const int indexEvery = 12;
            const int signalsEvery = 60;
            const int screenEvery = 6;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var ist = GetIST();
                    var isMarket = IsMarketOpen();
                    loopCount++;

                    // Daily reset at 9:15 AM
                    if (ist.Hour == 9 && ist.Minute == 15 && ist.Second < 10)
                        riskManager.ResetDaily();

                    // WebSocket screener every 30s
                    if (wsService.IsConnected && loopCount % screenEvery == 0)
                    {
                        var candidates = screener.Screen();
                        if (candidates.Any())
                            await hub.Clients.All.SendAsync("ScreenerUpdated",
                                candidates.Take(20).Select(s => new
                                {
                                    s.Symbol,
                                    s.Price,
                                    s.ChangePercent,
                                    s.Volume,
                                    s.MomentumScore
                                }), ct);
                    }

                    if (isMarket)
                    {
                        var tier1 = scanner.GetTier1Symbols();

                        // Live prices
                        var toUpdate = loopCount % 6 == 0
                            ? tier1.Take(50).ToList()
                            : tier1.Take(20).ToList();
                        await scanner.UpdateLivePricesAsync(toUpdate);

                        var prices = scanner.GetLivePrices();
                        if (prices.Any())
                            await hub.Clients.All.SendAsync("PricesUpdated", prices, ct);

                        // Indices every 60s
                        if (loopCount % indexEvery == 0)
                        {
                            var nifty = await angelOne.GetLiveIndexAsync("NSE", "26000");
                            var sensex = await angelOne.GetLiveIndexAsync("BSE", "1");
                            if (nifty != null || sensex != null)
                                await hub.Clients.All.SendAsync("IndexUpdated",
                                    new { nifty, sensex }, ct);
                        }

                        // Signals + AI + Alerts every 5 min
                        if (loopCount % signalsEvery == 0)
                        {
                            _logger.LogInformation("🔄 Refreshing intraday signals...");
                            await scanner.RefreshIntradaySignalsAsync(tier1.Take(30).ToList());
                            await scanner.RefreshRecommendationsAsync();

                            var recs = scanner.GetRecommendations();
                            await hub.Clients.All.SendAsync("RecommendationsUpdated", recs, ct);

                            // Nifty LTP for alert context
                            double niftyLtp = 0;
                            try
                            {
                                var nd = await angelOne.GetLiveIndexAsync("NSE", "26000");
                                if (nd != null)
                                    niftyLtp = JObject.FromObject(nd)["ltp"]?.Value<double>() ?? 0;
                            }
                            catch { }

                            // Send alerts after 9:20 AM
                            if (ist.Hour > 9 || (ist.Hour == 9 && ist.Minute >= 20))
                            {
                                await alertEngine.ProcessSignalsAsync(recs, niftyLtp, 0, capital);

                                // Record BUY signals to SQLite
                                var mktCtx = await nse.GetMarketContextAsync();
                                foreach (var rec in recs.Where(r =>
                                    r.AiAnalysis?.Recommendation == "BUY"))
                                {
                                    try { await signalTracker.RecordSignalAsync(rec, mktCtx); }
                                    catch { }
                                }

                                // Fill outcomes for signals 2+ hrs old
                                await signalTracker.FillOutcomesAsync(async (sym) =>
                                {
                                    var lp = scanner.GetLivePrice(sym);
                                    return lp?.LTP ?? 0;
                                });
                            }

                            await hub.Clients.All.SendAsync("AlertsUpdated",
                                alertEngine.GetAlertHistory(), ct);

                            // Entry triggers for locked picks
                            var triggers = scanner.GetEntryTriggers();
                            if (triggers.Any())
                                _logger.LogInformation("🎯 Entry triggers: {syms}",
                                    string.Join(", ", triggers.Select(t => t.Symbol)));
                        }
                    }
                    else
                    {
                        // Outside market hours
                        if (loopCount % indexEvery == 0)
                        {
                            var nifty = await angelOne.GetLiveIndexAsync("NSE", "26000");
                            var sensex = await angelOne.GetLiveIndexAsync("BSE", "1");
                            if (nifty != null || sensex != null)
                                await hub.Clients.All.SendAsync("IndexUpdated",
                                    new { nifty, sensex }, ct);
                        }

                        // Pre-market scan at 8:45 AM
                        if (ist.Hour == 8 && ist.Minute == 45 && loopCount % indexEvery == 0)
                        {
                            _logger.LogInformation("⏰ Pre-market scan...");
                            await scanner.RunFullDailyScanAsync();
                        }

                        // Re-login every 5 hours
                        if (loopCount % 3600 == 0)
                        {
                            await angelOne.LoginAsync();
                            if (wsService.IsConnected)
                                await wsService.ConnectAsync();
                        }
                    }

                    var interval = config.GetValue<int>(
                        "AppSettings:PriceFetchIntervalSeconds", 5);
                    await Task.Delay(interval * 1000, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ PriceFeedWorker error");
                    await Task.Delay(10000, ct);
                }
            }

            await wsService.DisconnectAsync();
        }

        private bool IsMarketOpen()
        {
            var now = GetIST();
            if (now.DayOfWeek == DayOfWeek.Saturday ||
                now.DayOfWeek == DayOfWeek.Sunday) return false;
            return now.TimeOfDay >= new TimeSpan(9, 15, 0) &&
                   now.TimeOfDay <= new TimeSpan(15, 30, 0);
        }

        private DateTime GetIST()
        {
            try
            {
                return TimeZoneInfo.ConvertTime(DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"));
            }
            catch { return DateTime.UtcNow.AddHours(5).AddMinutes(30); }
        }
    }
}
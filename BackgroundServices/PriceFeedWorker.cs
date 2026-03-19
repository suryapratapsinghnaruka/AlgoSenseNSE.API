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

        public PriceFeedWorker(IServiceProvider services,
            ILogger<PriceFeedWorker> logger)
        { _services = services; _logger = logger; }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await Task.Delay(5000, ct);

            using var scope = _services.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<MarketScanService>();
            var angelOne = scope.ServiceProvider.GetRequiredService<AngelOneService>();
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<MarketHub>>();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var alertEngine = scope.ServiceProvider.GetRequiredService<AlertEngine>();
            var riskManager = scope.ServiceProvider.GetRequiredService<RiskManager>();
            var telegram = scope.ServiceProvider.GetRequiredService<TelegramService>();

            var priceSeconds = config.GetValue<int>("AppSettings:PriceFetchIntervalSeconds", 5);
            var capital = config.GetValue<double>("Trading:Capital", 3000);

            await scanner.InitializeAsync();

            await telegram.SendMessageAsync(
                "🤖 <b>AlgoSense Started</b>\n\n" +
                $"Capital: ₹{capital:N0}\n" +
                $"Stocks tracked: {scanner.GetTier1Symbols().Count}\n" +
                $"Time: {GetIST():HH:mm} IST\n\n" +
                "Signals will start after 9:20 AM.");

            int loopCount = 0;
            const int indexEvery = 12;
            const int signalsEvery = 60;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var ist = GetIST();
                    var isMarket = IsMarketOpen();
                    loopCount++;

                    // Reset daily risk at 9:15 AM
                    if (ist.Hour == 9 && ist.Minute == 15 && ist.Second < 10)
                        riskManager.ResetDaily();

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

                            // Get Nifty price for alert context
                            double niftyLtp = 0;
                            try
                            {
                                var nd = await angelOne.GetLiveIndexAsync("NSE", "26000");
                                if (nd != null)
                                    niftyLtp = JObject.FromObject(nd)["ltp"]?.Value<double>() ?? 0;
                            }
                            catch { }

                            // Send Telegram alerts after 9:20 AM only
                            if (ist.Hour > 9 || (ist.Hour == 9 && ist.Minute >= 20))
                                await alertEngine.ProcessSignalsAsync(recs, niftyLtp, 0, capital);

                            await hub.Clients.All.SendAsync("AlertsUpdated",
                                alertEngine.GetAlertHistory(), ct);
                        }
                    }
                    else
                    {
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
                    }

                    await Task.Delay(priceSeconds * 1000, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ PriceFeedWorker error");
                    await Task.Delay(10000, ct);
                }
            }
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
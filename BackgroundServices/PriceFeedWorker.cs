using AlgoSenseNSE.API.Hubs;
using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.SignalR;

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
            var scanner = scope.ServiceProvider.GetRequiredService<MarketScanService>();
            var angelOne = scope.ServiceProvider.GetRequiredService<AngelOneService>();
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<MarketHub>>();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

            var priceIntervalSeconds = config.GetValue<int>("AppSettings:PriceFetchIntervalSeconds", 5);

            // ── Initialize once ───────────────────────
            await scanner.InitializeAsync();

            // Counters for less-frequent updates
            int loopCount = 0;
            // Index fetch: every 60 seconds (not every 5!)
            const int indexEveryNLoops = 12;  // 12 × 5s = 60s
            // Recommendations: every 5 minutes
            const int recsEveryNLoops = 60;  // 60 × 5s = 300s

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var isMarketHours = IsMarketOpen();
                    loopCount++;

                    if (isMarketHours)
                    {
                        var tier1 = scanner.GetTier1Symbols();

                        // ── Live prices: every 5 seconds ─────
                        // Sequential (not parallel!) to respect rate limit
                        // Only update first 20 for the frequent cycle
                        // Full 100 updated every 30s
                        var toUpdate = loopCount % 6 == 0
                            ? tier1.Take(100).ToList()
                            : tier1.Take(20).ToList();

                        await scanner.UpdateLivePricesAsync(toUpdate);

                        var prices = scanner.GetLivePrices();
                        if (prices.Any())
                            await hub.Clients.All.SendAsync("PricesUpdated", prices, ct);

                        // ── Index update: every 60 seconds ───
                        if (loopCount % indexEveryNLoops == 0)
                        {
                            var nifty = await angelOne.GetLiveIndexAsync("NSE", "26000");
                            var sensex = await angelOne.GetLiveIndexAsync("BSE", "1");

                            if (nifty != null || sensex != null)
                                await hub.Clients.All.SendAsync("IndexUpdated",
                                    new { nifty, sensex }, ct);
                        }

                        // ── Technical + recommendations: every 5 min ─
                        if (loopCount % recsEveryNLoops == 0)
                        {
                            await scanner.RefreshTechnicalAnalysisAsync(
                                tier1.Take(20).ToList());
                            await scanner.RefreshRecommendationsAsync();

                            var recs = scanner.GetRecommendations();
                            await hub.Clients.All.SendAsync("RecommendationsUpdated", recs, ct);
                        }
                    }
                    else
                    {
                        // Outside market hours — just push index once a minute
                        if (loopCount % indexEveryNLoops == 0)
                        {
                            var nifty = await angelOne.GetLiveIndexAsync("NSE", "26000");
                            var sensex = await angelOne.GetLiveIndexAsync("BSE", "1");
                            if (nifty != null || sensex != null)
                                await hub.Clients.All.SendAsync("IndexUpdated",
                                    new { nifty, sensex }, ct);
                        }
                    }

                    await Task.Delay(priceIntervalSeconds * 1000, ct);
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
            try
            {
                var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow,
                    TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"));

                if (now.DayOfWeek == DayOfWeek.Saturday ||
                    now.DayOfWeek == DayOfWeek.Sunday)
                    return false;

                var open = new TimeSpan(9, 15, 0);
                var close = new TimeSpan(15, 30, 0);
                return now.TimeOfDay >= open && now.TimeOfDay <= close;
            }
            catch
            {
                // Fallback if timezone not found (Linux/Docker)
                var now = DateTime.UtcNow.AddHours(5).AddMinutes(30);
                if (now.DayOfWeek == DayOfWeek.Saturday ||
                    now.DayOfWeek == DayOfWeek.Sunday) return false;
                return now.Hour >= 9 && now.Hour < 16;
            }
        }
    }
}
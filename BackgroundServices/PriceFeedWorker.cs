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

        protected override async Task ExecuteAsync(
            CancellationToken ct)
        {
            // Wait for app to fully start
            await Task.Delay(5000, ct);

            using var scope = _services.CreateScope();
            var scanner = scope.ServiceProvider
                .GetRequiredService<MarketScanService>();
            var hub = scope.ServiceProvider
                .GetRequiredService<IHubContext<MarketHub>>();
            var config = scope.ServiceProvider
                .GetRequiredService<IConfiguration>();

            var intervalSeconds = config.GetValue<int>(
                "AppSettings:PriceFetchIntervalSeconds", 5);

            // Initialize on startup
            await scanner.InitializeAsync();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var isMarketHours = IsMarketOpen();

                    if (isMarketHours)
                    {
                        // Update Tier 1 prices every 5 seconds
                        var tier1 = scanner.GetTier1Symbols();
                        await scanner.UpdateLivePricesAsync(tier1);

                        // Push live prices to all connected browsers
                        var prices = scanner.GetLivePrices();
                        await hub.Clients.All.SendAsync(
                            "PricesUpdated", prices, ct);

                        // Update recommendations every 5 minutes
                        if (DateTime.Now.Second < intervalSeconds)
                        {
                            await scanner.RefreshTechnicalAnalysisAsync(
                                tier1.Take(20).ToList());
                            await scanner.RefreshRecommendationsAsync();

                            var recs = scanner.GetRecommendations();
                            await hub.Clients.All.SendAsync(
                                "RecommendationsUpdated", recs, ct);
                        }
                    }

                    await Task.Delay(
                        intervalSeconds * 1000, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "❌ PriceFeedWorker error");
                    await Task.Delay(10000, ct);
                }
            }
        }

        // ── Check NSE market hours ───────────────────
        private bool IsMarketOpen()
        {
            var now = TimeZoneInfo.ConvertTime(
                DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById(
                    "India Standard Time"));

            // Monday to Friday only
            if (now.DayOfWeek == DayOfWeek.Saturday ||
                now.DayOfWeek == DayOfWeek.Sunday)
                return false;

            var open = new TimeSpan(9, 15, 0);
            var close = new TimeSpan(15, 30, 0);
            return now.TimeOfDay >= open &&
                   now.TimeOfDay <= close;
        }
    }
}
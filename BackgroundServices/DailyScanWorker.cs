using AlgoSenseNSE.API.Services;

namespace AlgoSenseNSE.API.BackgroundServices
{
    public class DailyScanWorker : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<DailyScanWorker> _logger;

        public DailyScanWorker(
            IServiceProvider services,
            ILogger<DailyScanWorker> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(
            CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;

                    // Run full scan at 6:00 AM every day
                    var nextRun = DateTime.Today.AddHours(6);
                    if (now > nextRun)
                        nextRun = nextRun.AddDays(1);

                    var delay = nextRun - now;
                    _logger.LogInformation(
                        "⏰ Next full scan at {time} " +
                        "(in {hours:F1} hours)",
                        nextRun, delay.TotalHours);

                    await Task.Delay(delay, ct);

                    // Run full market scan
                    using var scope = _services.CreateScope();
                    var scanner = scope.ServiceProvider
                        .GetRequiredService<MarketScanService>();

                    _logger.LogInformation(
                        "🌅 6AM daily scan starting...");
                    await scanner.RunFullDailyScanAsync();
                    _logger.LogInformation(
                        "✅ 6AM daily scan complete");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "❌ DailyScanWorker error");
                    await Task.Delay(
                        TimeSpan.FromHours(1), ct);
                }
            }
        }
    }
}
using AlgoSenseNSE.API.Hubs;
using AlgoSenseNSE.API.Services;
using Microsoft.AspNetCore.SignalR;

namespace AlgoSenseNSE.API.BackgroundServices
{
    public class NewsPipelineWorker : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<NewsPipelineWorker> _logger;

        public NewsPipelineWorker(
            IServiceProvider services,
            ILogger<NewsPipelineWorker> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(
            CancellationToken ct)
        {
            await Task.Delay(8000, ct);

            using var scope = _services.CreateScope();
            var newsService = scope.ServiceProvider
                .GetRequiredService<NewsService>();
            var hub = scope.ServiceProvider
                .GetRequiredService<IHubContext<MarketHub>>();
            var config = scope.ServiceProvider
                .GetRequiredService<IConfiguration>();

            var intervalSeconds = config.GetValue<int>(
                "AppSettings:NewsFetchIntervalSeconds", 30);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Fetch latest news
                    var news = await newsService.FetchAllNewsAsync();

                    // Push to all connected browsers
                    await hub.Clients.All.SendAsync(
                        "NewsUpdated",
                        news.Take(50).ToList(), ct);

                    _logger.LogInformation(
                        "📰 News pipeline: {count} items pushed",
                        news.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "❌ NewsPipelineWorker error");
                }

                await Task.Delay(intervalSeconds * 1000, ct);
            }
        }
    }
}
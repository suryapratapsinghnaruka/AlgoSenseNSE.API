using AlgoSenseNSE.API.BackgroundServices;
using AlgoSenseNSE.API.Hubs;
using AlgoSenseNSE.API.Services;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

Directory.CreateDirectory("/app/data");
// ── Controllers ──────────────────────────────────
builder.Services.AddControllers();

// ── SignalR ───────────────────────────────────────
builder.Services.AddSignalR();

// ── Memory Cache ──────────────────────────────────
builder.Services.AddMemoryCache();

// ── CORS — allow browser access ───────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// ── HTTP Clients ──────────────────────────────────
builder.Services.AddHttpClient("AngelOne", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

builder.Services.AddHttpClient("Screener", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.BaseAddress = new Uri("https://www.screener.in");
});

builder.Services.AddHttpClient("News", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
}).ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler
    {
        UseCookies = true,
        CookieContainer = new System.Net.CookieContainer(),
        AllowAutoRedirect = true
    });

builder.Services.AddHttpClient("Claude", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.BaseAddress =
        new Uri("https://api.anthropic.com");
});

// ── Register Services (Singleton = shared state) ──
builder.Services.AddSingleton<AngelOneService>();
builder.Services.AddSingleton<TechnicalAnalysisService>();
builder.Services.AddSingleton<FundamentalService>();
builder.Services.AddSingleton<NewsService>();
builder.Services.AddSingleton<ScoringEngine>();
builder.Services.AddSingleton<ClaudeAiService>();
builder.Services.AddSingleton<MarketScanService>();

// ── Background Workers ────────────────────────────
builder.Services.AddHostedService<PriceFeedWorker>();
builder.Services.AddHostedService<NewsPipelineWorker>();
builder.Services.AddHostedService<DailyScanWorker>();

// ── Serve static files (index.html) ──────────────
builder.Services.AddDirectoryBrowser();

var app = builder.Build();

// ── Middleware ────────────────────────────────────
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapControllers();

// ── SignalR Hub ───────────────────────────────────
app.MapHub<MarketHub>("/hubs/market");

// ── Redirect root to index.html ───────────────────
app.MapGet("/", () =>
    Results.Redirect("/index.html"));

app.Run();

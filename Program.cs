using AlgoSenseNSE.API.BackgroundServices;
using AlgoSenseNSE.API.Hubs;
using AlgoSenseNSE.API.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// ── Directories ───────────────────────────────────
foreach (var dir in new[] { "/app/data", "/app/logs", "logs", "data" })
    try { Directory.CreateDirectory(dir); } catch { }

// ── Logging ───────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(new FileLoggerProvider());
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

// ── Services ──────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// HTTP Clients
builder.Services.AddHttpClient("AngelOne", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient("Screener", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.BaseAddress = new Uri("https://www.screener.in");
});
builder.Services.AddHttpClient("News", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseCookies = true,
    CookieContainer = new System.Net.CookieContainer(),
    AllowAutoRedirect = true
});
builder.Services.AddHttpClient("Claude", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.BaseAddress = new Uri("https://api.anthropic.com");
});
builder.Services.AddHttpClient("Telegram", c =>
    c.Timeout = TimeSpan.FromSeconds(10));

// ── Core Services ─────────────────────────────────
// WebSocket first — AngelOne needs it during login
builder.Services.AddSingleton<AngelOneWebSocketService>();
builder.Services.AddSingleton<StockScreenerService>();
builder.Services.AddSingleton<AngelOneService>(sp =>
{
    var svc = new AngelOneService(
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<AngelOneService>>(),
        sp.GetRequiredService<IHttpClientFactory>());

    // Wire WebSocket service into AngelOne
    var ws = sp.GetRequiredService<AngelOneWebSocketService>();
    svc.SetWebSocketService(ws);
    return svc;
});

builder.Services.AddSingleton<TechnicalAnalysisService>();
builder.Services.AddSingleton<FundamentalService>();
builder.Services.AddSingleton<NewsService>();
builder.Services.AddSingleton<ScoringEngine>();
builder.Services.AddSingleton<NseIndiaService>();
builder.Services.AddSingleton<ClaudeAiService>();
builder.Services.AddSingleton<SignalTrackingService>();
builder.Services.AddSingleton<MarketScanService>();
builder.Services.AddSingleton<TelegramService>();
builder.Services.AddSingleton<RejectedTradeTracker>();
builder.Services.AddSingleton<NseIndiaService>();
builder.Services.AddSingleton<RiskManager>(sp =>
    new RiskManager(
        sp.GetRequiredService<ILogger<RiskManager>>(),
        capital: builder.Configuration
            .GetValue<double>("Trading:Capital", 1500)));
builder.Services.AddSingleton<AlertEngine>();

// ── Background Workers ────────────────────────────
builder.Services.AddHostedService<PriceFeedWorker>();
builder.Services.AddHostedService<NewsPipelineWorker>();
builder.Services.AddHostedService<DailyScanWorker>();

builder.Services.AddDirectoryBrowser();

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapControllers();
app.MapHub<MarketHub>("/hubs/market");

app.MapGet("/", () => Results.Redirect("/index.html"));

// ── Test endpoints ────────────────────────────────
app.MapGet("/api/test-telegram", async (TelegramService tg) =>
{
    await tg.SendTestAlertAsync();
    return Results.Ok(new { message = "✅ Check Telegram!" });
});

app.MapGet("/api/alerts", (AlertEngine ae) =>
    Results.Ok(ae.GetAlertHistory()));

app.MapGet("/api/risk", (RiskManager rm) =>
    Results.Ok(rm.GetSummary()));

// ── WebSocket status ──────────────────────────────
app.MapGet("/api/websocket/status",
    (AngelOneWebSocketService ws, StockScreenerService screener) =>
    Results.Ok(new
    {
        connected = ws.IsConnected,
        tickCount = ws.TickCount,
        candidates = screener.CandidateCount,
        topGainers = screener.GetTopGainers(5)
            .Select(s => new { s.Symbol, s.Price, s.ChangePercent })
    }));

// ── Signal tracking endpoints ─────────────────────
app.MapGet("/api/signals/stats",
    async (SignalTrackingService st) =>
    Results.Ok(await st.GetAccuracyStatsAsync(30)));

app.MapGet("/api/signals/export",
    async (SignalTrackingService st) =>
    Results.Ok(await st.ExportTrainingDataAsync()));

// ── Log viewer endpoints ──────────────────────────
app.MapGet("/api/logs", () =>
{
    var file = GetLogFile();
    if (file == null)
        return Results.Ok(new { message = "No log file yet" });
    var lines = File.ReadAllLines(file).TakeLast(200).ToArray();
    return Results.Ok(new { logFile = file, lines = lines.Length, logs = lines });
});

app.MapGet("/api/logs/signals", () =>
{
    var file = GetLogFile();
    if (file == null)
        return Results.Ok(new { message = "No log file yet" });
    var keywords = new[]
    {
        "OHLCV", "Score=", "RSI=", "VWAP",
        "BUY", "AVOID", "Picks:", "Alert",
        "Telegram", "AI ", "Nifty", "WebSocket",
        "Screener", "Market context", "VIX",
        "ERROR", "❌", "✅", "⚠️"
    };
    var lines = File.ReadAllLines(file)
        .Where(l => keywords.Any(k =>
            l.Contains(k, StringComparison.OrdinalIgnoreCase)))
        .TakeLast(300).ToArray();
    return Results.Ok(new { logFile = file, filtered = lines.Length, logs = lines });
});

app.MapGet("/api/logs/errors", () =>
{
    var file = GetLogFile();
    if (file == null)
        return Results.Ok(new { message = "No log file yet" });
    var lines = File.ReadAllLines(file)
        .Where(l => l.Contains("ERROR") || l.Contains("❌") ||
                    l.Contains("Exception"))
        .TakeLast(100).ToArray();
    return Results.Ok(new { logFile = file, errors = lines.Length, logs = lines });
});

app.MapGet("/api/logs/ohlcv", () =>
{
    var file = GetLogFile();
    if (file == null)
        return Results.Ok(new { message = "No log file yet" });
    var lines = File.ReadAllLines(file)
        .Where(l => l.Contains("OHLCV") || l.Contains("Score=") ||
                    l.Contains("RSI=") || l.Contains("Supertrend") ||
                    l.Contains("signals updated"))
        .TakeLast(200).ToArray();
    return Results.Ok(new { logFile = file, logs = lines });
});

app.Run();

static string? GetLogFile()
{
    var today = DateTime.Now.ToString("yyyy-MM-dd");
    var candidates = new[]
    {
        $"/app/logs/algosense-{today}.log",
        $"logs/algosense-{today}.log",
        "/app/logs/algosense.log",
        "logs/algosense.log"
    };
    return candidates.FirstOrDefault(File.Exists);
}

// ── File Logger ───────────────────────────────────
public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDir;
    public FileLoggerProvider()
    {
        _logDir = Directory.Exists("/app/logs") ? "/app/logs" : "logs";
        Directory.CreateDirectory(_logDir);
    }
    public ILogger CreateLogger(string category) =>
        new FileLogger(category, _logDir);
    public void Dispose() { }
}

public class FileLogger : ILogger
{
    private readonly string _category;
    private readonly string _logDir;
    private static readonly object _lock = new();

    private static readonly string[] _allowedPrefixes =
    {
        "AlgoSenseNSE",
        "Microsoft.Hosting.Lifetime"
    };

    public FileLogger(string category, string logDir)
    {
        _category = category;
        _logDir = logDir;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel level)
    {
        if (level >= LogLevel.Error) return true;
        return _allowedPrefixes.Any(p => _category.StartsWith(p));
    }

    public void Log<TState>(
        LogLevel level, EventId eventId, TState state,
        Exception? ex, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;

        var lvl = level switch
        {
            LogLevel.Error => "ERROR",
            LogLevel.Warning => "WARN ",
            _ => "INFO "
        };

        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var cat = _category.Split('.').Last();
        var msg = formatter(state, ex);
        var line = $"[{ts}] [{lvl}] [{cat}] {msg}";
        if (ex != null) line += $"\n  >> {ex.Message}";

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var dayFile = Path.Combine(_logDir, $"algosense-{today}.log");
        var mainFile = Path.Combine(_logDir, "algosense.log");

        lock (_lock)
        {
            try
            {
                File.AppendAllText(dayFile, line + "\n");
                File.AppendAllText(mainFile, line + "\n");
                var all = File.ReadAllLines(mainFile);
                if (all.Length > 6000)
                    File.WriteAllLines(mainFile, all.TakeLast(5000));
            }
            catch { }
        }
    }
}

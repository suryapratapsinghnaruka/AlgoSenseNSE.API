using AlgoSenseNSE.API.BackgroundServices;
using AlgoSenseNSE.API.Hubs;
using AlgoSenseNSE.API.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// ── Port ──────────────────────────────────────────
//var port = Environment.GetEnvironmentVariable("PORT") ?? "5150";
//builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ── Directories ───────────────────────────────────
foreach (var dir in new[] { "/app/data", "/app/logs", "logs", "data" })
    try { Directory.CreateDirectory(dir); } catch { }

// ── Logging — console + file ──────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(new FileLoggerProvider());
builder.Logging.SetMinimumLevel(LogLevel.Information);
// Suppress noisy HTTP client logs
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFramework", LogLevel.Warning);

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

// App Services
builder.Services.AddSingleton<AngelOneService>();
builder.Services.AddSingleton<TechnicalAnalysisService>();
builder.Services.AddSingleton<FundamentalService>();
builder.Services.AddSingleton<NewsService>();
builder.Services.AddSingleton<ScoringEngine>();
builder.Services.AddSingleton<ClaudeAiService>();
builder.Services.AddSingleton<MarketScanService>();
builder.Services.AddSingleton<TelegramService>();
builder.Services.AddSingleton<NseIndiaService>();
builder.Services.AddSingleton<RiskManager>(sp =>
    new RiskManager(
        sp.GetRequiredService<ILogger<RiskManager>>(),
        capital: builder.Configuration.GetValue<double>("Trading:Capital", 1500)));
builder.Services.AddSingleton<AlertEngine>();

// Background Workers
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

// ── Standard endpoints ────────────────────────────
app.MapGet("/", () => Results.Redirect("/index.html"));

app.MapGet("/api/test-telegram", async (TelegramService tg) =>
{
    await tg.SendTestAlertAsync();
    return Results.Ok(new { message = "✅ Check Telegram!" });
});

app.MapGet("/api/alerts", (AlertEngine ae) =>
    Results.Ok(ae.GetAlertHistory()));

app.MapGet("/api/risk", (RiskManager rm) =>
    Results.Ok(rm.GetSummary()));

// ── LOG VIEWER — so I can confirm everything works ─
// GET /api/logs          → last 200 lines of today's log
// GET /api/logs/signals  → only signal/indicator lines
// GET /api/logs/errors   → only errors
app.MapGet("/api/logs", () =>
{
    var file = GetLogFile();
    if (file == null)
        return Results.Ok(new { message = "No log file yet" });

    var lines = File.ReadAllLines(file).TakeLast(200).ToArray();
    return Results.Ok(new
    {
        logFile = file,
        lines = lines.Length,
        logs = lines
    });
});

app.MapGet("/api/logs/signals", () =>
{
    var file = GetLogFile();
    if (file == null)
        return Results.Ok(new { message = "No log file yet" });

    // Key trading keywords to look for
    var keywords = new[]
    {
        "Intraday score", "Technical score", "Score=",
        "RSI=", "VWAP=", "Supertrend",
        "OHLCV", "candle", "5-min",
        "BUY", "SELL", "AVOID",
        "Picks:", "signal", "Alert",
        "Telegram", "Claude AI", "AI ",
        "Nifty", "Sensex",
        "ERROR", "Error", "failed", "❌", "✅", "⚠️"
    };

    var lines = File.ReadAllLines(file)
        .Where(l => keywords.Any(k =>
            l.Contains(k, StringComparison.OrdinalIgnoreCase)))
        .TakeLast(300)
        .ToArray();

    return Results.Ok(new
    {
        logFile = file,
        filtered = lines.Length,
        logs = lines
    });
});

app.MapGet("/api/logs/errors", () =>
{
    var file = GetLogFile();
    if (file == null)
        return Results.Ok(new { message = "No log file yet" });

    var lines = File.ReadAllLines(file)
        .Where(l => l.Contains("ERROR") ||
                    l.Contains("Error") ||
                    l.Contains("failed") ||
                    l.Contains("❌") ||
                    l.Contains("Exception"))
        .TakeLast(100)
        .ToArray();

    return Results.Ok(new
    {
        logFile = file,
        errors = lines.Length,
        logs = lines
    });
});

app.MapGet("/api/logs/ohlcv", () =>
{
    var file = GetLogFile();
    if (file == null)
        return Results.Ok(new { message = "No log file yet" });

    // Specifically for confirming OHLCV + indicators work
    var lines = File.ReadAllLines(file)
        .Where(l => l.Contains("OHLCV") ||
                    l.Contains("candle") ||
                    l.Contains("5-min") ||
                    l.Contains("Intraday score") ||
                    l.Contains("Score=") ||
                    l.Contains("RSI=") ||
                    l.Contains("Supertrend") ||
                    l.Contains("VWAP") ||
                    l.Contains("MACD") ||
                    l.Contains("Refreshing") ||
                    l.Contains("signals updated"))
        .TakeLast(200)
        .ToArray();

    return Results.Ok(new
    {
        logFile = file,
        description = "OHLCV + Indicator logs",
        lines = lines.Length,
        logs = lines
    });
});

app.Run();

// ── Helper: find today's log file ─────────────────
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

// ══════════════════════════════════════════════════
// FILE LOGGER
// Saves all app logs to /app/logs/algosense-DATE.log
// ══════════════════════════════════════════════════
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

    // Only save our app logs + critical errors
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
            LogLevel.Information => "INFO ",
            _ => "DEBUG"
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
                // Write to today's file
                File.AppendAllText(dayFile, line + "\n");

                // Write to rolling main file
                File.AppendAllText(mainFile, line + "\n");

                // Trim main file if too large (keep last 5000 lines)
                var all = File.ReadAllLines(mainFile);
                if (all.Length > 6000)
                    File.WriteAllLines(mainFile, all.TakeLast(5000));
            }
            catch { }
        }
    }
}
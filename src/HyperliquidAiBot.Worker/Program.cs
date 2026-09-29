using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Services;
using HyperliquidAiBot.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Enable systemd support when running on Linux systems
if (OperatingSystem.IsLinux())
{
    builder.Host.UseSystemd();
}

// Bind BotSettings from appsettings.json and environment variables
builder.Services.Configure<BotSettings>(builder.Configuration.GetSection(BotSettings.SectionName));
var botSettings = builder.Configuration.GetSection(BotSettings.SectionName).Get<BotSettings>() ?? new BotSettings();

// Register In-Memory State Repository for Visual Dashboard
builder.Services.AddSingleton<IBotStateService, BotStateService>();

// Register EIP-712 Signer
builder.Services.AddSingleton<IHyperliquidSigner>(sp =>
{
    var pk = botSettings.Hyperliquid.PrivateKey;
    if (string.IsNullOrWhiteSpace(pk))
    {
        // Ephemeral fallback key for initial startup before live keys are injected
        pk = "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    }
    return new HyperliquidSigner(pk);
});

// Register HttpClients with resilient timeout settings
builder.Services.AddHttpClient<IHyperliquidClient, HyperliquidClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHttpClient<IResearchEngine, ResearchEngine>(client =>
{
    var timeout = botSettings.Llm.TimeoutSeconds > 0
        ? botSettings.Llm.TimeoutSeconds
        : (botSettings.OpenAi.TimeoutSeconds > 0 ? botSettings.OpenAi.TimeoutSeconds : 30);
    client.Timeout = TimeSpan.FromSeconds(timeout);
});

// Register Core Domain Services
builder.Services.AddSingleton<ITechnicalAnalysisService, TechnicalAnalysisService>();
builder.Services.AddSingleton<IRiskManager, RiskManager>();

// Register Exchange Clients & Adapters
builder.Services.AddHttpClient<PaperTradingExchangeClient>();
builder.Services.AddHttpClient<BinanceFuturesExchangeClient>();
builder.Services.AddSingleton<HyperliquidExchangeAdapter>();

builder.Services.AddSingleton<IExchangeClient>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<BotSettings>>().Value;
    var active = opts.Exchange.ActiveExchange?.Trim().ToLowerInvariant() ?? "papertrading";

    return active switch
    {
        "binance" or "binancefutures" => sp.GetRequiredService<BinanceFuturesExchangeClient>(),
        "hyperliquid" => sp.GetRequiredService<HyperliquidExchangeAdapter>(),
        _ => sp.GetRequiredService<PaperTradingExchangeClient>()
    };
});

// Register Continuous Background Trading Worker
builder.Services.AddHostedService<TradingWorker>();

var app = builder.Build();

// Enable static files to serve the visual dashboard (/wwwroot/index.html)
app.UseDefaultFiles();
app.UseStaticFiles();

// Visual Dashboard API Endpoints
app.MapGet("/api/status", (IBotStateService state, IRiskManager risk, IResearchEngine research, IExchangeClient exchange, IOptions<BotSettings> options) =>
{
    var snapshot = state.GetSnapshot(risk.IsTradingFrozen, risk.DailyDrawdownPct);
    var settings = options.Value;
    var targetSymbol = !string.IsNullOrWhiteSpace(settings.Exchange.Symbol)
        ? settings.Exchange.Symbol
        : (!string.IsNullOrWhiteSpace(settings.Hyperliquid.Asset) ? settings.Hyperliquid.Asset : "BTCUSDT");

    return Results.Json(new
    {
        snapshot.IsRunning,
        snapshot.LastCycleUtc,
        snapshot.LatestContext,
        snapshot.LatestDecision,
        snapshot.LatestRisk,
        snapshot.IsTradingFrozen,
        snapshot.DailyDrawdownPct,
        snapshot.RecentCycles,
        activeProvider = research.ActiveProvider,
        activeModel = research.ActiveModel,
        activeExchange = exchange.ExchangeName,
        useTestnet = settings.Exchange.ActiveExchange.Equals("Binance", StringComparison.OrdinalIgnoreCase)
            ? settings.Binance.UseTestnet
            : settings.Hyperliquid.UseTestnet,
        dryRun = settings.Execution.DryRun,
        asset = targetSymbol
    });
});

app.MapPost("/api/trigger", (IBotStateService state) =>
{
    state.RequestImmediateCycle();
    return Results.Ok(new { message = "Evaluation cycle triggered", timestampUtc = DateTime.UtcNow });
});

// Health check endpoint for IIS / uptime monitors
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    utc = DateTime.UtcNow
}));

await app.RunAsync();

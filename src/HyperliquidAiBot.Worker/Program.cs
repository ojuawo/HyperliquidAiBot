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
    client.Timeout = TimeSpan.FromSeconds(botSettings.OpenAi.TimeoutSeconds > 0 ? botSettings.OpenAi.TimeoutSeconds : 30);
});

// Register Core Domain Services
builder.Services.AddSingleton<ITechnicalAnalysisService, TechnicalAnalysisService>();
builder.Services.AddSingleton<IRiskManager, RiskManager>();

// Register Continuous Background Trading Worker
builder.Services.AddHostedService<TradingWorker>();

var app = builder.Build();

// Enable static files to serve the visual dashboard (/wwwroot/index.html)
app.UseDefaultFiles();
app.UseStaticFiles();

// Visual Dashboard API Endpoints
app.MapGet("/api/status", (IBotStateService state, IRiskManager risk) =>
{
    return Results.Json(state.GetSnapshot(risk.IsTradingFrozen, risk.DailyDrawdownPct));
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

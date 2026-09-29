using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Services;
using HyperliquidAiBot.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .UseSystemd() // Native Linux Systemd service support
    .ConfigureAppConfiguration((hostingContext, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((hostContext, services) =>
    {
        var configuration = hostContext.Configuration;

        // Bind Options
        services.Configure<BotSettings>(configuration.GetSection(BotSettings.SectionName));
        var botSettings = configuration.GetSection(BotSettings.SectionName).Get<BotSettings>() ?? new BotSettings();

        // Register EIP-712 Signer
        services.AddSingleton<IHyperliquidSigner>(sp =>
        {
            var pk = botSettings.Hyperliquid.PrivateKey;
            if (string.IsNullOrWhiteSpace(pk))
            {
                // Fallback ephemeral key for test/dry-run startup if not yet set in environment
                pk = "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            }
            return new HyperliquidSigner(pk);
        });

        // Register HttpClients
        services.AddHttpClient<IHyperliquidClient, HyperliquidClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
        });

        services.AddHttpClient<IResearchEngine, ResearchEngine>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(botSettings.OpenAi.TimeoutSeconds > 0 ? botSettings.OpenAi.TimeoutSeconds : 30);
        });

        // Register Core Domain Services
        services.AddSingleton<ITechnicalAnalysisService, TechnicalAnalysisService>();
        services.AddSingleton<IRiskManager, RiskManager>();

        // Register Background Worker
        services.AddHostedService<TradingWorker>();
    })
    .ConfigureLogging((hostContext, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

await host.RunAsync();

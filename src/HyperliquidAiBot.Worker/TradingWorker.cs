using System.Text.Json;
using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using HyperliquidAiBot.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperliquidAiBot.Worker;

/// <summary>
/// Continuous asynchronous trading worker orchestrating universal market ingestion,
/// technical analysis, LLM research decision-making, deterministic risk validation,
/// and pluggable exchange order execution.
/// </summary>
public class TradingWorker : BackgroundService
{
    private readonly IExchangeClient _exchangeClient;
    private readonly ITechnicalAnalysisService _taService;
    private readonly IResearchEngine _researchEngine;
    private readonly IRiskManager _riskManager;
    private readonly IBotStateService _botStateService;
    private readonly BotSettings _settings;
    private readonly ILogger<TradingWorker> _logger;

    public TradingWorker(
        IExchangeClient exchangeClient,
        ITechnicalAnalysisService taService,
        IResearchEngine researchEngine,
        IRiskManager riskManager,
        IBotStateService botStateService,
        IOptions<BotSettings> settings,
        ILogger<TradingWorker> logger)
    {
        _exchangeClient = exchangeClient;
        _taService = taService;
        _researchEngine = researchEngine;
        _riskManager = riskManager;
        _botStateService = botStateService;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var targetSymbol = ResolveTargetSymbol();
        _logger.LogInformation("================================================================================");
        _logger.LogInformation("Algorithmic Trading Bot initialized. Active Exchange: {Exchange}, Target Symbol: {Symbol}",
            _exchangeClient.ExchangeName, targetSymbol);
        _logger.LogInformation("================================================================================");

        // Initialize exchange connection and metadata
        await _exchangeClient.InitializeAsync(stoppingToken);

        var pollInterval = _settings.Exchange.PollIntervalSeconds > 0
            ? _settings.Exchange.PollIntervalSeconds
            : (_settings.Hyperliquid.PollIntervalSeconds > 0 ? _settings.Hyperliquid.PollIntervalSeconds : 60);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteTradingCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in trading execution loop");
            }

            await _botStateService.WaitAsync(TimeSpan.FromSeconds(Math.Max(10, pollInterval)), stoppingToken);
        }

        _logger.LogInformation("Trading worker background execution stopped gracefully.");
    }

    private string ResolveTargetSymbol()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Exchange.Symbol))
            return _settings.Exchange.Symbol;
        if (!string.IsNullOrWhiteSpace(_settings.Hyperliquid.Asset))
            return _settings.Hyperliquid.Asset;
        return "BTCUSDT";
    }

    private string ResolveInterval()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Exchange.Interval))
            return _settings.Exchange.Interval;
        if (!string.IsNullOrWhiteSpace(_settings.Hyperliquid.CandleInterval))
            return _settings.Hyperliquid.CandleInterval;
        return "1h";
    }

    private int ResolveCandleLimit()
    {
        if (_settings.Exchange.CandleLimit > 0)
            return _settings.Exchange.CandleLimit;
        if (_settings.Hyperliquid.CandleLimit > 0)
            return _settings.Hyperliquid.CandleLimit;
        return 100;
    }

    private async Task ExecuteTradingCycleAsync(CancellationToken ct)
    {
        var targetSymbol = ResolveTargetSymbol();
        var interval = ResolveInterval();
        var candleLimit = ResolveCandleLimit();

        _logger.LogInformation("--- Starting trading cycle for {Symbol} on {Exchange} at {Time:u} ---",
            targetSymbol, _exchangeClient.ExchangeName, DateTime.UtcNow);

        // 1. Ingest Market Data
        var marketSnapshot = await _exchangeClient.GetMarketDataAsync(targetSymbol, interval, candleLimit, ct);
        if (marketSnapshot.Candles.Count == 0)
        {
            var msg = $"No historical candles returned for '{targetSymbol}'.";
            _logger.LogWarning("{Msg} Skipping cycle.", msg);
            _botStateService.RecordCycle(
                new MarketContext { Asset = targetSymbol, CurrentPrice = marketSnapshot.CurrentPrice },
                new TradeDecision { Action = TradeAction.Hold, Reasoning = msg },
                RiskEvaluation.Reject(msg, TradeAction.Hold, 0m));
            return;
        }

        // 2. Compute Technical Analysis
        var indicators = _taService.CalculateIndicators(marketSnapshot.Candles);
        _logger.LogInformation("TA computed: RSI={Rsi:F2}, MACD={Macd:F4}, Trend={Trend}, EMA9={Ema9:F2}, EMA21={Ema21:F2}",
            indicators.Rsi, indicators.Macd, indicators.TrendSignal, indicators.Ema9, indicators.Ema21);

        // 3. Ingest Portfolio & Positions
        var portfolio = await _exchangeClient.GetAccountPortfolioAsync(ct);
        var position = portfolio.GetPosition(targetSymbol);

        _logger.LogInformation("Portfolio: Equity=${Equity:F2}, AvailableMargin=${Margin:F2}, Position={Pos:F4} (UnrealizedPnL=${Pnl:F2})",
            portfolio.AccountEquity, portfolio.AvailableMargin, position?.Size ?? 0m, position?.UnrealizedPnl ?? 0m);

        // 4. Build Universal Market Context
        var marketContext = new MarketContext
        {
            Asset = targetSymbol,
            AssetIndex = 0,
            CurrentPrice = marketSnapshot.CurrentPrice,
            BestBid = marketSnapshot.BestBid,
            BestAsk = marketSnapshot.BestAsk,
            AccountEquity = portfolio.AccountEquity,
            AvailableMargin = portfolio.AvailableMargin,
            CurrentPositionSize = position?.Size ?? 0m,
            CurrentPositionEntryPrice = position?.EntryPrice ?? 0m,
            UnrealizedPnl = position?.UnrealizedPnl ?? 0m,
            Indicators = indicators,
            RecentCandles = marketSnapshot.Candles.TakeLast(10).ToList(),
            TimestampUtc = DateTime.UtcNow
        };

        // 5. Query AI Research Engine
        _logger.LogInformation("Querying AI Research Engine ({Provider} / {Model})...",
            _researchEngine.ActiveProvider, _researchEngine.ActiveModel);
        var decision = await _researchEngine.EvaluateMarketAsync(marketContext, ct);
        _logger.LogInformation("Decision: Action={Action}, Confidence={Confidence:F2}, Allocation={Alloc:P1}",
            decision.Action, decision.Confidence, decision.AllocationPct);

        // 6. Deterministic Risk Validation
        var riskResult = _riskManager.Evaluate(decision, marketContext);
        foreach (var audit in riskResult.SafetyAuditLogs)
        {
            _logger.LogInformation("[RiskAudit] {Log}", audit);
        }

        // 7. Dispatch Order If Approved
        if (riskResult.IsApproved && (riskResult.Action == TradeAction.Buy || riskResult.Action == TradeAction.Sell || riskResult.Action == TradeAction.Close))
        {
            await DispatchApprovedOrderAsync(targetSymbol, riskResult, ct);
        }
        else
        {
            _logger.LogInformation("Cycle completed with no order dispatched: Approved={Approved}, Reason={Reason}",
                riskResult.IsApproved, riskResult.RejectionReason ?? "None");
        }

        // 8. Update Heartbeat and Dashboard State
        UpdateHeartbeatFile(portfolio.AccountEquity, riskResult);
        _botStateService.RecordCycle(marketContext, decision, riskResult);
    }

    private async Task DispatchApprovedOrderAsync(string symbol, RiskEvaluation risk, CancellationToken ct)
    {
        if (_settings.Execution.DryRun)
        {
            _logger.LogWarning("[DRY-RUN] Simulated Order: Action={Action}, Size={Size:F6}, Price=${Px:F2}, SL=${SL:F2}, TP=${TP:F2}",
                risk.Action, risk.ApprovedSize, risk.EntryPrice, risk.StopLossPrice, risk.TakeProfitPrice);
            return;
        }

        var isBuy = risk.Action == TradeAction.Buy || (risk.Action == TradeAction.Close && risk.ApprovedSize < 0);
        var side = isBuy ? TradeOrderSide.Buy : TradeOrderSide.Sell;
        var reduceOnly = risk.Action == TradeAction.Close;

        var orderRequest = new TradeOrderRequest(
            Symbol: symbol,
            Side: side,
            Type: TradeOrderType.Market,
            Size: risk.ApprovedSize,
            Price: risk.EntryPrice,
            StopLossPrice: risk.StopLossPrice,
            TakeProfitPrice: risk.TakeProfitPrice,
            ReduceOnly: reduceOnly
        );

        _logger.LogInformation("Dispatching order to {Exchange}: {Side} {Size:F4} {Symbol} @ ${Px:F2}",
            _exchangeClient.ExchangeName, side, risk.ApprovedSize, symbol, risk.EntryPrice);

        var result = await _exchangeClient.PlaceOrderAsync(orderRequest, ct);
        _logger.LogInformation("Order Result: Success={Success}, OrderId={Id}, Status={Status}, ExecPx=${Px:F2}",
            result.Success, result.OrderId, result.Status, result.ExecutedPrice);
    }

    private void UpdateHeartbeatFile(decimal equity, RiskEvaluation riskResult)
    {
        try
        {
            var hbPath = _settings.Execution.HeartbeatFilePath;
            if (string.IsNullOrWhiteSpace(hbPath)) return;

            var dir = Path.GetDirectoryName(hbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = new
            {
                timestampUtc = DateTime.UtcNow,
                exchange = _exchangeClient.ExchangeName,
                equity = equity,
                isTradingFrozen = _riskManager.IsTradingFrozen,
                dailyDrawdownPct = _riskManager.DailyDrawdownPct,
                lastAction = riskResult.Action.ToString(),
                lastApproved = riskResult.IsApproved,
                lastRejectionReason = riskResult.RejectionReason
            };

            File.WriteAllText(hbPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update heartbeat file");
        }
    }
}

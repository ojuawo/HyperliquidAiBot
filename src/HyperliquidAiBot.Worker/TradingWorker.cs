using System.Text.Json;
using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using HyperliquidAiBot.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperliquidAiBot.Worker;

/// <summary>
/// Continuous asynchronous trading worker orchestrating ingestion, TA calculation,
/// LLM decision-making, deterministic risk validation, and order dispatch.
/// </summary>
public class TradingWorker : BackgroundService
{
    private readonly IHyperliquidClient _hyperliquidClient;
    private readonly ITechnicalAnalysisService _taService;
    private readonly IResearchEngine _researchEngine;
    private readonly IRiskManager _riskManager;
    private readonly BotSettings _settings;
    private readonly ILogger<TradingWorker> _logger;

    private readonly Dictionary<string, (int Index, int SzDecimals, int MaxLeverage)> _assetLookup = new(StringComparer.OrdinalIgnoreCase);

    public TradingWorker(
        IHyperliquidClient hyperliquidClient,
        ITechnicalAnalysisService taService,
        IResearchEngine researchEngine,
        IRiskManager riskManager,
        IOptions<BotSettings> settings,
        ILogger<TradingWorker> logger)
    {
        _hyperliquidClient = hyperliquidClient;
        _taService = taService;
        _researchEngine = researchEngine;
        _riskManager = riskManager;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("================================================================================");
        _logger.LogInformation("Hyperliquid AI Trading Bot starting. Target Asset: {Asset}, Testnet: {IsTestnet}",
            _settings.Hyperliquid.Asset, _settings.Hyperliquid.UseTestnet);
        _logger.LogInformation("================================================================================");

        // 1. Initialize Universe Metadata
        await InitializeUniverseMetadataAsync(stoppingToken);

        var intervalSeconds = Math.Max(10, _settings.Hyperliquid.PollIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        // Initial cycle immediately
        await ExecuteTradingCycleAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
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
        }

        _logger.LogInformation("Trading worker background execution stopped gracefully.");
    }

    private async Task InitializeUniverseMetadataAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Fetching Hyperliquid perpetual universe metadata...");
            var meta = await _hyperliquidClient.GetUniverseMetaAsync(ct);
            _assetLookup.Clear();

            for (int i = 0; i < meta.Universe.Count; i++)
            {
                var asset = meta.Universe[i];
                _assetLookup[asset.Name] = (i, asset.SzDecimals, asset.MaxLeverage);
            }

            _logger.LogInformation("Discovered {Count} perpetual assets from exchange.", _assetLookup.Count);

            if (_assetLookup.TryGetValue(_settings.Hyperliquid.Asset, out var assetInfo))
            {
                _logger.LogInformation("Resolved target asset '{Asset}': Index={Index}, SzDecimals={Decimals}, MaxLeverage={Leverage}x",
                    _settings.Hyperliquid.Asset, assetInfo.Index, assetInfo.SzDecimals, assetInfo.MaxLeverage);
            }
            else
            {
                _logger.LogWarning("Target asset '{Asset}' not found in perpetual universe! Available sample: {Sample}",
                    _settings.Hyperliquid.Asset, string.Join(", ", _assetLookup.Keys.Take(10)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize universe metadata");
        }
    }

    private async Task ExecuteTradingCycleAsync(CancellationToken ct)
    {
        var targetAsset = _settings.Hyperliquid.Asset;
        _logger.LogInformation("--- Starting trading cycle for {Asset} at {Time:u} ---", targetAsset, DateTime.UtcNow);

        // 1. Resolve Asset Metadata
        if (!_assetLookup.TryGetValue(targetAsset, out var assetMeta))
        {
            _logger.LogWarning("Asset {Asset} metadata missing, attempting refresh...", targetAsset);
            await InitializeUniverseMetadataAsync(ct);
            if (!_assetLookup.TryGetValue(targetAsset, out assetMeta))
            {
                _logger.LogError("Unable to resolve asset {Asset}. Skipping cycle.", targetAsset);
                return;
            }
        }

        // 2. Ingest Candles
        _logger.LogInformation("Ingesting {Limit} candles of timeframe {Interval}...",
            _settings.Hyperliquid.CandleLimit, _settings.Hyperliquid.CandleInterval);
        var candles = await _hyperliquidClient.GetCandleSnapshotAsync(
            targetAsset,
            _settings.Hyperliquid.CandleInterval,
            _settings.Hyperliquid.CandleLimit,
            ct);

        if (candles.Count == 0)
        {
            _logger.LogWarning("No candles retrieved for {Asset}. Skipping cycle.", targetAsset);
            return;
        }

        var latestCandle = candles.OrderBy(c => c.OpenTimeMs).Last();
        var currentPrice = latestCandle.CloseDecimal;

        // 3. Compute Technical Indicators
        var indicators = _taService.CalculateIndicators(candles);
        _logger.LogInformation("Computed TA: RSI={Rsi:F2}, MACD={Macd:F4}, Signal={Signal}, EMA9={Ema9:F2}, EMA21={Ema21:F2}",
            indicators.Rsi, indicators.Macd, indicators.TrendSignal, indicators.Ema9, indicators.Ema21);

        // 4. Ingest Account & Position State (if wallet configured)
        decimal equity = 10000m; // Default simulation value
        decimal availableMargin = 10000m;
        decimal currentPositionSize = 0m;
        decimal entryPrice = 0m;
        decimal unrealizedPnl = 0m;

        if (!string.IsNullOrWhiteSpace(_settings.Hyperliquid.WalletAddress))
        {
            try
            {
                var clearinghouse = await _hyperliquidClient.GetClearinghouseStateAsync(_settings.Hyperliquid.WalletAddress, ct);
                equity = clearinghouse.MarginSummary.AccountValueDecimal;
                availableMargin = decimal.TryParse(clearinghouse.Withdrawable, out var wd) ? wd : equity;

                var position = clearinghouse.AssetPositions.FirstOrDefault(p =>
                    string.Equals(p.Position.Coin, targetAsset, StringComparison.OrdinalIgnoreCase))?.Position;

                if (position != null)
                {
                    currentPositionSize = position.SizeDecimal;
                    entryPrice = position.EntryPriceDecimal;
                    unrealizedPnl = position.UnrealizedPnlDecimal;
                }

                _logger.LogInformation("Account State: Equity=${Equity:F2}, AvailableMargin=${Margin:F2}, OpenPos={Pos:F4} (PnL=${Pnl:F2})",
                    equity, availableMargin, currentPositionSize, unrealizedPnl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fetch live clearinghouse state. Falling back to configured defaults.");
            }
        }

        // 5. Ingest Orderbook Spread
        decimal bestBid = currentPrice;
        decimal bestAsk = currentPrice;
        try
        {
            var book = await _hyperliquidClient.GetL2BookAsync(targetAsset, ct);
            if (book != null && book.Levels.Count >= 2)
            {
                var bids = book.Levels[0];
                var asks = book.Levels[1];
                if (bids.Count > 0 && decimal.TryParse(bids[0].Px, out var b)) bestBid = b;
                if (asks.Count > 0 && decimal.TryParse(asks[0].Px, out var a)) bestAsk = a;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch L2 book. Using last candle price for bid/ask.");
        }

        // 6. Build Market Context
        var marketContext = new MarketContext
        {
            Asset = targetAsset,
            AssetIndex = assetMeta.Index,
            CurrentPrice = currentPrice,
            BestBid = bestBid,
            BestAsk = bestAsk,
            AccountEquity = equity,
            AvailableMargin = availableMargin,
            CurrentPositionSize = currentPositionSize,
            CurrentPositionEntryPrice = entryPrice,
            UnrealizedPnl = unrealizedPnl,
            Indicators = indicators,
            RecentCandles = candles.TakeLast(10).Select(c => new CandleSummary(
                c.DateTimeUtc, c.OpenDecimal, c.HighDecimal, c.LowDecimal, c.CloseDecimal, c.VolumeDecimal
            )).ToList(),
            TimestampUtc = DateTime.UtcNow
        };

        // 7. Request LLM Analysis from ResearchEngine
        _logger.LogInformation("Querying ResearchEngine for quantitative decision...");
        var decision = await _researchEngine.EvaluateMarketAsync(marketContext, ct);
        _logger.LogInformation("Decision: Action={Action}, Confidence={Confidence:F2}, Reasoning={Reasoning}",
            decision.Action, decision.Confidence, decision.Reasoning);

        // 8. Deterministic Risk Evaluation
        var riskResult = _riskManager.Evaluate(decision, marketContext);
        foreach (var log in riskResult.SafetyAuditLogs)
        {
            _logger.LogInformation("[RiskAudit] {Log}", log);
        }

        // 9. Execute Order If Approved
        if (riskResult.IsApproved && (riskResult.Action == TradeAction.Buy || riskResult.Action == TradeAction.Sell || riskResult.Action == TradeAction.Close))
        {
            await ExecuteApprovedTradeAsync(riskResult, assetMeta.Index, assetMeta.SzDecimals, ct);
        }
        else
        {
            _logger.LogInformation("No order dispatched. Status: Approved={IsApproved}, Reason={Reason}",
                riskResult.IsApproved, riskResult.RejectionReason ?? "None");
        }

        // 10. Update Heartbeat for VPS Monitoring
        UpdateHeartbeatFile(equity, riskResult);
    }

    private async Task ExecuteApprovedTradeAsync(RiskEvaluation risk, int assetIndex, int szDecimals, CancellationToken ct)
    {
        if (_settings.Execution.DryRun)
        {
            _logger.LogWarning("[DRY-RUN] Order simulated: Action={Action}, Size={Size:F6}, Px=${Px:F2}, SL=${SL:F2}, TP=${TP:F2}",
                risk.Action, risk.ApprovedSize, risk.EntryPrice, risk.StopLossPrice, risk.TakeProfitPrice);
            return;
        }

        var isBuy = risk.Action == TradeAction.Buy || (risk.Action == TradeAction.Close && risk.ApprovedSize < 0);
        var reduceOnly = risk.Action == TradeAction.Close;

        try
        {
            _logger.LogInformation("Dispatching primary market order: Action={Action}, IsBuy={IsBuy}, Size={Size:F6}, Px=${Px:F2}",
                risk.Action, isBuy, risk.ApprovedSize, risk.EntryPrice);

            // 1. Place Primary Order
            var primaryResponse = await _hyperliquidClient.PostOrderAsync(
                assetIndex: assetIndex,
                isBuy: isBuy,
                price: risk.EntryPrice,
                size: risk.ApprovedSize,
                szDecimals: szDecimals,
                reduceOnly: reduceOnly,
                tif: _settings.Hyperliquid.DefaultTif,
                trigger: null,
                cloid: null,
                ct: ct
            );

            _logger.LogInformation("Primary order executed. Status={Status}", primaryResponse.Status);

            // 2. Place Attached Stop-Loss Trigger Order if configured and not closing
            if (risk.StopLossPrice.HasValue && risk.Action != TradeAction.Close)
            {
                var slIsBuy = !isBuy; // Opposing direction to close
                var slTrigger = new TriggerOrderTypeWire(
                    TriggerPx: risk.StopLossPrice.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                    IsMarket: true,
                    Tpsl: "sl"
                );

                _logger.LogInformation("Attaching mandatory Stop-Loss trigger at ${SL:F2}...", risk.StopLossPrice.Value);

                var slResponse = await _hyperliquidClient.PostOrderAsync(
                    assetIndex: assetIndex,
                    isBuy: slIsBuy,
                    price: risk.StopLossPrice.Value,
                    size: risk.ApprovedSize,
                    szDecimals: szDecimals,
                    reduceOnly: true,
                    tif: "Gtc",
                    trigger: slTrigger,
                    cloid: null,
                    ct: ct
                );

                _logger.LogInformation("Stop-loss attached. Status={Status}", slResponse.Status);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute exchange order");
        }
    }

    private void UpdateHeartbeatFile(decimal equity, RiskEvaluation lastRisk)
    {
        try
        {
            var path = _settings.Execution.HeartbeatFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var status = new
            {
                timestampUtc = DateTime.UtcNow,
                equity,
                isTradingFrozen = _riskManager.IsTradingFrozen,
                dailyDrawdownPct = _riskManager.DailyDrawdownPct,
                lastAction = lastRisk.Action.ToString(),
                lastApproved = lastRisk.IsApproved,
                lastRejectionReason = lastRisk.RejectionReason
            };

            File.WriteAllText(path, JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not write heartbeat file");
        }
    }
}

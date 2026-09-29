using System.Collections.Concurrent;
using System.Threading.Channels;
using HyperliquidAiBot.Core.Models;

namespace HyperliquidAiBot.Core.Services;

public record BotStateSnapshot(
    bool IsRunning,
    DateTime LastCycleUtc,
    MarketContext? LatestContext,
    TradeDecision? LatestDecision,
    RiskEvaluation? LatestRisk,
    bool IsTradingFrozen,
    decimal DailyDrawdownPct,
    List<CycleRecord> RecentCycles
);

public record CycleRecord(
    DateTime TimestampUtc,
    string Asset,
    decimal Price,
    TradeAction Action,
    decimal Confidence,
    bool IsApproved,
    string? RejectionReason,
    decimal ApprovedSize,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    decimal? Rsi,
    string? TrendSignal,
    string Reasoning
);

public interface IBotStateService
{
    BotStateSnapshot GetSnapshot(bool isTradingFrozen, decimal dailyDrawdownPct);
    void RecordCycle(MarketContext context, TradeDecision decision, RiskEvaluation risk);
    void RequestImmediateCycle();
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Thread-safe in-memory state repository powering the visual dashboard.
/// </summary>
public class BotStateService : IBotStateService
{
    private MarketContext? _latestContext;
    private TradeDecision? _latestDecision;
    private RiskEvaluation? _latestRisk;
    private DateTime _lastCycleUtc = DateTime.UtcNow;
    private readonly ConcurrentQueue<CycleRecord> _recentCycles = new();
    private const int MaxCycleHistory = 50;

    private readonly Channel<bool> _triggerChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    public void RecordCycle(MarketContext context, TradeDecision decision, RiskEvaluation risk)
    {
        _latestContext = context;
        _latestDecision = decision;
        _latestRisk = risk;
        _lastCycleUtc = DateTime.UtcNow;

        var record = new CycleRecord(
            TimestampUtc: _lastCycleUtc,
            Asset: context.Asset,
            Price: context.CurrentPrice,
            Action: decision.Action,
            Confidence: decision.Confidence,
            IsApproved: risk.IsApproved,
            RejectionReason: risk.RejectionReason,
            ApprovedSize: risk.ApprovedSize,
            StopLossPrice: risk.StopLossPrice,
            TakeProfitPrice: risk.TakeProfitPrice,
            Rsi: context.Indicators.Rsi,
            TrendSignal: context.Indicators.TrendSignal,
            Reasoning: decision.Reasoning
        );

        _recentCycles.Enqueue(record);
        while (_recentCycles.Count > MaxCycleHistory && _recentCycles.TryDequeue(out _)) { }
    }

    public BotStateSnapshot GetSnapshot(bool isTradingFrozen, decimal dailyDrawdownPct)
    {
        return new BotStateSnapshot(
            IsRunning: true,
            LastCycleUtc: _lastCycleUtc,
            LatestContext: _latestContext,
            LatestDecision: _latestDecision,
            LatestRisk: _latestRisk,
            IsTradingFrozen: isTradingFrozen,
            DailyDrawdownPct: dailyDrawdownPct,
            RecentCycles: _recentCycles.Reverse().ToList()
        );
    }

    public void RequestImmediateCycle()
    {
        _triggerChannel.Writer.TryWrite(true);
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await _triggerChannel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on timeout or service shutdown
        }
    }
}

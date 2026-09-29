using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperliquidAiBot.Core.Services;

public interface IRiskManager
{
    RiskEvaluation Evaluate(TradeDecision decision, MarketContext context);
    void UpdateDailyBalance(decimal currentEquity);
    bool IsTradingFrozen { get; }
    decimal DailyDrawdownPct { get; }
}

/// <summary>
/// Deterministic trade safety engine executing mandatory risk guardrails.
/// </summary>
public class RiskManager : IRiskManager
{
    private readonly RiskRules _rules;
    private readonly ILogger<RiskManager> _logger;

    private decimal _startOfDayBalance;
    private DateTime _lastResetDateUtc;
    private bool _isFrozen;

    public bool IsTradingFrozen => _isFrozen;
    public decimal DailyDrawdownPct { get; private set; }

    public RiskManager(IOptions<BotSettings> settings, ILogger<RiskManager> logger)
    {
        _rules = settings.Value.Risk;
        _logger = logger;
        _lastResetDateUtc = DateTime.UtcNow.Date;
        _startOfDayBalance = 0m;
        _isFrozen = false;
    }

    /// <summary>
    /// Updates the equity tracking and checks for daily max-loss drawdown breaches.
    /// </summary>
    public void UpdateDailyBalance(decimal currentEquity)
    {
        if (currentEquity <= 0) return;

        var todayUtc = DateTime.UtcNow.Date;
        if (todayUtc > _lastResetDateUtc || _startOfDayBalance == 0m)
        {
            _startOfDayBalance = currentEquity;
            _lastResetDateUtc = todayUtc;
            _isFrozen = false;
            DailyDrawdownPct = 0m;
            _logger.LogInformation("Daily balance tracking reset for {Date:yyyy-MM-dd}. Starting equity: ${Equity:F2}",
                todayUtc, currentEquity);
        }

        if (_startOfDayBalance > 0m)
        {
            var drawdown = (_startOfDayBalance - currentEquity) / _startOfDayBalance;
            DailyDrawdownPct = Math.Max(0m, drawdown);

            if (DailyDrawdownPct >= _rules.MaxDailyDrawdownPct)
            {
                if (!_isFrozen)
                {
                    _logger.LogCritical("CIRCUIT BREAKER TRIGGERED: Daily drawdown {Drawdown:P2} exceeded limit {Limit:P2}. Freezing trading!",
                        DailyDrawdownPct, _rules.MaxDailyDrawdownPct);
                }
                _isFrozen = true;
            }
        }
    }

    /// <summary>
    /// Validates proposed trade against all deterministic safety constraints.
    /// </summary>
    public RiskEvaluation Evaluate(TradeDecision decision, MarketContext context)
    {
        var auditLogs = new List<string>();

        // 1. Check Circuit Breaker
        UpdateDailyBalance(context.AccountEquity);
        if (_isFrozen)
        {
            return RiskEvaluation.Reject(
                $"Trading is frozen due to daily drawdown protection ({DailyDrawdownPct:P2} >= {_rules.MaxDailyDrawdownPct:P2})",
                decision.Action,
                decision.Confidence
            );
        }

        // 2. Pass-through for Hold or Close
        if (decision.Action == TradeAction.Hold)
        {
            return new RiskEvaluation
            {
                IsApproved = false,
                RejectionReason = "Model decided to HOLD",
                Action = TradeAction.Hold,
                OriginalConfidence = decision.Confidence,
                SafetyAuditLogs = { "Decision is HOLD. No orders placed." }
            };
        }

        if (decision.Action == TradeAction.Close)
        {
            if (context.CurrentPositionSize == 0m)
            {
                return RiskEvaluation.Reject("Requested CLOSE but no active position is currently open.", decision.Action, decision.Confidence);
            }

            return new RiskEvaluation
            {
                IsApproved = true,
                Action = TradeAction.Close,
                OriginalConfidence = decision.Confidence,
                ApprovedSize = Math.Abs(context.CurrentPositionSize),
                EntryPrice = context.CurrentPrice,
                SafetyAuditLogs = { $"Approved closing existing position of size {context.CurrentPositionSize:F4}" }
            };
        }

        // 3. Confidence Threshold Check
        if (decision.Confidence < _rules.MinConfidenceThreshold)
        {
            var reason = $"Confidence {decision.Confidence:F2} below mandatory threshold {_rules.MinConfidenceThreshold:F2}";
            _logger.LogWarning("Trade rejected: {Reason}", reason);
            return RiskEvaluation.Reject(reason, decision.Action, decision.Confidence);
        }
        auditLogs.Add($"Confidence check passed: {decision.Confidence:F2} >= {_rules.MinConfidenceThreshold:F2}");

        // 4. Portfolio Allocation Cap & Position Sizing
        var equity = context.AccountEquity > 0 ? context.AccountEquity : 1000m; // Fallback for simulated/test environments
        var maxAllocUsd = equity * _rules.MaxPortfolioAllocationPct;

        // Desired allocation from decision, clamped to MaxPortfolioAllocationPct
        var requestedAllocPct = decision.AllocationPct > 0 ? decision.AllocationPct : _rules.MaxPortfolioAllocationPct;
        var appliedAllocPct = Math.Min(requestedAllocPct, _rules.MaxPortfolioAllocationPct);
        var targetUsdValue = equity * appliedAllocPct;

        if (context.CurrentPrice <= 0)
        {
            return RiskEvaluation.Reject("Invalid market price (<= 0)", decision.Action, decision.Confidence);
        }

        var calculatedSize = targetUsdValue / context.CurrentPrice;
        auditLogs.Add($"Size calculation: Equity=${equity:F2}, AllocPct={appliedAllocPct:P2}, TargetUSD=${targetUsdValue:F2}, Size={calculatedSize:F6}");

        // 5. Minimum Order Value Check ($10 USD)
        if (targetUsdValue < _rules.MinOrderValueUsd)
        {
            var reason = $"Order value ${targetUsdValue:F2} below exchange minimum ${_rules.MinOrderValueUsd:F2}";
            _logger.LogWarning("Trade rejected: {Reason}", reason);
            return RiskEvaluation.Reject(reason, decision.Action, decision.Confidence);
        }
        auditLogs.Add($"Min order value check passed: ${targetUsdValue:F2} >= ${_rules.MinOrderValueUsd:F2}");

        // 6. Hardcoded Mandatory Stop-Loss Enforcement (1.0% <= stop_loss <= 5.0%)
        var rawSl = decision.SuggestedStopLossPct ?? _rules.DefaultStopLossPct;
        var clampedSl = Math.Clamp(rawSl, _rules.MinStopLossPct, _rules.MaxStopLossPct);

        var rawTp = decision.SuggestedTakeProfitPct ?? _rules.DefaultTakeProfitPct;
        var clampedTp = Math.Max(rawTp, clampedSl * 1.5m); // Ensure favorable risk/reward

        auditLogs.Add($"Stop-loss bounded: raw={rawSl:P2}, clamped={clampedSl:P2} (range [{_rules.MinStopLossPct:P2} - {_rules.MaxStopLossPct:P2}])");

        // 7. Calculate Exact Trigger Prices
        decimal slPrice;
        decimal tpPrice;

        if (decision.Action == TradeAction.Buy)
        {
            slPrice = context.CurrentPrice * (1.0m - clampedSl);
            tpPrice = context.CurrentPrice * (1.0m + clampedTp);
        }
        else // Sell / Short
        {
            slPrice = context.CurrentPrice * (1.0m + clampedSl);
            tpPrice = context.CurrentPrice * (1.0m - clampedTp);
        }

        auditLogs.Add($"Execution parameters: Entry=${context.CurrentPrice:F2}, StopLoss=${slPrice:F2}, TakeProfit=${tpPrice:F2}");

        return new RiskEvaluation
        {
            IsApproved = true,
            Action = decision.Action,
            OriginalConfidence = decision.Confidence,
            ApprovedSize = calculatedSize,
            EntryPrice = context.CurrentPrice,
            StopLossPrice = slPrice,
            TakeProfitPrice = tpPrice,
            AppliedStopLossPct = clampedSl,
            AppliedTakeProfitPct = clampedTp,
            NotionalUsdValue = targetUsdValue,
            SafetyAuditLogs = auditLogs
        };
    }
}

using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using HyperliquidAiBot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HyperliquidAiBot.Tests;

public class RiskManagerTests
{
    private readonly BotSettings _settings;
    private readonly RiskManager _riskManager;

    public RiskManagerTests()
    {
        _settings = new BotSettings
        {
            Risk = new RiskRules
            {
                MinConfidenceThreshold = 0.75m,
                MaxPortfolioAllocationPct = 0.02m,
                MinStopLossPct = 0.01m,
                MaxStopLossPct = 0.05m,
                DefaultStopLossPct = 0.02m,
                DefaultTakeProfitPct = 0.04m,
                MaxDailyDrawdownPct = 0.05m,
                MinOrderValueUsd = 10.0m
            }
        };

        var options = Options.Create(_settings);
        _riskManager = new RiskManager(options, NullLogger<RiskManager>.Instance);
    }

    [Fact]
    public void Evaluate_ConfidenceBelowThreshold_ShouldRejectOrder()
    {
        // Arrange
        var decision = new TradeDecision
        {
            Action = TradeAction.Buy,
            Confidence = 0.70m, // Below 0.75
            AllocationPct = 0.02m
        };

        var context = new MarketContext
        {
            CurrentPrice = 50000m,
            AccountEquity = 10000m
        };

        // Act
        var result = _riskManager.Evaluate(decision, context);

        // Assert
        Assert.False(result.IsApproved);
        Assert.Contains("below mandatory threshold", result.RejectionReason);
    }

    [Fact]
    public void Evaluate_ConfidenceAtOrAboveThreshold_ShouldApprove()
    {
        // Arrange
        var decision = new TradeDecision
        {
            Action = TradeAction.Buy,
            Confidence = 0.85m, // Above 0.75
            AllocationPct = 0.02m
        };

        var context = new MarketContext
        {
            CurrentPrice = 50000m,
            AccountEquity = 10000m
        };

        // Act
        var result = _riskManager.Evaluate(decision, context);

        // Assert
        Assert.True(result.IsApproved);
        Assert.Equal(TradeAction.Buy, result.Action);
    }

    [Fact]
    public void Evaluate_AllocationExceedsMaxPortfolioAllocation_ShouldClampToMax()
    {
        // Arrange: requested 5% (> 2% max allowed)
        var decision = new TradeDecision
        {
            Action = TradeAction.Buy,
            Confidence = 0.80m,
            AllocationPct = 0.05m
        };

        var context = new MarketContext
        {
            CurrentPrice = 50000m,
            AccountEquity = 10000m
        };

        // Act
        var result = _riskManager.Evaluate(decision, context);

        // Assert: 2% of $10,000 is $200. At $50,000/BTC, size should be 200 / 50000 = 0.004 BTC
        Assert.True(result.IsApproved);
        Assert.Equal(200m, result.NotionalUsdValue);
        Assert.Equal(0.004m, result.ApprovedSize);
    }

    [Fact]
    public void Evaluate_OrderValueBelowMinimumUsd_ShouldRejectOrder()
    {
        // Arrange: $100 equity * 2% = $2, which is < $10 min order value
        var decision = new TradeDecision
        {
            Action = TradeAction.Buy,
            Confidence = 0.90m,
            AllocationPct = 0.02m
        };

        var context = new MarketContext
        {
            CurrentPrice = 50000m,
            AccountEquity = 100m
        };

        // Act
        var result = _riskManager.Evaluate(decision, context);

        // Assert
        Assert.False(result.IsApproved);
        Assert.Contains("below exchange minimum", result.RejectionReason);
    }

    [Fact]
    public void Evaluate_StopLossBelowMin_ShouldClampToMin()
    {
        // Arrange: model suggests 0.5% stop loss (below 1.0% min)
        var decision = new TradeDecision
        {
            Action = TradeAction.Buy,
            Confidence = 0.85m,
            AllocationPct = 0.02m,
            SuggestedStopLossPct = 0.005m
        };

        var context = new MarketContext
        {
            CurrentPrice = 50000m,
            AccountEquity = 10000m
        };

        // Act
        var result = _riskManager.Evaluate(decision, context);

        // Assert
        Assert.True(result.IsApproved);
        Assert.Equal(0.01m, result.AppliedStopLossPct); // Clamped to 1.0%
        Assert.Equal(49500m, result.StopLossPrice);     // 50000 * (1 - 0.01)
    }

    [Fact]
    public void Evaluate_StopLossAboveMax_ShouldClampToMax()
    {
        // Arrange: model suggests 8.0% stop loss (above 5.0% max)
        var decision = new TradeDecision
        {
            Action = TradeAction.Buy,
            Confidence = 0.85m,
            AllocationPct = 0.02m,
            SuggestedStopLossPct = 0.08m
        };

        var context = new MarketContext
        {
            CurrentPrice = 50000m,
            AccountEquity = 10000m
        };

        // Act
        var result = _riskManager.Evaluate(decision, context);

        // Assert
        Assert.True(result.IsApproved);
        Assert.Equal(0.05m, result.AppliedStopLossPct); // Clamped to 5.0%
        Assert.Equal(47500m, result.StopLossPrice);     // 50000 * (1 - 0.05)
    }

    [Fact]
    public void Evaluate_SellShortOrder_ShouldComputeCorrectTriggerPrices()
    {
        // Arrange: Sell order at $50,000 with 2% SL
        var decision = new TradeDecision
        {
            Action = TradeAction.Sell,
            Confidence = 0.80m,
            AllocationPct = 0.02m,
            SuggestedStopLossPct = 0.02m
        };

        var context = new MarketContext
        {
            CurrentPrice = 50000m,
            AccountEquity = 10000m
        };

        // Act
        var result = _riskManager.Evaluate(decision, context);

        // Assert: for short, stop loss is ABOVE entry price: 50000 * (1 + 0.02) = 51000
        Assert.True(result.IsApproved);
        Assert.Equal(51000m, result.StopLossPrice);
    }

    [Fact]
    public void Evaluate_DailyDrawdownExceeded_ShouldFreezeTrading()
    {
        // Arrange: Initial starting equity $10,000
        _riskManager.UpdateDailyBalance(10000m);

        // Act: Equity drops to $9,400 (6% loss, > 5% threshold)
        var context = new MarketContext
        {
            CurrentPrice = 50000m,
            AccountEquity = 9400m
        };

        var decision = new TradeDecision
        {
            Action = TradeAction.Buy,
            Confidence = 0.95m,
            AllocationPct = 0.02m
        };

        var result = _riskManager.Evaluate(decision, context);

        // Assert
        Assert.True(_riskManager.IsTradingFrozen);
        Assert.False(result.IsApproved);
        Assert.Contains("Trading is frozen due to daily drawdown protection", result.RejectionReason);
    }
}

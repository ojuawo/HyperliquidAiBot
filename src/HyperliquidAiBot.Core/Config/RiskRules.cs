namespace HyperliquidAiBot.Core.Config;

/// <summary>
/// Deterministic risk configuration enforcing portfolio safety rules.
/// </summary>
public class RiskRules
{
    public const string SectionName = "RiskRules";

    /// <summary>
    /// Minimum confidence score [0.0 - 1.0] from AI research engine required to execute a trade.
    /// </summary>
    public decimal MinConfidenceThreshold { get; set; } = 0.75m;

    /// <summary>
    /// Maximum percentage of total portfolio equity permitted for a single position (e.g., 0.02 = 2%).
    /// </summary>
    public decimal MaxPortfolioAllocationPct { get; set; } = 0.02m;

    /// <summary>
    /// Minimum mandatory stop-loss percentage (1.0% = 0.01).
    /// </summary>
    public decimal MinStopLossPct { get; set; } = 0.01m;

    /// <summary>
    /// Maximum mandatory stop-loss percentage (5.0% = 0.05).
    /// </summary>
    public decimal MaxStopLossPct { get; set; } = 0.05m;

    /// <summary>
    /// Default stop-loss percentage if model suggestion is missing or clamped (2.0% = 0.02).
    /// </summary>
    public decimal DefaultStopLossPct { get; set; } = 0.02m;

    /// <summary>
    /// Default take-profit percentage if model suggestion is missing (4.0% = 0.04).
    /// </summary>
    public decimal DefaultTakeProfitPct { get; set; } = 0.04m;

    /// <summary>
    /// Maximum daily portfolio drawdown threshold before trading is frozen (e.g., 0.05 = 5%).
    /// </summary>
    public decimal MaxDailyDrawdownPct { get; set; } = 0.05m;

    /// <summary>
    /// Minimum notional order value in USD required by Hyperliquid exchange ($10).
    /// </summary>
    public decimal MinOrderValueUsd { get; set; } = 10.0m;

    /// <summary>
    /// Maximum permitted leverage for perpetual positions.
    /// </summary>
    public int MaxLeverage { get; set; } = 5;

    /// <summary>
    /// Maximum number of concurrent open positions allowed across all assets.
    /// </summary>
    public int MaxOpenPositions { get; set; } = 3;
}

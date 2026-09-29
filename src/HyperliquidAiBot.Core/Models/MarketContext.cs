namespace HyperliquidAiBot.Core.Models;

/// <summary>
/// Aggregated snapshot of current exchange market state, account equity, and technical indicators.
/// </summary>
public record MarketContext
{
    public string Asset { get; init; } = "BTC";
    public int AssetIndex { get; init; }
    public decimal CurrentPrice { get; init; }
    public decimal BestBid { get; init; }
    public decimal BestAsk { get; init; }
    public decimal AccountEquity { get; init; }
    public decimal AvailableMargin { get; init; }
    public decimal CurrentPositionSize { get; init; }
    public decimal CurrentPositionEntryPrice { get; init; }
    public decimal UnrealizedPnl { get; init; }
    public TechnicalIndicatorsSnapshot Indicators { get; init; } = new();
    public List<CandleSummary> RecentCandles { get; init; } = new();
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Computed technical indicators evaluated on historical candles.
/// </summary>
public record TechnicalIndicatorsSnapshot
{
    public decimal? Rsi { get; init; }
    public decimal? Macd { get; init; }
    public decimal? MacdSignal { get; init; }
    public decimal? MacdHistogram { get; init; }
    public decimal? Sma20 { get; init; }
    public decimal? Sma50 { get; init; }
    public decimal? Sma200 { get; init; }
    public decimal? Ema9 { get; init; }
    public decimal? Ema21 { get; init; }
    public decimal? BollingerUpper { get; init; }
    public decimal? BollingerMiddle { get; init; }
    public decimal? BollingerLower { get; init; }
    public decimal? Atr { get; init; }
    public string TrendSignal { get; init; } = "Neutral";
    public bool IsOverbought => Rsi.HasValue && Rsi.Value >= 70m;
    public bool IsOversold => Rsi.HasValue && Rsi.Value <= 30m;
    public bool IsGoldenCross => Sma50.HasValue && Sma200.HasValue && Sma50.Value > Sma200.Value;
    public bool IsDeathCross => Sma50.HasValue && Sma200.HasValue && Sma50.Value < Sma200.Value;
}

public record CandleSummary(
    DateTime TimestampUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume
);

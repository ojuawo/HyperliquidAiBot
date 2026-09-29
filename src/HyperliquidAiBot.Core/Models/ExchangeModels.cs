namespace HyperliquidAiBot.Core.Models;

/// <summary>
/// Universal market snapshot ingested from any exchange.
/// </summary>
public record MarketSnapshot(
    string Symbol,
    decimal CurrentPrice,
    decimal BestBid,
    decimal BestAsk,
    List<CandleSummary> Candles,
    DateTime TimestampUtc
);

/// <summary>
/// Universal account portfolio snapshot.
/// </summary>
public record AccountPortfolio(
    decimal AccountEquity,
    decimal AvailableMargin,
    List<ExchangePosition> Positions
)
{
    public ExchangePosition? GetPosition(string symbol) =>
        Positions.FirstOrDefault(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Position details across any exchange.
/// </summary>
public record ExchangePosition(
    string Symbol,
    decimal Size,
    decimal EntryPrice,
    decimal UnrealizedPnl,
    decimal? LiquidationPrice = null
);

public enum TradeOrderSide
{
    Buy,
    Sell
}

public enum TradeOrderType
{
    Market,
    Limit
}

/// <summary>
/// Universal order request sent to an exchange adapter.
/// </summary>
public record TradeOrderRequest(
    string Symbol,
    TradeOrderSide Side,
    TradeOrderType Type,
    decimal Size,
    decimal? Price = null,
    decimal? StopLossPrice = null,
    decimal? TakeProfitPrice = null,
    string? ClientOrderId = null,
    bool ReduceOnly = false
);

/// <summary>
/// Universal order execution result returned by an exchange adapter.
/// </summary>
public record OrderExecutionResult(
    bool Success,
    string OrderId,
    string Status,
    decimal ExecutedPrice,
    decimal ExecutedSize,
    string? Message = null,
    DateTime? TimestampUtc = null
);

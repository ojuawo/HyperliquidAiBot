using HyperliquidAiBot.Core.Models;

namespace HyperliquidAiBot.Core.Services;

/// <summary>
/// Universal trading exchange adapter contract.
/// Allows the bot to run across any exchange (Binance, Bybit, Hyperliquid, PaperTrading, Alpaca).
/// </summary>
public interface IExchangeClient
{
    /// <summary>
    /// Friendly name of the connected exchange adapter.
    /// </summary>
    string ExchangeName { get; }

    /// <summary>
    /// Ingests live order book best bid/ask and historical candlestick data for technical analysis.
    /// </summary>
    Task<MarketSnapshot> GetMarketDataAsync(string symbol, string interval, int limit, CancellationToken ct = default);

    /// <summary>
    /// Retrieves current account equity, available balance, and open position state.
    /// </summary>
    Task<AccountPortfolio> GetAccountPortfolioAsync(CancellationToken ct = default);

    /// <summary>
    /// Dispatches an order to the exchange.
    /// </summary>
    Task<OrderExecutionResult> PlaceOrderAsync(TradeOrderRequest order, CancellationToken ct = default);

    /// <summary>
    /// Cancels an existing order by ID.
    /// </summary>
    Task<bool> CancelOrderAsync(string orderId, string symbol, CancellationToken ct = default);

    /// <summary>
    /// Initializes exchange universe metadata, decimals, and connections.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
}

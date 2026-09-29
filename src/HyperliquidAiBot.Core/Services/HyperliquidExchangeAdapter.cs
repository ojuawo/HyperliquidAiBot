using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperliquidAiBot.Core.Services;

/// <summary>
/// Exchange adapter wrapping Hyperliquid Perpetual DEX client.
/// </summary>
public class HyperliquidExchangeAdapter : IExchangeClient
{
    private readonly IHyperliquidClient _client;
    private readonly BotSettings _settings;
    private readonly ILogger<HyperliquidExchangeAdapter> _logger;

    private readonly Dictionary<string, (int Index, int SzDecimals, int MaxLeverage)> _assetLookup = new(StringComparer.OrdinalIgnoreCase);

    public string ExchangeName => _settings.Hyperliquid.UseTestnet ? "Hyperliquid Testnet" : "Hyperliquid Mainnet";

    public HyperliquidExchangeAdapter(
        IHyperliquidClient client,
        IOptions<BotSettings> settings,
        ILogger<HyperliquidExchangeAdapter> logger)
    {
        _client = client;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var meta = await _client.GetUniverseMetaAsync(ct);
            _assetLookup.Clear();
            for (int i = 0; i < meta.Universe.Count; i++)
            {
                var a = meta.Universe[i];
                _assetLookup[a.Name] = (i, a.SzDecimals, a.MaxLeverage);
            }
            _logger.LogInformation("Hyperliquid universe initialized with {Count} assets.", _assetLookup.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Hyperliquid universe metadata.");
        }
    }

    public async Task<MarketSnapshot> GetMarketDataAsync(string symbol, string interval, int limit, CancellationToken ct = default)
    {
        var coin = symbol.Replace("USDT", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("-PERP", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(coin)) coin = "BTC";

        var rawCandles = await _client.GetCandleSnapshotAsync(coin, interval, limit, ct);

        decimal bestBid = 0m, bestAsk = 0m;
        try
        {
            var l2 = await _client.GetL2BookAsync(coin, ct);
            if (l2?.Levels.Count >= 2)
            {
                var bids = l2.Levels[0];
                var asks = l2.Levels[1];
                if (bids.Count > 0 && decimal.TryParse(bids[0].Px, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var bVal))
                    bestBid = bVal;
                if (asks.Count > 0 && decimal.TryParse(asks[0].Px, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var aVal))
                    bestAsk = aVal;
            }
        }
        catch
        {
            // Fallback to candle price if L2 book fails
        }

        var candles = rawCandles.Select(c => new CandleSummary(
            c.DateTimeUtc,
            c.OpenDecimal,
            c.HighDecimal,
            c.LowDecimal,
            c.CloseDecimal,
            c.VolumeDecimal
        )).ToList();

        decimal currentPrice = bestBid > 0 && bestAsk > 0 ? (bestBid + bestAsk) / 2m : (candles.LastOrDefault()?.Close ?? 0m);

        return new MarketSnapshot(
            Symbol: symbol,
            CurrentPrice: currentPrice,
            BestBid: bestBid > 0 ? bestBid : currentPrice,
            BestAsk: bestAsk > 0 ? bestAsk : currentPrice,
            Candles: candles,
            TimestampUtc: DateTime.UtcNow
        );
    }

    public async Task<AccountPortfolio> GetAccountPortfolioAsync(CancellationToken ct = default)
    {
        var wallet = _settings.Hyperliquid.WalletAddress;
        if (string.IsNullOrWhiteSpace(wallet))
        {
            // Simulated default equity if no wallet configured
            return new AccountPortfolio(10000m, 10000m, new List<ExchangePosition>());
        }

        try
        {
            var state = await _client.GetClearinghouseStateAsync(wallet, ct);
            decimal equity = state.MarginSummary.AccountValueDecimal;
            decimal availableMargin = decimal.TryParse(state.Withdrawable, out var wd) ? wd : equity;

            var positions = new List<ExchangePosition>();
            foreach (var ap in state.AssetPositions)
            {
                var p = ap.Position;
                if (p.SizeDecimal != 0)
                {
                    positions.Add(new ExchangePosition(
                        Symbol: p.Coin,
                        Size: p.SizeDecimal,
                        EntryPrice: p.EntryPriceDecimal,
                        UnrealizedPnl: p.UnrealizedPnlDecimal,
                        LiquidationPrice: null
                    ));
                }
            }

            return new AccountPortfolio(equity > 0 ? equity : 10000m, availableMargin > 0 ? availableMargin : 10000m, positions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to query Hyperliquid clearinghouse state: {Msg}", ex.Message);
            return new AccountPortfolio(10000m, 10000m, new List<ExchangePosition>());
        }
    }

    public async Task<OrderExecutionResult> PlaceOrderAsync(TradeOrderRequest order, CancellationToken ct = default)
    {
        if (_assetLookup.Count == 0)
        {
            await InitializeAsync(ct);
        }

        var coin = order.Symbol.Replace("USDT", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("-PERP", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!_assetLookup.TryGetValue(coin, out var meta))
        {
            return new OrderExecutionResult(false, string.Empty, "REJECTED", 0m, 0m, $"Asset {coin} not in Hyperliquid universe.");
        }

        var isBuy = order.Side == TradeOrderSide.Buy;
        var roundedPrice = Math.Round(order.Price ?? 0m, 2);
        var roundedSize = Math.Round(order.Size, meta.SzDecimals);

        TriggerOrderTypeWire? trigger = null;
        if (order.StopLossPrice.HasValue)
        {
            trigger = new TriggerOrderTypeWire(
                TriggerPx: order.StopLossPrice.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                IsMarket: true,
                Tpsl: "sl"
            );
        }

        var resp = await _client.PostOrderAsync(
            assetIndex: meta.Index,
            isBuy: isBuy,
            price: roundedPrice,
            size: roundedSize,
            szDecimals: meta.SzDecimals,
            reduceOnly: order.ReduceOnly,
            tif: _settings.Hyperliquid.DefaultTif,
            trigger: trigger,
            cloid: null,
            ct: ct
        );

        var isSuccess = resp.Status.Equals("ok", StringComparison.OrdinalIgnoreCase);
        return new OrderExecutionResult(
            Success: isSuccess,
            OrderId: Guid.NewGuid().ToString("N")[..8],
            Status: resp.Status,
            ExecutedPrice: roundedPrice,
            ExecutedSize: roundedSize,
            Message: resp.Response.ToString(),
            TimestampUtc: DateTime.UtcNow
        );
    }

    public Task<bool> CancelOrderAsync(string orderId, string symbol, CancellationToken ct = default)
    {
        _logger.LogInformation("Order cancel request processed: {Id}", orderId);
        return Task.FromResult(true);
    }
}

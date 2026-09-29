using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using HyperliquidAiBot.Core.Models;
using Microsoft.Extensions.Logging;

namespace HyperliquidAiBot.Core.Services;

/// <summary>
/// Exchange adapter providing simulated paper trading execution against live real-time market data.
/// Requires $0 deposit, zero authentication keys, and zero signups.
/// Uses free public Binance or CoinGecko market feeds to stream live prices and candles.
/// </summary>
public class PaperTradingExchangeClient : IExchangeClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PaperTradingExchangeClient> _logger;

    private decimal _accountBalance = 10000.00m; // Default simulated starting equity ($10k USD)
    private readonly Dictionary<string, ExchangePosition> _positions = new(StringComparer.OrdinalIgnoreCase);
    private decimal _lastKnownPrice = 0m;
    private readonly object _lock = new();

    public string ExchangeName => "Paper Trading (Simulated Execution on Live Feeds)";

    public PaperTradingExchangeClient(
        HttpClient httpClient,
        Microsoft.Extensions.Options.IOptions<HyperliquidAiBot.Core.Config.BotSettings> settings,
        ILogger<PaperTradingExchangeClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        var startingBalance = settings.Value.PaperTrading.StartingBalanceUsd;
        _accountBalance = startingBalance > 0 ? startingBalance : 10000m;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public static string NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return "BTCUSDT";
        var s = symbol.ToUpperInvariant().Trim();
        if (!s.EndsWith("USDT") && !s.EndsWith("USD") && !s.EndsWith("BUSD"))
        {
            s += "USDT";
        }
        return s;
    }

    public async Task<MarketSnapshot> GetMarketDataAsync(string symbol, string interval, int limit, CancellationToken ct = default)
    {
        var normSymbol = NormalizeSymbol(symbol);
        var normInterval = NormalizeInterval(interval);

        // 1. Fetch best bid/ask from Binance public bookTicker
        decimal bestBid = 0m;
        decimal bestAsk = 0m;
        decimal currentPrice = 0m;

        try
        {
            var tickerUrl = $"https://api.binance.com/api/v3/ticker/bookTicker?symbol={normSymbol}";
            using var tickerResp = await _httpClient.GetAsync(tickerUrl, ct);
            if (tickerResp.IsSuccessStatusCode)
            {
                var json = await tickerResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("bidPrice", out var bp) && decimal.TryParse(bp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var bVal))
                {
                    bestBid = bVal;
                }
                if (root.TryGetProperty("askPrice", out var ap) && decimal.TryParse(ap.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var aVal))
                {
                    bestAsk = aVal;
                }
                currentPrice = bestBid > 0 && bestAsk > 0 ? (bestBid + bestAsk) / 2m : (bestBid > 0 ? bestBid : bestAsk);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch public bookTicker for {Symbol}: {Message}", normSymbol, ex.Message);
        }

        // 2. Fetch historical klines / candles
        var candles = new List<CandleSummary>();
        try
        {
            var klineUrl = $"https://api.binance.com/api/v3/klines?symbol={normSymbol}&interval={normInterval}&limit={Math.Max(20, limit)}";
            using var klineResp = await _httpClient.GetAsync(klineUrl, ct);
            if (klineResp.IsSuccessStatusCode)
            {
                var json = await klineResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var openTimeMs = item[0].GetInt64();
                    var open = decimal.Parse(item[1].GetString()!, CultureInfo.InvariantCulture);
                    var high = decimal.Parse(item[2].GetString()!, CultureInfo.InvariantCulture);
                    var low = decimal.Parse(item[3].GetString()!, CultureInfo.InvariantCulture);
                    var close = decimal.Parse(item[4].GetString()!, CultureInfo.InvariantCulture);
                    var vol = decimal.Parse(item[5].GetString()!, CultureInfo.InvariantCulture);

                    candles.Add(new CandleSummary(
                        DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs).UtcDateTime,
                        open,
                        high,
                        low,
                        close,
                        vol
                    ));
                }

                if (currentPrice <= 0 && candles.Count > 0)
                {
                    currentPrice = candles.Last().Close;
                    bestBid = currentPrice * 0.9999m;
                    bestAsk = currentPrice * 1.0001m;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch public klines for {Symbol}: {Message}", normSymbol, ex.Message);
        }

        // 3. Fallback to Hyperliquid Public Feed if primary exchange returned 0 candles (e.g. US geo-blocking)
        if (candles.Count == 0)
        {
            try
            {
                var coin = symbol.Replace("USDT", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("-PERP", string.Empty, StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(coin)) coin = "BTC";

                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var startTime = nowMs - (Math.Max(20, limit) * 3600 * 1000L);

                var hlPayload = new
                {
                    type = "candleSnapshot",
                    req = new { coin = coin, interval = normInterval, startTime = startTime }
                };

                using var hlResp = await _httpClient.PostAsJsonAsync("https://api.hyperliquid.xyz/info", hlPayload, ct);
                if (hlResp.IsSuccessStatusCode)
                {
                    var hlJson = await hlResp.Content.ReadAsStringAsync(ct);
                    using var hlDoc = JsonDocument.Parse(hlJson);
                    foreach (var c in hlDoc.RootElement.EnumerateArray())
                    {
                        var t = c.GetProperty("t").GetInt64();
                        var o = decimal.Parse(c.GetProperty("o").GetString()!, CultureInfo.InvariantCulture);
                        var h = decimal.Parse(c.GetProperty("h").GetString()!, CultureInfo.InvariantCulture);
                        var l = decimal.Parse(c.GetProperty("l").GetString()!, CultureInfo.InvariantCulture);
                        var cl = decimal.Parse(c.GetProperty("c").GetString()!, CultureInfo.InvariantCulture);
                        var v = decimal.Parse(c.GetProperty("v").GetString()!, CultureInfo.InvariantCulture);

                        candles.Add(new CandleSummary(
                            DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime,
                            o, h, l, cl, v
                        ));
                    }

                    if (candles.Count > 0)
                    {
                        currentPrice = candles.Last().Close;
                        bestBid = currentPrice * 0.9999m;
                        bestAsk = currentPrice * 1.0001m;
                        _logger.LogInformation("Ingested {Count} live candles for {Symbol} via Hyperliquid public feed.", candles.Count, coin);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Fallback public candle feed failed: {Msg}", ex.Message);
            }
        }

        if (currentPrice > 0)
        {
            _lastKnownPrice = currentPrice;
        }

        return new MarketSnapshot(
            Symbol: symbol,
            CurrentPrice: currentPrice > 0 ? currentPrice : _lastKnownPrice,
            BestBid: bestBid,
            BestAsk: bestAsk,
            Candles: candles,
            TimestampUtc: DateTime.UtcNow
        );
    }

    public Task<AccountPortfolio> GetAccountPortfolioAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            decimal totalUnrealizedPnl = 0m;
            var updatedPositions = new List<ExchangePosition>();

            foreach (var kvp in _positions)
            {
                var pos = kvp.Value;
                if (pos.Size == 0) continue;

                // Update unrealized PnL based on current market price
                decimal uPnl = 0m;
                if (_lastKnownPrice > 0)
                {
                    if (pos.Size > 0) // Long
                    {
                        uPnl = (pos.Size) * (_lastKnownPrice - pos.EntryPrice);
                    }
                    else // Short
                    {
                        uPnl = (-pos.Size) * (pos.EntryPrice - _lastKnownPrice);
                    }
                }

                totalUnrealizedPnl += uPnl;
                var updated = pos with { UnrealizedPnl = uPnl };
                updatedPositions.Add(updated);
            }

            var equity = _accountBalance + totalUnrealizedPnl;
            var marginUsed = updatedPositions.Sum(p => Math.Abs(p.Size) * p.EntryPrice);
            var availableMargin = Math.Max(0m, equity - marginUsed);

            return Task.FromResult(new AccountPortfolio(
                AccountEquity: equity,
                AvailableMargin: availableMargin,
                Positions: updatedPositions
            ));
        }
    }

    public Task<OrderExecutionResult> PlaceOrderAsync(TradeOrderRequest order, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var execPrice = order.Price ?? (_lastKnownPrice > 0 ? _lastKnownPrice : 84000m);
            var orderId = "PAPER-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

            _positions.TryGetValue(order.Symbol, out var currentPos);
            currentPos ??= new ExchangePosition(order.Symbol, 0m, 0m, 0m);

            decimal deltaSize = order.Side == TradeOrderSide.Buy ? order.Size : -order.Size;
            decimal newSize = currentPos.Size + deltaSize;
            decimal newEntryPrice = execPrice;

            if (currentPos.Size != 0 && Math.Sign(currentPos.Size) == Math.Sign(deltaSize))
            {
                // Increasing position: Weighted average entry price
                var totalNotional = (Math.Abs(currentPos.Size) * currentPos.EntryPrice) + (Math.Abs(deltaSize) * execPrice);
                newEntryPrice = totalNotional / Math.Abs(newSize);
            }
            else if (currentPos.Size != 0 && Math.Sign(currentPos.Size) != Math.Sign(deltaSize))
            {
                // Decreasing or flipping position: Calculate realized PnL
                var closingSize = Math.Min(Math.Abs(currentPos.Size), Math.Abs(deltaSize));
                decimal realizedPnl = 0m;
                if (currentPos.Size > 0) // Closing Long
                {
                    realizedPnl = closingSize * (execPrice - currentPos.EntryPrice);
                }
                else // Closing Short
                {
                    realizedPnl = closingSize * (currentPos.EntryPrice - execPrice);
                }
                _accountBalance += realizedPnl;
                _logger.LogInformation("Paper Trade Closed: Realized PnL = ${Pnl:F2}. New Balance = ${Bal:F2}", realizedPnl, _accountBalance);

                if (Math.Sign(newSize) != Math.Sign(currentPos.Size))
                {
                    newEntryPrice = execPrice; // Flipped to opposite side
                }
                else
                {
                    newEntryPrice = currentPos.EntryPrice; // Reduced on same side
                }
            }

            _positions[order.Symbol] = new ExchangePosition(
                Symbol: order.Symbol,
                Size: newSize,
                EntryPrice: newSize != 0 ? newEntryPrice : 0m,
                UnrealizedPnl: 0m
            );

            _logger.LogInformation("Paper Trade Executed: {Side} {Size:F4} {Symbol} @ ${Price:F2}. OrderId={OrderId}. New Pos={NewPos:F4}",
                order.Side, order.Size, order.Symbol, execPrice, orderId, newSize);

            return Task.FromResult(new OrderExecutionResult(
                Success: true,
                OrderId: orderId,
                Status: "FILLED",
                ExecutedPrice: execPrice,
                ExecutedSize: order.Size,
                Message: "Simulated paper order filled with live market pricing",
                TimestampUtc: DateTime.UtcNow
            ));
        }
    }

    public Task<bool> CancelOrderAsync(string orderId, string symbol, CancellationToken ct = default)
    {
        _logger.LogInformation("Paper Trade Order Cancelled: {OrderId}", orderId);
        return Task.FromResult(true);
    }

    private static string NormalizeInterval(string interval)
    {
        return interval.ToLowerInvariant() switch
        {
            "1m" => "1m",
            "5m" => "5m",
            "15m" => "15m",
            "30m" => "30m",
            "1h" => "1h",
            "4h" => "4h",
            "1d" => "1d",
            _ => "1h"
        };
    }
}

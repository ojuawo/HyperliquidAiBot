using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperliquidAiBot.Core.Services;

/// <summary>
/// Binance Futures exchange adapter supporting Testnet (15k free USDT) and Mainnet.
/// </summary>
public class BinanceFuturesExchangeClient : IExchangeClient
{
    private readonly HttpClient _httpClient;
    private readonly BotSettings _settings;
    private readonly ILogger<BinanceFuturesExchangeClient> _logger;

    public string ExchangeName => _settings.Binance.UseTestnet ? "Binance Futures Testnet" : "Binance Futures Mainnet";

    private string BaseUrl => _settings.Binance.UseTestnet
        ? "https://testnet.binancefuture.com"
        : "https://fapi.binance.com";

    public BinanceFuturesExchangeClient(
        HttpClient httpClient,
        IOptions<BotSettings> settings,
        ILogger<BinanceFuturesExchangeClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<MarketSnapshot> GetMarketDataAsync(string symbol, string interval, int limit, CancellationToken ct = default)
    {
        var normSymbol = PaperTradingExchangeClient.NormalizeSymbol(symbol);
        var normInterval = interval.ToLowerInvariant();

        decimal bestBid = 0m, bestAsk = 0m, currentPrice = 0m;

        // 1. Ingest Best Bid/Ask
        try
        {
            var tickerUrl = $"{BaseUrl}/fapi/v1/ticker/bookTicker?symbol={normSymbol}";
            using var resp = await _httpClient.GetAsync(tickerUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("bidPrice", out var bp) && decimal.TryParse(bp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var bVal))
                    bestBid = bVal;
                if (root.TryGetProperty("askPrice", out var ap) && decimal.TryParse(ap.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var aVal))
                    bestAsk = aVal;

                currentPrice = bestBid > 0 && bestAsk > 0 ? (bestBid + bestAsk) / 2m : (bestBid > 0 ? bestBid : bestAsk);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Binance bookTicker fetch failed for {Symbol}: {Msg}", normSymbol, ex.Message);
        }

        // 2. Ingest Historical Klines
        var candles = new List<CandleSummary>();
        try
        {
            var klineUrl = $"{BaseUrl}/fapi/v1/klines?symbol={normSymbol}&interval={normInterval}&limit={Math.Max(20, limit)}";
            using var resp = await _httpClient.GetAsync(klineUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
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
            _logger.LogWarning("Binance klines fetch failed for {Symbol}: {Msg}", normSymbol, ex.Message);
        }

        return new MarketSnapshot(
            Symbol: symbol,
            CurrentPrice: currentPrice,
            BestBid: bestBid,
            BestAsk: bestAsk,
            Candles: candles,
            TimestampUtc: DateTime.UtcNow
        );
    }

    public async Task<AccountPortfolio> GetAccountPortfolioAsync(CancellationToken ct = default)
    {
        var apiKey = _settings.Binance.ApiKey;
        var apiSecret = _settings.Binance.ApiSecret;

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            // Fallback simulated testnet portfolio if keys are not yet injected
            return new AccountPortfolio(
                AccountEquity: 15000m,
                AvailableMargin: 15000m,
                Positions: new List<ExchangePosition>()
            );
        }

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var queryString = $"timestamp={timestamp}";
            var signature = CreateHmacSha256(queryString, apiSecret);
            var url = $"{BaseUrl}/fapi/v2/account?{queryString}&signature={signature}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-MBX-APIKEY", apiKey);

            using var resp = await _httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                decimal equity = decimal.Parse(root.GetProperty("totalMarginBalance").GetString()!, CultureInfo.InvariantCulture);
                decimal avail = decimal.Parse(root.GetProperty("availableBalance").GetString()!, CultureInfo.InvariantCulture);

                var positions = new List<ExchangePosition>();
                if (root.TryGetProperty("positions", out var posArray))
                {
                    foreach (var p in posArray.EnumerateArray())
                    {
                        var amt = decimal.Parse(p.GetProperty("positionAmt").GetString()!, CultureInfo.InvariantCulture);
                        if (amt != 0)
                        {
                            var sym = p.GetProperty("symbol").GetString()!;
                            var entry = decimal.Parse(p.GetProperty("entryPrice").GetString()!, CultureInfo.InvariantCulture);
                            var uPnl = decimal.Parse(p.GetProperty("unrealizedProfit").GetString()!, CultureInfo.InvariantCulture);

                            positions.Add(new ExchangePosition(sym, amt, entry, uPnl));
                        }
                    }
                }

                return new AccountPortfolio(equity, avail, positions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Binance Futures account state");
        }

        return new AccountPortfolio(15000m, 15000m, new List<ExchangePosition>());
    }

    public async Task<OrderExecutionResult> PlaceOrderAsync(TradeOrderRequest order, CancellationToken ct = default)
    {
        var apiKey = _settings.Binance.ApiKey;
        var apiSecret = _settings.Binance.ApiSecret;
        var normSymbol = PaperTradingExchangeClient.NormalizeSymbol(order.Symbol);

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            return new OrderExecutionResult(
                Success: true,
                OrderId: "SIM-BINANCE-" + Guid.NewGuid().ToString("N")[..6],
                Status: "FILLED_SIMULATED",
                ExecutedPrice: order.Price ?? 84000m,
                ExecutedSize: order.Size,
                Message: "Simulated execution (Set BOTSETTINGS__BINANCE__APIKEY for live testnet orders)",
                TimestampUtc: DateTime.UtcNow
            );
        }

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var side = order.Side == TradeOrderSide.Buy ? "BUY" : "SELL";
            var type = order.Type == TradeOrderType.Market ? "MARKET" : "LIMIT";

            var sb = new StringBuilder();
            sb.Append($"symbol={normSymbol}&side={side}&type={type}&quantity={order.Size.ToString(CultureInfo.InvariantCulture)}&timestamp={timestamp}");

            if (order.Type == TradeOrderType.Limit && order.Price.HasValue)
            {
                sb.Append($"&price={order.Price.Value.ToString(CultureInfo.InvariantCulture)}&timeInForce=GTC");
            }

            var queryString = sb.ToString();
            var signature = CreateHmacSha256(queryString, apiSecret);
            var url = $"{BaseUrl}/fapi/v1/order?{queryString}&signature={signature}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-MBX-APIKEY", apiKey);

            using var resp = await _httpClient.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(raw);
                var oId = doc.RootElement.GetProperty("orderId").GetInt64().ToString();
                var status = doc.RootElement.GetProperty("status").GetString()!;
                decimal.TryParse(doc.RootElement.GetProperty("avgPrice").GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var avgPx);

                return new OrderExecutionResult(
                    Success: true,
                    OrderId: oId,
                    Status: status,
                    ExecutedPrice: avgPx > 0 ? avgPx : (order.Price ?? 0m),
                    ExecutedSize: order.Size,
                    Message: raw,
                    TimestampUtc: DateTime.UtcNow
                );
            }

            _logger.LogError("Binance order placement rejected ({Code}): {Body}", resp.StatusCode, raw);
            return new OrderExecutionResult(false, string.Empty, "REJECTED", 0m, 0m, raw, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception placing order on Binance Futures");
            return new OrderExecutionResult(false, string.Empty, "ERROR", 0m, 0m, ex.Message, DateTime.UtcNow);
        }
    }

    public async Task<bool> CancelOrderAsync(string orderId, string symbol, CancellationToken ct = default)
    {
        var apiKey = _settings.Binance.ApiKey;
        var apiSecret = _settings.Binance.ApiSecret;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret)) return true;

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var normSymbol = PaperTradingExchangeClient.NormalizeSymbol(symbol);
            var qs = $"symbol={normSymbol}&orderId={orderId}&timestamp={timestamp}";
            var sig = CreateHmacSha256(qs, apiSecret);
            var url = $"{BaseUrl}/fapi/v1/order?{qs}&signature={sig}";

            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.Add("X-MBX-APIKEY", apiKey);
            using var resp = await _httpClient.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateHmacSha256(string message, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

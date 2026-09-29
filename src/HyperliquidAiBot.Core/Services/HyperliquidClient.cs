using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperliquidAiBot.Core.Services;

public interface IHyperliquidClient
{
    Task<HyperliquidMetaResponse> GetUniverseMetaAsync(CancellationToken ct = default);
    Task<List<CandleSnapshot>> GetCandleSnapshotAsync(string coin, string interval, int limit = 100, CancellationToken ct = default);
    Task<ClearinghouseState> GetClearinghouseStateAsync(string userAddress, CancellationToken ct = default);
    Task<L2BookResponse?> GetL2BookAsync(string coin, CancellationToken ct = default);
    Task<ExchangeResponse> PostOrderAsync(
        int assetIndex,
        bool isBuy,
        decimal price,
        decimal size,
        int szDecimals,
        bool reduceOnly = false,
        string tif = "Gtc",
        TriggerOrderTypeWire? trigger = null,
        string? cloid = null,
        CancellationToken ct = default
    );
    Task StartWebSocketStreamAsync(string coin, Action<string> onMessageReceived, CancellationToken ct);
}

/// <summary>
/// High-performance asynchronous client for the Hyperliquid Perpetual DEX.
/// </summary>
public class HyperliquidClient : IHyperliquidClient
{
    private readonly HttpClient _httpClient;
    private readonly HyperliquidSettings _settings;
    private readonly IHyperliquidSigner? _signer;
    private readonly ILogger<HyperliquidClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public HyperliquidClient(
        HttpClient httpClient,
        IOptions<BotSettings> settings,
        ILogger<HyperliquidClient> logger,
        IHyperliquidSigner? signer = null)
    {
        _httpClient = httpClient;
        _settings = settings.Value.Hyperliquid;
        _logger = logger;
        _signer = signer;

        _httpClient.BaseAddress = new Uri(_settings.ActiveApiUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(15);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<HyperliquidMetaResponse> GetUniverseMetaAsync(CancellationToken ct = default)
    {
        var requestPayload = new InfoRequest("meta");
        var response = await _httpClient.PostAsJsonAsync("/info", requestPayload, _jsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var meta = await response.Content.ReadFromJsonAsync<HyperliquidMetaResponse>(_jsonOptions, ct);
        return meta ?? new HyperliquidMetaResponse(new List<AssetMeta>());
    }

    public async Task<List<CandleSnapshot>> GetCandleSnapshotAsync(string coin, string interval, int limit = 100, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var endTime = now.ToUnixTimeMilliseconds();

        // Calculate approximate startTime based on interval
        var intervalSpan = interval switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "1h" => TimeSpan.FromHours(1),
            "4h" => TimeSpan.FromHours(4),
            "1d" => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1)
        };
        var startTime = now.Subtract(intervalSpan * limit).ToUnixTimeMilliseconds();

        var requestPayload = new InfoRequest(
            Type: "candleSnapshot",
            Req: new CandleSnapshotReq(coin, interval, startTime, endTime)
        );

        var response = await _httpClient.PostAsJsonAsync("/info", requestPayload, _jsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var candles = await response.Content.ReadFromJsonAsync<List<CandleSnapshot>>(_jsonOptions, ct);
        return candles ?? new List<CandleSnapshot>();
    }

    public async Task<ClearinghouseState> GetClearinghouseStateAsync(string userAddress, CancellationToken ct = default)
    {
        var requestPayload = new InfoRequest(
            Type: "clearinghouseState",
            User: userAddress
        );

        var response = await _httpClient.PostAsJsonAsync("/info", requestPayload, _jsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var state = await response.Content.ReadFromJsonAsync<ClearinghouseState>(_jsonOptions, ct);
        return state ?? throw new InvalidOperationException($"Unable to deserialize clearinghouse state for {userAddress}");
    }

    public async Task<L2BookResponse?> GetL2BookAsync(string coin, CancellationToken ct = default)
    {
        var requestPayload = new InfoRequest(
            Type: "l2Book",
            Coin: coin
        );

        var response = await _httpClient.PostAsJsonAsync("/info", requestPayload, _jsonOptions, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<L2BookResponse>(_jsonOptions, ct);
    }

    /// <summary>
    /// Formats order parameters, executes EIP-712 signing, and dispatches to POST /exchange.
    /// Prices and sizes MUST be formatted as strings with exact asset decimal constraints.
    /// </summary>
    public async Task<ExchangeResponse> PostOrderAsync(
        int assetIndex,
        bool isBuy,
        decimal price,
        decimal size,
        int szDecimals,
        bool reduceOnly = false,
        string tif = "Gtc",
        TriggerOrderTypeWire? trigger = null,
        string? cloid = null,
        CancellationToken ct = default)
    {
        if (_signer == null)
        {
            throw new InvalidOperationException("HyperliquidSigner is not configured. Cannot sign exchange orders.");
        }

        // Format size according to asset decimal precision
        var formattedSize = Math.Round(size, szDecimals).ToString($"F{szDecimals}", CultureInfo.InvariantCulture);

        // Prices in Hyperliquid perps are standard up to 6 significant figures or appropriate decimals
        var formattedPrice = price.ToString("0.######", CultureInfo.InvariantCulture);

        OrderTypeWire orderTypeWire;
        if (trigger != null)
        {
            orderTypeWire = new OrderTypeWire(Trigger: trigger);
        }
        else
        {
            orderTypeWire = new OrderTypeWire(Limit: new LimitOrderTypeWire(tif));
        }

        var orderWire = new OrderWire(
            Asset: assetIndex,
            IsBuy: isBuy,
            LimitPx: formattedPrice,
            Sz: formattedSize,
            ReduceOnly: reduceOnly,
            OrderType: orderTypeWire,
            Cloid: cloid
        );

        var orderAction = new OrderAction(
            Type: "order",
            Orders: new List<OrderWire> { orderWire },
            Grouping: "na"
        );

        var nonce = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var isMainnet = !_settings.UseTestnet;

        _logger.LogInformation("Signing order action for AssetIndex={AssetIndex}, IsBuy={IsBuy}, Px={Px}, Sz={Sz}, Nonce={Nonce}, Mainnet={IsMainnet}",
            assetIndex, isBuy, formattedPrice, formattedSize, nonce, isMainnet);

        var signature = _signer.SignAction(orderAction, nonce, isMainnet, _settings.VaultAddress);

        var payload = new ExchangeOrderPayload(
            Action: orderAction,
            Nonce: (long)nonce,
            Signature: signature,
            VaultAddress: _settings.VaultAddress
        );

        var response = await _httpClient.PostAsJsonAsync("/exchange", payload, _jsonOptions, ct);
        var rawResponse = await response.Content.ReadAsStringAsync(ct);

        _logger.LogInformation("Exchange response: Status={Status}, Body={Body}", response.StatusCode, rawResponse);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Exchange rejected order: {response.StatusCode} - {rawResponse}");
        }

        var exchangeResponse = JsonSerializer.Deserialize<ExchangeResponse>(rawResponse, _jsonOptions);
        return exchangeResponse ?? new ExchangeResponse("err", default);
    }

    /// <summary>
    /// Long-running background WebSocket client subscribing to real-time events.
    /// </summary>
    public async Task StartWebSocketStreamAsync(string coin, Action<string> onMessageReceived, CancellationToken ct)
    {
        var uri = new Uri(_settings.ActiveWsUrl);
        using var ws = new ClientWebSocket();

        _logger.LogInformation("Connecting to Hyperliquid WebSocket: {Uri}", uri);
        await ws.ConnectAsync(uri, ct);

        // Subscription request
        var subMessage = JsonSerializer.Serialize(new
        {
            method = "subscribe",
            subscription = new { type = "l2Book", coin }
        });
        var subBytes = Encoding.UTF8.GetBytes(subMessage);
        await ws.SendAsync(new ArraySegment<byte>(subBytes), WebSocketMessageType.Text, true, ct);

        var buffer = new byte[1024 * 32];
        var lastPing = DateTime.UtcNow;

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            // Send periodic heartbeat ping every 45s
            if ((DateTime.UtcNow - lastPing).TotalSeconds > 45)
            {
                var pingBytes = Encoding.UTF8.GetBytes("{\"method\":\"ping\"}");
                await ws.SendAsync(new ArraySegment<byte>(pingBytes), WebSocketMessageType.Text, true, ct);
                lastPing = DateTime.UtcNow;
            }

            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                break;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            onMessageReceived(message);
        }
    }
}

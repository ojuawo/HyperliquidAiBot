using System.Text.Json.Serialization;
using MessagePack;

namespace HyperliquidAiBot.Core.Models;

#region Info Endpoint Models

/// <summary>
/// General info request payload sent to /info.
/// </summary>
public record InfoRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("user")] string? User = null,
    [property: JsonPropertyName("coin")] string? Coin = null,
    [property: JsonPropertyName("req")] object? Req = null
);

/// <summary>
/// Universe metadata response mapping perpetual asset info.
/// </summary>
public record HyperliquidMetaResponse(
    [property: JsonPropertyName("universe")] List<AssetMeta> Universe
);

public record AssetMeta(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("szDecimals")] int SzDecimals,
    [property: JsonPropertyName("maxLeverage")] int MaxLeverage,
    [property: JsonPropertyName("onlyIsolated")] bool OnlyIsolated
);

/// <summary>
/// Historical candle snapshot request.
/// </summary>
public record CandleSnapshotReq(
    [property: JsonPropertyName("coin")] string Coin,
    [property: JsonPropertyName("interval")] string Interval,
    [property: JsonPropertyName("startTime")] long StartTime,
    [property: JsonPropertyName("endTime")] long EndTime
);

/// <summary>
/// Individual candlestick data returned by Hyperliquid.
/// </summary>
public record CandleSnapshot(
    [property: JsonPropertyName("t")] long OpenTimeMs,
    [property: JsonPropertyName("T")] long CloseTimeMs,
    [property: JsonPropertyName("s")] string Coin,
    [property: JsonPropertyName("i")] string Interval,
    [property: JsonPropertyName("o")] string Open,
    [property: JsonPropertyName("c")] string Close,
    [property: JsonPropertyName("h")] string High,
    [property: JsonPropertyName("l")] string Low,
    [property: JsonPropertyName("v")] string Volume,
    [property: JsonPropertyName("n")] long TradesCount
)
{
    [JsonIgnore]
    public decimal OpenDecimal => decimal.TryParse(Open, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
    [JsonIgnore]
    public decimal CloseDecimal => decimal.TryParse(Close, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
    [JsonIgnore]
    public decimal HighDecimal => decimal.TryParse(High, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
    [JsonIgnore]
    public decimal LowDecimal => decimal.TryParse(Low, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
    [JsonIgnore]
    public decimal VolumeDecimal => decimal.TryParse(Volume, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
    [JsonIgnore]
    public DateTime DateTimeUtc => DateTimeOffset.FromUnixTimeMilliseconds(OpenTimeMs).UtcDateTime;
}

/// <summary>
/// User clearinghouse balance and margin state.
/// </summary>
public record ClearinghouseState(
    [property: JsonPropertyName("marginSummary")] MarginSummary MarginSummary,
    [property: JsonPropertyName("crossMarginSummary")] MarginSummary? CrossMarginSummary,
    [property: JsonPropertyName("withdrawable")] string Withdrawable,
    [property: JsonPropertyName("assetPositions")] List<AssetPositionWrapper> AssetPositions
);

public record MarginSummary(
    [property: JsonPropertyName("accountValue")] string AccountValue,
    [property: JsonPropertyName("totalMarginUsed")] string TotalMarginUsed,
    [property: JsonPropertyName("totalNtlPos")] string TotalNtlPos,
    [property: JsonPropertyName("totalRawUsd")] string TotalRawUsd
)
{
    [JsonIgnore]
    public decimal AccountValueDecimal => decimal.TryParse(AccountValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
    [JsonIgnore]
    public decimal TotalMarginUsedDecimal => decimal.TryParse(TotalMarginUsed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
}

public record AssetPositionWrapper(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("position")] PositionDetails Position
);

public record PositionDetails(
    [property: JsonPropertyName("coin")] string Coin,
    [property: JsonPropertyName("szi")] string Szi,
    [property: JsonPropertyName("entryPx")] string? EntryPx,
    [property: JsonPropertyName("positionValue")] string? PositionValue,
    [property: JsonPropertyName("unrealizedPnl")] string? UnrealizedPnl,
    [property: JsonPropertyName("returnOnEquity")] string? ReturnOnEquity
)
{
    [JsonIgnore]
    public decimal SizeDecimal => decimal.TryParse(Szi, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
    [JsonIgnore]
    public decimal EntryPriceDecimal => decimal.TryParse(EntryPx, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
    [JsonIgnore]
    public decimal UnrealizedPnlDecimal => decimal.TryParse(UnrealizedPnl, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
}

/// <summary>
/// Orderbook L2 snapshot.
/// </summary>
public record L2BookResponse(
    [property: JsonPropertyName("coin")] string Coin,
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("levels")] List<List<BookLevel>> Levels
);

public record BookLevel(
    [property: JsonPropertyName("px")] string Px,
    [property: JsonPropertyName("sz")] string Sz,
    [property: JsonPropertyName("n")] int OrderCount
);

#endregion

#region Exchange / Order Signing Models

/// <summary>
/// Limit order type wire format.
/// </summary>
[MessagePackObject]
public record LimitOrderTypeWire(
    [property: Key("tif"), JsonPropertyName("tif")] string Tif
);

/// <summary>
/// Trigger order type wire format (Stop-Loss / Take-Profit).
/// </summary>
[MessagePackObject]
public record TriggerOrderTypeWire(
    [property: Key("triggerPx"), JsonPropertyName("triggerPx")] string TriggerPx,
    [property: Key("isMarket"), JsonPropertyName("isMarket")] bool IsMarket,
    [property: Key("tpsl"), JsonPropertyName("tpsl")] string Tpsl // "tp" or "sl"
);

/// <summary>
/// Order type container for limit or trigger orders.
/// </summary>
[MessagePackObject]
public record OrderTypeWire(
    [property: Key("limit"), JsonPropertyName("limit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LimitOrderTypeWire? Limit = null,
    [property: Key("trigger"), JsonPropertyName("trigger"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TriggerOrderTypeWire? Trigger = null
);

/// <summary>
/// Wire representation of a single Hyperliquid order action item.
/// </summary>
[MessagePackObject]
public record OrderWire(
    [property: Key("a"), JsonPropertyName("a")] int Asset,
    [property: Key("b"), JsonPropertyName("b")] bool IsBuy,
    [property: Key("p"), JsonPropertyName("p")] string LimitPx,
    [property: Key("s"), JsonPropertyName("s")] string Sz,
    [property: Key("r"), JsonPropertyName("r")] bool ReduceOnly,
    [property: Key("t"), JsonPropertyName("t")] OrderTypeWire OrderType,
    [property: Key("c"), JsonPropertyName("c"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cloid = null
);

/// <summary>
/// Order Action sent in exchange payload.
/// </summary>
[MessagePackObject]
public record OrderAction(
    [property: Key("type"), JsonPropertyName("type")] string Type,
    [property: Key("orders"), JsonPropertyName("orders")] List<OrderWire> Orders,
    [property: Key("grouping"), JsonPropertyName("grouping")] string Grouping = "na",
    [property: Key("builder"), JsonPropertyName("builder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Builder = null
);

/// <summary>
/// Cryptographic ECDSA signature components from EIP-712.
/// </summary>
public record HyperliquidSignature(
    [property: JsonPropertyName("r")] string R,
    [property: JsonPropertyName("s")] string S,
    [property: JsonPropertyName("v")] int V
);

/// <summary>
/// Full POST body payload to /exchange.
/// </summary>
public record ExchangeOrderPayload(
    [property: JsonPropertyName("action")] OrderAction Action,
    [property: JsonPropertyName("nonce")] long Nonce,
    [property: JsonPropertyName("signature")] HyperliquidSignature Signature,
    [property: JsonPropertyName("vaultAddress")] string? VaultAddress = null
);

/// <summary>
/// Exchange execution response wrapper.
/// </summary>
public record ExchangeResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("response")] System.Text.Json.JsonElement Response
);

#endregion

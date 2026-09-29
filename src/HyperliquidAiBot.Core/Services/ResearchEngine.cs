using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperliquidAiBot.Core.Services;

public interface IResearchEngine
{
    Task<TradeDecision> EvaluateMarketAsync(MarketContext context, CancellationToken ct = default);
}

/// <summary>
/// OpenAI-powered quantitative research engine using Structured Outputs (JSON Schema).
/// </summary>
public class ResearchEngine : IResearchEngine
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiSettings _settings;
    private readonly ILogger<ResearchEngine> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public ResearchEngine(
        HttpClient httpClient,
        IOptions<BotSettings> settings,
        ILogger<ResearchEngine> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value.OpenAi;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_settings.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
        }
        _httpClient.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds > 0 ? _settings.TimeoutSeconds : 30);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<TradeDecision> EvaluateMarketAsync(MarketContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            _logger.LogWarning("OpenAI API key not configured. Defaulting to HOLD decision.");
            return new TradeDecision
            {
                Action = TradeAction.Hold,
                Confidence = 0.0m,
                Reasoning = "OpenAI API key not configured."
            };
        }

        var systemPrompt = @"You are a Principal Quantitative Risk Analyst and Algorithmic Trading Strategist.
Your goal is to evaluate the technical indicators, current market structure, price momentum, and account exposure for the given cryptocurrency perpetual contract.
Make disciplined, high-probability trading decisions. Preserve capital as the top priority.
You must output strictly conforming JSON matching the provided schema.";

        var userPrompt = BuildAnalysisPrompt(context);

        var requestBody = new
        {
            model = _settings.Model,
            temperature = _settings.Temperature,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "trade_decision",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new
                            {
                                type = "string",
                                @enum = new[] { "Hold", "Buy", "Sell", "Close" },
                                description = "Trading action to take"
                            },
                            confidence = new
                            {
                                type = "number",
                                description = "Certainty score from 0.0 (no conviction) to 1.0 (extreme conviction)"
                            },
                            allocationPct = new
                            {
                                type = "number",
                                description = "Suggested percentage of portfolio to allocate (e.g. 0.015 for 1.5%)"
                            },
                            suggestedStopLossPct = new
                            {
                                type = "number",
                                description = "Suggested stop-loss percentage from entry price (e.g. 0.02 for 2%)"
                            },
                            suggestedTakeProfitPct = new
                            {
                                type = "number",
                                description = "Suggested take-profit percentage from entry price (e.g. 0.04 for 4%)"
                            },
                            reasoning = new
                            {
                                type = "string",
                                description = "Concise quantitative justification based on indicators and market conditions"
                            },
                            keyIndicatorsCited = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "List of indicators driving this decision (e.g. RSI, MACD, EMA_CROSS)"
                            }
                        },
                        required = new[]
                        {
                            "action",
                            "confidence",
                            "allocationPct",
                            "suggestedStopLossPct",
                            "suggestedTakeProfitPct",
                            "reasoning",
                            "keyIndicatorsCited"
                        },
                        additionalProperties = false
                    }
                }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            request.Content = JsonContent.Create(requestBody);

            var response = await _httpClient.SendAsync(request, ct);
            var rawContent = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI API call failed with status {StatusCode}: {Error}", response.StatusCode, rawContent);
                return new TradeDecision
                {
                    Action = TradeAction.Hold,
                    Confidence = 0.0m,
                    Reasoning = $"OpenAI API error: {response.StatusCode}"
                };
            }

            var jsonDoc = JsonDocument.Parse(rawContent);
            var choiceContent = jsonDoc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(choiceContent))
            {
                _logger.LogWarning("OpenAI returned empty message content.");
                return new TradeDecision { Action = TradeAction.Hold, Confidence = 0.0m, Reasoning = "Empty LLM response" };
            }

            var decision = JsonSerializer.Deserialize<TradeDecision>(choiceContent, _jsonOptions);
            _logger.LogInformation("ResearchEngine produced decision: Action={Action}, Confidence={Confidence}, Allocation={Alloc:P1}",
                decision?.Action, decision?.Confidence, decision?.AllocationPct);

            return decision ?? new TradeDecision { Action = TradeAction.Hold, Confidence = 0.0m, Reasoning = "Deserialization failed" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during ResearchEngine market evaluation");
            return new TradeDecision
            {
                Action = TradeAction.Hold,
                Confidence = 0.0m,
                Reasoning = $"Exception in ResearchEngine: {ex.Message}"
            };
        }
    }

    private static string BuildAnalysisPrompt(MarketContext ctx)
    {
        var ind = ctx.Indicators;
        return $@"Analyze market conditions for perpetual asset {ctx.Asset}:
- Current Price: ${ctx.CurrentPrice:F2} (Best Bid: ${ctx.BestBid:F2}, Best Ask: ${ctx.BestAsk:F2})
- Account Equity: ${ctx.AccountEquity:F2} | Available Margin: ${ctx.AvailableMargin:F2}
- Current Position Size: {ctx.CurrentPositionSize:F4} (Entry: ${ctx.CurrentPositionEntryPrice:F2}, Unrealized PnL: ${ctx.UnrealizedPnl:F2})

Technical Indicators:
- Trend Signal: {ind.TrendSignal} (Overbought: {ind.IsOverbought}, Oversold: {ind.IsOversold})
- RSI (14): {ind.Rsi:F2}
- MACD: {ind.Macd:F4} | Signal: {ind.MacdSignal:F4} | Histogram: {ind.MacdHistogram:F4}
- EMAs: EMA(9)={ind.Ema9:F2}, EMA(21)={ind.Ema21:F2}
- SMAs: SMA(20)={ind.Sma20:F2}, SMA(50)={ind.Sma50:F2}, SMA(200)={ind.Sma200:F2}
- Bollinger Bands: Upper=${ind.BollingerUpper:F2}, Mid=${ind.BollingerMiddle:F2}, Lower=${ind.BollingerLower:F2}
- ATR (14): ${ind.Atr:F2}

Recommend action: Buy, Sell, Hold, or Close. Specify confidence [0.0 - 1.0], allocation percentage, and stop loss.";
    }
}

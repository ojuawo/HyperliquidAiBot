using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperliquidAiBot.Core.Services;

public interface IResearchEngine
{
    Task<TradeDecision> EvaluateMarketAsync(MarketContext context, CancellationToken ct = default);
    string ActiveModel { get; }
    string ActiveProvider { get; }
}

/// <summary>
/// Quantitative research engine supporting Google Gemini (native structured JSON) and OpenAI.
/// </summary>
public class ResearchEngine : IResearchEngine
{
    private readonly HttpClient _httpClient;
    private readonly BotSettings _settings;
    private readonly ILogger<ResearchEngine> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public string ActiveProvider { get; }
    public string ActiveModel { get; }

    public ResearchEngine(
        HttpClient httpClient,
        IOptions<BotSettings> settings,
        ILogger<ResearchEngine> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        // Resolve active provider and model
        ActiveProvider = !string.IsNullOrWhiteSpace(_settings.Llm.Provider) ? _settings.Llm.Provider : "Gemini";
        ActiveModel = !string.IsNullOrWhiteSpace(_settings.Llm.Model) ? _settings.Llm.Model : "gemini-2.5-flash";

        var timeout = _settings.Llm.TimeoutSeconds > 0 ? _settings.Llm.TimeoutSeconds : 30;
        _httpClient.Timeout = TimeSpan.FromSeconds(timeout);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };
        _jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    }

    public async Task<TradeDecision> EvaluateMarketAsync(MarketContext context, CancellationToken ct = default)
    {
        var apiKey = !string.IsNullOrWhiteSpace(_settings.Llm.ApiKey)
            ? _settings.Llm.ApiKey
            : _settings.OpenAi.ApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("{Provider} API key not configured. Defaulting to HOLD decision.", ActiveProvider);
            return new TradeDecision
            {
                Action = TradeAction.Hold,
                Confidence = 0.0m,
                Reasoning = $"{ActiveProvider} API key not configured. (Set BOTSETTINGS__LLM__APIKEY in environment or appsettings.json)"
            };
        }

        var systemPrompt = @"You are a Principal Quantitative Risk Analyst and Algorithmic Trading Strategist.
Your goal is to evaluate technical indicators, current market structure, price momentum, and account exposure for the given cryptocurrency perpetual contract.
Make disciplined, high-probability trading decisions. Preserve capital as the top priority.
You must output strictly conforming JSON matching the provided schema.";

        var userPrompt = BuildAnalysisPrompt(context);

        if (ActiveProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            return await EvaluateWithGeminiAsync(apiKey, systemPrompt, userPrompt, ct);
        }

        return await EvaluateWithOpenAiAsync(apiKey, systemPrompt, userPrompt, ct);
    }

    private async Task<TradeDecision> EvaluateWithGeminiAsync(string apiKey, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var baseEndpoint = !string.IsNullOrWhiteSpace(_settings.Llm.BaseUrl)
            ? _settings.Llm.BaseUrl.TrimEnd('/')
            : "https://generativelanguage.googleapis.com/v1beta";
        var url = $"{baseEndpoint}/models/{ActiveModel}:generateContent";

        var requestBody = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = systemPrompt } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userPrompt } }
                }
            },
            generationConfig = new
            {
                temperature = _settings.Llm.Temperature,
                responseMimeType = "application/json",
                responseSchema = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        action = new
                        {
                            type = "STRING",
                            @enum = new[] { "Hold", "Buy", "Sell", "Close" }
                        },
                        confidence = new { type = "NUMBER" },
                        allocationPct = new { type = "NUMBER" },
                        suggestedStopLossPct = new { type = "NUMBER" },
                        suggestedTakeProfitPct = new { type = "NUMBER" },
                        reasoning = new { type = "STRING" },
                        keyIndicatorsCited = new
                        {
                            type = "ARRAY",
                            items = new { type = "STRING" }
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
                    }
                }
            }
        };

        try
        {
            _logger.LogInformation("Calling Google Gemini ({Model}) with structured schema...", ActiveModel);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Content = JsonContent.Create(requestBody, options: _jsonOptions);

            var response = await _httpClient.SendAsync(request, ct);
            var rawContent = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Gemini API call failed ({Status}): {Body}", response.StatusCode, rawContent);
                return new TradeDecision
                {
                    Action = TradeAction.Hold,
                    Confidence = 0.0m,
                    Reasoning = $"Gemini API error ({response.StatusCode}): {rawContent}"
                };
            }

            var jsonDoc = JsonDocument.Parse(rawContent);
            var text = jsonDoc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("Gemini returned empty candidate text.");
                return new TradeDecision { Action = TradeAction.Hold, Confidence = 0.0m, Reasoning = "Empty Gemini response" };
            }

            var cleanedText = text.Trim();
            if (cleanedText.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                cleanedText = cleanedText.Substring(7);
            }
            else if (cleanedText.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                cleanedText = cleanedText.Substring(3);
            }

            if (cleanedText.EndsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                cleanedText = cleanedText.Substring(0, cleanedText.Length - 3);
            }
            cleanedText = cleanedText.Trim();

            var decision = JsonSerializer.Deserialize<TradeDecision>(cleanedText, _jsonOptions);
            _logger.LogInformation("Gemini Decision: Action={Action}, Confidence={Confidence:F2}, Allocation={Alloc:P1}",
                decision?.Action, decision?.Confidence, decision?.AllocationPct);

            return decision ?? new TradeDecision { Action = TradeAction.Hold, Confidence = 0.0m, Reasoning = "Deserialization failed" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception calling Google Gemini API");
            return new TradeDecision
            {
                Action = TradeAction.Hold,
                Confidence = 0.0m,
                Reasoning = $"Gemini Exception: {ex.Message}"
            };
        }
    }

    private async Task<TradeDecision> EvaluateWithOpenAiAsync(string apiKey, string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(_settings.OpenAi.BaseUrl)
            ? _settings.OpenAi.BaseUrl
            : "https://api.openai.com/v1/";

        var requestBody = new
        {
            model = !string.IsNullOrWhiteSpace(_settings.OpenAi.Model) ? _settings.OpenAi.Model : "gpt-4o",
            temperature = _settings.OpenAi.Temperature,
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
                            action = new { type = "string", @enum = new[] { "Hold", "Buy", "Sell", "Close" } },
                            confidence = new { type = "number" },
                            allocationPct = new { type = "number" },
                            suggestedStopLossPct = new { type = "number" },
                            suggestedTakeProfitPct = new { type = "number" },
                            reasoning = new { type = "string" },
                            keyIndicatorsCited = new { type = "array", items = new { type = "string" } }
                        },
                        required = new[] { "action", "confidence", "allocationPct", "suggestedStopLossPct", "suggestedTakeProfitPct", "reasoning", "keyIndicatorsCited" },
                        additionalProperties = false
                    }
                }
            }
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "chat/completions"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = JsonContent.Create(requestBody);

            var response = await _httpClient.SendAsync(req, ct);
            var rawContent = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("OpenAI API call failed ({Status}): {Body}", response.StatusCode, rawContent);
                return new TradeDecision { Action = TradeAction.Hold, Confidence = 0.0m, Reasoning = $"OpenAI error: {response.StatusCode}" };
            }

            var jsonDoc = JsonDocument.Parse(rawContent);
            var choice = jsonDoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return JsonSerializer.Deserialize<TradeDecision>(choice ?? "{}", _jsonOptions) ?? new TradeDecision();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception calling OpenAI API");
            return new TradeDecision { Action = TradeAction.Hold, Confidence = 0.0m, Reasoning = $"OpenAI Exception: {ex.Message}" };
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

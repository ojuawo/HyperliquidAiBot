using System.Net;
using System.Text.Json;
using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using HyperliquidAiBot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HyperliquidAiBot.Tests;

public class ResearchEngineTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private static MarketContext CreateSampleContext()
    {
        return new MarketContext
        {
            Asset = "BTC",
            CurrentPrice = 90000m,
            BestBid = 89995m,
            BestAsk = 90005m,
            AccountEquity = 10000m,
            AvailableMargin = 9500m,
            CurrentPositionSize = 0m,
            CurrentPositionEntryPrice = 0m,
            UnrealizedPnl = 0m,
            Indicators = new TechnicalIndicatorsSnapshot
            {
                Rsi = 45m,
                Macd = 12.5m,
                MacdSignal = 10.0m,
                MacdHistogram = 2.5m,
                Ema9 = 90100m,
                Ema21 = 89800m,
                Sma20 = 89900m,
                Sma50 = 89000m,
                Sma200 = 85000m,
                BollingerUpper = 92000m,
                BollingerMiddle = 90000m,
                BollingerLower = 88000m,
                Atr = 1200m,
                TrendSignal = "Bullish"
            }
        };
    }

    [Fact]
    public async Task EvaluateMarketAsync_MissingApiKey_ReturnsHoldWithExplanation()
    {
        var settings = Options.Create(new BotSettings
        {
            Llm = new LlmSettings { Provider = "Gemini", ApiKey = "" }
        });

        var client = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var engine = new ResearchEngine(client, settings, NullLogger<ResearchEngine>.Instance);

        var result = await engine.EvaluateMarketAsync(CreateSampleContext());

        Assert.Equal(TradeAction.Hold, result.Action);
        Assert.Equal(0.0m, result.Confidence);
        Assert.Contains("API key not configured", result.Reasoning);
    }

    [Fact]
    public async Task EvaluateMarketAsync_GeminiSuccess_ParsesDecisionAccurately()
    {
        var expectedDecisionJson = """
        {
            "action": "Buy",
            "confidence": 0.85,
            "allocationPct": 0.02,
            "suggestedStopLossPct": 0.025,
            "suggestedTakeProfitPct": 0.05,
            "reasoning": "Bullish momentum aligned with EMA9 above EMA21 and healthy RSI.",
            "keyIndicatorsCited": ["EMA_STACK", "RSI"]
        }
        """;

        var geminiEnvelope = $$"""
        {
            "candidates": [
                {
                    "content": {
                        "parts": [
                            {
                                "text": {{JsonSerializer.Serialize(expectedDecisionJson)}}
                            }
                        ]
                    }
                }
            ]
        }
        """;

        HttpRequestMessage? interceptedRequest = null;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            interceptedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(geminiEnvelope, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var settings = Options.Create(new BotSettings
        {
            Llm = new LlmSettings
            {
                Provider = "Gemini",
                ApiKey = "AIzaSyFakeKey123",
                Model = "gemini-3.5-flash"
            }
        });

        var client = new HttpClient(mockHandler);
        var engine = new ResearchEngine(client, settings, NullLogger<ResearchEngine>.Instance);

        var result = await engine.EvaluateMarketAsync(CreateSampleContext());

        Assert.NotNull(interceptedRequest);
        Assert.True(interceptedRequest.Headers.Contains("x-goog-api-key"));
        Assert.Equal("AIzaSyFakeKey123", interceptedRequest.Headers.GetValues("x-goog-api-key").First());
        Assert.Contains("/models/gemini-3.5-flash:generateContent", interceptedRequest.RequestUri?.ToString());

        Assert.Equal(TradeAction.Buy, result.Action);
        Assert.Equal(0.85m, result.Confidence);
        Assert.Equal(0.02m, result.AllocationPct);
        Assert.Equal(0.025m, result.SuggestedStopLossPct);
        Assert.Equal(0.05m, result.SuggestedTakeProfitPct);
        Assert.Equal(2, result.KeyIndicatorsCited.Count);
    }

    [Fact]
    public async Task EvaluateMarketAsync_GeminiWithMarkdownFences_StripsAndParsesCleanly()
    {
        var rawDecisionWithMarkdown = "```json\n{\n  \"action\": \"Sell\",\n  \"confidence\": 0.80,\n  \"allocationPct\": 0.015,\n  \"suggestedStopLossPct\": 0.02,\n  \"suggestedTakeProfitPct\": 0.04,\n  \"reasoning\": \"Overbought RSI divergence.\",\n  \"keyIndicatorsCited\": [\"RSI\"]\n}\n```";

        var geminiEnvelope = $$"""
        {
            "candidates": [
                {
                    "content": {
                        "parts": [
                            {
                                "text": {{JsonSerializer.Serialize(rawDecisionWithMarkdown)}}
                            }
                        ]
                    }
                }
            ]
        }
        """;

        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(geminiEnvelope, System.Text.Encoding.UTF8, "application/json")
        });

        var settings = Options.Create(new BotSettings
        {
            Llm = new LlmSettings
            {
                Provider = "Gemini",
                ApiKey = "AIzaSyFakeKey123",
                Model = "gemini-3.5-flash"
            }
        });

        var client = new HttpClient(mockHandler);
        var engine = new ResearchEngine(client, settings, NullLogger<ResearchEngine>.Instance);

        var result = await engine.EvaluateMarketAsync(CreateSampleContext());

        Assert.Equal(TradeAction.Sell, result.Action);
        Assert.Equal(0.80m, result.Confidence);
        Assert.Equal(0.015m, result.AllocationPct);
    }

    [Fact]
    public async Task EvaluateMarketAsync_GeminiHttpError_ReturnsHoldGracefully()
    {
        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("Quota exceeded", System.Text.Encoding.UTF8, "text/plain")
        });

        var settings = Options.Create(new BotSettings
        {
            Llm = new LlmSettings
            {
                Provider = "Gemini",
                ApiKey = "AIzaSyFakeKey123",
                Model = "gemini-3.5-flash"
            }
        });

        var client = new HttpClient(mockHandler);
        var engine = new ResearchEngine(client, settings, NullLogger<ResearchEngine>.Instance);

        var result = await engine.EvaluateMarketAsync(CreateSampleContext());

        Assert.Equal(TradeAction.Hold, result.Action);
        Assert.Equal(0.0m, result.Confidence);
        Assert.Contains("Gemini API error", result.Reasoning);
    }
}

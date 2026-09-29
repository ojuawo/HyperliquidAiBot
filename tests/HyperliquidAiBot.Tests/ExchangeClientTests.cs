using HyperliquidAiBot.Core.Config;
using HyperliquidAiBot.Core.Models;
using HyperliquidAiBot.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HyperliquidAiBot.Tests;

public class ExchangeClientTests
{
    [Fact]
    public void PaperTrading_NormalizeSymbol_AppendsUsdtIfMissing()
    {
        Assert.Equal("BTCUSDT", PaperTradingExchangeClient.NormalizeSymbol("BTC"));
        Assert.Equal("ETHUSDT", PaperTradingExchangeClient.NormalizeSymbol("eth"));
        Assert.Equal("SOLUSDT", PaperTradingExchangeClient.NormalizeSymbol("SOLUSDT"));
    }

    [Fact]
    public async Task PaperTrading_PlaceOrderAndCheckPortfolio_UpdatesPositionAndMargin()
    {
        var client = new HttpClient();
        var paperClient = new PaperTradingExchangeClient(
            client,
            NullLogger<PaperTradingExchangeClient>.Instance,
            startingBalance: 10000m
        );

        // 1. Initial State
        var initial = await paperClient.GetAccountPortfolioAsync();
        Assert.Equal(10000m, initial.AccountEquity);
        Assert.Equal(10000m, initial.AvailableMargin);
        Assert.Empty(initial.Positions);

        // 2. Buy 0.1 BTC @ $80,000
        var buyOrder = new TradeOrderRequest(
            Symbol: "BTCUSDT",
            Side: TradeOrderSide.Buy,
            Type: TradeOrderType.Market,
            Size: 0.1m,
            Price: 80000m
        );

        var result = await paperClient.PlaceOrderAsync(buyOrder);
        Assert.True(result.Success);
        Assert.Equal("FILLED", result.Status);
        Assert.Equal(80000m, result.ExecutedPrice);

        // 3. Verify Position
        var portfolioAfterBuy = await paperClient.GetAccountPortfolioAsync();
        var pos = portfolioAfterBuy.GetPosition("BTCUSDT");
        Assert.NotNull(pos);
        Assert.Equal(0.1m, pos.Size);
        Assert.Equal(80000m, pos.EntryPrice);

        // Margin used = 0.1 * 80000 = $8,000
        Assert.Equal(2000m, portfolioAfterBuy.AvailableMargin);

        // 4. Sell / Close 0.1 BTC @ $85,000 (Profit = 0.1 * 5000 = +$500)
        var sellOrder = new TradeOrderRequest(
            Symbol: "BTCUSDT",
            Side: TradeOrderSide.Sell,
            Type: TradeOrderType.Market,
            Size: 0.1m,
            Price: 85000m
        );

        var sellResult = await paperClient.PlaceOrderAsync(sellOrder);
        Assert.True(sellResult.Success);

        var portfolioAfterSell = await paperClient.GetAccountPortfolioAsync();
        // New equity should be $10,000 + $500 = $10,500
        Assert.Equal(10500m, portfolioAfterSell.AccountEquity);
        Assert.Equal(10500m, portfolioAfterSell.AvailableMargin);
        Assert.Equal(0m, portfolioAfterSell.GetPosition("BTCUSDT")?.Size ?? 0m);
    }

    [Fact]
    public async Task BinanceFutures_UnconfiguredKeys_ReturnsFallbackTestnetState()
    {
        var settings = Options.Create(new BotSettings
        {
            Binance = new BinanceSettings { UseTestnet = true, ApiKey = "", ApiSecret = "" }
        });

        var client = new HttpClient();
        var binanceClient = new BinanceFuturesExchangeClient(
            client,
            settings,
            NullLogger<BinanceFuturesExchangeClient>.Instance
        );

        var portfolio = await binanceClient.GetAccountPortfolioAsync();
        Assert.Equal(15000m, portfolio.AccountEquity);

        var order = new TradeOrderRequest("BTCUSDT", TradeOrderSide.Buy, TradeOrderType.Market, 0.01m, 80000m);
        var exec = await binanceClient.PlaceOrderAsync(order);
        Assert.True(exec.Success);
        Assert.Equal("FILLED_SIMULATED", exec.Status);
    }
}

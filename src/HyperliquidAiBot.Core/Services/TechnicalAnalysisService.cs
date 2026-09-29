using HyperliquidAiBot.Core.Models;
using Microsoft.Extensions.Logging;
using Skender.Stock.Indicators;

namespace HyperliquidAiBot.Core.Services;

public interface ITechnicalAnalysisService
{
    TechnicalIndicatorsSnapshot CalculateIndicators(IReadOnlyList<CandleSnapshot> candles);
}

/// <summary>
/// Production technical analysis calculator wrapping Skender.Stock.Indicators.
/// </summary>
public class TechnicalAnalysisService : ITechnicalAnalysisService
{
    private readonly ILogger<TechnicalAnalysisService> _logger;

    public TechnicalAnalysisService(ILogger<TechnicalAnalysisService> logger)
    {
        _logger = logger;
    }

    public TechnicalIndicatorsSnapshot CalculateIndicators(IReadOnlyList<CandleSnapshot> candles)
    {
        if (candles == null || candles.Count < 20)
        {
            _logger.LogWarning("Insufficient candle data ({Count} candles) to compute full TA indicators", candles?.Count ?? 0);
            return new TechnicalIndicatorsSnapshot();
        }

        // Convert Hyperliquid candles into Skender Quote objects, sorted ascending by time
        var quotes = candles
            .OrderBy(c => c.OpenTimeMs)
            .Select(c => new Quote
            {
                Date = c.DateTimeUtc,
                Open = c.OpenDecimal,
                High = c.HighDecimal,
                Low = c.LowDecimal,
                Close = c.CloseDecimal,
                Volume = c.VolumeDecimal
            })
            .ToList();

        try
        {
            // 1. RSI (14)
            var rsiResults = quotes.GetRsi(14).ToList();
            var lastRsi = rsiResults.LastOrDefault()?.Rsi;

            // 2. MACD (12, 26, 9)
            var macdResults = quotes.GetMacd(12, 26, 9).ToList();
            var lastMacd = macdResults.LastOrDefault();

            // 3. SMAs (20, 50, 200 if sufficient bars)
            var sma20Results = quotes.GetSma(20).ToList();
            var lastSma20 = sma20Results.LastOrDefault()?.Sma;

            double? lastSma50 = null;
            if (quotes.Count >= 50)
            {
                lastSma50 = quotes.GetSma(50).LastOrDefault()?.Sma;
            }

            double? lastSma200 = null;
            if (quotes.Count >= 200)
            {
                lastSma200 = quotes.GetSma(200).LastOrDefault()?.Sma;
            }

            // 4. EMAs (9, 21)
            var ema9Results = quotes.GetEma(9).ToList();
            var lastEma9 = ema9Results.LastOrDefault()?.Ema;

            var ema21Results = quotes.GetEma(21).ToList();
            var lastEma21 = ema21Results.LastOrDefault()?.Ema;

            // 5. Bollinger Bands (20, 2)
            var bbResults = quotes.GetBollingerBands(20, 2).ToList();
            var lastBb = bbResults.LastOrDefault();

            // 6. ATR (14)
            var atrResults = quotes.GetAtr(14).ToList();
            var lastAtr = atrResults.LastOrDefault()?.Atr;

            // Derive overall trend signal
            var trendSignal = DetermineTrendSignal(lastRsi, lastMacd, lastEma9, lastEma21, lastSma20);

            return new TechnicalIndicatorsSnapshot
            {
                Rsi = lastRsi.HasValue ? (decimal)lastRsi.Value : null,
                Macd = lastMacd?.Macd.HasValue == true ? (decimal)lastMacd.Macd.Value : null,
                MacdSignal = lastMacd?.Signal.HasValue == true ? (decimal)lastMacd.Signal.Value : null,
                MacdHistogram = lastMacd?.Histogram.HasValue == true ? (decimal)lastMacd.Histogram.Value : null,
                Sma20 = lastSma20.HasValue ? (decimal)lastSma20.Value : null,
                Sma50 = lastSma50.HasValue ? (decimal)lastSma50.Value : null,
                Sma200 = lastSma200.HasValue ? (decimal)lastSma200.Value : null,
                Ema9 = lastEma9.HasValue ? (decimal)lastEma9.Value : null,
                Ema21 = lastEma21.HasValue ? (decimal)lastEma21.Value : null,
                BollingerUpper = lastBb?.UpperBand.HasValue == true ? (decimal)lastBb.UpperBand.Value : null,
                BollingerMiddle = lastBb?.Sma.HasValue == true ? (decimal)lastBb.Sma.Value : null,
                BollingerLower = lastBb?.LowerBand.HasValue == true ? (decimal)lastBb.LowerBand.Value : null,
                Atr = lastAtr.HasValue ? (decimal)lastAtr.Value : null,
                TrendSignal = trendSignal
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing technical indicators");
            return new TechnicalIndicatorsSnapshot();
        }
    }

    private static string DetermineTrendSignal(double? rsi, MacdResult? macd, double? ema9, double? ema21, double? sma20)
    {
        int bullishScore = 0;
        int bearishScore = 0;

        if (rsi.HasValue)
        {
            if (rsi.Value is > 50 and < 70) bullishScore++;
            else if (rsi.Value is < 50 and > 30) bearishScore++;
            else if (rsi.Value <= 30) bullishScore++; // oversold reversal potential
            else if (rsi.Value >= 70) bearishScore++; // overbought exhaustion potential
        }

        if (macd?.Histogram.HasValue == true)
        {
            if (macd.Histogram.Value > 0) bullishScore++;
            else if (macd.Histogram.Value < 0) bearishScore++;
        }

        if (ema9.HasValue && ema21.HasValue)
        {
            if (ema9.Value > ema21.Value) bullishScore++;
            else if (ema9.Value < ema21.Value) bearishScore++;
        }

        if (bullishScore > bearishScore) return "Bullish";
        if (bearishScore > bullishScore) return "Bearish";
        return "Neutral";
    }
}

namespace HyperliquidAiBot.Core.Config;

/// <summary>
/// Root application settings mapped from appsettings.json and environment variables.
/// </summary>
public class BotSettings
{
    public const string SectionName = "BotSettings";

    public HyperliquidSettings Hyperliquid { get; set; } = new();
    public LlmSettings Llm { get; set; } = new();
    public OpenAiSettings OpenAi { get; set; } = new(); // Backward compatibility
    public RiskRules Risk { get; set; } = new();
    public ExecutionSettings Execution { get; set; } = new();
}

public class HyperliquidSettings
{
    /// <summary>
    /// When true, targets Hyperliquid Testnet; otherwise Mainnet.
    /// </summary>
    public bool UseTestnet { get; set; } = true;

    public string MainnetApiUrl { get; set; } = "https://api.hyperliquid.xyz";
    public string TestnetApiUrl { get; set; } = "https://api.hyperliquid-testnet.xyz";

    public string MainnetWsUrl { get; set; } = "wss://api.hyperliquid.xyz/ws";
    public string TestnetWsUrl { get; set; } = "wss://api.hyperliquid-testnet.xyz/ws";

    /// <summary>
    /// Ethereum private key (with or without 0x prefix) for EIP-712 order signing.
    /// </summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Ethereum public wallet or authorized agent wallet address.
    /// </summary>
    public string WalletAddress { get; set; } = string.Empty;

    /// <summary>
    /// Optional vault address if trading on behalf of a Hyperliquid vault.
    /// </summary>
    public string? VaultAddress { get; set; }

    /// <summary>
    /// Target trading asset symbol (e.g., "BTC", "ETH", "SOL").
    /// </summary>
    public string Asset { get; set; } = "BTC";

    /// <summary>
    /// Candle interval for technical analysis (e.g. "1m", "5m", "15m", "1h", "4h", "1d").
    /// </summary>
    public string CandleInterval { get; set; } = "1h";

    /// <summary>
    /// Number of historical candle snapshots to fetch for TA calculation.
    /// </summary>
    public int CandleLimit { get; set; } = 100;

    /// <summary>
    /// Frequency of trading worker evaluation loop in seconds.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Default Time-In-Force: "Gtc" (Good-Til-Cancelled), "Ioc" (Immediate-Or-Cancel), "Alo" (Add-Liquidity-Only / Post-Only).
    /// </summary>
    public string DefaultTif { get; set; } = "Gtc";

    /// <summary>
    /// Active API URL based on UseTestnet setting.
    /// </summary>
    public string ActiveApiUrl => UseTestnet ? TestnetApiUrl : MainnetApiUrl;

    /// <summary>
    /// Active WebSocket URL based on UseTestnet setting.
    /// </summary>
    public string ActiveWsUrl => UseTestnet ? TestnetWsUrl : MainnetWsUrl;
}

/// <summary>
/// Quantitative AI Model configuration supporting Google Gemini and OpenAI.
/// </summary>
public class LlmSettings
{
    /// <summary>
    /// LLM Provider: "Gemini" (default) or "OpenAI".
    /// </summary>
    public string Provider { get; set; } = "Gemini";

    /// <summary>
    /// API Key for Google Gemini (from Google AI Studio) or OpenAI.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model name. For Gemini: "gemini-2.5-flash", "gemini-2.5-pro", "gemini-1.5-pro", "gemini-1.5-flash".
    /// For OpenAI: "gpt-4o", "gpt-4o-mini".
    /// </summary>
    public string Model { get; set; } = "gemini-3.5-flash";

    public double Temperature { get; set; } = 0.2;
    public string? BaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public class OpenAiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o";
    public double Temperature { get; set; } = 0.2;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public int TimeoutSeconds { get; set; } = 30;
}

public class ExecutionSettings
{
    /// <summary>
    /// If true, simulates all order placements without dispatching signed transactions to the live exchange.
    /// </summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// Heartbeat status file written on every loop tick for VPS liveness monitoring.
    /// </summary>
    public string HeartbeatFilePath { get; set; } = "/tmp/hyperliquid_bot_heartbeat";
}

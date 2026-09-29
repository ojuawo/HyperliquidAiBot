# Hyperliquid AI Trading Bot (.NET 8)

A production-grade, asynchronous C# (.NET 8) AI-driven quantitative trading bot targeting the **Hyperliquid Perpetual DEX**, engineered for containerized Linux VPS deployment and native systemd execution.

---

## Architecture Overview

```
HyperliquidAiBot/
├── src/
│   ├── HyperliquidAiBot.Core/
│   │   ├── Models/
│   │   │   ├── MarketContext.cs        # Aggregated market, TA, and portfolio state
│   │   │   ├── TradeDecision.cs        # AI proposals & risk evaluation contracts
│   │   │   └── HyperliquidPayloads.cs  # REST, WS, MessagePack & EIP-712 payload schemas
│   │   ├── Services/
│   │   │   ├── HyperliquidSigner.cs    # EIP-712 Phantom Agent signer (Nethereum)
│   │   │   ├── HyperliquidClient.cs    # REST & WebSocket client (/info, /exchange)
│   │   │   ├── TechnicalAnalysisService.cs # Skender.Stock.Indicators TA engine
│   │   │   ├── ResearchEngine.cs       # Google Gemini & OpenAI Quantitative Engine (JSON Schema)
│   │   │   └── RiskManager.cs          # Deterministic safety guardrail engine
│   │   └── Config/
│   │       ├── BotSettings.cs          # Options pattern configuration
│   │       └── RiskRules.cs            # Immutable risk parameters
│   └── HyperliquidAiBot.Worker/
│       ├── Program.cs                  # HostBuilder, DI, Systemd integration
│       ├── TradingWorker.cs            # BackgroundService execution loop
│       ├── appsettings.json
│       └── appsettings.Development.json
├── tests/
│   └── HyperliquidAiBot.Tests/
│       ├── RiskManagerTests.cs         # xUnit tests for size caps, stop loss, and drawdown freeze
│       └── Eip712SigningTests.cs       # Verification of Hyperliquid EIP-712 signing logic
├── Dockerfile                          # Multi-stage build (sdk:8.0 -> alpine:8.0)
├── docker-compose.yml                  # VPS compose configuration with log rotation
├── .env.example
└── README.md
```

---

## Key Hyperliquid Specifications

1. **EIP-712 Authentication**: Hyperliquid uses typed message signing via Ethereum private keys (either master wallet or an approved Agent Wallet) rather than API Key/Secret pairs.
   - The bot packages orders as `OrderAction`, computes a binary `action_hash` via `MessagePack` and `Keccak-256`, and constructs an EIP-712 `Agent` struct on `chainId: 1337` (`source: "a"` on Mainnet, `"b"` on Testnet).
2. **Endpoints**:
   - **Testnet**: `https://api.hyperliquid-testnet.xyz` | WebSocket: `wss://api.hyperliquid-testnet.xyz/ws`
   - **Mainnet**: `https://api.hyperliquid.xyz` | WebSocket: `wss://api.hyperliquid.xyz/ws`
3. **Asset Indexing**: Assets are indexed via integers discovered through `/info` universe metadata (e.g. BTC = 0, ETH = 1).
4. **Data Formatting**: Price and size fields in payloads are formatted as strings with exact asset decimal constraints (`szDecimals`). Minimum order notional is **$10 USD**.

---

## Deterministic Safety Guardrails (`RiskManager.cs`)

Before any order is dispatched, `RiskManager` enforces strict C# safety rules:

- **Confidence Threshold**: Orders are rejected if `decision.Confidence < MinConfidenceThreshold` (default `0.75`).
- **Portfolio Allocation Cap**: Position size is strictly capped at `MaxPortfolioAllocationPct` (default `2.0%` of portfolio equity).
- **Mandatory Stop-Loss Range**: Hardcoded clamp enforcing $1.0\% \le \text{stop\_loss} \le 5.0\%$. Stop-loss trigger orders are dispatched automatically upon order fill.
- **Daily Max Drawdown Circuit Breaker**: Tracks start-of-day equity (00:00 UTC reset). If account balance experiences $\ge 5\%$ loss, trading is immediately frozen for the remainder of the trading day.
- **Minimum Order Value**: Reject any order under $10 USD.

---

## Quickstart & Local Development

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Docker & Docker Compose (optional for containerized run)

### Build & Run Tests
```bash
# Restore and build solution
dotnet build HyperliquidAiBot.sln

# Run test suite (RiskManager and EIP-712 signing tests)
dotnet test HyperliquidAiBot.sln
```

### Local Dry-Run Execution
```bash
cp .env.example .env
# Edit .env with your settings (keep BOTSETTINGS__EXECUTION__DRYRUN=true for simulation)
dotnet run --project src/HyperliquidAiBot.Worker/HyperliquidAiBot.Worker.csproj
```

---

## Linux VPS Deployment Guide

### Option 1: Docker Compose (Recommended)

1. **Clone repository on VPS**:
   ```bash
   git clone <your-repo-url> /opt/hyperliquid-ai-bot
   cd /opt/hyperliquid-ai-bot
   ```

2. **Configure Environment Variables**:
   ```bash
   cp .env.example .env
   chmod 600 .env
   nano .env
   ```
   Set:
   - `BOTSETTINGS__HYPERLIQUID__USETESTNET=false` (when deploying to Mainnet)
   - `BOTSETTINGS__HYPERLIQUID__PRIVATEKEY=0x...`
   - `BOTSETTINGS__HYPERLIQUID__WALLETADDRESS=0x...`
   - `BOTSETTINGS__LLM__PROVIDER=Gemini`
   - `BOTSETTINGS__LLM__APIKEY=AIzaSy...` (from Google AI Studio)
   - `BOTSETTINGS__LLM__MODEL=gemini-2.5-flash`
   - `BOTSETTINGS__EXECUTION__DRYRUN=false` (for live orders)

3. **Launch Container with Log Rotation**:
   ```bash
   docker compose up -d --build
   ```

4. **Monitor Live Logs**:
   ```bash
   docker compose logs -f --tail=100
   ```

5. **Inspect Health & Heartbeat**:
   ```bash
   cat data/heartbeat/hyperliquid_bot_heartbeat
   docker inspect --format='{{json .State.Health}}' hyperliquid-ai-bot | jq
   ```

---

### Option 2: Automated Hosting / FTP Deployment via GitHub Actions

The repository includes a GitHub Actions workflow (`.github/workflows/deploy.yml`) that automatically builds, tests, publishes with `web.config` for IIS, and deploys via FTP directly to your hosting server whenever changes are pushed to `main`.

1. **Configure GitHub Repository Secrets**:
   Go to **Settings** > **Secrets and variables** > **Actions** in your GitHub repository and add:
   - `FTP_SERVER`: Your FTP host (e.g., `ftp.yourhost.com`).
   - `FTP_USERNAME`: Your FTP account username.
   - `FTP_PASSWORD`: Your FTP account password.
   - *(Optional)* `FTP_SERVER_DIR`: Destination folder (defaults to `/`).

2. **Trigger Deployment**:
   - Push to `main` or trigger manually from the **Actions** tab in GitHub.
   - The workflow compiles the solution, executes all unit tests, publishes the release package with `web.config` and the dashboard UI, and synchronizes to your FTP destination.

3. **Verify Live Application & Visual Dashboard**:
   - Visual Terminal & Dashboard: `https://<your-domain>/`
   - API Status endpoint: `https://<your-domain>/api/status`
   - Healthcheck endpoint: `https://<your-domain>/health`

---

### Option 3: Native Systemd Service (Ubuntu/Debian)

Because `Program.cs` includes `.UseSystemd()`, the bot operates natively as a system daemon.

1. **Publish Release Binary**:
   ```bash
   dotnet publish src/HyperliquidAiBot.Worker/HyperliquidAiBot.Worker.csproj \
     -c Release \
     -o /opt/hyperliquid-bot/publish
   ```

2. **Create Dedicated System User**:
   ```bash
   sudo useradd -r -s /bin/false hyperbot
   sudo chown -R hyperbot:hyperbot /opt/hyperliquid-bot
   ```

3. **Install Systemd Unit**:
   Create `/etc/systemd/system/hyperliquid-bot.service`:
   ```ini
   [Unit]
   Description=Hyperliquid AI Trading Bot
   After=network.target

   [Service]
   Type=notify
   User=hyperbot
   Group=hyperbot
   WorkingDirectory=/opt/hyperliquid-bot/publish
   ExecStart=/usr/bin/dotnet /opt/hyperliquid-bot/publish/HyperliquidAiBot.Worker.dll
   Restart=always
   RestartSec=10
   KillSignal=SIGINT
   EnvironmentFile=/opt/hyperliquid-bot/.env
   Environment=DOTNET_ENVIRONMENT=Production

   # Security Sandboxing
   ProtectSystem=full
   ProtectHome=true
   NoNewPrivileges=true

   [Install]
   WantedBy=multi-user.target
   ```

4. **Enable & Start Service**:
   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable --now hyperliquid-bot
   sudo journalctl -u hyperliquid-bot -f
   ```

---

## Operational Monitoring & Maintenance

| Command | Purpose |
|---|---|
| `docker compose logs -f` | Tail container stdout / trading cycles |
| `journalctl -u hyperliquid-bot -f` | Tail systemd daemon logs |
| `cat /tmp/hyperliquid_bot_heartbeat` | View heartbeat JSON (equity, drawdown, freeze status) |
| `docker compose restart` | Graceful restart of worker container |
| `systemctl restart hyperliquid-bot` | Graceful restart of systemd daemon |

# ==============================================================================
# Build Stage: .NET 8 SDK
# ==============================================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy solution and project definitions first for efficient layer caching
COPY HyperliquidAiBot.sln ./
COPY src/HyperliquidAiBot.Core/HyperliquidAiBot.Core.csproj src/HyperliquidAiBot.Core/
COPY src/HyperliquidAiBot.Worker/HyperliquidAiBot.Worker.csproj src/HyperliquidAiBot.Worker/
COPY tests/HyperliquidAiBot.Tests/HyperliquidAiBot.Tests.csproj tests/HyperliquidAiBot.Tests/

RUN dotnet restore HyperliquidAiBot.sln

# Copy the remaining source files and compile
COPY src/ src/
COPY tests/ tests/

RUN dotnet test tests/HyperliquidAiBot.Tests/HyperliquidAiBot.Tests.csproj -c Release --no-restore

RUN dotnet publish src/HyperliquidAiBot.Worker/HyperliquidAiBot.Worker.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ==============================================================================
# Runtime Stage: Lightweight, hardened .NET 8 ASP.NET Alpine
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# Install security certificates, timezone data, and curl for container health checks
RUN apk add --no-cache tzdata ca-certificates curl

# Create non-root application user and group for security hardening
RUN addgroup -S appgroup && adduser -S appuser -G appgroup

COPY --from=build /app/publish .

# Prepare heartbeat directory with appropriate permissions
RUN mkdir -p /tmp/hyperliquid && chown -R appuser:appgroup /app /tmp/hyperliquid

USER appuser

ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    TZ=UTC

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD test -f /tmp/hyperliquid_bot_heartbeat || exit 1

ENTRYPOINT ["dotnet", "HyperliquidAiBot.Worker.dll"]

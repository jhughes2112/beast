# -----------------------------
# Stage 1: Build
# -----------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore
COPY ["Agent/Agent.csproj", "Agent/"]
RUN dotnet restore "Agent/Agent.csproj"

# Copy source and publish
COPY . .
RUN dotnet publish "Agent/Agent.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -o /app/publish


# -----------------------------
# Stage 2: Runtime
# -----------------------------
FROM ubuntu:24.04 AS final
WORKDIR /app

# Install developer tools: runtimes, compilers, interpreters, and common utilities
RUN apt-get update && apt-get install -y --no-install-recommends \
    # Core tools
    bash curl wget git ca-certificates gnupg lsb-release \
    # C / C++
    build-essential gcc g++ gdb cmake ninja-build \
    # Python
    python3 python3-pip python3-venv \
    # Node.js (LTS via NodeSource)
    nodejs npm \
    # Ruby
    ruby ruby-dev \
    # JVM (Java + Kotlin via sdkman is typical, but openjdk is sufficient here)
    openjdk-21-jdk-headless \
    # Shell / scripting utilities
    jq yq sqlite3 zip unzip bc file tree \
    # .NET runtime deps (required for self-contained .NET 10 binary)
    libicu74 libssl3 zlib1g \
    && rm -rf /var/lib/apt/lists/*

# Copy the single-file published executable from the build stage
COPY --from=build /app/publish/Agent .

# Make executable
RUN chmod +x /app/Agent

EXPOSE 13131

ENTRYPOINT ["/app/Agent"]

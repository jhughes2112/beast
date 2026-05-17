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
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
WORKDIR /app

# Copy the single-file published executable from the build stage
COPY --from=build /app/publish/Agent .

# Make executable
RUN chmod +x /app/Agent

ENTRYPOINT ["/app/Agent"]

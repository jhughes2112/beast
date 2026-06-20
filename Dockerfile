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

# Install base tools and language runtimes available via apt
RUN apt-get update && apt-get install -y --no-install-recommends \
    # Core tools
    bash curl wget git ca-certificates gnupg lsb-release \
    # C / C++
    build-essential gcc g++ gdb cmake ninja-build \
    # Python (python-is-python3 provides a `python` -> python3 alias so the many tools/scripts
    # that invoke `python` rather than `python3` resolve correctly)
    python3 python3-pip python3-venv python-is-python3 \
    # Ruby
    ruby ruby-dev \
    # JVM
    openjdk-21-jdk-headless \
    # Shell / scripting utilities (ripgrep gives the agents fast `rg` search)
    jq sqlite3 zip unzip bc file tree ripgrep \
    # PDF text extraction (poppler-utils provides `pdftotext`, used by the WebFetch role on PDFs)
    poppler-utils \
    # Archive, document, and text-handling tools — all light. The WebFetch role and agent bash use
    # these to inspect/extract fetched files: 7z + extra (de)compressors, xmllint, document->text via
    # pandoc, line-ending fixups. (tar/gzip ship with the base image; no heavy media tools by design.)
    xz-utils bzip2 p7zip-full libxml2-utils pandoc dos2unix \
    # .NET runtime deps (required for self-contained .NET 10 binary)
    libicu74 libssl3 zlib1g \
    && rm -rf /var/lib/apt/lists/*

# .NET 10 SDK — installed via the official Microsoft package feed so the agent can build
# and run .NET projects, not just host its own binary.
RUN curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
        -o /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends dotnet-sdk-10.0 \
    && rm -rf /var/lib/apt/lists/*

# Node.js current LTS — installed via the official NodeSource setup script so we get a
# recent version rather than the outdated one bundled with Ubuntu 24.04.
RUN curl -fsSL https://deb.nodesource.com/setup_lts.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

# Rust (stable toolchain) — installs rustup + cargo into /usr/local/cargo so all users
# can invoke rustc/cargo without sourcing a per-user profile.
ENV RUSTUP_HOME=/usr/local/rustup \
    CARGO_HOME=/usr/local/cargo \
    PATH=/usr/local/cargo/bin:$PATH
RUN curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs \
        | sh -s -- -y --no-modify-path --profile minimal \
    && chmod -R a+w /usr/local/rustup /usr/local/cargo

# Go — download the latest stable release and install to /usr/local/go.
RUN curl -fsSL https://go.dev/dl/go1.24.3.linux-amd64.tar.gz \
        -o /tmp/go.tar.gz \
    && tar -C /usr/local -xzf /tmp/go.tar.gz \
    && rm /tmp/go.tar.gz
ENV PATH=/usr/local/go/bin:$PATH

# Read-only command allowlist for readonly_bash. The tool launches `bash -r` (restricted) with PATH set to
# only this directory, so a locked-down role can run just these curated, read-only programs and nothing else
# on the image resolves. Deliberately excludes anything that can spawn an unrestricted shell by absolute path
# or write in place — that's awk/find/xargs/sed/perl/python/env/tee/sqlite3/pandoc/less/vim, not edge cases:
# `find -exec /bin/sh`, `awk 'BEGIN{system(...)}'`, `xargs /bin/sh` all bypass the whole restriction. Bash
# builtins (echo, pwd, printf, test, read, ...) still work; only external command resolution is narrowed.
# Names not present on the image are skipped, so the list can name optional tools harmlessly.
RUN mkdir -p /opt/agent-bins/readonly \
    && for cmd in \
         cat head tail nl tac \
         ls stat file readlink basename dirname realpath du df tree \
         grep egrep fgrep rg \
         wc sort uniq cut tr comm cmp diff diff3 paste join column fold fmt expand unexpand rev \
         od xxd hexdump strings \
         sha256sum sha1sum md5sum b2sum cksum base64 base32 \
         git jq xmllint pdftotext \
         date uname whoami id which printenv ; do \
         target="$(command -v "$cmd" || true)" ; \
         if [ -n "$target" ]; then ln -sf "$target" "/opt/agent-bins/readonly/$cmd" ; fi ; \
       done

# Copy the single-file published executable from the build stage
COPY --from=build /app/publish/Agent .

# Make executable
RUN chmod +x /app/Agent

EXPOSE 13131

ENTRYPOINT ["/app/Agent"]

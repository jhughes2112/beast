# -----------------------------
# Stage 1: Build
# -----------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Native AOT cross-compilation toolchain: the Agent publishes as a native linux-x64 binary.
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

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
    # C / C++ — both compiler families (many projects require clang for sanitizers or format
    # with clang-format) and both build universes (cmake/ninja plus the autotools chain)
    build-essential gcc g++ gdb cmake ninja-build clang clang-format autoconf automake libtool pkg-config \
    # Python (python-is-python3 provides a `python` -> python3 alias so the many tools/scripts
    # that invoke `python` rather than `python3` resolve correctly; python3-dev supplies the
    # headers without which pip installs of native-extension packages fail)
    python3 python3-pip python3-venv python-is-python3 python3-dev \
    # Ruby
    ruby ruby-dev \
    # JVM — Maven from apt; Gradle is installed from the official dist below (apt's is ancient)
    openjdk-21-jdk-headless maven \
    # Process/system debugging: the bare Ubuntu base has no `ps`; lsof answers "who holds this
    # port/file"; strace is the last-resort syscall tracer
    procps lsof strace \
    # Git LFS so cloning LFS-using repos fetches real content instead of pointer files
    git-lfs \
    # Shell / scripting utilities (ripgrep gives the agents fast `rg` search)
    jq sqlite3 zip unzip bc file tree ripgrep \
    # Networking diagnostics: ping (iputils-ping), dig/nslookup/host (dnsutils), ip/ss (iproute2),
    # ifconfig/netstat (net-tools), traceroute, and nc (netcat-openbsd) for port checks
    iputils-ping dnsutils iproute2 net-tools traceroute netcat-openbsd \
    # PDF text extraction (poppler-utils provides `pdftotext`, used by the WebFetch role on PDFs)
    poppler-utils \
    # Archive, document, and text-handling tools — all light. The WebFetch role and agent bash use
    # these to inspect/extract fetched files: 7z + extra (de)compressors, xmllint, document->text via
    # pandoc, line-ending fixups. (tar/gzip ship with the base image.)
    xz-utils bzip2 p7zip-full libxml2-utils pandoc dos2unix \
    # Audio/video transcoding and probing (ffprobe) for media-handling tasks
    ffmpeg \
    # ICU/SSL/zlib: the AOT Agent itself needs only libssl3/zlib1g (it is InvariantGlobalization);
    # libicu74 stays for the .NET SDK below and any user projects that need globalization
    libicu74 libssl3 zlib1g \
    && rm -rf /var/lib/apt/lists/* \
    # Register the LFS filters system-wide so every clone/pull smudges LFS content automatically
    && git lfs install --system

# .NET 10 SDK — installed via the official Microsoft package feed so the agent can build
# and run .NET projects, not just host its own binary.
RUN curl -fsSL https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
        -o /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends dotnet-sdk-10.0 \
    && rm -rf /var/lib/apt/lists/* \
    # Warm the SDK's first-run experience here, during the build. On the first "real" CLI command the SDK
    # prints the "Welcome to .NET" banner, the telemetry notice, and the dev HTTPS certificate notice, then
    # writes one-time sentinel files (~/.dotnet/*Sentinel) so it never repeats them. Doing it once now bakes
    # those sentinels into the image, so every `dotnet` command the agent runs at runtime stays quiet instead
    # of re-emitting the banner and wasting output tokens. `nuget locals --list` is used because it triggers
    # the first-run path with no project and no side effects; intrinsic options like `--version`/`--info` are
    # handled before the first-run configurer and do NOT consume it.
    && dotnet nuget locals all --list >/dev/null

# Node.js current LTS — installed via the official NodeSource setup script so we get a
# recent version rather than the outdated one bundled with Ubuntu 24.04.
RUN curl -fsSL https://deb.nodesource.com/setup_lts.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

# Headless browser for rendering/screenshotting the agent's own work. Only chrome-headless-shell:
# it's what Playwright picks by default for headless chromium anyway. Full Chromium (389MB) and
# Firefox (302MB) omitted — they buy only headed mode and cross-engine checks. So headless=False
# and channel="chromium" have no binary. Fixed browser path so python playwright shares these.
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/ms-playwright \
    NODE_PATH=/usr/lib/node_modules
RUN npm install -g playwright@1.62.1 \
    && playwright install --with-deps chromium-headless-shell \
    && rm -rf /var/lib/apt/lists/*

# The agent runs as root, and Chromium refuses to start as root with its sandbox enabled. Playwright
# and Puppeteer launch with the sandbox on by default, so the browser died before the CDP endpoint
# appeared and both timed out at launch. Wrap the real binary in place so every launcher (node/python
# Playwright, Puppeteer, the CLI wrapper below) gets --no-sandbox and --disable-dev-shm-usage (the
# 64MB /dev/shm crashes it) without scripts having to pass them. `exec` preserves the pid and
# inherited fds, so Playwright's --remote-debugging-pipe (fds 3/4) still reaches the browser.
RUN BIN=$(ls -d /opt/ms-playwright/chromium_headless_shell-*/chrome-headless-shell-linux64/chrome-headless-shell) \
    && mv "$BIN" "$BIN.real" \
    && printf '#!/bin/bash\nexec "%s.real" --no-sandbox --disable-dev-shm-usage "$@"\n' "$BIN" > "$BIN" \
    && chmod +x "$BIN"

# Plain `chromium` for script-free checks: --dump-dom, --screenshot=out.png, --print-to-pdf.
# --no-sandbox (won't start as root) and --disable-dev-shm-usage (64MB /dev/shm crashes it).
# The grep drops ~6 lines of dbus/GPU/bluetooth noise this binary emits unconditionally and that
# --log-level/--disable-logging don't suppress; they say "ERROR:" and read as a failed check.
# Other stderr passes through. fd 3 keeps --dump-dom off the filter; PIPESTATUS keeps chrome's code.
RUN printf '%s\n' '#!/bin/bash' \
      'BIN=$(ls -d /opt/ms-playwright/chromium_headless_shell-*/chrome-headless-shell-linux64/chrome-headless-shell | head -n1)' \
      '{ "$BIN" --no-sandbox --disable-dev-shm-usage "$@" 2>&1 1>&3 | grep -vE "dbus/bus\.cc|dbus/object_proxy\.cc|vaapi_wrapper\.cc|sandbox_linux\.cc|bluez_dbus_manager\.cc" >&2; } 3>&1' \
      'exit ${PIPESTATUS[0]}' \
      > /usr/local/bin/chromium \
    && chmod +x /usr/local/bin/chromium \
    # Raw binary at a stable path for puppeteer, which passes its own flags.
    && ln -s "$(ls -d /opt/ms-playwright/chromium_headless_shell-*/chrome-headless-shell-linux64/chrome-headless-shell | head -n1)" /opt/ms-playwright/chrome

# Puppeteer, pointed at the browser above instead of downloading its own Chrome (~170MB).
# A default puppeteer.launch() drives the headless shell fine.
ENV PUPPETEER_SKIP_DOWNLOAD=1 \
    PUPPETEER_EXECUTABLE_PATH=/opt/ms-playwright/chrome
RUN npm install -g puppeteer@25.6.0

# Python packages agents would otherwise pip-install on nearly every task. Ubuntu 24.04 marks its
# system Python externally managed (PEP 668), hence --break-system-packages. playwright stays on the
# same 1.62 line as the node package so it reuses the browser above instead of downloading its own.
RUN pip3 install --break-system-packages --no-cache-dir \
        requests httpx beautifulsoup4 lxml html5lib \
        pillow \
        numpy pandas matplotlib \
        pyyaml python-dateutil jsonschema tabulate \
        pytest ruff \
        playwright==1.62.0

# Headless matplotlib; otherwise plotting scripts die on "no display" before savefig().
ENV MPLBACKEND=Agg

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

# Gradle — official distribution (Ubuntu's apt package is years out of date). Wrapper-based
# projects download their own pinned Gradle; this serves repos without a wrapper.
RUN curl -fsSL https://services.gradle.org/distributions/gradle-8.14-bin.zip -o /tmp/gradle.zip \
    && unzip -q /tmp/gradle.zip -d /opt \
    && ln -s /opt/gradle-8.14/bin/gradle /usr/local/bin/gradle \
    && rm /tmp/gradle.zip

# uv — fast Python package/environment manager; increasingly what project READMEs assume.
RUN curl -LsSf https://astral.sh/uv/install.sh | env UV_INSTALL_DIR=/usr/local/bin sh

# Read-only command allowlist for readonly_bash. The tool launches `bash -r` (restricted) with PATH set to
# only this directory, so a locked-down role can run just these curated, read-only programs and nothing else
# on the image resolves. Deliberately excludes anything that can spawn an unrestricted shell by absolute path
# or write in place — that's awk/find/xargs/sed/perl/python/env/tee/sqlite3/pandoc/less/vim, not edge cases:
# `find -exec /bin/sh`, `awk 'BEGIN{system(...)}'`, `xargs /bin/sh` all bypass the whole restriction. Bash
# builtins (echo, pwd, printf, test, read, ...) still work; only external command resolution is narrowed.
# Names not present on the image are skipped, so the list can name optional tools harmlessly.
# `chromium` is the deliberate exception: it fetches and writes, but only to explicit --screenshot/
# --print-to-pdf paths, and can't spawn a shell. Its wrapper needs ls/head/grep from this list.
# Network diagnostics are observe-only: ping/traceroute/dig/nslookup/host/ss/netstat answer "can I
# reach it / what resolves / what's listening" but can't reconfigure anything. `ip` and `ifconfig`
# stay out (their argument forms mutate interfaces/routes when root), as does `nc` (an arbitrary
# data channel, not a diagnostic).
RUN mkdir -p /opt/agent-bins/readonly \
    && for cmd in \
         cat head tail nl tac \
         ls stat file readlink basename dirname realpath du df tree \
         grep egrep fgrep rg \
         wc sort uniq cut tr comm cmp diff diff3 paste join column fold fmt expand unexpand rev \
         od xxd hexdump strings \
         sha256sum sha1sum md5sum b2sum cksum base64 base32 \
         git jq xmllint pdftotext \
         chromium \
         ping traceroute dig nslookup host ss netstat \
         ps lsof \
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

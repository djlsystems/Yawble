# Local Podman image. The named volume mounted at /data outlives this image.
#
# THE VERSION IS PASSED IN. The build context has no .git, so scripts/version.mjs cannot ask
# git here; the caller asks it and hands the answer over:
#   podman build --build-arg HARNESS_VERSION=$(node scripts/version.mjs) \
#                --build-arg HARNESS_COMMIT=$(git rev-parse HEAD) ...
# Both stages stamp from these, and the image carries them as OCI labels. Left out, every stamp
# reads 0.0.0+unknown - never a release.
#
# BUILT FOR linux/amd64 AND linux/arm64 by scripts/release.ps1; scripts/podman-up.ps1 builds the
# local one for the machine it runs on. --platform=$BUILDPLATFORM on the two compile stages: they
# run natively on the machine doing the build and their output (JavaScript, a framework-dependent
# .NET publish) is the same for both targets, so only the runtime stage runs under emulation for
# the arm64 half.
ARG HARNESS_VERSION=""
ARG HARNESS_COMMIT=""

FROM --platform=$BUILDPLATFORM node:22-bookworm AS web
ARG HARNESS_VERSION
ARG HARNESS_COMMIT
WORKDIR /src/web
# quasar.config.ts imports the version rule from ../scripts.
COPY scripts/version.mjs /src/scripts/
COPY web/ ./
# The image's npm 10 fails this tree with "Cannot read properties of null (reading 'edgesOut')".
RUN npm install -g npm@11 && npm ci && npx quasar build

# The compiler runs natively and CROSS-publishes for TARGETARCH. Running the .NET SDK under QEMU for
# the arm64 half is an hour and crashes; a framework-dependent publish only has to pick the right
# native SQLite and PTY libraries, which `-a` does.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG HARNESS_VERSION
ARG HARNESS_COMMIT
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
COPY --from=web /src/web/dist/spa/ src/Harness.Host/wwwroot/
# HARNESS_VERSION and HARNESS_COMMIT reach MSBuild as properties from the environment.
RUN dotnet publish src/Harness.Host/Harness.Host.csproj -c Release -o /out --no-self-contained -a "$TARGETARCH"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# amd64 or arm64. The Node and Go tarballs below name an architecture; everything else picks its own.
ARG TARGETARCH
# THE VERSION IS DECLARED AT THE END OF THIS STAGE, NOT HERE. A build reuses cached layers only up to
# the first instruction that changed, and the version changes every release: declared here, it made
# every release reinstall the whole toolchain below, twice, the arm64 half under emulation.
# Tools an agent can assume are already on PATH. The coding CLIs (claude, codex,
# copilot, grok) are not in this list: the entrypoint installs those onto /data
# because they change often. This layer is the ordinary toolchain.
#
# In: git, GitHub CLI, ssh, curl, jq, ripgrep, fd, sqlite, unzip, a C compiler,
#     Node 22 and npm, Python 3, pip, venv, uv, Go, and the .NET 10 SDK.
# Out, on purpose: a JDK, Rust, PHP, Ruby, and a Docker client. Each is large,
#     and none is required to build the kind of work this image is aimed at.
#     Add one when a team actually needs it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git ca-certificates curl wget xz-utils unzip zip openssh-client \
        build-essential pkg-config \
        python3 python3-pip python3-venv python3-dev \
        jq ripgrep fd-find sqlite3 less procps util-linux passwd \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
        -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && printf 'deb [arch=%s signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main\n' \
        "$(dpkg --print-architecture)" > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends gh \
    && rm -rf /var/lib/apt/lists/* \
    && ln -sf "$(command -v fdfind)" /usr/local/bin/fd \
    && printf '[global]\nbreak-system-packages = true\n' > /etc/pip.conf \
    && case "$TARGETARCH" in amd64) node_arch=x64 ;; arm64) node_arch=arm64 ;; *) echo "unsupported TARGETARCH '$TARGETARCH'" && exit 1 ;; esac \
    && curl -fsSL "https://nodejs.org/dist/v22.23.2/node-v22.23.2-linux-${node_arch}.tar.xz" \
        | tar -xJ --no-same-owner -C /usr/local --strip-components=1 \
    && corepack enable \
    && curl -fsSL "https://go.dev/dl/go1.27.1.linux-${TARGETARCH}.tar.gz" | tar --no-same-owner -C /usr/local -xz \
    && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && curl -LsSf https://astral.sh/uv/install.sh | env UV_INSTALL_DIR=/usr/local/bin sh \
    && node --version && npm --version && python3 --version && /usr/local/go/bin/go version \
    && dotnet --list-sdks && gh --version && uv --version && rg --version
# A headless browser for agents that test web apps. Only the SYSTEM LIBRARIES are baked in here,
# because they need the package manager; the browser itself is downloaded onto the data volume
# at start (ensure-agent-clis.sh), so an image rebuild does not fetch it again and a project's own
# Playwright version can add the revision it wants beside it.
RUN npx -y playwright install-deps chromium \
    && rm -rf /var/lib/apt/lists/*
# TWO USERS. `harness` runs the host; `agent` runs every agent child and the Concierge PTY,
# so an agent cannot signal the host or read its database and keys. The ids are FIXED: they are
# what the volume records, so a rebuilt image must mean the same users by them. `harness` is also in
# group `agent`, to create and write inside the team trees; `agent` is in no group of the host's.
#   harness  10001:10001   agent  10002:10002
# The host finds `agent` by name. The container starts as root; the entrypoint sets ownership on
# the volume, installs the "System packages" setting, and drops to `harness` with setpriv
# (util-linux, installed above, as is runuser). It calls setpriv and dotnet by absolute path, which
# the RUN below checks. scripts/prepare-volume.sh holds the ownership map.
# NPM_CONFIG_PREFIX below sends an agent's `npm -g` to the volume: /usr/local is root's.
RUN groupadd --gid 10001 harness \
    && groupadd --gid 10002 agent \
    && useradd --uid 10001 --gid harness --groups agent --no-create-home --home-dir /data/agent-home --shell /usr/sbin/nologin harness \
    && useradd --uid 10002 --gid agent --no-create-home --home-dir /data/agent-home --shell /bin/bash agent \
    && command -v setpriv && command -v runuser \
    && test -x /usr/bin/setpriv && test -x /usr/bin/dotnet
WORKDIR /app
COPY --from=build /out .
COPY scripts/container-entrypoint.sh scripts/ensure-agent-clis.sh scripts/npm-torn-install.sh scripts/prepare-volume.sh /opt/harness/
RUN chmod +x /opt/harness/container-entrypoint.sh /opt/harness/ensure-agent-clis.sh /opt/harness/npm-torn-install.sh /opt/harness/prepare-volume.sh \
    && mkdir -p /data \
    && sed -i 's/\r$//' /opt/harness/container-entrypoint.sh /opt/harness/ensure-agent-clis.sh /opt/harness/npm-torn-install.sh /opt/harness/prepare-volume.sh \
    && ln -sf /opt/harness/container-entrypoint.sh /entrypoint.sh
# IS_SANDBOX: Claude Code refuses --dangerously-skip-permissions when it runs as root unless it is
# told it is inside a sandbox. This container is that sandbox. Every agent child inherits it; a
# child normally runs as `agent`, not root, and the variable is kept for a launch that still is root.
# PATH: the system folders FIRST, then the agent-owned tool folders. An agent can write
# /data/bin and the rest; a name it drops there must never shadow find, git, setpriv or setsid for
# root or the host. The entrypoint runs its root steps on the system folders alone.
# HOME stays agent-home here, for what does not come through the entrypoint: a person's
# `podman exec` CLI login and `--doctor`'s sign-in check read it. The entrypoint's root section overrides it with /root, because agent-home is agent's and
# root must read no config from it; the CLI install and the host get agent-home back explicitly.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    HARNESS_DATA_ROOT=/data \
    IS_SANDBOX=1 \
    HOME=/data/agent-home \
    DOTNET_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_PACKAGES=/data/nuget \
    GOPATH=/data/go \
    GOCACHE=/data/go-cache \
    npm_config_cache=/data/npm-cache \
    PIP_CACHE_DIR=/data/pip-cache \
    PLAYWRIGHT_BROWSERS_PATH=/data/ms-playwright \
    NPM_CONFIG_PREFIX=/data/npm-global \
    PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/local/go/bin:/data/agent-home/.grok/bin:/data/npm-global/bin:/data/bin:/data/go/bin:/data/agent-home/.local/bin:/data/agent-home/.dotnet/tools
VOLUME /data
EXPOSE 8080
# /healthz answers 503 when the database will not open, the data root will not take a write, or the
# delivery pump has stopped turning. Loopback, so it asks the Host and not the published port.
# The long start period is the first boot, which installs the agent CLIs onto /data before the Host
# listens; a success ends it early. Podman keeps HEALTHCHECK only in the docker image format, which
# is why scripts/dev-up.ps1 and scripts/release.ps1 build with --format docker - the default OCI
# format drops it with a warning and the container never reports health at all.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10m --retries=3 \
    CMD curl -fsS -o /dev/null http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["/entrypoint.sh"]
# The version, last: it changes every release, and nothing above may depend on it.
ARG HARNESS_VERSION
ARG HARNESS_COMMIT
LABEL org.opencontainers.image.version="${HARNESS_VERSION}" \
      org.opencontainers.image.revision="${HARNESS_COMMIT}"

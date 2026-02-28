FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG GIT_SHA=unknown
ENV LEAN_GIT_SHA=$GIT_SHA
ENV DEBIAN_FRONTEND=noninteractive
ENV PATH="/root/.cargo/bin:${PATH}"

WORKDIR /src

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl build-essential pkg-config libssl-dev \
    && rm -rf /var/lib/apt/lists/*

RUN curl -sSf https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain 1.91.1

COPY . .

RUN set -eux; \
    case "${TARGETARCH}" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "Unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    cargo build --release --manifest-path /src/native/lean-crypto-ffi/Cargo.toml; \
    mkdir -p "/src/src/Lean.Client/runtimes/${rid}/native"; \
    cp /src/native/lean-crypto-ffi/target/release/liblean_crypto_ffi.so "/src/src/Lean.Client/runtimes/${rid}/native/"; \
    dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -r "${rid}" --self-contained true -p:PublishSingleFile=true -o /app/publish

# Profiling runtime: uses SDK image for diagnostic tools
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates wget gnupg procps \
    && wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends libmsquic \
    && rm -f /tmp/packages-microsoft-prod.deb \
    && rm -rf /var/lib/apt/lists/*

# Install .NET diagnostic tools
RUN dotnet tool install --global dotnet-trace \
    && dotnet tool install --global dotnet-counters \
    && dotnet tool install --global dotnet-gcdump \
    && dotnet tool install --global dotnet-dump
ENV PATH="${PATH}:/root/.dotnet/tools"
ENV DOTNET_EnableDiagnostics=1
# Limit glibc malloc arenas to reduce native memory fragmentation
# Default is 8*cores; with 81 threads this causes ~5GB of arena overhead
ENV MALLOC_ARENA_MAX=2
ENV DOTNET_GCHeapHardLimit=0x40000000

WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["/app/Lean.Client"]

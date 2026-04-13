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

# rec_aggregation's compilation.rs reads .py source files at runtime to verify
# a bytecode fingerprint. CARGO_MANIFEST_DIR is baked into the binary at compile
# time, so we must copy the crate source to a known staging path and recreate
# the exact directory in the runtime image.
# Use -maxdepth and -not to avoid matching inside target/ build artifacts,
# sort for deterministic ordering, and validate the result.
RUN set -eux; \
    rec_agg_dir=$(find /root/.cargo/git/checkouts -maxdepth 4 -path "*/rec_aggregation" -type d \
        -not -path "*/target/*" | sort | head -1); \
    test -n "$rec_agg_dir" || { echo "ERROR: rec_aggregation crate source not found in cargo checkouts"; exit 1; }; \
    cp -r "$rec_agg_dir" /tmp/rec_aggregation_src; \
    echo "$rec_agg_dir" > /tmp/rec_aggregation_path

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates wget gnupg \
    && wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends libmsquic libjemalloc2 \
    && rm -f /tmp/packages-microsoft-prod.deb \
    && rm -rf /var/lib/apt/lists/*
ENV LD_PRELOAD=libjemalloc.so.2
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /tmp/rec_aggregation_src /tmp/rec_aggregation_src
COPY --from=build /tmp/rec_aggregation_path /tmp/rec_aggregation_path
RUN mkdir -p "$(cat /tmp/rec_aggregation_path)" \
    && cp -r /tmp/rec_aggregation_src/* "$(cat /tmp/rec_aggregation_path)/" \
    && rm -rf /tmp/rec_aggregation_src /tmp/rec_aggregation_path
ENTRYPOINT ["/app/Lean.Client"]

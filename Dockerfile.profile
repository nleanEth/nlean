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

# Build jemalloc from source with profiling support (Ubuntu package lacks --enable-prof)
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential autoconf \
    && curl -sSL https://github.com/jemalloc/jemalloc/releases/download/5.3.0/jemalloc-5.3.0.tar.bz2 | tar xj -C /tmp \
    && cd /tmp/jemalloc-5.3.0 \
    && ./configure --enable-prof --prefix=/usr/local \
    && make -j$(nproc) && make install \
    && rm -rf /tmp/jemalloc-5.3.0 \
    && apt-get purge -y build-essential autoconf && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# === Native heap profiling (jemalloc) ===
# prof:true          — enable heap profiling
# lg_prof_interval:30 — dump profile every 1 GB allocated (2^30 bytes)
# lg_prof_sample:17  — sample every 128 KB (2^17 bytes) for low overhead
# prof_prefix:/tmp/jeprof — profile dump file prefix
# prof_gdump:true    — auto-dump when heap size doubles
#
# Collect heap profile on demand:
#   docker exec <container> kill -USR2 1
# Copy out and analyze:
#   docker cp <container>:/tmp/ ./jeprof-dumps/
#   jeprof --svg /app/Lean.Client jeprof.*.heap > heap-profile.svg
#
# === CPU profiling ===
# Option 1: dotnet-trace (managed + native, low overhead):
#   docker exec <container> dotnet-trace collect -p 1 --duration 00:00:30 -o /tmp/trace.nettrace
#   docker cp <container>:/tmp/trace.nettrace .
#   # Open in PerfView (Windows) or `dotnet-trace convert trace.nettrace --format speedscope`
#
# Option 2: perf (full native stacks including Rust FFI, needs --privileged):
#   docker exec <container> perf record -g -p 1 --call-graph dwarf -o /tmp/perf.data -- sleep 30
#   docker cp <container>:/tmp/perf.data .
#   perf script -i perf.data | inferno-flamegraph > cpu-flamegraph.svg
ENV LD_PRELOAD=/usr/local/lib/libjemalloc.so
ENV MALLOC_CONF=prof:true,lg_prof_interval:30,lg_prof_sample:17,prof_prefix:/tmp/jeprof,prof_gdump:true

WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["/app/Lean.Client"]

# nlean

Lean consensus client in C#, built with .NET 10+, Rust FFI for hash-based crypto (leanSig/leanMultisig), and Nethermind dotnet-libp2p for networking.

## Prerequisites

- [.NET SDK 10.0+](https://dotnet.microsoft.com/) (see `global.json`)
- [Rust toolchain](https://rustup.rs/) (`cargo`) for native crypto FFI
- [libmsquic](https://github.com/microsoft/msquic) (version 2+) for QUIC transport
- Docker (for integration tests and interop)
- Git submodules: `git submodule update --init --recursive`

### Installing libmsquic

**macOS (Homebrew):**

```bash
brew install microsoft/msquic/libmsquic
```

After installing, copy the library next to the published binary so .NET can find it (macOS SIP strips `DYLD_LIBRARY_PATH`):

```bash
cp /opt/homebrew/lib/libmsquic.dylib <publish-dir>/
```

**Ubuntu / Debian:**

```bash
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
sudo apt-get update && sudo apt-get install -y libmsquic
```

## Quick Start

### Build from Source

```bash
# Clone with submodules
git clone --recursive https://github.com/nleanEth/nlean.git
cd nlean

# Build native crypto bindings (requires Rust toolchain)
./scripts/build-native.sh

# Build patched libp2p packages (required for gossip/quic compatibility)
./scripts/libp2p/build-patched-pubsub-package.sh
./scripts/libp2p/build-patched-quic-package.sh

# Build
dotnet build Lean.sln -c Release

# Run all unit tests
dotnet test Lean.sln -c Release
```

## Running the Client

There are three ways to run nlean: **Docker**, **release binary**, or **build from source**. Choose one below.

### Option 1: Docker (Recommended)

Everything is self-contained — no need to install libmsquic or .NET separately.

```bash
# Pull pre-built image
docker pull ghcr.io/nleaneth/nlean:latest

# Or build locally
docker build -t nlean --build-arg GIT_SHA=$(git rev-parse --short HEAD) .

# Run
docker run ghcr.io/nleaneth/nlean:latest \
  --validator-config /path/to/validator-config.yaml \
  --node nlean_0 \
  --data-dir /data/nlean_0 \
  --network devnet0 \
  --node-key /path/to/nlean_0.key \
  --socket-port 9101 \
  --api-port 5052 \
  --metrics-port 18081 \
  --hash-sig-key-dir /path/to/hash-sig-keys \
  --is-aggregator \
  --log Information
```

### Option 2: Release Binary

Download a release archive from [GitHub Releases](https://github.com/nleanEth/nlean/releases).

**Linux (single-file or portable):**

```bash
# Download and extract (example: linux-x64 single-file)
tar -xzf nlean-linux-x64.tar.gz

# Install libmsquic (required)
wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
sudo apt-get update && sudo apt-get install -y libmsquic

# Run (single-file binary — libmsquic is loaded from system path)
./lean-client-linux-x64/Lean.Client --help
```

**macOS arm64 (multi-file only):**

```bash
# Download and extract
tar -xzf nlean-osx-arm64.tar.gz

# Install libmsquic via Homebrew
brew install microsoft/msquic/libmsquic

# Copy libmsquic next to the binary (required — macOS SIP strips DYLD_LIBRARY_PATH)
cp /opt/homebrew/lib/libmsquic.dylib lean-client-osx-arm64-portable/

# Run
./lean-client-osx-arm64-portable/Lean.Client --help
```

### Option 3: Build and Publish from Source

**Linux — framework-dependent (requires .NET 10 runtime installed):**

```bash
# Publish
dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false

# Install libmsquic (required)
wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
sudo apt-get update && sudo apt-get install -y libmsquic

# Run
./artifacts/lean-client/Lean.Client --help
```

**Linux — self-contained single-file:**

```bash
dotnet publish src/Lean.Client/Lean.Client.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true \
  -o artifacts/lean-client

# Install libmsquic (required)
sudo apt-get install -y libmsquic

# Run
./artifacts/lean-client/Lean.Client --help
```

**macOS arm64 — self-contained multi-file:**

```bash
dotnet publish src/Lean.Client/Lean.Client.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -o artifacts/lean-client

# Install libmsquic and copy next to the binary
brew install microsoft/msquic/libmsquic
cp /opt/homebrew/lib/libmsquic.dylib artifacts/lean-client/

# Run
./artifacts/lean-client/Lean.Client --help
```

### Running with Flags

```bash
./artifacts/lean-client/Lean.Client \
  --validator-config /path/to/validator-config.yaml \
  --node nlean_0 \
  --data-dir data/nlean_0 \
  --network devnet0 \
  --node-key /path/to/nlean_0.key \
  --socket-port 9101 \
  --api-port 5052 \
  --metrics-port 18081 \
  --hash-sig-key-dir /path/to/hash-sig-keys \
  --is-aggregator \
  --log Information
```

## Project Structure

```
src/
  Lean.Client/        CLI entry point
  Lean.Consensus/     Consensus, fork choice, sync (backfill, head, checkpoint)
  Lean.Crypto/        Rust FFI bindings for leanSig / leanMultisig
  Lean.Metrics/       Prometheus metrics
  Lean.Network/       libp2p networking (gossip, RPC, discovery)
  Lean.Node/          Node app orchestration
  Lean.Storage/       Persistent storage layer
  Lean.Validator/     Validator duties (propose, attest, aggregate)
tests/
  Lean.Consensus.Tests/    Fork choice, sync, state transition (~291 tests)
  Lean.Crypto.Tests/       FFI round-trip (~5 tests)
  Lean.Network.Tests/      Networking (~40 tests)
  Lean.Validator.Tests/    Validator service (~28 tests)
  Lean.Integration.Tests/  Multi-node devnet (~4 tests)
native/
  lean-crypto-ffi/    Rust crate for crypto bindings
client-cmds/
  nlean-cmd.sh        lean-quickstart client-cmd contract
scripts/
  build-native.sh     Build Rust FFI
  libp2p/             Patched pubsub/quic package builders
vendor/
  lean-quickstart/    Git submodule — devnet orchestration
```

## Testing

```bash
# All unit tests
dotnet test Lean.sln -c Release

# Consensus simulation (CI-aligned)
dotnet test tests/Lean.Consensus.Tests/Lean.Consensus.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName~ConsensusMultiNodeFinalizationTests" \
  /m:1 /nodeReuse:false

# Integration tests (requires published binary + libmsquic)
dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false
# Linux: sudo apt-get install -y libmsquic
# macOS: brew install microsoft/msquic/libmsquic && cp /opt/homebrew/lib/libmsquic.dylib artifacts/lean-client/
dotnet test tests/Lean.Integration.Tests/Lean.Integration.Tests.csproj -c Release

# Format check
dotnet tool install --tool-path ./.dotnet-tools dotnet-format
./.dotnet-tools/dotnet-format Lean.sln --check --fix-whitespace --exclude vendor
```

## Local Devnet

Uses [lean-quickstart](https://github.com/blockblaz/lean-quickstart) via git submodule:

```bash
git submodule update --init --recursive vendor/lean-quickstart

cd vendor/lean-quickstart
NETWORK_DIR=local-devnet-nlean ./spin-node.sh --node all
```

The `client-cmds/nlean-cmd.sh` script follows lean-quickstart's client-cmd contract, passing CLI flags directly.

## Docker

See [Option 1: Docker](#option-1-docker-recommended) above for running via Docker.

Pre-built multi-arch images are published to GitHub Container Registry on every tagged release:

```bash
docker pull ghcr.io/nleaneth/nlean:latest
```

## CI

GitHub Actions (`.github/workflows/ci.yml`) on PR and push to `main`:

| Job | Description |
|-----|-------------|
| `format-check` | dotnet-format whitespace check |
| `build-test (ubuntu/macos)` | Build + unit tests + publish |
| `consensus-simulation` | Multi-node finalization simulation |
| `integration-tests` | 4-node devnet integration (45 min timeout) |

On tag `v*` push (`.github/workflows/docker-publish.yml` + `.github/workflows/release.yml`):

| Job | Description |
|-----|-------------|
| `docker build-and-push` | Multi-arch Docker image → `ghcr.io/nleaneth/nlean` |
| `release build` | Self-contained binaries (linux-x64, linux-arm64, osx-arm64) |
| `release create-release` | GitHub Release with tar.gz artifacts |

## Release Binaries

Each tagged release publishes the following artifacts:

| Artifact | Platform | Format | Description |
|----------|----------|--------|-------------|
| `nlean-linux-x64.tar.gz` | Linux x64 | Single file | Self-contained single executable with all native libs bundled |
| `nlean-linux-x64-portable.tar.gz` | Linux x64 | Multi-file | Self-contained publish with separate DLLs and native libs |
| `nlean-linux-arm64.tar.gz` | Linux arm64 | Single file | Self-contained single executable with all native libs bundled |
| `nlean-linux-arm64-portable.tar.gz` | Linux arm64 | Multi-file | Self-contained publish with separate DLLs and native libs |
| `nlean-osx-arm64.tar.gz` | macOS arm64 | Multi-file | Self-contained publish with separate DLLs and native libs |

> **Note:** macOS releases are multi-file only due to intermittent `libmsquic` crashes with single-file publish. On macOS, you must copy `libmsquic.dylib` next to the binary — see [Option 2: Release Binary](#option-2-release-binary) for details.

## License

See [LICENSE](LICENSE).

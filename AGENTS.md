# AGENTS.md

## Project Overview

- **Name**: `nlean` — Lean consensus client in C#
- **Stack**: .NET 10+ · Rust FFI (leanSig/leanMultisig) · Nethermind dotnet-libp2p
- **Solution**: `Lean.sln`

## Repository Layout

```
src/
  Lean.Client/        # CLI entry point
  Lean.Consensus/     # Consensus, fork choice, sync (backfill, head, checkpoint)
  Lean.Crypto/        # Rust FFI bindings for leanSig / leanMultisig
  Lean.Metrics/       # Prometheus metrics
  Lean.Network/       # libp2p networking (gossip, RPC, discovery)
  Lean.Node/          # Node app orchestration
  Lean.Storage/       # Persistent storage layer
  Lean.Validator/     # Validator duties (propose, attest, aggregate)
tests/
  Lean.Consensus.Tests/    # ~291 tests — fork choice, sync, state transition
  Lean.Crypto.Tests/       # ~5 tests — FFI round-trip
  Lean.Network.Tests/      # ~40 tests — networking
  Lean.Validator.Tests/    # ~28 tests — validator service
  Lean.Integration.Tests/  # ~4 tests — multi-node devnet (requires published binary)
native/
  lean-crypto-ffi/    # Rust crate for crypto bindings
client-cmds/
  nlean-cmd.sh        # lean-quickstart client-cmd contract
scripts/
  build-native.sh     # Build Rust FFI
  libp2p/             # Patched pubsub/quic package builders
vendor/
  lean-quickstart/    # Git submodule — devnet orchestration
```

## Prerequisites

- .NET SDK 10.0+ (see `global.json`)
- Rust toolchain (`cargo`) for native crypto builds
- [libmsquic](https://github.com/microsoft/msquic) (version 2+) for QUIC transport
  - macOS: `brew install microsoft/msquic/libmsquic` then copy `libmsquic.dylib` next to the binary
  - Linux: install via Microsoft apt repo (see README)
- Git submodules: `git submodule update --init --recursive`
- Docker (for integration tests and interop)

## Common Commands

```bash
# Build native crypto bindings
./scripts/build-native.sh

# Build patched pubsub package (required for gossip compatibility)
./scripts/libp2p/build-patched-pubsub-package.sh

# Build patched quic package
./scripts/libp2p/build-patched-quic-package.sh

# Build solution
dotnet build Lean.sln -c Release

# Run all unit tests (excludes integration)
dotnet test Lean.sln -c Release --filter "TestCategory!=Integration"

# Run all tests (unit + integration, works on both Linux and macOS)
dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false && dotnet test Lean.sln -c Release

# Run format check
dotnet tool install --tool-path ./.dotnet-tools dotnet-format
./.dotnet-tools/dotnet-format Lean.sln --check --fix-whitespace --exclude vendor

# Run consensus simulation tests
dotnet test tests/Lean.Consensus.Tests/Lean.Consensus.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName~ConsensusMultiNodeFinalizationV2Tests" \
  /m:1 /nodeReuse:false

# Run integration tests (publish binary first)
dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false
dotnet test tests/Lean.Integration.Tests/Lean.Integration.Tests.csproj -c Release

# Run client
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

## Local Devnet via lean-quickstart

```bash
git submodule update --init --recursive vendor/lean-quickstart

cd vendor/lean-quickstart
NETWORK_DIR=local-devnet-nlean ./spin-node.sh --node all
```

The `client-cmds/nlean-cmd.sh` follows lean-quickstart's client-cmd contract, passing CLI flags directly.

## CI Jobs

GitHub Actions (`.github/workflows/ci.yml`) on PR and push to `main`:

| Job | Description |
|-----|-------------|
| `format-check` | `dotnet-format` whitespace check |
| `build-test (ubuntu-latest)` | Build + unit tests + publish (linux-x64) |
| `build-test (macos-latest)` | Build + unit tests + publish (osx-arm64) |
| `consensus-simulation` | Multi-node finalization simulation |
| `integration-tests` | 4-node devnet integration tests (45 min timeout) |

GitHub Actions (`.github/workflows/docker-publish.yml`) on tag `v*` push:

| Job | Description |
|-----|-------------|
| `build-and-push` | Multi-arch Docker image → `ghcr.io/nleaneth/nlean` |

GitHub Actions (`.github/workflows/release.yml`) on tag `v*` push:

| Job | Description |
|-----|-------------|
| `build (linux-x64/linux-arm64/osx-arm64)` | Self-contained publish + tar.gz archive |
| `create-release` | GitHub Release with binary artifacts |

## PR Checklist

- [ ] Format check: `./.dotnet-tools/dotnet-format Lean.sln --check --fix-whitespace --exclude vendor`
- [ ] Unit tests: `dotnet test Lean.sln -c Release`
- [ ] Publish: `dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -r osx-arm64 --self-contained false`
- [ ] If consensus changed: `dotnet test tests/Lean.Consensus.Tests/Lean.Consensus.Tests.csproj -c Release --filter "FullyQualifiedName~ConsensusMultiNodeFinalizationV2Tests" /m:1 /nodeReuse:false`
- [ ] If integration-relevant: `dotnet test tests/Lean.Integration.Tests/Lean.Integration.Tests.csproj -c Release`

## Notes

- If `dotnet` is not on PATH, use `~/.dotnet/dotnet`.
- Rust toolchain (`cargo`) is required for native builds/tests.
- `vendor/` should not be modified unless explicitly required.
- Consensus behavior changes must include corresponding `Lean.Consensus.Tests`.

- If `dotnet` is not on PATH, use `~/.dotnet/dotnet`.
- Rust toolchain (`cargo`) is required for native-related builds/tests.

# nlean

Lean consensus client in C#, built with .NET 10+, Rust FFI for hash-based crypto (leanSig/leanMultisig), and Nethermind dotnet-libp2p for networking.

## Prerequisites

- [.NET SDK 10.0+](https://dotnet.microsoft.com/) (see `global.json`)
- [Rust toolchain](https://rustup.rs/) (`cargo`) for native crypto FFI
- Docker (for integration tests and interop)
- Git submodules: `git submodule update --init --recursive`

## Quick Start

```bash
# Build native crypto bindings
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

```bash
# Publish
dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false

# Run
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

# Integration tests (requires published binary)
dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false
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

```bash
docker build -t nlean --build-arg GIT_SHA=$(git rev-parse --short HEAD) .
```

## CI

GitHub Actions (`.github/workflows/ci.yml`) on PR and push to `main`:

| Job | Description |
|-----|-------------|
| `format-check` | dotnet-format whitespace check |
| `build-test (ubuntu/macos)` | Build + unit tests + publish |
| `consensus-simulation` | Multi-node finalization simulation |
| `integration-tests` | 4-node devnet integration (45 min timeout) |

## License

See [LICENSE](LICENSE).

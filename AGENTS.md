# AGENT.md

## Project at a Glance

- Name: `nlean` (Lean C# client scaffold)
- Main stack: `.NET 10+` + `Rust FFI` + `Nethermind dotnet-libp2p`
- Solution file: `Lean.sln`

## Environment Prerequisites

- .NET SDK version from `global.json`
- Rust toolchain (`cargo`) for native/FFI build paths
- Git submodules initialized for interop work:

```bash
git submodule update --init --recursive vendor/lean-quickstart
```

## Common Commands

```bash
# Build native crypto bindings
./scripts/build-native.sh

# Build patched pubsub package (required for Anonymous gossip compatibility)
./scripts/libp2p/build-patched-pubsub-package.sh

# Build solution
dotnet build Lean.sln -c Release

# Run all tests
dotnet test Lean.sln -c Release

# Run format check aligned with CI
dotnet tool install --tool-path ./.dotnet-tools dotnet-format
./.dotnet-tools/dotnet-format Lean.sln --check --fix-whitespace --exclude vendor

# Run consensus simulation test shard aligned with CI
dotnet test tests/Lean.Consensus.Tests/Lean.Consensus.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName~ConsensusMultiNodeFinalizationTests" \
  /m:1 \
  /nodeReuse:false

# Run client (example)
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

# Run integration tests (requires published binary)
dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false
dotnet test tests/Lean.Integration.Tests/Lean.Integration.Tests.csproj -c Release
```

## Devnet via lean-quickstart

Initialize submodule once:

```bash
git submodule update --init --recursive vendor/lean-quickstart
```

The `client-cmds/nlean-cmd.sh` script follows lean-quickstart's client-cmd contract,
passing CLI flags directly (no JSON config generation). To run a local devnet:

```bash
cd vendor/lean-quickstart
NETWORK_DIR=local-devnet-nlean ./spin-node.sh --node all
```

## PR / CI Checklist

Before opening PR:

- Ensure local format check passes:
  - `./.dotnet-tools/dotnet-format Lean.sln --check --fix-whitespace --exclude vendor`
- Ensure solution tests pass:
  - `dotnet test Lean.sln -c Release`
- Ensure publish step still works for changed runtime target(s):
  - `dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -r osx-arm64 --self-contained false`
  - `dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -r linux-x64 --self-contained false`
- If consensus code changed, run CI-aligned simulation shard:
  - `dotnet test tests/Lean.Consensus.Tests/Lean.Consensus.Tests.csproj -c Release --filter "FullyQualifiedName~ConsensusMultiNodeFinalizationTests" /m:1 /nodeReuse:false`

Expected GitHub CI jobs on PR/push to `main`:
- `format-check`
- `build-test (ubuntu-latest)`
- `build-test (macos-latest)`
- `consensus-simulation`

## Notes

- If `dotnet` is not on PATH, use `~/.dotnet/dotnet`.
- Rust toolchain (`cargo`) is required for native-related builds/tests.

# Lean C# Client

This repo contains a .NET 10+ Lean consensus client scaffold with Rust FFI bindings for leanSig/leanMultisig and Nethermind dotnet-libp2p networking stack.

## Quick start

```bash
# Build native crypto bindings
./scripts/build-native.sh

# Build patched dotnet-libp2p pubsub package (Anonymous gossip compatibility)
./scripts/libp2p/build-patched-pubsub-package.sh

# Run the client
./src/Lean.Client/bin/Debug/net10.0/Lean.Client --validator-config validator-config.yaml --node lean_client_0
```

## lean-quickstart interop

```bash
# Initialize quickstart submodule once
git submodule update --init --recursive vendor/lean-quickstart

# Wrapper script (kept) with pass/fail checks
./scripts/interop/run-lean-quickstart-devnet2.sh
# Override quickstart checkout path if needed:
# ./scripts/interop/run-lean-quickstart-devnet2.sh --quickstart-dir /path/to/lean-quickstart
# Run a specific topology (example: nlean + ethlambda):
# ./scripts/interop/run-lean-quickstart-devnet2.sh --nodes nlean_0,ethlambda_0
# Disable sudo shim if your quickstart setup requires real sudo:
# ./scripts/interop/run-lean-quickstart-devnet2.sh --no-sudo-shim
# Override gossip network name if needed:
# ./scripts/interop/run-lean-quickstart-devnet2.sh --network-name devnet2
# Keep network running after checks:
# ./scripts/interop/run-lean-quickstart-devnet2.sh --keep-running
# Skip checks and only boot the network:
# ./scripts/interop/run-lean-quickstart-devnet2.sh --skip-checks
```

Direct `spin-node.sh` interop (nlean + ethlambda):

```bash
# Install nlean client command into quickstart
install -m 755 ./client-cmds/nlean-cmd.sh ./vendor/lean-quickstart/client-cmds/nlean-cmd.sh

# Start nlean + ethlambda from quickstart
cd ./vendor/lean-quickstart
NETWORK_DIR=local-devnet-nlean-ethlambda \
NLEAN_REPO="$(cd ../.. && pwd)" \
NLEAN_QUICKSTART_SETUP=docker \
NLEAN_DOCKER_IMAGE=nlean-local:devnet2 \
NLEAN_NETWORK_NAME=devnet0 \
NLEAN_QUICKSTART_NODES=nlean_0,ethlambda_0 \
./spin-node.sh --node nlean_0,ethlambda_0 --generateGenesis --metrics
```

`local-devnet-nlean-ethlambda` is intended for local interop iteration under `vendor/lean-quickstart`.

Notes:
- On macOS, if `--nlean-setup binary` is used with non-`nlean_*` peers, the script auto-switches nlean to docker mode to avoid QUIC handshake timeouts in mixed host/docker runs.
- `bootstrapNodeNames` generation is opt-in for quickstart configs. Set `NLEAN_ENABLE_BOOTSTRAP_NODE_NAMES=true` only when peer-id derivation is known to match your peer clients.
- By default, the interop script starts quickstart in background, verifies nlean Prometheus metrics, then stops nodes and exits with non-zero on failure.

What this does:
- installs `client-cmds/nlean-cmd.sh` into your lean-quickstart checkout
- uses a quickstart-generated `validator-config.yaml` as the devnet validator layout
- builds patched pubsub package, publishes `Lean.Client`, builds Rust FFI native library, and starts quickstart via `spin-node.sh`
- maps consensus scenarios to interop checks on nlean metrics:
  - from-genesis progress: `lean_head_slot` reaches target (`--min-head-slot`, default `3`)
  - finalize progression: `lean_latest_finalized_slot` reaches target (`--min-finalized-slot`, default `0`; raise it when running finalized-gated interop)
  - multi-node consistency: min/max finalized slot across selected `nlean_*` jobs converge

## Crypto binding tests

```bash
# Optional but recommended before interop runs (forces local Anonymous-compatible pubsub package)
./scripts/libp2p/build-patched-pubsub-package.sh --force

# One-line: builds native FFI and runs crypto tests
dotnet test tests/Lean.Crypto.Tests/Lean.Crypto.Tests.csproj -c Release

# Consensus tests
dotnet test tests/Lean.Consensus.Tests/Lean.Consensus.Tests.csproj -c Release

# Full solution tests
dotnet test Lean.sln -c Release

# Single test (example)
dotnet test tests/Lean.Crypto.Tests/Lean.Crypto.Tests.csproj -c Release --filter FullyQualifiedName~LeanSigXmssInteropTests
```

If `dotnet` is not on PATH, use `~/.dotnet/dotnet` instead. The test build expects a Rust toolchain (`cargo`) to be available.
If you hit local socket permission errors in CI or sandboxed shells, retry with `--disable-build-servers /m:1 /nodeReuse:false`.

## Docker

```bash
docker build -t lean-client --build-arg GIT_SHA=<git_sha> .
```

## Notes

- validator-config.yaml format follows lean-quickstart.
- Native crypto is built from pinned leanSig/leanMultisig commits.
- Consensus, state transition, and fork choice are stubbed and need leanSpec wiring.

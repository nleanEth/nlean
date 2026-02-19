# Lean C# Client

This repo contains a .NET 10+ Lean consensus client scaffold with Rust FFI bindings for leanSig/leanMultisig and Nethermind dotnet-libp2p networking stack.

## Quick start

```bash
# Build native crypto bindings
./scripts/build-native.sh

# Build patched dotnet-libp2p pubsub package (Anonymous gossip compatibility)
./scripts/libp2p/build-patched-pubsub-package.sh

# Run the client
./src/Lean.Client/bin/Debug/net10.0/Lean.Client --config config/node-config.json --validator-config config/validator-config.yaml --node lean_client_0
```

## lean-quickstart interop

```bash
# One-line local interop run (nlean + zeam + ream) with pass/fail checks
./scripts/interop/run-lean-quickstart-devnet2.sh --quickstart-dir /path/to/lean-quickstart
# Disable sudo shim if your quickstart setup requires real sudo:
# ./scripts/interop/run-lean-quickstart-devnet2.sh --quickstart-dir /path/to/lean-quickstart --no-sudo-shim
# Override gossip network name if needed:
# ./scripts/interop/run-lean-quickstart-devnet2.sh --quickstart-dir /path/to/lean-quickstart --network-name devnet2
# Keep network running after checks:
# ./scripts/interop/run-lean-quickstart-devnet2.sh --quickstart-dir /path/to/lean-quickstart --keep-running
# Skip checks and only boot the network:
# ./scripts/interop/run-lean-quickstart-devnet2.sh --quickstart-dir /path/to/lean-quickstart --skip-checks
```

Notes:
- On macOS, if `--nlean-setup binary` is used with non-`nlean_*` peers, the script auto-switches nlean to docker mode to avoid QUIC handshake timeouts in mixed host/docker runs.
- `bootstrapNodeNames` generation is opt-in for quickstart configs. Set `NLEAN_ENABLE_BOOTSTRAP_NODE_NAMES=true` only when peer-id derivation is known to match your peer clients.
- By default, the interop script starts quickstart in background, verifies nlean Prometheus metrics, then stops nodes and exits with non-zero on failure.

What this does:
- installs `client-cmds/nlean-cmd.sh` into your lean-quickstart checkout
- uses `config/validator-config.quickstart.yaml` as the devnet validator layout
- builds patched pubsub package, publishes `Lean.Client`, builds Rust FFI native library, and starts quickstart via `spin-node.sh`
- maps consensus scenarios to interop checks on nlean metrics:
  - from-genesis progress: `lean_consensus_head_slot` reaches target (`--min-head-slot`, default `3`)
  - finalize progression: `lean_consensus_finalized_slot` reaches target (`--min-finalized-slot`, default `0`; raise it when running finalized-gated interop)
  - multi-node consistency: min/max finalized slot across selected `nlean_*` jobs converge
  - optional recovery signal: `--require-blocks-by-root` requires `lean_sync_blocks_by_root_requests_total > 0`

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

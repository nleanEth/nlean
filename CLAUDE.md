# CLAUDE.md

This file provides guidance for AI coding assistants working on this codebase.

## Quick Reference

- **Build**: `dotnet build Lean.sln -c Release`
- **Test**: `dotnet test Lean.sln -c Release`
- **Format**: `./.dotnet-tools/dotnet-format Lean.sln --check --fix-whitespace --exclude vendor`
- **Native crypto**: `./scripts/build-native.sh`
- **Patched pubsub**: `./scripts/libp2p/build-patched-pubsub-package.sh`
- **Patched quic**: `./scripts/libp2p/build-patched-quic-package.sh`

## Project Structure

`nlean` is a Lean consensus client written in C# (.NET 10+) with Rust FFI for crypto and Nethermind dotnet-libp2p for networking.

Key source directories:
- `src/Lean.Consensus/` — consensus logic, fork choice (proto-array), sync (backfill, head, checkpoint)
- `src/Lean.Crypto/` — Rust FFI bindings for leanSig / leanMultisig
- `src/Lean.Network/` — libp2p gossip, RPC, discovery
- `src/Lean.Validator/` — validator duties (propose, attest, aggregate)
- `src/Lean.Node/` — node app orchestration
- `src/Lean.Client/` — CLI entry point
- `native/lean-crypto-ffi/` — Rust crate for crypto

## Coding Conventions

- Do NOT modify files under `vendor/` unless explicitly required.
- Consensus behavior changes must include corresponding tests in `Lean.Consensus.Tests`.
- Use NUnit (`[Test]`, `[TestFixture]`) for all test projects.
- If `dotnet` is not on PATH, use `~/.dotnet/dotnet`.
- Rust toolchain (`cargo`) is required for native crypto builds/tests.

## Testing

~368 tests total across 5 test projects:
- `Lean.Consensus.Tests` (~291) — fork choice, sync, state transition, multi-node simulation
- `Lean.Network.Tests` (~40) — networking
- `Lean.Validator.Tests` (~28) — validator service
- `Lean.Crypto.Tests` (~5) — FFI round-trip
- `Lean.Integration.Tests` (~4) — multi-node devnet (requires published binary first)

Integration tests require:
```bash
dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false
dotnet test tests/Lean.Integration.Tests/Lean.Integration.Tests.csproj -c Release
```

Consensus simulation (CI-aligned):
```bash
dotnet test tests/Lean.Consensus.Tests/Lean.Consensus.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName~ConsensusMultiNodeFinalizationTests" \
  /m:1 /nodeReuse:false
```

## CI

GitHub Actions runs on PR and push to `main`:
- `format-check` — dotnet-format whitespace check
- `build-test` — build + unit tests + publish (ubuntu + macos)
- `consensus-simulation` — multi-node finalization simulation
- `integration-tests` — 4-node devnet (45 min timeout)

On tag `v*` push:
- `docker-publish` — multi-arch Docker image to `ghcr.io/nleaneth/nlean`
- `release` — GitHub Release with self-contained binaries (linux-x64, linux-arm64, osx-arm64)

## Local Devnet

```bash
git submodule update --init --recursive vendor/lean-quickstart
cd vendor/lean-quickstart
NETWORK_DIR=local-devnet-nlean ./spin-node.sh --node all
```

`client-cmds/nlean-cmd.sh` follows lean-quickstart's client-cmd contract.

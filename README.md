# Lean C# Client Devnet-2

This repo contains a .NET 10+ Lean consensus client scaffold with Rust FFI bindings for leanSig/leanMultisig and Nethermind dotnet-libp2p networking stack.

## Quick start

```bash
# Build native crypto bindings
./scripts/build-native.sh

# Run the client
./src/Lean.Client/bin/Debug/net10.0/Lean.Client --config config/node-config.json --validator-config config/validator-config.yaml --node lean_client_0
```

## Crypto binding tests

```bash
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

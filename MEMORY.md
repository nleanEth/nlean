# MEMORY.md

## Project Identity

- Repo: `nlean`
- Goal: Lean C# client scaffold built with `.NET 10+`, `Rust FFI`, and `dotnet-libp2p`.
- Main solution: `Lean.sln`

## Environment Baseline

- Required .NET SDK version is defined in `global.json`.
- Rust toolchain (`cargo`) is required for native/FFI build and tests.
- Interop depends on quickstart submodule: `vendor/lean-quickstart`.

## Critical Commands

```bash
# Native/crypto
./scripts/build-native.sh

# Patched pubsub package for Anonymous gossip compatibility
./scripts/libp2p/build-patched-pubsub-package.sh

# Build and test
dotnet build Lean.sln -c Release
dotnet test Lean.sln -c Release

# Interop wrapper
./scripts/interop/run-lean-quickstart-devnet2.sh
```

## CI Gates

Expected GitHub CI jobs on PR/push to `main`:

- `format-check`
- `build-test (ubuntu-latest)`
- `build-test (macos-latest)`
- `consensus-simulation`

## Known Pitfalls

- If `cargo` is missing, native-related build/tests fail.
- Interop and CI expect patched pubsub package to be built.
- If `dotnet` is not on PATH, use `~/.dotnet/dotnet`.

## Stable Team Preferences

- Prefer edits in `src/`, `tests/`, `scripts/`, and `config/`.
- Avoid modifying `vendor/` unless explicitly required.
- Consensus behavior changes should include `Lean.Consensus.Tests`.

## Decisions Log

Append only. Use one line per decision:

- `YYYY-MM-DD`: decision, reason, impact

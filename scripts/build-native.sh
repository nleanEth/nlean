#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
CRATE_DIR="$ROOT_DIR/native/lean-crypto-ffi"
# The TEST-scheme crate is a separate cdylib because rec_aggregation compiles
# its bytecode against a single scheme at build time. Only required for
# spec-test aggregate-verify fixtures (leanEnv=test).
TEST_CRATE_DIR="$ROOT_DIR/native/lean-crypto-ffi-test"

cargo build --release --manifest-path "$CRATE_DIR/Cargo.toml"
cargo build --release --manifest-path "$TEST_CRATE_DIR/Cargo.toml"

OS=$(uname -s)
ARCH=$(uname -m)

if [[ "$OS" == "Darwin" ]]; then
  if [[ "$ARCH" == "arm64" ]]; then
    RID="osx-arm64"
  else
    RID="osx-x64"
  fi
  LIB_NAME="liblean_crypto_ffi.dylib"
  TEST_LIB_NAME="liblean_crypto_ffi_test.dylib"
elif [[ "$OS" == "Linux" ]]; then
  RID="linux-x64"
  LIB_NAME="liblean_crypto_ffi.so"
  TEST_LIB_NAME="liblean_crypto_ffi_test.so"
else
  echo "Unsupported OS for build-native.sh: $OS" >&2
  exit 1
fi

OUT_DIR="$ROOT_DIR/src/Lean.Client/runtimes/$RID/native"
mkdir -p "$OUT_DIR"

cp "$CRATE_DIR/target/release/$LIB_NAME" "$OUT_DIR/"
cp "$TEST_CRATE_DIR/target/release/$TEST_LIB_NAME" "$OUT_DIR/"

echo "Copied $LIB_NAME and $TEST_LIB_NAME to $OUT_DIR"

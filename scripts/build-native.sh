#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
CRATE_DIR="$ROOT_DIR/native/lean-crypto-ffi"

cargo build --release --manifest-path "$CRATE_DIR/Cargo.toml"

OS=$(uname -s)
ARCH=$(uname -m)

if [[ "$OS" == "Darwin" ]]; then
  if [[ "$ARCH" == "arm64" ]]; then
    RID="osx-arm64"
  else
    RID="osx-x64"
  fi
  LIB_NAME="liblean_crypto_ffi.dylib"
elif [[ "$OS" == "Linux" ]]; then
  RID="linux-x64"
  LIB_NAME="liblean_crypto_ffi.so"
else
  echo "Unsupported OS for build-native.sh: $OS" >&2
  exit 1
fi

OUT_DIR="$ROOT_DIR/src/Lean.Client/runtimes/$RID/native"
mkdir -p "$OUT_DIR"

cp "$CRATE_DIR/target/release/$LIB_NAME" "$OUT_DIR/"

echo "Copied $LIB_NAME to $OUT_DIR"

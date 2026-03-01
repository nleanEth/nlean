#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
PATCH_FILE="$ROOT_DIR/scripts/libp2p/patches/quic-pooled-buffers.patch"
LOCAL_FEED_DIR="$ROOT_DIR/local-nuget"

UPSTREAM_REPO="https://github.com/NethermindEth/dotnet-libp2p"
UPSTREAM_COMMIT="b6f8ab0c2cd9e170431f43af86f92ee298ff0e69"
WORK_DIR="${TMPDIR:-/tmp}/nlean-dotnet-libp2p"

PACKAGE_ID="Nethermind.Libp2p.Protocols.Quic"
PACKAGE_VERSION="1.0.0-preview.51"
PACKAGE_FILE="$LOCAL_FEED_DIR/${PACKAGE_ID}.${PACKAGE_VERSION}.nupkg"
PACKAGE_CACHE_DIR="${NUGET_PACKAGES:-$HOME/.nuget/packages}/nethermind.libp2p.protocols.quic/${PACKAGE_VERSION}"

FORCE=false
if [[ "${1:-}" == "--force" ]]; then
  FORCE=true
fi

if [[ "$FORCE" == false && -f "$PACKAGE_FILE" ]]; then
  echo "Using existing patched package: $PACKAGE_FILE"
  exit 0
fi

if [[ ! -f "$PATCH_FILE" ]]; then
  echo "Missing patch file: $PATCH_FILE" >&2
  exit 1
fi

mkdir -p "$LOCAL_FEED_DIR"

if [[ ! -d "$WORK_DIR/.git" ]]; then
  git clone "$UPSTREAM_REPO" "$WORK_DIR"
fi

git -C "$WORK_DIR" fetch --depth 1 origin "$UPSTREAM_COMMIT"
git -C "$WORK_DIR" checkout -f "$UPSTREAM_COMMIT"
git -C "$WORK_DIR" clean -fdx
git -C "$WORK_DIR" apply "$PATCH_FILE"

dotnet_sdk_version=$(dotnet --version)
cat > "$WORK_DIR/global.json" <<EOF
{
  "sdk": {
    "version": "${dotnet_sdk_version}",
    "allowPrerelease": false,
    "rollForward": "latestFeature"
  }
}
EOF

pushd "$WORK_DIR" > /dev/null
dotnet pack "src/libp2p/Libp2p.Protocols.Quic/Libp2p.Protocols.Quic.csproj" \
  -c Release \
  -p:PackageVersion="$PACKAGE_VERSION" \
  -o "$LOCAL_FEED_DIR"
popd > /dev/null

# Force restore to consume the local patched package instead of a cached copy.
rm -rf "$PACKAGE_CACHE_DIR"

echo "Patched package generated: $PACKAGE_FILE"

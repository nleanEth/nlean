#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
PATCH_FILE="$ROOT_DIR/scripts/libp2p/patches/identify-best-effort.patch"
PATCH_FILE_SESSION_RECONNECT="$ROOT_DIR/scripts/libp2p/patches/session-reconnect-takeover.patch"
LOCAL_FEED_DIR="$ROOT_DIR/local-nuget"

UPSTREAM_REPO="https://github.com/NethermindEth/dotnet-libp2p"
UPSTREAM_COMMIT="b6f8ab0c2cd9e170431f43af86f92ee298ff0e69"
WORK_DIR="${TMPDIR:-/tmp}/nlean-dotnet-libp2p"

PACKAGE_ID="Nethermind.Libp2p"
CORE_PACKAGE_ID="Nethermind.Libp2p.Core"
PACKAGE_VERSION="1.0.0-preview.51"
PACKAGE_FILE="$LOCAL_FEED_DIR/${PACKAGE_ID}.${PACKAGE_VERSION}.nupkg"
CORE_PACKAGE_FILE="$LOCAL_FEED_DIR/${CORE_PACKAGE_ID}.${PACKAGE_VERSION}.nupkg"
PACKAGE_CACHE_DIR="${NUGET_PACKAGES:-$HOME/.nuget/packages}/nethermind.libp2p/${PACKAGE_VERSION}"
CORE_PACKAGE_CACHE_DIR="${NUGET_PACKAGES:-$HOME/.nuget/packages}/nethermind.libp2p.core/${PACKAGE_VERSION}"

FORCE=false
if [[ "${1:-}" == "--force" ]]; then
  FORCE=true
fi

if [[ "$FORCE" == false && -f "$PACKAGE_FILE" && -f "$CORE_PACKAGE_FILE" ]]; then
  echo "Using existing patched packages: $PACKAGE_FILE, $CORE_PACKAGE_FILE"
  exit 0
fi

if [[ ! -f "$PATCH_FILE" ]]; then
  echo "Missing patch file: $PATCH_FILE" >&2
  exit 1
fi

if [[ ! -f "$PATCH_FILE_SESSION_RECONNECT" ]]; then
  echo "Missing patch file: $PATCH_FILE_SESSION_RECONNECT" >&2
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
git -C "$WORK_DIR" apply "$PATCH_FILE_SESSION_RECONNECT"

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
# Pack both Libp2p (façade) and Libp2p.Core. The session-reconnect-takeover
# patch lives in Libp2p.Core (Peer.cs); the façade pulls it as a transitive
# nuget dep, so without packing Libp2p.Core the patched DLL never reaches the
# nlean publish output.
dotnet pack "src/libp2p/Libp2p.Core/Libp2p.Core.csproj" \
  -c Release \
  -p:PackageVersion="$PACKAGE_VERSION" \
  -o "$LOCAL_FEED_DIR"
dotnet pack "src/libp2p/Libp2p/Libp2p.csproj" \
  -c Release \
  -p:PackageVersion="$PACKAGE_VERSION" \
  -o "$LOCAL_FEED_DIR"
popd > /dev/null

# Force restore to consume the local patched packages instead of cached copies.
rm -rf "$PACKAGE_CACHE_DIR" "$CORE_PACKAGE_CACHE_DIR"

echo "Patched packages generated: $PACKAGE_FILE, $CORE_PACKAGE_FILE"

#!/usr/bin/env bash
set -euo pipefail

configDir=${1:-/config}
dataDir=${2:-/data}
nodeName=${3:-lean_client_0}

exec /app/Lean.Client \
  --config "$configDir/node-config.json" \
  --validator-config "$configDir/validator-config.yaml" \
  --node "$nodeName" \
  --data-dir "$dataDir" \
  --metrics

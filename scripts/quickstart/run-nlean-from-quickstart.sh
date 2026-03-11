#!/usr/bin/env bash
set -euo pipefail

nlean_repo=""
config_dir=""
data_dir=""
node_name=""
node_key_path=""
quic_port=""
metrics_port=""
enable_metrics="${NLEAN_ENABLE_METRICS:-true}"
network_name="${NLEAN_NETWORK_NAME:-devnet2}"
log_level="${NLEAN_LOG_LEVEL:-}"

usage() {
  cat <<USAGE
Usage:
  run-nlean-from-quickstart.sh \
    --nlean-repo PATH \
    --config-dir PATH \
    --data-dir PATH \
    --node-name NAME \
    --node-key-path PATH \
    --quic-port PORT \
    --metrics-port PORT \
    [--enable-metrics true|false]
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --nlean-repo)
      nlean_repo="$2"
      shift 2
      ;;
    --config-dir)
      config_dir="$2"
      shift 2
      ;;
    --data-dir)
      data_dir="$2"
      shift 2
      ;;
    --node-name)
      node_name="$2"
      shift 2
      ;;
    --node-key-path)
      node_key_path="$2"
      shift 2
      ;;
    --quic-port)
      quic_port="$2"
      shift 2
      ;;
    --metrics-port)
      metrics_port="$2"
      shift 2
      ;;
    --enable-metrics)
      enable_metrics="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

enable_metrics=$(echo "$enable_metrics" | tr '[:upper:]' '[:lower:]')
if [[ "$enable_metrics" != "true" && "$enable_metrics" != "false" ]]; then
  echo "Invalid --enable-metrics value: $enable_metrics (expected true|false)" >&2
  exit 1
fi

if [[ -z "$nlean_repo" || -z "$config_dir" || -z "$data_dir" || -z "$node_name" || -z "$quic_port" || -z "$metrics_port" ]]; then
  usage >&2
  exit 1
fi

if [[ -z "${network_name// }" ]]; then
  echo "NLEAN_NETWORK_NAME must not be empty." >&2
  exit 1
fi

log_level="${log_level#"${log_level%%[![:space:]]*}"}"
log_level="${log_level%"${log_level##*[![:space:]]}"}"

if [[ -z "$node_key_path" ]]; then
  node_key_path="$config_dir/$node_name.key"
fi

if [[ ! -f "$node_key_path" ]]; then
  echo "Missing libp2p node key at $node_key_path" >&2
  exit 1
fi

validator_index="0"
if [[ "$node_name" =~ _([0-9]+)$ ]]; then
  validator_index="${BASH_REMATCH[1]}"
fi

validator_public_key_path="$config_dir/hash-sig-keys/validator_${validator_index}_pk.ssz"
validator_secret_key_path="$config_dir/hash-sig-keys/validator_${validator_index}_sk.ssz"
if [[ ! -f "$validator_public_key_path" || ! -f "$validator_secret_key_path" ]]; then
  echo "Missing hash-sig key files for validator index $validator_index under $config_dir/hash-sig-keys" >&2
  exit 1
fi

binary_path="$nlean_repo/artifacts/lean-client/Lean.Client"
if [[ ! -x "$binary_path" ]]; then
  echo "Missing Lean.Client binary at $binary_path" >&2
  echo "Build first: dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false" >&2
  exit 1
fi

configure_macos_quic_runtime() {
  if [[ "$(uname -s)" != "Darwin" ]]; then
    return
  fi

  local -a runtime_library_dirs=()
  if [[ -n "${NLEAN_DYLD_LIBRARY_PATH:-}" ]]; then
    IFS=':' read -r -a runtime_library_dirs <<<"${NLEAN_DYLD_LIBRARY_PATH}"
  else
    runtime_library_dirs=(
      "/opt/homebrew/lib"
      "/opt/homebrew/opt/libmsquic/lib"
      "/usr/local/lib"
      "/usr/local/opt/libmsquic/lib"
      "$(dirname "$binary_path")"
    )
  fi

  local found_msquic="false"
  local merged_dyld_path=""
  local candidate
  for candidate in "${runtime_library_dirs[@]}"; do
    if [[ ! -d "$candidate" ]]; then
      continue
    fi

    if [[ -n "$merged_dyld_path" ]]; then
      merged_dyld_path="${merged_dyld_path}:$candidate"
    else
      merged_dyld_path="$candidate"
    fi

    if [[ -f "$candidate/libmsquic.2.dylib" || -f "$candidate/libmsquic.dylib" ]]; then
      found_msquic="true"
    fi
  done

  if [[ -n "${DYLD_LIBRARY_PATH:-}" ]]; then
    if [[ -n "$merged_dyld_path" ]]; then
      merged_dyld_path="${merged_dyld_path}:$DYLD_LIBRARY_PATH"
    else
      merged_dyld_path="$DYLD_LIBRARY_PATH"
    fi
  fi

  if [[ -n "$merged_dyld_path" ]]; then
    export DYLD_LIBRARY_PATH="$merged_dyld_path"
  fi

  if [[ "$found_msquic" != "true" ]]; then
    echo "Warning: libmsquic was not found in configured library locations. QUIC startup may fail on macOS." >&2
  fi
}

validator_config="$config_dir/validator-config.yaml"
if [[ ! -f "$validator_config" ]]; then
  echo "Missing validator config at $validator_config" >&2
  exit 1
fi

mkdir -p "$data_dir"
configure_macos_quic_runtime

bootstrap_peers_json=""
if [[ -n "${NLEAN_BOOTSTRAP_PEERS:-}" ]]; then
  IFS=',' read -r -a peers <<<"${NLEAN_BOOTSTRAP_PEERS}"
  for raw_peer in "${peers[@]}"; do
    peer="${raw_peer#${raw_peer%%[![:space:]]*}}"
    peer="${peer%${peer##*[![:space:]]}}"
    if [[ -z "$peer" ]]; then
      continue
    fi

    escaped_peer="${peer//\\/\\\\}"
    escaped_peer="${escaped_peer//\"/\\\"}"
    if [[ -n "$bootstrap_peers_json" ]]; then
      bootstrap_peers_json+=","
    fi
    bootstrap_peers_json+="\"${escaped_peer}\""
  done
fi

# Use a dialable loopback listen host for local quickstart interop by default.
# 0.0.0.0 works for binding but can surface as non-dialable peer records for pubsub reconnects.
listen_host="${NLEAN_LISTEN_HOST:-127.0.0.1}"
if [[ -z "${listen_host// }" ]]; then
  listen_host="127.0.0.1"
fi

node_config="$data_dir/node-config.quickstart.json"
cat > "$node_config" <<EOF_CONFIG
{
  "dataDir": "${data_dir}",
  "network": "${network_name}",
  "apiPort": $((metrics_port + 100)),
  "metrics": {
    "enabled": ${enable_metrics},
    "host": "0.0.0.0",
    "port": ${metrics_port}
  },
  "libp2p": {
    "listenAddresses": ["/ip4/${listen_host}/udp/${quic_port}/quic-v1"],
    "bootstrapPeers": [${bootstrap_peers_json}],
    "privateKeyPath": "${node_key_path}",
    "enableMdns": ${NLEAN_ENABLE_MDNS:-false},
    "enablePubsub": true,
    "enableQuic": true
  },
  "validator": {
    "enabled": true,
    "validatorIndex": ${validator_index},
    "publicKeyPath": "${validator_public_key_path}",
    "secretKeyPath": "${validator_secret_key_path}",
    "publishAggregates": ${NLEAN_PUBLISH_AGGREGATES:-false}
  }
}
EOF_CONFIG

exec "$binary_path" \
  --config "$node_config" \
  --validator-config "$validator_config" \
  --node "$node_name" \
  --data-dir "$data_dir" \
  --metrics "$enable_metrics" \
  ${log_level:+--log "$log_level"}

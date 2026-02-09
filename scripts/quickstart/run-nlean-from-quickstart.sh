#!/usr/bin/env bash
set -euo pipefail

nlean_repo=""
config_dir=""
data_dir=""
node_name=""
quic_port=""
metrics_port=""

usage() {
  cat <<USAGE
Usage:
  run-nlean-from-quickstart.sh \
    --nlean-repo PATH \
    --config-dir PATH \
    --data-dir PATH \
    --node-name NAME \
    --quic-port PORT \
    --metrics-port PORT
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
    --quic-port)
      quic_port="$2"
      shift 2
      ;;
    --metrics-port)
      metrics_port="$2"
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

if [[ -z "$nlean_repo" || -z "$config_dir" || -z "$data_dir" || -z "$node_name" || -z "$quic_port" || -z "$metrics_port" ]]; then
  usage >&2
  exit 1
fi

binary_path="$nlean_repo/artifacts/lean-client/Lean.Client"
if [[ ! -x "$binary_path" ]]; then
  echo "Missing Lean.Client binary at $binary_path" >&2
  echo "Build first: dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false" >&2
  exit 1
fi

validator_config="$config_dir/validator-config.yaml"
if [[ ! -f "$validator_config" ]]; then
  echo "Missing validator config at $validator_config" >&2
  exit 1
fi

mkdir -p "$data_dir"

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

node_config="$data_dir/node-config.quickstart.json"
cat > "$node_config" <<EOF_CONFIG
{
  "dataDir": "${data_dir}",
  "network": "devnet2",
  "metrics": {
    "enabled": true,
    "host": "0.0.0.0",
    "port": ${metrics_port}
  },
  "libp2p": {
    "listenAddresses": ["/ip4/0.0.0.0/udp/${quic_port}/quic-v1"],
    "bootstrapPeers": [${bootstrap_peers_json}],
    "enableMdns": ${NLEAN_ENABLE_MDNS:-true},
    "enablePubsub": true,
    "enableQuic": true
  }
}
EOF_CONFIG

exec "$binary_path" \
  --config "$node_config" \
  --validator-config "$validator_config" \
  --node "$node_name" \
  --data-dir "$data_dir" \
  --metrics

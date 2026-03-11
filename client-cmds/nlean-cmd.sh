#!/bin/bash

#-----------------------nlean setup----------------------
# This script follows lean-quickstart's client-cmd contract:
# it must populate node_binary / node_docker / node_setup.
# It is not a direct container entrypoint wrapper.
# NLEAN_REPO should point to this repository when lean-quickstart is outside this workspace.
# Default assumes sibling checkouts: <workspace>/nlean and <workspace>/lean-quickstart.
nlean_repo="${NLEAN_REPO:-$scriptDir/../nlean}"
nlean_docker_image="${NLEAN_DOCKER_IMAGE:-nlean-local:devnet3}"
nlean_network_name="${NLEAN_NETWORK_NAME:-devnet0}"
log_level="${NLEAN_LOG_LEVEL:-}"
enable_metrics="${enableMetrics:-false}"

enable_metrics=$(echo "$enable_metrics" | tr '[:upper:]' '[:lower:]')
if [[ "$enable_metrics" != "true" && "$enable_metrics" != "false" ]]; then
  enable_metrics="false"
fi

if [[ -z "${nlean_network_name// }" ]]; then
  nlean_network_name="devnet0"
fi

log_level="${log_level#${log_level%%[![:space:]]*}}"
log_level="${log_level%${log_level##*[![:space:]]}}"
log_level_arg=""
if [[ -n "$log_level" ]]; then
  log_level_arg=" --log ${log_level}"
fi

validator_index="0"
if [[ "$item" =~ _([0-9]+)$ ]]; then
  validator_index="${BASH_REMATCH[1]}"
fi
node_private_key_path="${privKeyPath:-${item}.key}"

node_config_host_path="$dataDir/$item/node-config.quickstart.json"
mkdir -p "$dataDir/$item"

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
nlean_listen_host="${NLEAN_LISTEN_HOST:-127.0.0.1}"
if [[ -z "${nlean_listen_host// }" ]]; then
  nlean_listen_host="127.0.0.1"
fi

# Map isAggregator (from parse-vc.sh) to nlean's publishAggregates
publish_aggregates="false"
if [[ "${isAggregator:-false}" == "true" ]]; then
  publish_aggregates="true"
fi

# Build consensus config section (only include non-default fields)
consensus_json=""
if [[ -n "${attestationCommitteeCount:-}" ]]; then
  consensus_json="$(cat <<EOF_CONSENSUS
  "consensus": {
    "attestationCommitteeCount": ${attestationCommitteeCount}
  },
EOF_CONSENSUS
)"
fi

cat > "$node_config_host_path" <<EOF_CONFIG
{
  "dataDir": "/data",
  "network": "${nlean_network_name}",
${consensus_json}
  "apiPort": $((metricsPort + 100)),
  "metrics": {
    "enabled": ${enable_metrics},
    "host": "0.0.0.0",
    "port": ${metricsPort}
  },
  "libp2p": {
    "listenAddresses": ["/ip4/${nlean_listen_host}/udp/${quicPort}/quic-v1"],
    "bootstrapPeers": [${bootstrap_peers_json}],
    "privateKeyPath": "/config/${node_private_key_path}",
    "enableMdns": ${NLEAN_ENABLE_MDNS:-false},
    "enablePubsub": true,
    "enableQuic": true
  },
  "validator": {
    "enabled": true,
    "validatorIndex": ${validator_index},
    "publicKeyPath": "/config/hash-sig-keys/validator_${validator_index}_pk.ssz",
    "secretKeyPath": "/config/hash-sig-keys/validator_${validator_index}_sk.ssz",
    "publishAggregates": ${publish_aggregates}
  }
}
EOF_CONFIG

node_binary="NLEAN_PUBLISH_AGGREGATES=${publish_aggregates} \
      $nlean_repo/scripts/quickstart/run-nlean-from-quickstart.sh \
      --nlean-repo $nlean_repo \
      --config-dir $configDir \
      --data-dir $dataDir/$item \
      --node-name $item \
      --node-key-path $configDir/$node_private_key_path \
      --quic-port $quicPort \
      --metrics-port $metricsPort \
      --enable-metrics ${enable_metrics}"

nlean_docker_extra_env=""
if [[ -n "${NLEAN_DEBUG_DUMP_ATTESTATIONS:-}" ]]; then
  nlean_docker_extra_env+=" -e NLEAN_DEBUG_DUMP_ATTESTATIONS=${NLEAN_DEBUG_DUMP_ATTESTATIONS}"
fi
if [[ -n "${NLEAN_DEBUG_DUMP_DIR:-}" ]]; then
  nlean_docker_extra_env+=" -e NLEAN_DEBUG_DUMP_DIR=${NLEAN_DEBUG_DUMP_DIR}"
fi
if [[ -n "${NLEAN_DEBUG_DUMP_BLOCKS:-}" ]]; then
  nlean_docker_extra_env+=" -e NLEAN_DEBUG_DUMP_BLOCKS=${NLEAN_DEBUG_DUMP_BLOCKS}"
fi
if [[ -n "${NLEAN_DEBUG_DUMP_BLOCK_DIR:-}" ]]; then
  nlean_docker_extra_env+=" -e NLEAN_DEBUG_DUMP_BLOCK_DIR=${NLEAN_DEBUG_DUMP_BLOCK_DIR}"
fi
if [[ -n "${NLEAN_DEBUG_DUMP_OBSERVED_PROOFS:-}" ]]; then
  nlean_docker_extra_env+=" -e NLEAN_DEBUG_DUMP_OBSERVED_PROOFS=${NLEAN_DEBUG_DUMP_OBSERVED_PROOFS}"
fi
if [[ -n "${NLEAN_DEBUG_DUMP_OBSERVED_PROOFS_DIR:-}" ]]; then
  nlean_docker_extra_env+=" -e NLEAN_DEBUG_DUMP_OBSERVED_PROOFS_DIR=${NLEAN_DEBUG_DUMP_OBSERVED_PROOFS_DIR}"
fi
if [[ -n "${NLEAN_DEBUG_DUMP_OBSERVED_BLOCKS:-}" ]]; then
  nlean_docker_extra_env+=" -e NLEAN_DEBUG_DUMP_OBSERVED_BLOCKS=${NLEAN_DEBUG_DUMP_OBSERVED_BLOCKS}"
fi
if [[ -n "${NLEAN_DEBUG_DUMP_OBSERVED_BLOCKS_DIR:-}" ]]; then
  nlean_docker_extra_env+=" -e NLEAN_DEBUG_DUMP_OBSERVED_BLOCKS_DIR=${NLEAN_DEBUG_DUMP_OBSERVED_BLOCKS_DIR}"
fi

# Docker mode requires an image that already contains lean-quickstart compatible config.
node_docker="${nlean_docker_extra_env} ${nlean_docker_image} \
      --config /data/node-config.quickstart.json \
      --validator-config /config/validator-config.yaml \
      --node $item \
      --data-dir /data \
      --metrics ${enable_metrics}${log_level_arg}"

# choose either binary or docker
node_setup="${NLEAN_QUICKSTART_SETUP:-binary}"

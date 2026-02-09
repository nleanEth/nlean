#!/bin/bash

#-----------------------nlean setup----------------------
# NLEAN_REPO should point to this repository when lean-quickstart is outside this workspace.
# Default assumes sibling checkouts: <workspace>/nlean and <workspace>/lean-quickstart.
nlean_repo="${NLEAN_REPO:-$scriptDir/../nlean}"

node_binary="$nlean_repo/scripts/quickstart/run-nlean-from-quickstart.sh \
      --nlean-repo $nlean_repo \
      --config-dir $configDir \
      --data-dir $dataDir/$item \
      --node-name $item \
      --quic-port $quicPort \
      --metrics-port $metricsPort"

# Docker mode requires an image that already contains lean-quickstart compatible config.
node_docker="ghcr.io/nlean-eth/nlean:devnet2 \
      --config /config/node-config.json \
      --validator-config /config/validator-config.yaml \
      --node $item \
      --data-dir /data \
      --metrics"

# choose either binary or docker
node_setup="${NLEAN_QUICKSTART_SETUP:-binary}"

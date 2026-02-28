#!/usr/bin/env bash
set -euo pipefail

# Catchup interop test for devnet3:
#   Phase 1: Start 3 nodes, wait for >= 2 finalizations
#   Phase 2: Stop 1 nlean node, wait for >= 2 more finalizations with 2 nodes
#   Phase 3: Restart the stopped node, wait for >= 2 more finalizations with 3 nodes

root_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
quickstart_dir="${NLEAN_QUICKSTART_DIR:-$root_dir/vendor/lean-quickstart}"
network_dir="local-devnet-nlean"
network_name="${NLEAN_NETWORK_NAME:-devnet3}"
nodes="nlean_0,nlean_1,nlean_2"
stopped_node="nlean_2"
with_metrics="true"
nlean_setup="${NLEAN_QUICKSTART_SETUP:-docker}"
nlean_docker_image="${NLEAN_DOCKER_IMAGE:-nlean-local:devnet3}"
use_sudo_shim="${NLEAN_QUICKSTART_USE_SUDO_SHIM:-true}"
skip_docker_build="${NLEAN_SKIP_DOCKER_BUILD:-false}"
check_timeout_seconds="${NLEAN_INTEROP_CHECK_TIMEOUT_SECONDS:-600}"
check_poll_seconds="${NLEAN_INTEROP_CHECK_POLL_SECONDS:-5}"
finalize_count=2

# Epochs are ~6 slots each with SlotsPerEpoch=6 in quickstart config.
# 2 finalizations = finalized slot advances by at least 2 epoch boundaries.
slots_per_epoch=6

usage() {
  cat <<USAGE
Usage:
  run-lean-quickstart-devnet3-catchup.sh [options]

Catchup interop test:
  Phase 1: 3 nodes finalize >= ${finalize_count} times
  Phase 2: Stop ${stopped_node}, 2 nodes finalize >= ${finalize_count} more times
  Phase 3: Restart ${stopped_node}, 3 nodes finalize >= ${finalize_count} more times

Options:
  --quickstart-dir PATH   Path to lean-quickstart checkout (default: vendor/lean-quickstart)
  --stopped-node NAME     Node to stop/restart (default: nlean_1)
  --nlean-setup MODE      binary|docker (default: docker)
  --skip-docker-build     Skip docker image build
  --check-timeout SEC     Timeout per phase (default: 600)
  --finalize-count N      Min finalizations per phase (default: 2)
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --quickstart-dir) quickstart_dir="$2"; shift 2 ;;
    --stopped-node) stopped_node="$2"; shift 2 ;;
    --nlean-setup) nlean_setup="$2"; shift 2 ;;
    --skip-docker-build) skip_docker_build="true"; shift ;;
    --check-timeout) check_timeout_seconds="$2"; shift 2 ;;
    --finalize-count) finalize_count="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 1 ;;
  esac
done

if [[ ! -d "$quickstart_dir" || ! -f "$quickstart_dir/spin-node.sh" ]]; then
  echo "Invalid lean-quickstart directory: $quickstart_dir" >&2
  exit 1
fi

if ! command -v yq >/dev/null 2>&1; then
  echo "yq is required. Install it first." >&2
  exit 1
fi

# --- Reuse helpers from the base interop script ---

network_root="$quickstart_dir/$network_dir"
network_genesis_dir="$network_root/genesis"
mkdir -p "$network_genesis_dir" "$network_root/data"

float_ge() {
  awk -v left="$1" -v right="$2" 'BEGIN { exit !(left + 0 >= right + 0) }'
}

float_lt() {
  awk -v left="$1" -v right="$2" 'BEGIN { exit !(left + 0 < right + 0) }'
}

resolve_metrics_port() {
  local node_name="$1"
  yq eval ".validators[] | select(.name == \"${node_name}\") | .metricsPort" \
    "$network_genesis_dir/validator-config.yaml" 2>/dev/null | head -n 1
}

fetch_metrics_payload() {
  local node_name="$1"
  local metrics_port="$2"
  local payload=""

  payload=$(curl -fsS "http://127.0.0.1:${metrics_port}/metrics" 2>/dev/null || true)
  if [[ -n "$payload" ]]; then
    echo "$payload"
    return 0
  fi

  if docker ps --format '{{.Names}}' | grep -Fxq "$node_name"; then
    payload=$(docker exec "$node_name" sh -lc \
      "wget -qO- http://127.0.0.1:${metrics_port}/metrics 2>/dev/null || \
       curl -fsS http://127.0.0.1:${metrics_port}/metrics 2>/dev/null" 2>/dev/null || true)
    if [[ -n "$payload" ]]; then
      echo "$payload"
      return 0
    fi
  fi

  return 1
}

extract_metric_value() {
  local payload="$1"
  local metric_name="$2"
  awk -v metric="$metric_name" '$1 ~ ("^" metric "({|$)") { print $2; exit }' <<<"$payload"
}

# Wait for target_nodes to all have finalized_slot >= target_finalized.
# Only checks the nodes listed in check_nodes array.
# Returns the minimum finalized slot observed when target is met.
wait_for_finalization() {
  local phase_name="$1"
  local target_finalized="$2"
  shift 2
  local check_nodes=("$@")

  echo ""
  echo "=== ${phase_name}: waiting for finalized >= ${target_finalized} on nodes: ${check_nodes[*]} ==="

  local deadline=$((SECONDS + check_timeout_seconds))
  while (( SECONDS < deadline )); do
    local collected=0
    local finalized_min=""
    local finalized_max=""
    local head_min=""
    local scrape_failures=0

    for node_name in "${check_nodes[@]}"; do
      local metrics_port=""
      local payload=""
      local finalized_value=""
      local head_value=""

      metrics_port=$(resolve_metrics_port "$node_name")
      if [[ -z "$metrics_port" || "$metrics_port" == "null" ]]; then
        scrape_failures=$((scrape_failures + 1))
        continue
      fi

      payload=$(fetch_metrics_payload "$node_name" "$metrics_port" || true)
      if [[ -z "$payload" ]]; then
        scrape_failures=$((scrape_failures + 1))
        continue
      fi

      finalized_value=$(extract_metric_value "$payload" "lean_latest_finalized_slot")
      head_value=$(extract_metric_value "$payload" "lean_head_slot")

      if [[ -z "$finalized_value" || -z "$head_value" ]]; then
        scrape_failures=$((scrape_failures + 1))
        continue
      fi

      collected=$((collected + 1))

      if [[ -z "$finalized_min" ]] || float_lt "$finalized_value" "$finalized_min"; then
        finalized_min="$finalized_value"
      fi
      if [[ -z "$finalized_max" ]] || float_lt "$finalized_max" "$finalized_value"; then
        finalized_max="$finalized_value"
      fi
      if [[ -z "$head_min" ]] || float_lt "$head_value" "$head_min"; then
        head_min="$head_value"
      fi
    done

    if [[ "$collected" -gt 0 ]]; then
      echo "${phase_name}: collected=${collected}/${#check_nodes[@]}, finalized_min=${finalized_min}, finalized_max=${finalized_max}, head_min=${head_min}"

      if [[ "$collected" -eq "${#check_nodes[@]}" ]] &&
         float_ge "$finalized_min" "$target_finalized"; then
        echo "${phase_name}: PASSED (finalized_min=${finalized_min} >= ${target_finalized})"
        return 0
      fi
    fi

    sleep "$check_poll_seconds"
  done

  echo "${phase_name}: TIMED OUT after ${check_timeout_seconds}s (target finalized >= ${target_finalized})" >&2
  return 1
}

# Get the current minimum finalized slot across the given nodes.
get_min_finalized() {
  local check_nodes=("$@")
  local min_val=""

  for node_name in "${check_nodes[@]}"; do
    local metrics_port=""
    local payload=""
    local val=""

    metrics_port=$(resolve_metrics_port "$node_name")
    if [[ -z "$metrics_port" || "$metrics_port" == "null" ]]; then continue; fi

    payload=$(fetch_metrics_payload "$node_name" "$metrics_port" || true)
    if [[ -z "$payload" ]]; then continue; fi

    val=$(extract_metric_value "$payload" "lean_latest_finalized_slot")
    if [[ -z "$val" ]]; then continue; fi

    if [[ -z "$min_val" ]] || float_lt "$val" "$min_val"; then
      min_val="$val"
    fi
  done

  echo "${min_val:-0}"
}

wait_for_node_metrics_offline() {
  local node_name="$1"
  local timeout_seconds="${2:-30}"
  local metrics_port=""
  metrics_port=$(resolve_metrics_port "$node_name")
  if [[ -z "$metrics_port" || "$metrics_port" == "null" ]]; then
    return 0
  fi

  local deadline=$((SECONDS + timeout_seconds))
  while (( SECONDS < deadline )); do
    if ! fetch_metrics_payload "$node_name" "$metrics_port" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done

  return 1
}

wait_for_node_metrics_online() {
  local node_name="$1"
  local timeout_seconds="${2:-60}"
  local metrics_port=""
  metrics_port=$(resolve_metrics_port "$node_name")
  if [[ -z "$metrics_port" || "$metrics_port" == "null" ]]; then
    return 0
  fi

  local deadline=$((SECONDS + timeout_seconds))
  while (( SECONDS < deadline )); do
    if fetch_metrics_payload "$node_name" "$metrics_port" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done

  return 1
}

stop_binary_node_processes() {
  local node_name="$1"
  local pid_lines=""
  local pattern=""
  local matches=""

  for pattern in \
    "Lean\\.Client.*--node[[:space:]]+${node_name}([[:space:]]|$)" \
    "run-nlean-from-quickstart\\.sh.*--node-name[[:space:]]+${node_name}([[:space:]]|$)"
  do
    matches=$(pgrep -f "$pattern" 2>/dev/null || true)
    if [[ -n "$matches" ]]; then
      pid_lines+=$'\n'"$matches"
    fi
  done

  pid_lines=$(printf '%s\n' "$pid_lines" | awk 'NF' | sort -u)
  if [[ -z "$pid_lines" ]]; then
    echo "No binary process found for ${node_name}."
    return 0
  fi

  local pid=""
  while IFS= read -r pid; do
    [[ -z "$pid" ]] && continue
    [[ "$pid" -eq "$$" ]] && continue
    kill -TERM "$pid" >/dev/null 2>&1 || true
  done <<<"$pid_lines"

  for _ in {1..20}; do
    local any_alive="false"
    while IFS= read -r pid; do
      [[ -z "$pid" ]] && continue
      if kill -0 "$pid" >/dev/null 2>&1; then
        any_alive="true"
        break
      fi
    done <<<"$pid_lines"

    if [[ "$any_alive" == "false" ]]; then
      return 0
    fi
    sleep 0.5
  done

  while IFS= read -r pid; do
    [[ -z "$pid" ]] && continue
    kill -KILL "$pid" >/dev/null 2>&1 || true
  done <<<"$pid_lines"

  return 0
}

stop_node_for_mode() {
  local node_name="$1"

  if [[ "$nlean_setup" == "docker" ]]; then
    docker stop "$node_name" >/dev/null 2>&1 || true
    docker rm -f "$node_name" >/dev/null 2>&1 || true
    echo "Stopped and removed container: ${node_name}"
  else
    stop_binary_node_processes "$node_name"
    echo "Stopped binary process for node: ${node_name}"
  fi

  if ! wait_for_node_metrics_offline "$node_name" 30; then
    echo "Failed to observe ${node_name} metrics going offline after stop." >&2
    return 1
  fi
}

# --- Build / Setup ---

force_client_mode() {
  local cmd_file="$1"
  local mode="$2"
  if [[ -f "$cmd_file" ]]; then
    sed -i.bak -E "s/node_setup=\"[^\"]+\"/node_setup=\"${mode}\"/" "$cmd_file"
    rm -f "${cmd_file}.bak"
  fi
}

prepare_validator_config() {
  local source_file="$1"
  local dest_file="$2"
  local selected_nodes_csv="$3"

  cp "$source_file" "$dest_file"

  if [[ "$selected_nodes_csv" == "all" ]]; then return 0; fi

  local node_entries=()
  local selector=""
  local parsed_count=0
  IFS=',' read -r -a node_entries <<<"$selected_nodes_csv"

  for node_name in "${node_entries[@]}"; do
    node_name="${node_name#${node_name%%[![:space:]]*}}"
    node_name="${node_name%${node_name##*[![:space:]]}}"
    if [[ -z "$node_name" || "$node_name" == "all" ]]; then continue; fi
    if [[ -n "$selector" ]]; then selector="${selector} or "; fi
    selector="${selector}.name == \"${node_name}\""
    parsed_count=$((parsed_count + 1))
  done

  if [[ "$parsed_count" -eq 0 ]]; then return 0; fi
  yq eval -i ".validators = [.validators[] | select(${selector})]" "$dest_file"
}

"$root_dir/scripts/libp2p/build-patched-pubsub-package.sh"

prepare_validator_config \
  "$root_dir/config/validator-config.quickstart.yaml" \
  "$network_genesis_dir/validator-config.yaml" \
  "$nodes"
install -m 755 "$root_dir/client-cmds/nlean-cmd.sh" "$quickstart_dir/client-cmds/nlean-cmd.sh"
force_client_mode "$quickstart_dir/client-cmds/ream-cmd.sh" "docker"
force_client_mode "$quickstart_dir/client-cmds/zeam-cmd.sh" "docker"
force_client_mode "$quickstart_dir/client-cmds/ethlambda-cmd.sh" "docker"

if [[ "$nlean_setup" == "docker" ]]; then
  skip_docker_build=$(echo "$skip_docker_build" | tr '[:upper:]' '[:lower:]')
  if [[ "$skip_docker_build" == "true" ]]; then
    if docker image inspect "$nlean_docker_image" >/dev/null 2>&1; then
      echo "Skipping nlean docker build; using existing image $nlean_docker_image."
    else
      echo "NLEAN_SKIP_DOCKER_BUILD=true but image $nlean_docker_image is missing." >&2
      exit 1
    fi
  else
    git_sha=$(git -C "$root_dir" rev-parse --short=12 HEAD 2>/dev/null || echo unknown)
    docker build --build-arg GIT_SHA="$git_sha" -t "$nlean_docker_image" "$root_dir"
  fi
fi

# --- Cleanup ---

sudo_shim_dir=""
spin_pid=""
restart_spin_pid=""
spin_log="$network_root/data/spin-node.log"

cleanup_containers() {
  local container_names=("nlean_0" "nlean_1" "nlean_2" "ethlambda_0" "lean-prometheus" "lean-grafana")
  local existing_containers=""
  existing_containers=$(docker ps -a --format '{{.Names}}' || true)
  for name in "${container_names[@]}"; do
    if printf '%s\n' "$existing_containers" | grep -Fxq "$name"; then
      docker rm -f "$name" >/dev/null 2>&1 || true
    fi
  done
}

cleanup() {
  if [[ -n "$restart_spin_pid" ]] && kill -0 "$restart_spin_pid" >/dev/null 2>&1; then
    pkill -INT -P "$restart_spin_pid" >/dev/null 2>&1 || true
    kill -INT "$restart_spin_pid" >/dev/null 2>&1 || true
    for _ in {1..10}; do
      kill -0 "$restart_spin_pid" >/dev/null 2>&1 || break
      sleep 1
    done
    kill -KILL "$restart_spin_pid" >/dev/null 2>&1 || true
    wait "$restart_spin_pid" >/dev/null 2>&1 || true
  fi

  if [[ -n "$spin_pid" ]] && kill -0 "$spin_pid" >/dev/null 2>&1; then
    pkill -INT -P "$spin_pid" >/dev/null 2>&1 || true
    kill -INT "$spin_pid" >/dev/null 2>&1 || true
    for _ in {1..10}; do
      kill -0 "$spin_pid" >/dev/null 2>&1 || break
      sleep 1
    done
    kill -KILL "$spin_pid" >/dev/null 2>&1 || true
    wait "$spin_pid" >/dev/null 2>&1 || true
  fi
  cleanup_containers
  if [[ -n "$sudo_shim_dir" && -d "$sudo_shim_dir" ]]; then
    rm -rf "$sudo_shim_dir"
  fi
}
trap cleanup EXIT

if [[ "$use_sudo_shim" == "true" ]]; then
  sudo_shim_dir="$(mktemp -d "${TMPDIR:-/tmp}/nlean-sudo-shim.XXXXXX")"
  cat > "$sudo_shim_dir/sudo" <<'SHIM'
#!/usr/bin/env bash
set -euo pipefail
[[ $# -eq 0 ]] && exit 0
[[ "$1" == "--" ]] && shift
exec "$@"
SHIM
  chmod +x "$sudo_shim_dir/sudo"
fi

cleanup_containers

# Clean consensus data from previous runs so nodes start fresh with the new genesis.
IFS=',' read -ra clean_nodes <<< "$nodes"
for n in "${clean_nodes[@]}"; do
  rm -rf "$network_root/data/$n/consensus" 2>/dev/null || true
done

# --- Phase 1: Start 3 nodes, wait for 2 finalizations ---

echo "======================================================"
echo "PHASE 1: Starting 3 nodes: ${nodes}"
echo "======================================================"

# Run spin-node.sh for genesis generation only. Launch it in background,
# wait for genesis files + node configs to appear, then kill the coordinator.
# This avoids spin-node.sh's cleanup trap cascading and killing all containers
# when any single container exits.
(
  cd "$quickstart_dir"
  export NETWORK_DIR="$network_dir"
  export NLEAN_REPO="$root_dir"
  export NLEAN_QUICKSTART_SETUP="$nlean_setup"
  export NLEAN_DOCKER_IMAGE="$nlean_docker_image"
  export NLEAN_NETWORK_NAME="$network_name"
  export NLEAN_QUICKSTART_NODES="$nodes"

  cmd=(./spin-node.sh --node "$nodes" --generateGenesis --metrics)
  if [[ "$use_sudo_shim" == "true" ]]; then
    PATH="$sudo_shim_dir:$PATH" "${cmd[@]}"
  else
    "${cmd[@]}"
  fi
) >"$spin_log" 2>&1 &
spin_pid=$!
echo "spin-node log: $spin_log"

# Wait for all node configs to be generated, then kill the coordinator
# and start containers in detached mode ourselves.
phase1_config_deadline=$((SECONDS + 120))
all_configs_ready="false"
while (( SECONDS < phase1_config_deadline )); do
  all_ready="true"
  IFS=',' read -ra p1_node_list <<< "$nodes"
  for n in "${p1_node_list[@]}"; do
    if [[ ! -f "$network_root/data/$n/node-config.quickstart.json" ]]; then
      all_ready="false"
      break
    fi
  done
  if [[ "$all_ready" == "true" ]]; then
    all_configs_ready="true"
    break
  fi
  sleep 2
done

if [[ "$all_configs_ready" != "true" ]]; then
  echo "Phase 1 FAILED: node configs not generated in time." >&2
  exit 1
fi

# Kill the spin-node coordinator AND all its children so its cleanup trap
# doesn't race with our container startup below.
if [[ -n "$spin_pid" ]] && kill -0 "$spin_pid" >/dev/null 2>&1; then
  pkill -KILL -P "$spin_pid" >/dev/null 2>&1 || true
  kill -KILL "$spin_pid" >/dev/null 2>&1 || true
  wait "$spin_pid" >/dev/null 2>&1 || true
  spin_pid=""
fi

# Small delay to ensure any lingering child processes have fully exited.
sleep 2

# Remove any containers started by spin-node.sh
cleanup_containers

# Enable publishAggregates on ALL nodes since we bypass the coordinator's
# aggregator assignment.
IFS=',' read -ra p1_node_list <<< "$nodes"
for n in "${p1_node_list[@]}"; do
  local_cfg="$network_root/data/$n/node-config.quickstart.json"
  if [[ -f "$local_cfg" ]] && command -v python3 >/dev/null 2>&1; then
    python3 -c "
import json, sys
with open(sys.argv[1]) as f: cfg = json.load(f)
cfg.setdefault('validator', {})['publishAggregates'] = True
with open(sys.argv[1], 'w') as f: json.dump(cfg, f, indent=2)
" "$local_cfg"
  fi
done

# Launch all nodes in detached mode
if [[ "$nlean_setup" == "docker" ]]; then
  for n in "${p1_node_list[@]}"; do
    docker run -d --restart unless-stopped --pull=never \
      --name "$n" --network host \
      -v "$network_genesis_dir:/config" \
      -v "$network_root/data/$n:/data" \
      "$nlean_docker_image" \
      --config /data/node-config.quickstart.json \
      --validator-config /config/validator-config.yaml \
      --node "$n" \
      --data-dir /data \
      --metrics true \
      >>"$spin_log" 2>&1
    echo "Started detached container: ${n}"
  done
fi

# Phase 1: wait for first 2 finalizations (finalized >= 2 * slots_per_epoch)
phase1_target=$((finalize_count * slots_per_epoch))
nlean_phase1_nodes=("nlean_0" "nlean_1" "nlean_2")

if ! wait_for_finalization "Phase1-3nodes" "$phase1_target" "${nlean_phase1_nodes[@]}"; then
  echo "Phase 1 FAILED. Last logs:" >&2
  tail -n 40 "$spin_log" >&2 || true
  exit 1
fi

# Record the finalized baseline after phase 1
phase1_finalized=$(get_min_finalized "${nlean_phase1_nodes[@]}")
echo "Phase 1 complete. Finalized baseline: ${phase1_finalized}"

# --- Phase 2: Stop 1 node, wait for 2 more finalizations with 2 nodes ---

echo ""
echo "======================================================"
echo "PHASE 2: Stopping ${stopped_node}"
echo "======================================================"

# Containers are already running in detached mode from Phase 1.
# Just stop/remove the target node.
stop_node_for_mode "$stopped_node"

# Build list of remaining nlean nodes
remaining_nlean=()
for n in "${nlean_phase1_nodes[@]}"; do
  if [[ "$n" != "$stopped_node" ]]; then
    remaining_nlean+=("$n")
  fi
done
echo "Remaining nodes: ${remaining_nlean[*]}"

# Phase 2 target: current finalized + 2 more epoch-worth of finalization
phase2_target=$(awk "BEGIN { printf \"%d\", ${phase1_finalized} + ${finalize_count} * ${slots_per_epoch} }")

if ! wait_for_finalization "Phase2-2nodes" "$phase2_target" "${remaining_nlean[@]}"; then
  echo "Phase 2 FAILED. Last logs:" >&2
  tail -n 40 "$spin_log" >&2 || true
  exit 1
fi

phase2_finalized=$(get_min_finalized "${remaining_nlean[@]}")
echo "Phase 2 complete. Finalized: ${phase2_finalized}"

# --- Phase 3: Restart the stopped node, wait for 3 nodes to finalize 2 more times ---

echo ""
echo "======================================================"
echo "PHASE 3: Restarting ${stopped_node}"
echo "======================================================"

# Re-launch the stopped node directly via docker run.
# Bypass spin-node.sh to avoid: (1) re-randomizing the aggregator assignment,
# (2) restarting the metrics stack, (3) redundant docker-pull attempts.
# Phase 1 already generated the config at $network_root/data/$stopped_node/node-config.quickstart.json.
# Enable publishAggregates so the node can aggregate attestations regardless
# of the random aggregator assignment from Phase 1.
stopped_cfg="$network_root/data/$stopped_node/node-config.quickstart.json"
if [[ -f "$stopped_cfg" ]] && command -v python3 >/dev/null 2>&1; then
  python3 -c "
import json, sys
with open(sys.argv[1]) as f: cfg = json.load(f)
cfg.setdefault('validator', {})['publishAggregates'] = True
with open(sys.argv[1], 'w') as f: json.dump(cfg, f, indent=2)
" "$stopped_cfg"
fi

if [[ "$nlean_setup" == "docker" ]]; then
  docker run -d --restart unless-stopped --pull=never \
    --name "$stopped_node" --network host \
    -v "$network_genesis_dir:/config" \
    -v "$network_root/data/$stopped_node:/data" \
    "$nlean_docker_image" \
    --config /data/node-config.quickstart.json \
    --validator-config /config/validator-config.yaml \
    --node "$stopped_node" \
    --data-dir /data \
    --metrics true \
    >>"$spin_log" 2>&1
else
  (
    set +euo pipefail
    cd "$quickstart_dir"
    export NETWORK_DIR="$network_dir"
    export NLEAN_REPO="$root_dir"
    export NLEAN_QUICKSTART_SETUP="$nlean_setup"
    export NLEAN_DOCKER_IMAGE="$nlean_docker_image"
    export NLEAN_NETWORK_NAME="$network_name"

    cmd=(./spin-node.sh --node "$stopped_node" --metrics)
    if [[ "$use_sudo_shim" == "true" ]]; then
      PATH="$sudo_shim_dir:$PATH" "${cmd[@]}"
    else
      "${cmd[@]}"
    fi
  ) >>"$spin_log" 2>&1 &
  restart_spin_pid=$!
fi

echo "Restarted ${stopped_node}. Waiting for it to catch up..."
if ! wait_for_node_metrics_online "$stopped_node" 120; then
  echo "Phase 3 FAILED. ${stopped_node} did not come online after restart." >&2
  tail -n 40 "$spin_log" >&2 || true
  exit 1
fi

# Phase 3 target: current finalized + 2 more epoch-worth of finalization, check all 3 nlean nodes
# But we need the restarted node to also reach the target, proving it caught up.
phase3_target=$(awk "BEGIN { printf \"%d\", ${phase2_finalized} + ${finalize_count} * ${slots_per_epoch} }")

if ! wait_for_finalization "Phase3-3nodes-catchup" "$phase3_target" "${nlean_phase1_nodes[@]}"; then
  echo "Phase 3 FAILED. Last logs:" >&2
  tail -n 40 "$spin_log" >&2 || true
  exit 1
fi

phase3_finalized=$(get_min_finalized "${nlean_phase1_nodes[@]}")

echo ""
echo "======================================================"
echo "CATCHUP INTEROP TEST PASSED"
echo "======================================================"
echo "Phase 1 (3 nodes):   finalized to ${phase1_finalized}"
echo "Phase 2 (2 nodes):   finalized to ${phase2_finalized}"
echo "Phase 3 (3 nodes):   finalized to ${phase3_finalized} (${stopped_node} caught up)"
echo "======================================================"

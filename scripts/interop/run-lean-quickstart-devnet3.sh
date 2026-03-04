#!/usr/bin/env bash
set -euo pipefail

root_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
quickstart_dir="${NLEAN_QUICKSTART_DIR:-$root_dir/vendor/lean-quickstart}"
network_dir="local-devnet-nlean"
network_name="${NLEAN_NETWORK_NAME:-devnet0}"
nodes="${NLEAN_INTEROP_NODES:-nlean_0,nlean_1,qlean_0,qlean_1}"
with_metrics="true"
nlean_setup="${NLEAN_QUICKSTART_SETUP:-docker}"
nlean_docker_image="${NLEAN_DOCKER_IMAGE:-nlean-local:devnet3}"
use_sudo_shim="${NLEAN_QUICKSTART_USE_SUDO_SHIM:-true}"
skip_docker_build="${NLEAN_SKIP_DOCKER_BUILD:-false}"
skip_checks="${NLEAN_INTEROP_SKIP_CHECKS:-false}"
keep_running="${NLEAN_INTEROP_KEEP_RUNNING:-false}"
check_timeout_seconds="${NLEAN_INTEROP_CHECK_TIMEOUT_SECONDS:-600}"
check_poll_seconds="${NLEAN_INTEROP_CHECK_POLL_SECONDS:-5}"
min_finalized_slot="${NLEAN_INTEROP_MIN_FINALIZED_SLOT:-0}"
min_head_slot="${NLEAN_INTEROP_MIN_HEAD_SLOT:-3}"

usage() {
  cat <<USAGE
Usage:
  run-lean-quickstart-devnet3.sh [options]

Options:
  --quickstart-dir PATH   Path to lean-quickstart checkout (default: vendor/lean-quickstart)
  --network-dir NAME      Network directory under lean-quickstart (default: local-devnet-nlean)
  --network-name NAME     Gossip network name for nlean topics (default: devnet2)
  --nodes CSV             Node list for spin-node.sh (default: nlean_0,nlean_1,ethlambda_0)
  --nlean-setup MODE      nlean run mode: binary|docker (default: docker)
  --nlean-docker-image    Docker image tag used when nlean-setup=docker (default: nlean-local:devnet2)
  --no-sudo-shim          Do not inject sudo shim when calling spin-node.sh
  --no-metrics            Skip --metrics when running spin-node.sh
  --skip-checks           Do not run Prometheus interop checks after startup
  --keep-running          Keep quickstart nodes running after checks (do not auto-stop)
  --check-timeout SEC     Max seconds to wait for interop checks (default: 600)
  --check-poll SEC        Poll interval seconds for checks (default: 5)
  --min-finalized-slot N  Required finalized slot for nlean nodes (default: 0)
  --min-head-slot N       Required head slot for nlean nodes (default: 3)
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --quickstart-dir)
      quickstart_dir="$2"
      shift 2
      ;;
    --network-dir)
      network_dir="$2"
      shift 2
      ;;
    --network-name)
      network_name="$2"
      shift 2
      ;;
    --nodes)
      nodes="$2"
      shift 2
      ;;
    --nlean-setup)
      nlean_setup="$2"
      shift 2
      ;;
    --nlean-docker-image)
      nlean_docker_image="$2"
      shift 2
      ;;
    --no-sudo-shim)
      use_sudo_shim="false"
      shift
      ;;
    --no-metrics)
      with_metrics="false"
      shift
      ;;
    --skip-checks)
      skip_checks="true"
      shift
      ;;
    --keep-running)
      keep_running="true"
      shift
      ;;
    --check-timeout)
      check_timeout_seconds="$2"
      shift 2
      ;;
    --check-poll)
      check_poll_seconds="$2"
      shift 2
      ;;
    --min-finalized-slot)
      min_finalized_slot="$2"
      shift 2
      ;;
    --min-head-slot)
      min_head_slot="$2"
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

if [[ -z "${quickstart_dir// }" ]]; then
  quickstart_dir="$root_dir/vendor/lean-quickstart"
fi

if [[ "$nlean_setup" != "binary" && "$nlean_setup" != "docker" ]]; then
  echo "Invalid --nlean-setup: $nlean_setup (expected binary|docker)" >&2
  exit 1
fi

if [[ -z "${network_name// }" ]]; then
  echo "Invalid --network-name: must not be empty." >&2
  exit 1
fi

use_sudo_shim=$(echo "$use_sudo_shim" | tr '[:upper:]' '[:lower:]')
if [[ "$use_sudo_shim" != "true" && "$use_sudo_shim" != "false" ]]; then
  echo "Invalid sudo shim value: $use_sudo_shim (expected true|false)." >&2
  exit 1
fi

skip_checks=$(echo "$skip_checks" | tr '[:upper:]' '[:lower:]')
if [[ "$skip_checks" != "true" && "$skip_checks" != "false" ]]; then
  echo "Invalid skip checks value: $skip_checks (expected true|false)." >&2
  exit 1
fi

keep_running=$(echo "$keep_running" | tr '[:upper:]' '[:lower:]')
if [[ "$keep_running" != "true" && "$keep_running" != "false" ]]; then
  echo "Invalid keep running value: $keep_running (expected true|false)." >&2
  exit 1
fi

if ! [[ "$check_timeout_seconds" =~ ^[0-9]+$ ]] || [[ "$check_timeout_seconds" == "0" ]]; then
  echo "Invalid --check-timeout: $check_timeout_seconds (expected positive integer)." >&2
  exit 1
fi

if ! [[ "$check_poll_seconds" =~ ^[0-9]+$ ]] || [[ "$check_poll_seconds" == "0" ]]; then
  echo "Invalid --check-poll: $check_poll_seconds (expected positive integer)." >&2
  exit 1
fi

if ! [[ "$min_finalized_slot" =~ ^[0-9]+$ ]]; then
  echo "Invalid --min-finalized-slot: $min_finalized_slot (expected non-negative integer)." >&2
  exit 1
fi

if ! [[ "$min_head_slot" =~ ^[0-9]+$ ]]; then
  echo "Invalid --min-head-slot: $min_head_slot (expected non-negative integer)." >&2
  exit 1
fi

if [[ ! -d "$quickstart_dir" || ! -f "$quickstart_dir/spin-node.sh" ]]; then
  echo "Invalid lean-quickstart directory: $quickstart_dir" >&2
  if [[ "$quickstart_dir" == "$root_dir/vendor/lean-quickstart" ]]; then
    echo "Initialize submodule first: git submodule update --init --recursive vendor/lean-quickstart" >&2
  fi
  exit 1
fi

if ! command -v yq >/dev/null 2>&1; then
  echo "yq is required by lean-quickstart. Install it first." >&2
  exit 1
fi

force_client_mode() {
  local cmd_file="$1"
  local mode="$2"

  if [[ ! -f "$cmd_file" ]]; then
    return
  fi

  # quickstart client commands set node_setup="binary|docker"; replace it in-place.
  sed -i.bak -E "s/node_setup=\"[^\"]+\"/node_setup=\"${mode}\"/" "$cmd_file"
  rm -f "${cmd_file}.bak"
}

contains_non_nlean_node() {
  local nodes_csv="$1"
  local node_entries=()
  local node=""

  IFS=',' read -r -a node_entries <<<"$nodes_csv"
  for node in "${node_entries[@]}"; do
    node="${node#${node%%[![:space:]]*}}"
    node="${node%${node##*[![:space:]]}}"
    if [[ -z "$node" || "$node" == "all" ]]; then
      continue
    fi

    if [[ "$node" != nlean_* ]]; then
      return 0
    fi
  done

  return 1
}

network_root="$quickstart_dir/$network_dir"
network_genesis_dir="$network_root/genesis"
mkdir -p "$network_genesis_dir" "$network_root/data"

prepare_validator_config() {
  local source_file="$1"
  local dest_file="$2"
  local selected_nodes_csv="$3"

  cp "$source_file" "$dest_file"

  if [[ "$selected_nodes_csv" == "all" ]]; then
    return 0
  fi

  local node_entries=()
  local node_name=""
  local selector=""
  local parsed_count=0
  IFS=',' read -r -a node_entries <<<"$selected_nodes_csv"

  for node_name in "${node_entries[@]}"; do
    node_name="${node_name#${node_name%%[![:space:]]*}}"
    node_name="${node_name%${node_name##*[![:space:]]}}"
    if [[ -z "$node_name" || "$node_name" == "all" ]]; then
      continue
    fi

    if [[ -n "$selector" ]]; then
      selector="${selector} or "
    fi
    selector="${selector}.name == \"${node_name}\""
    parsed_count=$((parsed_count + 1))
  done

  if [[ "$parsed_count" -eq 0 ]]; then
    return 0
  fi

  yq eval -i ".validators = [.validators[] | select(${selector})]" "$dest_file"

  local selected_count=""
  selected_count=$(yq eval '.validators | length' "$dest_file")
  if [[ -z "$selected_count" || "$selected_count" == "0" ]]; then
    echo "Filtered validator set is empty for --nodes ${selected_nodes_csv}." >&2
    exit 1
  fi
}

"$root_dir/scripts/libp2p/build-patched-pubsub-package.sh"

prepare_validator_config \
  "$root_dir/config/validator-config.quickstart.yaml" \
  "$network_genesis_dir/validator-config.yaml" \
  "$nodes"
install -m 755 "$root_dir/client-cmds/nlean-cmd.sh" "$quickstart_dir/client-cmds/nlean-cmd.sh"
force_client_mode "$quickstart_dir/client-cmds/ream-cmd.sh" "docker"
force_client_mode "$quickstart_dir/client-cmds/zeam-cmd.sh" "docker"

if [[ "$(uname -s)" == "Darwin" && "$nlean_setup" == "binary" ]] && contains_non_nlean_node "$nodes"; then
  echo "macOS interop note: nlean binary + docker peers can cause QUIC handshake timeouts; switching nlean setup to docker."
  nlean_setup="docker"
fi

if [[ "$nlean_setup" == "binary" ]]; then
  dotnet publish "$root_dir/src/Lean.Client/Lean.Client.csproj" -c Release --self-contained false -o "$root_dir/artifacts/lean-client"
  "$root_dir/scripts/build-native.sh"
  if [[ "$(uname -s)" == "Darwin" ]]; then
    for libmsquic_path in \
      /opt/homebrew/lib/libmsquic.2.dylib \
      /opt/homebrew/lib/libmsquic.dylib \
      /opt/homebrew/opt/libmsquic/lib/libmsquic.2.dylib \
      /usr/local/lib/libmsquic.2.dylib \
      /usr/local/lib/libmsquic.dylib \
      /usr/local/opt/libmsquic/lib/libmsquic.2.dylib; do
      if [[ -f "$libmsquic_path" ]]; then
        cp -fL "$libmsquic_path" "$root_dir/artifacts/lean-client/"
        break
      fi
    done
  fi
else
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
    docker build \
      --build-arg GIT_SHA="$git_sha" \
      -t "$nlean_docker_image" \
      "$root_dir"
  fi
fi

sudo_shim_dir=""
spin_pid=""
spin_log="$network_root/data/spin-node.log"
nlean_job_regex=""
nlean_node_list=""
nlean_nodes=()
selected_nodes=()

run_spin_node() {
  local cmd=("./spin-node.sh" --node "$nodes")
  cmd+=(--generateGenesis)

  if [[ -n "${NLEAN_AGGREGATOR_NODE:-}" ]]; then
    cmd+=(--aggregator "$NLEAN_AGGREGATOR_NODE")
  fi

  if [[ "$with_metrics" == "true" ]]; then
    cmd+=(--metrics)
  fi

  (
    cd "$quickstart_dir"
    export NETWORK_DIR="$network_dir"
    export NLEAN_REPO="$root_dir"
    export NLEAN_QUICKSTART_SETUP="$nlean_setup"
    export NLEAN_DOCKER_IMAGE="$nlean_docker_image"
    export NLEAN_NETWORK_NAME="$network_name"
    export NLEAN_QUICKSTART_NODES="$nodes"

    if [[ "$use_sudo_shim" == "true" ]]; then
      PATH="$sudo_shim_dir:$PATH" "${cmd[@]}"
    else
      "${cmd[@]}"
    fi
  )
}

parse_selected_nodes() {
  local input_nodes="$1"
  local parsed_nodes=()
  local node_entries=()
  local node_name=""

  if [[ "$input_nodes" == "all" ]]; then
    while IFS= read -r node_name; do
      node_name="${node_name#${node_name%%[![:space:]]*}}"
      node_name="${node_name%${node_name##*[![:space:]]}}"
      if [[ -n "$node_name" ]]; then
        parsed_nodes+=("$node_name")
      fi
    done < <(yq eval '.validators[].name' "$network_genesis_dir/validator-config.yaml")
  else
    IFS=',' read -r -a node_entries <<<"$input_nodes"
    for node_name in "${node_entries[@]}"; do
      node_name="${node_name#${node_name%%[![:space:]]*}}"
      node_name="${node_name%${node_name##*[![:space:]]}}"
      if [[ -n "$node_name" && "$node_name" != "all" ]]; then
        parsed_nodes+=("$node_name")
      fi
    done
  fi

  if [[ ${#parsed_nodes[@]} -eq 0 ]]; then
    return 1
  fi

  selected_nodes=("${parsed_nodes[@]}")
  return 0
}

parse_nlean_nodes() {
  local input_nodes="$1"
  local parsed_nodes=()
  local node_name=""

  if ! parse_selected_nodes "$input_nodes"; then
    return 1
  fi

  for node_name in "${selected_nodes[@]}"; do
    if [[ "$node_name" == nlean_* ]]; then
      parsed_nodes+=("$node_name")
    fi
  done

  if [[ ${#parsed_nodes[@]} -eq 0 ]]; then
    return 1
  fi

  nlean_nodes=("${parsed_nodes[@]}")
  nlean_node_list=$(IFS=,; echo "${parsed_nodes[*]}")
  nlean_job_regex=$(IFS='|'; echo "${parsed_nodes[*]}")
  return 0
}

cleanup_preexisting_containers() {
  local container_name=""
  local existing_containers=""
  local container_names=()
  local removed_any="false"

  if ! parse_selected_nodes "$nodes"; then
    return
  fi

  container_names=("${selected_nodes[@]}")
  if [[ "$with_metrics" == "true" ]]; then
    container_names+=("lean-prometheus" "lean-grafana")
  fi

  existing_containers=$(docker ps -a --format '{{.Names}}' || true)

  for container_name in "${container_names[@]}"; do
    if printf '%s\n' "$existing_containers" | grep -Fxq "$container_name"; then
      echo "Removing stale container: ${container_name}"
      docker rm -f "$container_name" >/dev/null 2>&1 || true
      removed_any="true"
    fi
  done

  if [[ "$removed_any" == "true" ]]; then
    sleep 1
  fi
}

float_ge() {
  local left="$1"
  local right="$2"
  awk -v left="$left" -v right="$right" 'BEGIN { exit !(left + 0 >= right + 0) }'
}

float_lt() {
  local left="$1"
  local right="$2"
  awk -v left="$left" -v right="$right" 'BEGIN { exit !(left + 0 < right + 0) }'
}

resolve_metrics_port() {
  local node_name="$1"
  yq eval ".validators[] | select(.name == \"${node_name}\") | .metricsPort" "$network_genesis_dir/validator-config.yaml" 2>/dev/null | head -n 1
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
    payload=$(docker exec "$node_name" sh -lc "wget -qO- http://127.0.0.1:${metrics_port}/metrics 2>/dev/null || curl -fsS http://127.0.0.1:${metrics_port}/metrics 2>/dev/null" 2>/dev/null || true)
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
  echo "$payload" | awk -v metric="$metric_name" '$1 ~ ("^" metric "({|$)") { print $2; exit }'
}

run_interop_checks() {
  if [[ "$skip_checks" == "true" ]]; then
    echo "Skipping interop checks as requested."
    return 0
  fi

  if [[ "$with_metrics" != "true" ]]; then
    echo "Interop checks require --metrics. Re-run without --no-metrics, or pass --skip-checks." >&2
    return 1
  fi

  if ! parse_nlean_nodes "$nodes"; then
    echo "No nlean nodes selected in --nodes (${nodes}); interop checks skipped."
    return 0
  fi

  echo "Running interop checks against nlean nodes: ${nlean_node_list}"

  local deadline=$((SECONDS + check_timeout_seconds))
  while (( SECONDS < deadline )); do
    local collected_nodes=0
    local finalized_min=""
    local finalized_max=""
    local head_min=""
    local scrape_failures=0

    for node_name in "${nlean_nodes[@]}"; do
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

      collected_nodes=$((collected_nodes + 1))

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

    if [[ "$collected_nodes" -gt 0 ]]; then
      echo "interop-check: collected=${collected_nodes}/${#nlean_nodes[@]}, scrape_failures=${scrape_failures}, finalized_min=${finalized_min}, finalized_max=${finalized_max}, head_min=${head_min}"

      if [[ "$collected_nodes" -eq "${#nlean_nodes[@]}" ]] &&
         float_ge "$finalized_min" "$min_finalized_slot" &&
         float_ge "$head_min" "$min_head_slot" &&
         ! float_lt "$finalized_min" "$finalized_max"; then
        echo "Interop checks passed: finalized/head targets reached for nlean nodes."
        return 0
      fi
    fi

    sleep "$check_poll_seconds"
  done

  echo "Interop checks timed out after ${check_timeout_seconds}s. Required finalized>=${min_finalized_slot}, head>=${min_head_slot}." >&2
  return 1
}

cleanup() {
  if [[ -n "$spin_pid" ]]; then
    if kill -0 "$spin_pid" >/dev/null 2>&1; then
      echo "Stopping quickstart nodes for: ${nodes}"
      pkill -INT -P "$spin_pid" >/dev/null 2>&1 || true
      kill -INT "$spin_pid" >/dev/null 2>&1 || kill "$spin_pid" >/dev/null 2>&1 || true

      for _ in {1..10}; do
        if ! kill -0 "$spin_pid" >/dev/null 2>&1; then
          break
        fi
        sleep 1
      done

      if kill -0 "$spin_pid" >/dev/null 2>&1; then
        pkill -TERM -P "$spin_pid" >/dev/null 2>&1 || true
        kill -TERM "$spin_pid" >/dev/null 2>&1 || true
      fi

      for _ in {1..5}; do
        if ! kill -0 "$spin_pid" >/dev/null 2>&1; then
          break
        fi
        sleep 1
      done

      if kill -0 "$spin_pid" >/dev/null 2>&1; then
        pkill -KILL -P "$spin_pid" >/dev/null 2>&1 || true
        kill -KILL "$spin_pid" >/dev/null 2>&1 || true
      fi
    fi
    wait "$spin_pid" >/dev/null 2>&1 || true
  fi

  if [[ "$keep_running" != "true" ]]; then
    cleanup_preexisting_containers
  fi

  if [[ -n "$sudo_shim_dir" && -d "$sudo_shim_dir" ]]; then
    rm -rf "$sudo_shim_dir"
  fi
}
trap cleanup EXIT

if [[ "$use_sudo_shim" == "true" ]]; then
  sudo_shim_dir="$(mktemp -d "${TMPDIR:-/tmp}/nlean-sudo-shim.XXXXXX")"
  cat > "$sudo_shim_dir/sudo" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

if [[ $# -eq 0 ]]; then
  exit 0
fi

# lean-quickstart currently invokes sudo without complex option handling.
if [[ "$1" == "--" ]]; then
  shift
fi

exec "$@"
EOF
  chmod +x "$sudo_shim_dir/sudo"
fi

cleanup_preexisting_containers

echo "Starting lean-quickstart devnet-3 nodes: ${nodes}"
run_spin_node >"$spin_log" 2>&1 &
spin_pid=$!
echo "spin-node log: $spin_log"

if ! run_interop_checks; then
  echo "Interop validation failed. Last spin-node logs:" >&2
  tail -n 80 "$spin_log" >&2 || true
  exit 1
fi

if [[ "$keep_running" == "true" ]]; then
  echo "Checks passed; keeping nodes running. Ctrl+C to stop this wrapper."
  wait "$spin_pid"
fi

#!/usr/bin/env bash
set -euo pipefail

root_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
quickstart_dir=""
network_dir="local-devnet-nlean"
nodes="nlean_0,zeam_0,ream_0"
with_metrics="true"

usage() {
  cat <<USAGE
Usage:
  run-lean-quickstart-devnet2.sh --quickstart-dir PATH [options]

Options:
  --quickstart-dir PATH   Path to lean-quickstart checkout (required)
  --network-dir NAME      Network directory under lean-quickstart (default: local-devnet-nlean)
  --nodes CSV             Node list for spin-node.sh (default: nlean_0,zeam_0,ream_0)
  --no-metrics            Skip --metrics when running spin-node.sh
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
    --nodes)
      nodes="$2"
      shift 2
      ;;
    --no-metrics)
      with_metrics="false"
      shift
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

if [[ -z "$quickstart_dir" ]]; then
  usage >&2
  exit 1
fi

if [[ ! -d "$quickstart_dir" || ! -f "$quickstart_dir/spin-node.sh" ]]; then
  echo "Invalid lean-quickstart directory: $quickstart_dir" >&2
  exit 1
fi

if ! command -v yq >/dev/null 2>&1; then
  echo "yq is required by lean-quickstart. Install it first." >&2
  exit 1
fi

network_root="$quickstart_dir/$network_dir"
network_genesis_dir="$network_root/genesis"
mkdir -p "$network_genesis_dir" "$network_root/data"

cp "$root_dir/config/validator-config.quickstart.yaml" "$network_genesis_dir/validator-config.yaml"
install -m 755 "$root_dir/client-cmds/nlean-cmd.sh" "$quickstart_dir/client-cmds/nlean-cmd.sh"

dotnet publish "$root_dir/src/Lean.Client/Lean.Client.csproj" -c Release --self-contained false -o "$root_dir/artifacts/lean-client"
"$root_dir/scripts/build-native.sh"

export NETWORK_DIR="$network_dir"
export NLEAN_REPO="$root_dir"

cmd=("$quickstart_dir/spin-node.sh" --node "$nodes" --generateGenesis)
if [[ "$with_metrics" == "true" ]]; then
  cmd+=(--metrics)
fi

"${cmd[@]}"

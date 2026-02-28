#!/usr/bin/env bash
set -euo pipefail

# Profile nlean memory usage in a running Docker container
# Usage: ./scripts/profile-nlean-memory.sh [container_name] [duration_seconds]
#
# This script:
# 1. Captures initial GC stats via dotnet-counters
# 2. Takes a GC heap dump (baseline)
# 3. Waits for the specified duration
# 4. Takes another GC heap dump (after)
# 5. Copies both dumps to host for analysis

CONTAINER="${1:-nlean_0}"
DURATION="${2:-120}"
DUMP_DIR="${3:-/Users/grapebaba/Documents/projects/nlean/tmp/profile}"

mkdir -p "$DUMP_DIR"

echo "=== nlean Memory Profiling ==="
echo "Container: $CONTAINER"
echo "Duration: ${DURATION}s"
echo "Output: $DUMP_DIR"
echo ""

# Find the Lean.Client process PID inside the container
PID=$(docker exec "$CONTAINER" bash -c "ps aux | grep 'Lean.Client' | grep -v grep | awk '{print \$2}' | head -1")
if [[ -z "$PID" ]]; then
  echo "ERROR: Could not find Lean.Client process in $CONTAINER" >&2
  exit 1
fi
echo "Target PID: $PID"
echo ""

# Step 1: Show current GC counters snapshot
echo "=== Step 1: GC Counters Snapshot ==="
docker exec "$CONTAINER" dotnet-counters collect \
  -p "$PID" \
  --providers "System.Runtime[gc-heap-size,gc-fragmentation,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,gen-0-size,gen-1-size,gen-2-size,loh-size,poh-size,alloc-rate,assembly-count,threadpool-thread-count,working-set]" \
  --duration 5 \
  --format csv \
  -o /tmp/counters-baseline.csv 2>&1 || true
docker cp "$CONTAINER:/tmp/counters-baseline.csv" "$DUMP_DIR/counters-baseline.csv" 2>/dev/null || true
echo "Baseline counters saved."
echo ""

# Step 2: Take baseline GC dump
echo "=== Step 2: Baseline GC Heap Dump ==="
docker exec "$CONTAINER" dotnet-gcdump collect -p "$PID" -o /tmp/baseline.gcdump 2>&1
docker cp "$CONTAINER:/tmp/baseline.gcdump" "$DUMP_DIR/baseline.gcdump"
echo "Baseline dump saved to $DUMP_DIR/baseline.gcdump"
echo ""

# Step 3: Monitor memory growth
echo "=== Step 3: Monitoring for ${DURATION}s ==="
echo "Capturing counters during monitoring period..."
docker exec -d "$CONTAINER" dotnet-counters collect \
  -p "$PID" \
  --providers "System.Runtime[gc-heap-size,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,gen-0-size,gen-1-size,gen-2-size,loh-size,poh-size,alloc-rate,working-set]" \
  --duration "$DURATION" \
  --format csv \
  -o /tmp/counters-monitoring.csv

# Also capture docker stats periodically
echo "timestamp,container,mem_usage,mem_limit,mem_pct" > "$DUMP_DIR/docker-stats.csv"
END=$((SECONDS + DURATION))
INTERVAL=10
while (( SECONDS < END )); do
  STATS=$(docker stats "$CONTAINER" --no-stream --format "{{.MemUsage}},{{.MemPerc}}" 2>/dev/null || echo "N/A,N/A")
  echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),$CONTAINER,$STATS" >> "$DUMP_DIR/docker-stats.csv"

  REMAINING=$((END - SECONDS))
  if (( REMAINING > 0 )); then
    echo "  [$(date -u +%H:%M:%S)] Memory: $STATS (${REMAINING}s remaining)"
  fi
  sleep "$INTERVAL"
done
echo ""

# Step 4: Take after GC dump
echo "=== Step 4: After GC Heap Dump ==="
docker exec "$CONTAINER" dotnet-gcdump collect -p "$PID" -o /tmp/after.gcdump 2>&1
docker cp "$CONTAINER:/tmp/after.gcdump" "$DUMP_DIR/after.gcdump"
echo "After dump saved to $DUMP_DIR/after.gcdump"
echo ""

# Step 5: Copy monitoring counters
docker cp "$CONTAINER:/tmp/counters-monitoring.csv" "$DUMP_DIR/counters-monitoring.csv" 2>/dev/null || true

# Step 6: Report baseline GC dump summary
echo "=== Step 5: GC Dump Analysis ==="
echo ""
echo "--- Baseline Top Types by Size ---"
docker exec "$CONTAINER" dotnet-gcdump report /tmp/baseline.gcdump 2>&1 | head -40
echo ""
echo "--- After Top Types by Size ---"
docker exec "$CONTAINER" dotnet-gcdump report /tmp/after.gcdump 2>&1 | head -40

echo ""
echo "=== Docker Stats Over Time ==="
cat "$DUMP_DIR/docker-stats.csv"

echo ""
echo "=== Profiling Complete ==="
echo "Files in $DUMP_DIR:"
ls -lh "$DUMP_DIR/"
echo ""
echo "To analyze dumps interactively:"
echo "  dotnet-gcdump report $DUMP_DIR/baseline.gcdump"
echo "  dotnet-gcdump report $DUMP_DIR/after.gcdump"

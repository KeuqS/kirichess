#!/usr/bin/env bash
# Re-runs a single rung WITHOUT tearing the stack down, and captures the evidence that says whether a
# failure was the server or the harness. Used to attribute the Budget A N=400 ramp failure.
#
#   LOAD_CPUS=1.0 LOAD_MEMORY=512m N=400 LABEL=A-diag ./diagnose-rung.sh
#
# The question it answers: `restart: unless-stopped` in docker-compose.prod.yml means an OOM-killed
# backend comes straight back, so "connection forcibly closed" looks identical whether the container
# died or the client could not reach it. RestartCount / OOMKilled is what separates the two.

set -uo pipefail

: "${LOAD_CPUS:?}"; : "${LOAD_MEMORY:?}"; : "${N:?}"; : "${LABEL:?}"
export LOAD_CPUS LOAD_MEMORY

COMPOSE=(docker compose -p kirichess-load -f docker-compose.prod.yml -f docker-compose.load.yml)

"${COMPOSE[@]}" up -d db redis backend
echo "=== limits applied (NanoCpus Memory) ==="
docker inspect kirichess-backend --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}}'
"${COMPOSE[@]}" --profile migrate run --rm migrate

# Sample the backend's liveness every 2s for the whole run, so a restart is timestamped rather than
# inferred after the fact.
( while true; do
    echo "$(date +%H:%M:%S) $(docker inspect kirichess-backend \
        --format '{{.State.Status}} oom={{.State.OOMKilled}} exit={{.State.ExitCode}} restarts={{.RestartCount}}' 2>/dev/null)"
    sleep 2
  done ) > "loadtest/results/${LABEL}-n${N}-liveness.log" 2>&1 &
WATCH=$!

dotnet run -c Release --project loadtest/KiriChess.LoadTest -- \
    --games "$N" --pool steal3 --api http://localhost:8080 \
    --scrape-token load-only-not-a-secret-scrape-token \
    --think-ms 3000 --jitter-ms 1000 --warmup-seconds 90 --window-seconds 180 \
    --presence "${PRESENCE:-true}" --seed 20260808 --pinned-cpus "$LOAD_CPUS" --load-memory "$LOAD_MEMORY" \
    --ramp-batch-size "${RAMP_BATCH:-50}" \
    --label "${LABEL}-n${N}" --out "loadtest/results/${LABEL}-n${N}.json"
harness_exit=$?
kill $WATCH 2>/dev/null

echo "=== harness exit: $harness_exit ==="
echo "=== final container state ==="
docker inspect kirichess-backend \
    --format 'status={{.State.Status}} oomKilled={{.State.OOMKilled}} exitCode={{.State.ExitCode}} restartCount={{.RestartCount}} startedAt={{.State.StartedAt}}'
echo "=== liveness transitions (deduped) ==="
sort -u -k2 "loadtest/results/${LABEL}-n${N}-liveness.log" | head -20
echo "=== backend log tail ==="
"${COMPOSE[@]}" logs backend --no-color --tail 40
echo "=== stack left UP for inspection ==="

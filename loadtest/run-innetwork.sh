#!/usr/bin/env bash
# Runs one rung with the harness INSIDE the compose network, sampling container stats from the HOST.
#
#   LOAD_CPUS=1.0 LOAD_MEMORY=512m N=800 LABEL=A-innet ./run-innetwork.sh
#
# Why in-network: Docker Desktop's userspace port proxy resets connections somewhere around 850-950
# concurrent sockets on this host, which is a property of the host and not of the server (proven by
# N=400 presence-on failing through the published port and passing identically in-network). Anything
# above ~400 games x 4 sockets has to bypass the proxy or it measures Docker.
#
# Why the host-side sampler: the harness shells out to `docker stats`, and there is no Docker CLI
# inside the SDK container, so an in-network run reports 0% CPU - which is missing data, not a
# measurement. This samples from the host in parallel and writes a sidecar file the report merges in.

set -uo pipefail

: "${LOAD_CPUS:?}"; : "${LOAD_MEMORY:?}"; : "${N:?}"; : "${LABEL:?}"
export LOAD_CPUS LOAD_MEMORY
PRESENCE="${PRESENCE:-true}"
LAG="${LOAD_LAG_COMPENSATION:-true}"
export LOAD_LAG_COMPENSATION="$LAG"

COMPOSE=(docker compose -p kirichess-load -f docker-compose.prod.yml -f docker-compose.load.yml)
RUNG="${LABEL}-n${N}"

"${COMPOSE[@]}" down -v >/dev/null 2>&1
"${COMPOSE[@]}" up -d db redis backend
echo "=== limits applied (NanoCpus Memory) ==="
docker inspect kirichess-backend --format '{{.HostConfig.NanoCpus}} {{.HostConfig.Memory}}'
"${COMPOSE[@]}" --profile migrate run --rm migrate >/dev/null

STATS="loadtest/results/${RUNG}-hoststats.log"
( while true; do
    docker stats --no-stream --format '{{.Name}};{{.CPUPerc}};{{.MemUsage}}' kirichess-backend kirichess-db 2>/dev/null
    sleep 5
  done ) > "$STATS" 2>&1 &
SAMPLER=$!

W="$(pwd -W)"
MSYS_NO_PATHCONV=1 docker run --rm --network kirichess-load_default -v "${W}:/src" -w //src \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet run -c Release --project loadtest/KiriChess.LoadTest --artifacts-path /tmp/art -- \
    --games "$N" --pool steal3 --api http://backend:8080 \
    --scrape-token load-only-not-a-secret-scrape-token \
    --think-ms 3000 --jitter-ms 1000 --warmup-seconds 90 --window-seconds 180 \
    --presence "$PRESENCE" --seed 20260808 --pinned-cpus "$LOAD_CPUS" --load-memory "$LOAD_MEMORY" \
    --label "$RUNG" --out "loadtest/results/${RUNG}.json"
harness_exit=$?
kill $SAMPLER 2>/dev/null

echo "=== harness exit: $harness_exit ==="
echo "=== container state ==="
docker inspect kirichess-backend \
    --format 'status={{.State.Status}} oomKilled={{.State.OOMKilled}} exitCode={{.State.ExitCode}} restartCount={{.RestartCount}}'
echo "=== host-sampled backend CPU/mem (from $STATS) ==="
awk -F';' '/kirichess-backend/ {gsub(/%/,"",$2); cpu[NR]=$2+0; if($2+0>max)max=$2+0; sum+=$2+0; n++; mem=$3}
           END {printf "samples=%d cpuMean=%.1f%% cpuMax=%.1f%% lastMem=%s\n", n, (n?sum/n:0), max, mem}' "$STATS"
"${COMPOSE[@]}" down -v >/dev/null 2>&1
echo "=== done: $RUNG ==="

#!/usr/bin/env bash
set -euo pipefail
export AI_PROVIDER=fixture EMBEDDING_PROVIDER=fixture GRAPH_STORE=memory
export ASPNETCORE_URLS=http://127.0.0.1:18081
export API_BASE=http://127.0.0.1:18081 REQUIRE_NATIVE_AOT=true
mkdir -p artifacts/logs
artifacts/native/linux-x64/GraphRag.Api >artifacts/logs/native-api.log 2>&1 &
api_pid=$!
trap 'kill "$api_pid" 2>/dev/null || true; wait "$api_pid" 2>/dev/null || true' EXIT
for attempt in $(seq 1 150); do
    if curl --fail --silent "$API_BASE/health/ready" >/dev/null; then break; fi
    if ! kill -0 "$api_pid" 2>/dev/null; then cat artifacts/logs/native-api.log; exit 1; fi
    sleep 0.05
done
node scripts/smoke-api.mjs

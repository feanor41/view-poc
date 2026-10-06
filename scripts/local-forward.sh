#!/usr/bin/env bash
# Foreground supervisor for four local-only API forwards. Ctrl-C stops all forwards.
set -euo pipefail
context="kind-${VWP_CLUSTER_NAME:-vwp}"
pids=()
cleanup() { for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done; }
trap cleanup EXIT
trap 'exit 130' INT TERM
for pair in accounts:5101 financials:5102 cases:5103 notifications:5104; do
  service=${pair%:*}
  port=${pair#*:}
  kubectl --context "$context" --namespace vwp port-forward --address 127.0.0.1 "service/$service" "$port:8080" &
  pids+=("$!")
done
wait -n "${pids[@]}"

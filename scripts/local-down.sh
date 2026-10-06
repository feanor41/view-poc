#!/usr/bin/env bash
# Explicit teardown of the dedicated disposable VWP cluster and all its data/secrets.
set -euo pipefail
cluster=${VWP_CLUSTER_NAME:-vwp}
kind delete cluster --name "$cluster"

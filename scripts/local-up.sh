#!/usr/bin/env bash
# Provision only the dedicated local VWP kind cluster; never uses the current context.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cluster=${VWP_CLUSTER_NAME:-vwp}
namespace=vwp
context="kind-$cluster"
for tool in kind kubectl docker openssl; do
  command -v "$tool" >/dev/null || { echo "Missing prerequisite: $tool" >&2; exit 1; }
done
if [[ $(uname -m) != x86_64 ]]; then
  echo 'The SQL Server Linux image requires an x86_64 host. Use an x86_64 Docker environment.' >&2
  exit 1
fi
if ! kind get clusters | grep -Fxq "$cluster"; then
  kind create cluster --name "$cluster" --image kindest/node:v1.37.0@sha256:a1ed56cfb0e7b93589bdf97c8cd566405a265939e3620fc4f5de89adff580ae5 --config "$root/deploy/local/kind.yaml"
fi
kube() { kubectl --context "$context" --namespace "$namespace" "$@"; }
kubectl --context "$context" create namespace "$namespace" --dry-run=client -o yaml | kubectl --context "$context" apply -f -
# No password is printed or passed on the host command line. Files exist only in a private temp directory.
secret_dir=$(mktemp -d)
chmod 700 "$secret_dir"
trap 'rm -rf "$secret_dir"' EXIT
if ! kube get secret sql-admin >/dev/null 2>&1; then
  # Refuse partial installation; do not rotate existing service credentials implicitly.
  for service in accounts financials cases notifications; do
    if kube get secret "$service-database" >/dev/null 2>&1; then
      echo 'Partial secret installation detected; restore the missing secret or recreate the dedicated disposable cluster.' >&2
      exit 1
    fi
  done
  printf '%s' "Vwp1!$(openssl rand -hex 32)" > "$secret_dir/admin"
  kube create secret generic sql-admin --from-file=password="$secret_dir/admin"
  : > "$secret_dir/bootstrap.env"
  for service in Accounts Financials Cases Notifications; do
    slug=${service,,}
    password="Vwp1!$(openssl rand -hex 32)"
    printf '%sPassword=%s\n' "$service" "$password" >> "$secret_dir/bootstrap.env"
    printf 'Server=sqlserver,1433;Database=%s;User ID=vwp_%s;Password=%s;Encrypt=True;TrustServerCertificate=True;Connect Timeout=30' "$service" "$slug" "$password" > "$secret_dir/connection"
    kube create secret generic "$slug-database" --from-file=connectionString="$secret_dir/connection"
  done
  unset password
  kube create secret generic sql-bootstrap-passwords --from-env-file="$secret_dir/bootstrap.env"
else
  for secret in accounts-database financials-database cases-database notifications-database sql-bootstrap-passwords; do
    kube get secret "$secret" >/dev/null || { echo "Missing secret: $secret; restore it before continuing." >&2; exit 1; }
  done
fi
kube create configmap sql-bootstrap --from-file=init.sql="$root/database/init.sql" --dry-run=client -o yaml | kube apply -f -
kube apply -f "$root/deploy/local/sql-server.yaml"
kube rollout status deployment/sqlserver --timeout=600s
# This Job only creates absent tables/logins and refreshes ordinary views; never clears data.
kube delete job sql-bootstrap --ignore-not-found --wait=true
kube apply -f "$root/deploy/local/bootstrap.yaml"
if ! kube wait --for=condition=complete job/sql-bootstrap --timeout=600s; then
  echo "Bootstrap failed. Inspect: kubectl --context $context -n $namespace logs job/sql-bootstrap" >&2
  exit 1
fi
for service in Accounts Financials Cases Notifications; do
  slug=${service,,}
  docker build --file "$root/deploy/local/Dockerfile" --build-arg SERVICE="$service" --tag "vwp/$slug:local-v1" "$root"
  kind load docker-image "vwp/$slug:local-v1" --name "$cluster"
  kube apply -f "$root/deploy/local/$slug.yaml"
  kube rollout restart "deployment/$slug"
  kube rollout status "deployment/$slug" --timeout=300s
done
# Remove the elevated bootstrap material after its successful run. Service secrets remain.
kube delete job sql-bootstrap --wait=true
kube delete configmap sql-bootstrap
printf 'VWP is ready in context %s, namespace %s. See docs/local-kubernetes.md for port forwarding and verification.\n' "$context" "$namespace"

# Account-to-Financials Operation performance baseline and comparison

This guide preserves the VWP-4 local baseline from before VWP-6 and adds a repeat run against the VWP-6 implementation. The two runs use the same workload profiles and local machine, but are separate measurements rather than a controlled A/B experiment. They characterize this local PoC only; they do not establish production capacity or general scalability.

## What this measures

Each flow sends an Account `PUT` to Accounts, then immediately sends exactly one `GET` to Financials and checks the local Accounts view, then sends one Financials `POST /operations` for the same AccountId. Independent flows overlap, so an Operation for one Account can be written while another Account is being created. There is no sleep, polling, retry, replica copy, or synchronization step in a flow. The offered rate is Account flows and, for successful flows, the same number of Financials Operation writes per second.

The client records request latency with a monotonic clock. “Write to visible” is measured from the successful Accounts `PUT` response (after the source write was acknowledged) to the successful immediate Financials view `GET` response. It includes the GET request and response time; it is not a measurement of the internal SQL commit timestamp. Each flow performs only one view read, so a missing row is reported as a failure and is never retried.

## Environment and provenance

### Historical baseline (2026-10-06 UTC)

- Run date: 2026-10-06 UTC.
- Checkout: uncommitted changes based on `63491c64cd0b9f4d2695435819464f857785c9f0` (`main`).
- Host: AMD Ryzen 9 5900XT, 16 cores / 32 logical CPUs, 31 GiB RAM; Linux kernel `7.2.8-2-cachyos` on x86_64; Python 3.14.7.
- Docker Engine: 29.8.2, Linux host, 32 CPUs, 31.2 GiB visible memory.
- Kubernetes: one-node `kind-vwp` cluster, Kubernetes v1.37.0, node runtime containerd 2.3.4.
- SQL Server: one SQL Server 2022 CU23 Developer pod with four databases. SQL container requests 500m CPU and 2 GiB memory, with a 4 GiB memory limit.
- APIs: one pod each for Accounts, Financials, Cases, and Notifications; .NET SDK image 10.0.302 and runtime 10.0.12. Each API requests 50m CPU / 128 MiB memory and has a 512 MiB memory limit, with no CPU limit.
- Traffic entered Accounts and Financials through loopback `kubectl port-forward` connections. Temporary host ports 6211 and 6212 were used because the standard 5101–5104 forwards were already owned by the user's running cluster sessions.
- The SQL Server data volume is the local deployment's disposable `emptyDir`. The performance flows remain in it; the harness does not delete test rows. This experiment added 3,510 Accounts and 3,510 Financials Operations across both performance runs.

### Post-VWP-6 rerun (2026-10-07 UTC)

- Checkout: `ed866806efd05c281661d725766280888e9236e9` (`main`), including the VWP-6 mapping change. The four API images were built from this checkout and rolled out successfully to `kind-vwp`.
- Host: AMD Ryzen 9 5900XT, 16 cores / 32 logical CPUs, 31 GiB RAM; Linux kernel `7.2.8-2-cachyos` on x86_64; Python 3.14.7.
- Docker Engine: 29.8.2 on Linux.
- Kubernetes: one-node `kind-vwp` cluster, Kubernetes v1.37.0, node runtime containerd 2.3.4.
- SQL Server: one SQL Server 2022 CU23 Developer pod with four databases. SQL container requests 500m CPU and 2 GiB memory, with a 4 GiB memory limit.
- APIs: one pod each for Accounts, Financials, Cases, and Notifications; .NET SDK image 10.0.302 and runtime 10.0.12. Each API requests 50m CPU / 128 MiB memory and has a 512 MiB memory limit, with no CPU limit.
- Traffic entered Accounts and Financials through loopback `kubectl port-forward` connections on host ports 5101 and 5102.
- The SQL data remained in the dedicated disposable `emptyDir`; the rerun added another 3,510 Accounts and 3,510 Financials Operations. The harness did not delete rows.

## Results

Latency values are **p50 / p95 / p99 in milliseconds**. Both rounds used the documented workload and 20-flow in-flight cap.

### Historical VWP-4 baseline

#### Lower offered rates, 30 seconds each

Raw per-request evidence: [`results/performance/local-7f8d66b2b45e.json`](../results/performance/local-7f8d66b2b45e.json).

| Target flows/s | Successful / scheduled | Max in flight | Accounts PUT | Financials view GET | PUT response → visible view | Operation POST | End to end |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 60 / 60 | 1 | 10.617 / 11.691 / 12.887 | 2.341 / 2.896 / 5.292 | 2.388 / 2.948 / 5.343 | 8.057 / 9.884 / 11.605 | 21.183 / 23.921 / 24.536 |
| 5 | 150 / 150 | 1 | 10.064 / 11.624 / 12.713 | 2.097 / 2.958 / 3.559 | 2.144 / 3.007 / 3.612 | 7.658 / 8.529 / 12.491 | 20.144 / 22.106 / 24.045 |
| 10 | 300 / 300 | 1 | 8.229 / 9.749 / 12.690 | 2.011 / 2.801 / 3.451 | 2.053 / 2.846 / 3.614 | 7.464 / 8.528 / 11.712 | 17.853 / 21.443 / 23.193 |

These rates verified the flow repeatedly but did not create overlapping requests: the maximum active-flow count was one. Higher offered rates were therefore run to exercise concurrency.

#### Concurrent offered rates, 10 seconds each

Raw per-request evidence: [`results/performance/local-01e454f6edf0.json`](../results/performance/local-01e454f6edf0.json).

| Target flows/s | Successful / scheduled | Max in flight | Accounts PUT | Financials view GET | PUT response → visible view | Operation POST | End to end |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 50 | 500 / 500 | 3 | 7.735 / 10.385 / 12.474 | 1.814 / 2.375 / 2.710 | 1.852 / 2.411 / 2.747 | 6.674 / 7.984 / 10.861 | 16.467 / 20.244 / 22.641 |
| 100 | 1,000 / 1,000 | 12 | 8.130 / 13.768 / 20.452 | 1.634 / 2.293 / 3.303 | 1.671 / 2.345 / 3.338 | 8.555 / 13.411 / 25.133 | 18.587 / 28.908 / 91.231 |
| 150 | 1,500 / 1,500 | 6 | 11.585 / 15.195 / 17.908 | 1.610 / 2.224 / 2.620 | 1.645 / 2.267 / 2.671 | 12.524 / 18.327 / 22.788 | 25.579 / 32.232 / 36.256 |

Completed-flow throughput including the short drain was 50.019, 99.928, and 149.686 flows/s for the 50, 100, and 150 flows/s profiles. The Account-to-view-to-Operation path returned success for every flow; each success represents one Account and its matching Operation. The 100/s profile had a 91.231 ms p99 end-to-end latency despite a 28.908 ms p95, showing a small tail-latency outlier in that run.

### Post-VWP-6 rerun

Raw per-request evidence: [lower rates, run 4582a0d0e0ae](../results/performance/local-4582a0d0e0ae.json) and [concurrent rates, run 475f1fc0c513](../results/performance/local-475f1fc0c513.json).

All 3,510 scheduled flows started and succeeded, with zero failures and zero rejections at the concurrency cap. Each successful flow created one Account and its matching Operation. Completed-flow throughput including the drain was 2.032, 5.030, 10.027, 50.006, 99.883, and 149.788 flows/s at the corresponding offered rates.

#### Lower offered rates, 30 seconds each

| Target flows/s | Successful / scheduled | Max in flight | Accounts PUT | Financials view GET | PUT response → visible view | Operation POST | End to end |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 60 / 60 | 2 | 10.759 / 13.042 / 324.398 | 2.254 / 3.238 / 313.150 | 2.309 / 3.295 / 313.201 | 7.901 / 10.485 / 100.553 | 21.457 / 28.686 / 738.153 |
| 5 | 150 / 150 | 1 | 10.427 / 12.385 / 16.341 | 2.296 / 3.074 / 3.871 | 2.353 / 3.166 / 3.943 | 7.768 / 10.168 / 17.055 | 20.836 / 24.301 / 36.811 |
| 10 | 300 / 300 | 1 | 8.550 / 10.412 / 13.001 | 2.211 / 3.426 / 3.748 | 2.268 / 3.522 / 3.825 | 7.610 / 9.494 / 11.753 | 18.603 / 21.938 / 24.513 |

#### Concurrent offered rates, 10 seconds each

| Target flows/s | Successful / scheduled | Max in flight | Accounts PUT | Financials view GET | PUT response → visible view | Operation POST | End to end |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 50 | 500 / 500 | 3 | 7.759 / 9.673 / 13.382 | 1.909 / 2.894 / 3.822 | 1.953 / 2.941 / 3.866 | 7.459 / 8.563 / 11.692 | 17.251 / 20.722 / 24.466 |
| 100 | 1,000 / 1,000 | 4 | 9.153 / 13.628 / 15.155 | 1.781 / 2.724 / 3.598 | 1.826 / 2.772 / 3.692 | 8.765 / 13.687 / 14.294 | 22.502 / 29.025 / 30.731 |
| 150 | 1,500 / 1,500 | 7 | 12.539 / 15.351 / 17.162 | 1.671 / 2.625 / 3.415 | 1.712 / 2.686 / 3.472 | 12.895 / 17.888 / 21.520 | 27.254 / 31.495 / 36.507 |

### Side-by-side comparison

Values show the historical VWP-4 result → the post-VWP-6 result. Latencies are in milliseconds. Throughput is successful flows per wall-clock second including drain.

| Target flows/s | Successful / scheduled | Throughput | Max in flight | End-to-end p95 / p99 | Write-to-visible p95 / p99 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 60/60 → 60/60 | 2.032 → 2.032 | 1 → 2 | 23.921 / 24.536 → 28.686 / 738.153 | 2.948 / 5.343 → 3.295 / 313.201 |
| 5 | 150/150 → 150/150 | 5.030 → 5.030 | 1 → 1 | 22.106 / 24.045 → 24.301 / 36.811 | 3.007 / 3.612 → 3.166 / 3.943 |
| 10 | 300/300 → 300/300 | 10.028 → 10.027 | 1 → 1 | 21.443 / 23.193 → 21.938 / 24.513 | 2.846 / 3.614 → 3.522 / 3.825 |
| 50 | 500/500 → 500/500 | 50.019 → 50.006 | 3 → 3 | 20.244 / 22.641 → 20.722 / 24.466 | 2.411 / 2.747 → 2.941 / 3.866 |
| 100 | 1,000/1,000 → 1,000/1,000 | 99.928 → 99.883 | 12 → 4 | 28.908 / 91.231 → 29.025 / 30.731 | 2.345 / 3.338 → 2.772 / 3.692 |
| 150 | 1,500/1,500 → 1,500/1,500 | 149.686 → 149.788 | 6 → 7 | 32.232 / 36.256 → 31.495 / 36.507 | 2.267 / 2.671 → 2.686 / 3.472 |

Completed throughput stayed within 0.07% of the historical value at every offered rate. End-to-end p95 was 9.9% and 19.9% higher at 5/s and 2/s, respectively; it was within 2.4% of baseline for the other profiles. The 2/s rerun had a 738.153 ms p99 end-to-end result: with only 60 flows, the script's nearest-rank p99 is the maximum, and the slowest successful flow had a long delay across all three requests. At 100/s, the historical 91.231 ms end-to-end p99 did not recur; the new p99 was 30.731 ms. The single-run tails vary, so these measurements do not isolate a latency effect caused by VWP-6.

## Reproduce

From a clean checkout, first follow the [local Kubernetes guide](local-kubernetes.md) to provision the disposable cluster and start the APIs. Keep `scripts/local-forward.sh` running in another terminal. The performance script checks Accounts and Financials readiness before sending load. If ports 5101 and 5102 are already in use, stop only your own conflicting forwards or open temporary forwards on free loopback ports, for example in separate terminals:

```bash
kubectl --context kind-vwp --namespace vwp port-forward --address 127.0.0.1 service/accounts 6211:8080
kubectl --context kind-vwp --namespace vwp port-forward --address 127.0.0.1 service/financials 6212:8080
```

Run the lower-rate baseline:

```bash
ACCOUNTS_URL=http://127.0.0.1:6211 \
FINANCIALS_URL=http://127.0.0.1:6212 \
python3 scripts/performance-local.py --rates 2 5 10 --duration-seconds 30 --max-in-flight 20
```

Run the concurrent profile:

```bash
ACCOUNTS_URL=http://127.0.0.1:6211 \
FINANCIALS_URL=http://127.0.0.1:6212 \
python3 scripts/performance-local.py --rates 50 100 150 --duration-seconds 10 --max-in-flight 20
```

Each run writes a unique JSON result under `results/performance/`. The script permits up to 200 target flows/s, 300 seconds per profile, 100 in-flight flows, and 5,000 scheduled flows total; it performs zero retries. Failed or partial flows are retained in the result and produce a non-zero exit code.

The script does not delete its generated Accounts or Operations. The SQL data is disposable and is removed only when the dedicated cluster is deleted with `scripts/local-down.sh` as described in the local guide. The measured offer rate is Account flows per second; every successful flow also creates one matching Financials Operation.

## Limits of this evidence

These are local throughput characterizations of one pod per API and one SQL Server pod on a single kind node, on one developer machine. They show immediate view visibility and matching Operation writes under the tested offered rates. They do not establish production capacity, a service-level objective, multi-node behavior, or a horizontal scaling curve. The host shared CPU and memory with other processes. Pod CPU and memory were not sampled during the post-VWP-6 profiles; the Metrics API was unavailable during the historical run. Repeated runs add rows to the disposable local SQL data volume.

# Account-to-Financials Operation performance baseline

These measurements are a historical local baseline collected before VWP-6 replaced the consumer SaveChanges interceptor with EF Core native view-only behavior and model validation. They demonstrate the earlier Account-to-Operation flow under the listed conditions; they are not a fresh performance measurement of the current VWP-6 code. Use the commands below to collect a new result after starting the current checkout.

## What this measures

Each flow sends an Account `PUT` to Accounts, then immediately sends exactly one `GET` to Financials and checks the local Accounts view, then sends one Financials `POST /operations` for the same AccountId. Independent flows overlap, so an Operation for one Account can be written while another Account is being created. There is no sleep, polling, retry, replica copy, or synchronization step in a flow. The offered rate is Account flows and, for successful flows, the same number of Financials Operation writes per second.

The client records request latency with a monotonic clock. “Write to visible” is measured from the successful Accounts `PUT` response (after the source write was acknowledged) to the successful immediate Financials view `GET` response. It includes the GET request and response time; it is not a measurement of the internal SQL commit timestamp. Each flow performs only one view read, so a missing row is reported as a failure and is never retried.

## Environment

- Run date: 2026-10-06 UTC.
- Checkout: uncommitted changes based on `63491c64cd0b9f4d2695435819464f857785c9f0` (`main`).
- Host: AMD Ryzen 9 5900XT, 16 cores / 32 logical CPUs, 31 GiB RAM; Linux kernel `7.2.8-2-cachyos` on x86_64; Python 3.14.7.
- Docker Engine: 29.8.2, Linux host, 32 CPUs, 31.2 GiB visible memory.
- Kubernetes: one-node `kind-vwp` cluster, Kubernetes v1.37.0, node runtime containerd 2.3.4.
- SQL Server: one SQL Server 2022 CU23 Developer pod with four databases. SQL container requests 500m CPU and 2 GiB memory, with a 4 GiB memory limit.
- APIs: one pod each for Accounts, Financials, Cases, and Notifications; .NET SDK image 10.0.302 and runtime 10.0.12. Each API requests 50m CPU / 128 MiB memory and has a 512 MiB memory limit, with no CPU limit.
- Traffic entered Accounts and Financials through loopback `kubectl port-forward` connections. Temporary host ports 6211 and 6212 were used because the standard 5101–5104 forwards were already owned by the user's running cluster sessions.
- The SQL Server data volume is the local deployment's disposable `emptyDir`. The performance flows remain in it; the harness does not delete test rows. This experiment added 3,510 Accounts and 3,510 Financials Operations across both performance runs.

## Results

Latency values are **p50 / p95 / p99 in milliseconds**. All scheduled flows started, every flow completed, none failed, and none was rejected by the 20-flow concurrency cap.

### Lower offered rates, 30 seconds each

Raw per-request evidence: [`results/performance/local-7f8d66b2b45e.json`](../results/performance/local-7f8d66b2b45e.json).

| Target flows/s | Successful / scheduled | Max in flight | Accounts PUT | Financials view GET | PUT response → visible view | Operation POST | End to end |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 60 / 60 | 1 | 10.617 / 11.691 / 12.887 | 2.341 / 2.896 / 5.292 | 2.388 / 2.948 / 5.343 | 8.057 / 9.884 / 11.605 | 21.183 / 23.921 / 24.536 |
| 5 | 150 / 150 | 1 | 10.064 / 11.624 / 12.713 | 2.097 / 2.958 / 3.559 | 2.144 / 3.007 / 3.612 | 7.658 / 8.529 / 12.491 | 20.144 / 22.106 / 24.045 |
| 10 | 300 / 300 | 1 | 8.229 / 9.749 / 12.690 | 2.011 / 2.801 / 3.451 | 2.053 / 2.846 / 3.614 | 7.464 / 8.528 / 11.712 | 17.853 / 21.443 / 23.193 |

These rates verified the flow repeatedly but did not create overlapping requests: the maximum active-flow count was one. Higher offered rates were therefore run to exercise concurrency.

### Concurrent offered rates, 10 seconds each

Raw per-request evidence: [`results/performance/local-01e454f6edf0.json`](../results/performance/local-01e454f6edf0.json).

| Target flows/s | Successful / scheduled | Max in flight | Accounts PUT | Financials view GET | PUT response → visible view | Operation POST | End to end |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 50 | 500 / 500 | 3 | 7.735 / 10.385 / 12.474 | 1.814 / 2.375 / 2.710 | 1.852 / 2.411 / 2.747 | 6.674 / 7.984 / 10.861 | 16.467 / 20.244 / 22.641 |
| 100 | 1,000 / 1,000 | 12 | 8.130 / 13.768 / 20.452 | 1.634 / 2.293 / 3.303 | 1.671 / 2.345 / 3.338 | 8.555 / 13.411 / 25.133 | 18.587 / 28.908 / 91.231 |
| 150 | 1,500 / 1,500 | 6 | 11.585 / 15.195 / 17.908 | 1.610 / 2.224 / 2.620 | 1.645 / 2.267 / 2.671 | 12.524 / 18.327 / 22.788 | 25.579 / 32.232 / 36.256 |

Completed-flow throughput including the short drain was 50.019, 99.928, and 149.686 flows/s for the 50, 100, and 150 flows/s profiles. The Account-to-view-to-Operation path returned success for every flow; each success represents one Account and its matching Operation. The 100/s profile had a 91.231 ms p99 end-to-end latency despite a 28.908 ms p95, showing a small tail-latency outlier in that run.

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

This is a local throughput characterization of one pod per API and one SQL Server pod on a single kind node, on one developer machine. It shows immediate view visibility and matching Operation writes under the tested offered rates. It does not establish a production capacity, service-level objective, multi-node behavior, or horizontal scaling curve. The host shared CPU and memory with other processes. The Kubernetes Metrics API was unavailable during this run, so pod CPU and memory usage could not be recorded. Repeated runs add rows to the disposable local SQL data volume.

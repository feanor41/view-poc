#!/usr/bin/env python3
"""Run a bounded concurrent Account-to-Financials-Operation workload."""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import math
import os
import platform
import sys
import time
import urllib.error
import urllib.request
import uuid
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_ACCOUNTS_URL = os.environ.get("ACCOUNTS_URL", "http://127.0.0.1:5101")
DEFAULT_FINANCIALS_URL = os.environ.get("FINANCIALS_URL", "http://127.0.0.1:5102")
MAX_PROFILE_SECONDS = 300
MAX_RATE_PER_SECOND = 200
MAX_IN_FLIGHT = 100
MAX_TOTAL_FLOWS = 5_000
REQUEST_TIMEOUT_SECONDS = 15


def request(
    base_url: str,
    path: str,
    *,
    method: str = "GET",
    payload: dict[str, Any] | None = None,
) -> tuple[int | None, dict[str, Any], float, str | None]:
    """Make one HTTP request and return status, JSON body, latency, and transport error."""
    body = None if payload is None else json.dumps(payload, separators=(",", ":")).encode("utf-8")
    message = urllib.request.Request(
        f"{base_url.rstrip('/')}{path}",
        data=body,
        method=method,
        headers={"Content-Type": "application/json"},
    )
    started = time.perf_counter_ns()
    try:
        with urllib.request.urlopen(message, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            status = response.status
            response_body = response.read()
    except urllib.error.HTTPError as error:
        status = error.code
        response_body = error.read()
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        latency_ms = (time.perf_counter_ns() - started) / 1_000_000
        return None, {}, latency_ms, f"{type(error).__name__}: {error}"

    latency_ms = (time.perf_counter_ns() - started) / 1_000_000
    if not response_body:
        return status, {}, latency_ms, None
    try:
        parsed = json.loads(response_body)
    except json.JSONDecodeError:
        return status, {"body": response_body.decode("utf-8", errors="replace")[:500]}, latency_ms, None
    if not isinstance(parsed, dict):
        return status, {"body": parsed}, latency_ms, None
    return status, parsed, latency_ms, None


def failure(stage: str, message: str, result: dict[str, Any]) -> dict[str, Any]:
    result["ok"] = False
    result["failed_stage"] = stage
    result["error"] = message[:500]
    result["end_to_end_ms"] = (time.perf_counter_ns() - result["flow_started_ns"]) / 1_000_000
    result["completed_offset_ms"] = (time.perf_counter_ns() - result["run_started_ns"]) / 1_000_000
    result.pop("flow_started_ns", None)
    result.pop("run_started_ns", None)
    return result


def execute_flow(
    index: int,
    run_id: str,
    run_started_ns: int,
    accounts_url: str,
    financials_url: str,
) -> dict[str, Any]:
    """Run one Account PUT, immediate Financials view GET, and Operation POST without retries."""
    flow_started_ns = time.perf_counter_ns()
    account_id = str(uuid.uuid4())
    label = f"VWPperf-{run_id}-{index:05d}"
    result: dict[str, Any] = {
        "index": index,
        "account_id": account_id,
        "operation_id": None,
        "started_offset_ms": (flow_started_ns - run_started_ns) / 1_000_000,
        "account_create_ms": None,
        "account_view_read_ms": None,
        "account_write_to_visible_ms": None,
        "operation_create_ms": None,
        "end_to_end_ms": None,
        "ok": False,
        "failed_stage": None,
        "error": None,
        "flow_started_ns": flow_started_ns,
        "run_started_ns": run_started_ns,
    }

    status, body, latency_ms, transport_error = request(
        accounts_url,
        f"/accounts/{account_id}",
        method="PUT",
        payload={"name": label, "status": "Performance"},
    )
    result["account_create_ms"] = latency_ms
    if transport_error:
        return failure("account_create", transport_error, result)
    if status != 200 or body.get("id") != account_id:
        return failure("account_create", f"HTTP {status}, response={body}", result)

    account_write_completed_ns = time.perf_counter_ns()
    # This is deliberately the next request: one read, no wait, retry, or synchronization.
    status, body, latency_ms, transport_error = request(
        financials_url,
        f"/accounts/{account_id}",
    )
    result["account_view_read_ms"] = latency_ms
    if transport_error:
        return failure("account_view_read", transport_error, result)
    if status != 200 or body.get("id") != account_id or body.get("name") != label:
        return failure("account_view_read", f"HTTP {status}, response={body}", result)
    result["account_write_to_visible_ms"] = (
        time.perf_counter_ns() - account_write_completed_ns
    ) / 1_000_000

    status, body, latency_ms, transport_error = request(
        financials_url,
        "/operations",
        method="POST",
        payload={
            "accountId": account_id,
            "description": label,
            "amount": 1.25,
        },
    )
    result["operation_create_ms"] = latency_ms
    if transport_error:
        return failure("operation_create", transport_error, result)
    if status != 201 or body.get("accountId") != account_id or not body.get("id"):
        return failure("operation_create", f"HTTP {status}, response={body}", result)
    try:
        uuid.UUID(str(body["id"]))
    except (ValueError, TypeError, AttributeError):
        return failure("operation_create", f"Invalid Operation id: {body}", result)

    result["operation_id"] = body["id"]
    result["ok"] = True
    result["end_to_end_ms"] = (time.perf_counter_ns() - flow_started_ns) / 1_000_000
    result["completed_offset_ms"] = (time.perf_counter_ns() - run_started_ns) / 1_000_000
    result.pop("flow_started_ns", None)
    result.pop("run_started_ns", None)
    return result


def percentile(values: list[float], percent: int) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    index = max(0, math.ceil((percent / 100) * len(ordered)) - 1)
    return round(ordered[index], 3)


def latency_summary(records: list[dict[str, Any]], key: str) -> dict[str, Any]:
    values = [float(item[key]) for item in records if item.get(key) is not None]
    return {
        "count": len(values),
        "p50_ms": percentile(values, 50),
        "p95_ms": percentile(values, 95),
        "p99_ms": percentile(values, 99),
        "max_ms": round(max(values), 3) if values else None,
    }


def summarize_profile(
    rate: int,
    duration_seconds: int,
    records: list[dict[str, Any]],
    started_count: int,
    rejected: list[dict[str, Any]],
    max_observed_in_flight: int,
    schedule_window_seconds: float,
    wall_seconds: float,
) -> dict[str, Any]:
    all_records = records + rejected
    succeeded = sum(bool(item.get("ok")) for item in records)
    failure_counts = Counter(
        str(item.get("failed_stage")) for item in all_records if not item.get("ok")
    )
    return {
        "target_flows_per_second": rate,
        "duration_seconds": duration_seconds,
        "scheduled_count": rate * duration_seconds,
        "started_count": started_count,
        "completed_count": len(records),
        "successful_count": succeeded,
        "failed_count": len(all_records) - succeeded,
        "rejected_at_concurrency_cap": len(rejected),
        "failure_counts_by_stage": dict(sorted(failure_counts.items())),
        "achieved_successes_per_offered_second": round(succeeded / duration_seconds, 3),
        "completion_throughput_flows_per_wall_second": round(
            succeeded / wall_seconds if wall_seconds > 0 else 0, 3
        ),
        "max_observed_in_flight": max_observed_in_flight,
        "schedule_window_seconds": round(schedule_window_seconds, 3),
        "wall_seconds_including_drain": round(wall_seconds, 3),
        "latency_ms": {
            "account_create": latency_summary(records, "account_create_ms"),
            "account_view_read": latency_summary(records, "account_view_read_ms"),
            "account_write_to_visible": latency_summary(records, "account_write_to_visible_ms"),
            "operation_create": latency_summary(records, "operation_create_ms"),
            "end_to_end": latency_summary(records, "end_to_end_ms"),
        },
        "requests": sorted(all_records, key=lambda item: item["index"]),
    }


def run_profile(
    rate: int,
    duration_seconds: int,
    max_in_flight: int,
    run_id: str,
    accounts_url: str,
    financials_url: str,
) -> dict[str, Any]:
    scheduled_count = rate * duration_seconds
    run_started_ns = time.perf_counter_ns()
    schedule_start = time.monotonic()
    inflight: set[concurrent.futures.Future[dict[str, Any]]] = set()
    future_indices: dict[concurrent.futures.Future[dict[str, Any]], int] = {}
    records: list[dict[str, Any]] = []
    rejected: list[dict[str, Any]] = []
    max_observed_in_flight = 0
    started_count = 0
    next_index = 0

    def harvest(done: set[concurrent.futures.Future[dict[str, Any]]]) -> None:
        for future in done:
            inflight.discard(future)
            index = future_indices.pop(future)
            try:
                records.append(future.result())
            except Exception as error:  # Keep unexpected worker failures in the evidence.
                now_ns = time.perf_counter_ns()
                records.append({
                    "index": index,
                    "account_id": None,
                    "operation_id": None,
                    "started_offset_ms": None,
                    "account_create_ms": None,
                    "account_view_read_ms": None,
                    "account_write_to_visible_ms": None,
                    "operation_create_ms": None,
                    "end_to_end_ms": None,
                    "completed_offset_ms": (now_ns - run_started_ns) / 1_000_000,
                    "ok": False,
                    "failed_stage": "worker_exception",
                    "error": f"{type(error).__name__}: {error}"[:500],
                })

    with concurrent.futures.ThreadPoolExecutor(max_workers=max_in_flight) as executor:
        while next_index < scheduled_count:
            due = schedule_start + next_index / rate
            remaining = due - time.monotonic()
            if remaining > 0:
                if inflight:
                    done, _ = concurrent.futures.wait(
                        inflight,
                        timeout=min(remaining, 0.05),
                        return_when=concurrent.futures.FIRST_COMPLETED,
                    )
                    harvest(done)
                else:
                    time.sleep(min(remaining, 0.05))
                continue

            done = {future for future in inflight if future.done()}
            harvest(done)
            offset_ms = (time.monotonic() - schedule_start) * 1_000
            if len(inflight) >= max_in_flight:
                rejected.append({
                    "index": next_index,
                    "account_id": None,
                    "operation_id": None,
                    "started_offset_ms": None,
                    "scheduled_offset_ms": round(offset_ms, 3),
                    "account_create_ms": None,
                    "account_view_read_ms": None,
                    "account_write_to_visible_ms": None,
                    "operation_create_ms": None,
                    "end_to_end_ms": None,
                    "completed_offset_ms": round(offset_ms, 3),
                    "ok": False,
                    "failed_stage": "concurrency_limit",
                    "error": f"In-flight cap of {max_in_flight} reached; no request sent.",
                })
            else:
                future = executor.submit(
                    execute_flow,
                    next_index,
                    run_id,
                    run_started_ns,
                    accounts_url,
                    financials_url,
                )
                inflight.add(future)
                future_indices[future] = next_index
                started_count += 1
                max_observed_in_flight = max(max_observed_in_flight, len(inflight))
            next_index += 1

        schedule_window_seconds = time.monotonic() - schedule_start
        while inflight:
            done, _ = concurrent.futures.wait(
                inflight,
                return_when=concurrent.futures.FIRST_COMPLETED,
            )
            harvest(done)
    wall_seconds = (time.perf_counter_ns() - run_started_ns) / 1_000_000_000
    summary = summarize_profile(
        rate,
        duration_seconds,
        records,
        started_count,
        rejected,
        max_observed_in_flight,
        schedule_window_seconds,
        wall_seconds,
    )
    return summary


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rates", type=int, nargs="+", default=[2, 5, 10], help="Target flow starts per second.")
    parser.add_argument("--duration-seconds", type=int, default=30, help="Offered load duration for each rate.")
    parser.add_argument("--max-in-flight", type=int, default=20, help="Hard cap on concurrent Account flows.")
    parser.add_argument("--accounts-url", default=DEFAULT_ACCOUNTS_URL)
    parser.add_argument("--financials-url", default=DEFAULT_FINANCIALS_URL)
    parser.add_argument("--output", type=Path, help="Optional JSON output path; defaults to a unique results/performance file.")
    args = parser.parse_args()

    if not args.rates or any(rate < 1 or rate > MAX_RATE_PER_SECOND for rate in args.rates):
        parser.error(f"each rate must be between 1 and {MAX_RATE_PER_SECOND} flows per second")
    if args.duration_seconds < 1 or args.duration_seconds > MAX_PROFILE_SECONDS:
        parser.error(f"duration must be between 1 and {MAX_PROFILE_SECONDS} seconds")
    if args.max_in_flight < 1 or args.max_in_flight > MAX_IN_FLIGHT:
        parser.error(f"max-in-flight must be between 1 and {MAX_IN_FLIGHT}")
    total_flows = sum(args.rates) * args.duration_seconds
    if total_flows > MAX_TOTAL_FLOWS:
        parser.error(f"combined profiles may schedule at most {MAX_TOTAL_FLOWS} flows")
    return args


def main() -> int:
    args = parse_args()
    run_id = uuid.uuid4().hex[:12]
    run_started_utc = datetime.now(timezone.utc).isoformat()
    urls = {
        "Accounts": args.accounts_url.rstrip("/"),
        "Financials": args.financials_url.rstrip("/"),
    }
    for service, base_url in urls.items():
        status, body, _, error = request(base_url, "/health/ready")
        if error or status != 200:
            print(
                f"FAIL: {service} readiness at {base_url}: HTTP {status}, {error or body}",
                file=sys.stderr,
            )
            return 2

    profiles = [
        run_profile(
            rate,
            args.duration_seconds,
            args.max_in_flight,
            run_id,
            args.accounts_url,
            args.financials_url,
        )
        for rate in args.rates
    ]
    result = {
        "schema_version": 1,
        "run_id": run_id,
        "started_utc": run_started_utc,
        "host": {
            "platform": platform.platform(),
            "processor": platform.processor() or None,
            "logical_cpu_count": os.cpu_count(),
            "python": platform.python_version(),
        },
        "services": urls,
        "settings": {
            "rates_per_second": args.rates,
            "duration_seconds_per_profile": args.duration_seconds,
            "max_in_flight": args.max_in_flight,
            "request_timeout_seconds": REQUEST_TIMEOUT_SECONDS,
            "total_scheduled_flow_cap": MAX_TOTAL_FLOWS,
            "retries": 0,
        },
        "profiles": profiles,
    }
    output_path = args.output or Path("results/performance") / f"local-{run_id}.json"
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")

    print(f"Run {run_id}; result: {output_path}")
    any_failures = False
    for profile in profiles:
        print(
            "target={target_flows_per_second}/s scheduled={scheduled_count} started={started_count} "
            "success={successful_count} failed={failed_count} "
            "success/offered-second={achieved_successes_per_offered_second} "
            "completion/wall-second={completion_throughput_flows_per_wall_second} "
            "max-in-flight={max_observed_in_flight} e2e-p95-ms={p95}".format(
                **profile,
                p95=profile["latency_ms"]["end_to_end"]["p95_ms"],
            )
        )
        if profile["failed_count"]:
            any_failures = True
            print(f"  failures by stage: {profile['failure_counts_by_stage']}")
    return 1 if any_failures else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("Interrupted; use a smaller profile if needed.", file=sys.stderr)
        raise SystemExit(130)

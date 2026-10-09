#!/usr/bin/env python3
"""Measure bounded synthetic write-to-observed-report lag, not production capacity."""
from __future__ import annotations

import importlib.util
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import threading
import time
import uuid
from datetime import datetime, timezone

spec = importlib.util.spec_from_file_location("datalake_verify", Path(__file__).with_name("datalake-verify.py"))
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)


def percentile(values, percent):
    ordered = sorted(values)
    if not ordered:
        return None
    return round(ordered[min(len(ordered)-1, math.ceil(len(ordered) * percent / 100)-1)], 3)


def seed(engine, accounts: int, operations: int):
    ids = [str(uuid.uuid4()) for _ in range(accounts)]
    for offset in range(0, accounts, 100):
        if engine.name == "postgres":
            rows = ",\n".join(f"('{identifier}','benchmark account','Active',now())" for identifier in ids[offset:offset+100])
            engine.sql("accounts", "INSERT INTO public.accounts(id,name,status,created_at) VALUES " + rows + ";")
        else:
            rows = ",\n".join(f"('{identifier}',N'benchmark account',N'Active',SYSDATETIMEOFFSET())" for identifier in ids[offset:offset+100])
            engine.sql("accounts", "INSERT INTO dbo.Accounts(Id,Name,Status,CreatedAt) VALUES " + rows + ";")
    for offset in range(0, operations, 100):
        # A bounded source transaction never exceeds 100 rows.
        engine.insert_operations([str(uuid.uuid4()) for _ in range(min(100, operations-offset))], ids[(offset // 100) % len(ids)])
    return ids


def profile(engine, account_ids, rate, duration, cadence, settings, drain_seconds):
    pending = {}
    latencies = []
    lock = threading.Lock()
    failures = []
    counters = {"committed_rows": 0, "source_transactions": 0, "max_pending_rows": 0, "missed_schedule_ticks": 0}
    start = time.monotonic()
    offered = max(1, round(rate * duration))
    producer_done = threading.Event()
    cancelled = threading.Event()

    def produce():
        try:
            index = 0
            tick = 0
            while index < offered and not cancelled.is_set():
                due = start + tick * 0.1
                delay = due - time.monotonic()
                if delay > 0:
                    cancelled.wait(delay)
                if cancelled.is_set():
                    break
                if time.monotonic() > due + 0.1:
                    counters["missed_schedule_ticks"] += 1
                target = min(offered, round((tick + 1) * 0.1 * rate))
                if target <= index:
                    tick += 1
                    continue
                identifiers = [str(uuid.uuid4()) for _ in range(target-index)]
                engine.insert_operations(identifiers, account_ids[index % len(account_ids)])
                committed = time.monotonic()
                with lock:
                    pending.update({identifier: committed for identifier in identifiers})
                    counters["committed_rows"] += len(identifiers)
                    counters["source_transactions"] += 1
                    counters["max_pending_rows"] = max(counters["max_pending_rows"], len(pending))
                index = target
                tick += 1
        except Exception as error:
            failures.append(type(error).__name__)
        finally:
            counters["source_write_wall_seconds"] = round(time.monotonic()-start, 3)
            producer_done.set()

    producer = threading.Thread(target=produce, daemon=True)
    producer.start()
    sync_seconds = []
    observed_samples = []
    deadline = start + duration + drain_seconds
    try:
        while time.monotonic() < deadline:
            cycle = time.monotonic()
            _, elapsed = engine.sync(settings)
            sync_seconds.append(round(elapsed, 3))
            projected = {row["id"] for row in engine.projection()}
            with lock:
                observed = time.monotonic()
                visible = set(pending) & projected
                for identifier in visible:
                    latencies.append((observed-pending.pop(identifier))*1000)
                observed_samples.append({"elapsed_seconds": round(observed-start, 3), "pending_rows": len(pending)})
                complete = producer_done.is_set() and not pending
            if complete:
                break
            time.sleep(max(0, cadence-(time.monotonic()-cycle)))
    finally:
        cancelled.set()
        producer.join(timeout=180)
    if producer.is_alive():
        raise AssertionError("synthetic source producer exceeded bounded profile deadline")
    if failures:
        raise AssertionError("synthetic source producer failed; sensitive output withheld")
    with lock:
        remaining = len(pending)
    engine.assert_equal()
    wall = time.monotonic()-start
    return {"offered_rate_per_second": rate, "offered_rows": offered, "duration_seconds": duration,
            "wall_seconds_with_drain": round(wall, 3), "achieved_commits_per_wall_second": round(counters["committed_rows"]/wall, 3),
            **counters, "achieved_source_commits_per_second": round(counters["committed_rows"]/counters["source_write_wall_seconds"], 3) if counters["source_write_wall_seconds"] > 0 else None, "observed_rows": len(latencies), "remaining_pending_rows": remaining,
            "passed": remaining == 0 and len(latencies) == offered,
            "commit_completion_to_report_observed_ms": {"p50": percentile(latencies, 50), "p95": percentile(latencies, 95),
                                                       "p99": percentile(latencies, 99), "max": round(max(latencies), 3) if latencies else None},
            "synthetic_source_tick_seconds": 0.1, "sync_invocation_seconds": sync_seconds, "backlog_samples": observed_samples}


def main():
    parser = verify.parser(__doc__)
    parser.add_argument("--accounts", type=int, default=100)
    parser.add_argument("--operations", type=int, default=1000)
    parser.add_argument("--rate", type=float, default=58)
    parser.add_argument("--burst-rate", type=float)
    parser.add_argument("--burst-multiplier", type=float, default=2)
    parser.add_argument("--duration-seconds", type=int, default=10)
    parser.add_argument("--burst-seconds", type=int, default=5)
    parser.add_argument("--cadence-seconds", type=float, default=1)
    parser.add_argument("--batch-rows", type=int, default=1000)
    parser.add_argument("--batch-bytes", type=int, default=1048576)
    parser.add_argument("--retention-years", type=int, default=5)
    parser.add_argument("--drain-seconds", type=int, default=120)
    args = parser.parse_args()
    if args.burst_rate is None:
        args.burst_rate = args.rate * args.burst_multiplier
    if not (1 <= args.accounts <= 100000 and 0 <= args.operations <= 200000):
        parser.error("bounded local account/operation limits are 100000/200000")
    if not (0 < args.rate <= 1000 and 0 < args.burst_rate <= 1000):
        parser.error("local offered rates must be in (0,1000]")
    if not (1 <= args.duration_seconds <= 300 and 1 <= args.burst_seconds <= 300):
        parser.error("each local profile duration must be 1..300 seconds")
    if not (0.1 <= args.cadence_seconds <= 60 and 1 <= args.drain_seconds <= 600):
        parser.error("cadence must be 0.1..60 seconds and drain deadline 1..600 seconds")
    if args.batch_rows < 1 or args.batch_bytes < 1024 or args.retention_years < 5:
        parser.error("positive batch bounds and retention of at least five years required")
    destination = verify.output_path(args.output)
    env = verify.load_environment(args.env_file)
    settings = {"VWP_ETL_MAX_ROWS": str(args.batch_rows), "VWP_ETL_MAX_BYTES": str(args.batch_bytes),
                "VWP_ETL_RETENTION_YEARS": str(args.retention_years), "VWP_ETL_POLL_MS": "100"}
    env |= settings
    result = {"schema_version": 1, "harness_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "started_utc": datetime.now(timezone.utc).isoformat(),
              "host": {"platform": platform.platform(), "logical_cpu_count": os.cpu_count()},
              "reference_bound": {"vehicles": 2000000, "transactions_per_vehicle_day": 2.5,
                                  "transactions_per_day": 5000000, "average_transactions_per_second": 5000000/86400,
                                  "five_year_transactions_365_day_years": 9125000000},
              "retention_years": args.retention_years, "automatic_purge": False,
              "settings": {"batch_rows": args.batch_rows, "batch_bytes": args.batch_bytes,
                           "cadence_seconds": args.cadence_seconds, "poll_milliseconds": 100,
                           "drain_seconds": args.drain_seconds, "configuration": args.configuration},
              "limitations": ["Local synthetic measurement does not prove worst-case or multi-year production capacity.",
                              "Lag starts after source command commit completion and ends when a polling query observes the report; polling adds delay.",
                              "Host subprocess startup and docker exec overhead limit achieved offered write rate; no service API workload is measured.",
                              "Transactional row fanout, realistic peak, storage growth and CPU/memory contention are not established by this run."],
              "engines": [], "passed": False}
    try:
        for name in ("sqlserver", "postgres") if args.engine == "both" else (args.engine,):
            engine = verify.Engine(name, env, args.configuration)
            record = {"engine": name, "binary_sha256": engine.binary_sha256(), "profiles": [], "seed_accounts": args.accounts, "seed_operations": args.operations}
            result["engines"].append(record)
            engine.cli("bootstrap")
            table = "public.accounts" if name == "postgres" else "dbo.Accounts"
            operation_table = "public.operations" if name == "postgres" else "dbo.Operations"
            record["existing_source_counts"] = {"accounts": int(engine.sql("accounts", f"SELECT COUNT(*) FROM {table};")),
                                                "operations": int(engine.sql("financials", f"SELECT COUNT(*) FROM {operation_table};"))}
            account_ids = seed(engine, args.accounts, args.operations)
            _, record["snapshot_seconds"] = engine.snapshot()
            engine.drain(settings)
            record["baseline_observation"] = engine.observe()
            for label, rate, duration in (("baseline", args.rate, args.duration_seconds), ("burst", args.burst_rate, args.burst_seconds)):
                measurement = profile(engine, account_ids, rate, duration, args.cadence_seconds, settings, args.drain_seconds)
                measurement["profile"] = label
                record["profiles"].append(measurement)
                print(f"{name} {label}: rows={measurement['observed_rows']} pending={measurement['remaining_pending_rows']} p95_ms={measurement['commit_completion_to_report_observed_ms']['p95']}", flush=True)
            record["final_observation"] = engine.observe()
        result["passed"] = all(profile["passed"] for record in result["engines"] for profile in record["profiles"])
    except Exception as error:
        result["failure_type"] = type(error).__name__
        if isinstance(error, AssertionError):
            result["failure_detail"] = str(error)
    destination.write_text(json.dumps(result, indent=2)+"\n")
    print(f"Evidence: {destination}")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())

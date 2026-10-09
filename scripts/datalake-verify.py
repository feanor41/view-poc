#!/usr/bin/env python3
"""Verify native synchronization only against explicitly disposable databases."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import select
import subprocess
import sys
import time
import uuid
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
TABLES = {
    "sqlserver": {"accounts": ["Accounts", "Assets", "ContactInformation", "AccountHistory"],
                  "financials": ["Operations", "Transactions", "PaymentMeans", "CollectionOrders"]},
    "postgres": {"accounts": ["accounts", "account_history"],
                 "financials": ["operations", "transactions", "payment_means", "collection_orders"]},
}


def load_environment(path: Path | None) -> dict[str, str]:
    env = dict(os.environ)
    if path:
        if path.stat().st_mode & 0o077:
            raise ValueError("environment file must be private (mode 0600)")
        for line in path.read_text().splitlines():
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            key, separator, value = line.partition("=")
            if not separator or not re.fullmatch(r"[A-Z][A-Z0-9_]*", key):
                raise ValueError("environment file must contain raw KEY=value lines")
            env[key] = value
    return env


def output_path(path: Path) -> Path:
    resolved = path.resolve()
    if resolved == ROOT or ROOT in resolved.parents:
        raise ValueError("evidence must be written outside the checkout")
    resolved.parent.mkdir(parents=True, exist_ok=True)
    return resolved


def normalized(value):
    if isinstance(value, dict):
        return {key.lower(): normalized(item) for key, item in value.items()}
    if isinstance(value, list):
        return [normalized(item) for item in value]
    if isinstance(value, str) and re.fullmatch(r"[0-9a-fA-F-]{36}", value):
        return value.lower()
    return value


class Engine:
    def __init__(self, name: str, env: dict[str, str], configuration: str = "Release"):
        self.name, self.env, self.configuration = name, dict(env), configuration
        self.env["VWP_ETL_POLL_MS"] = "100"
        prefix = "VWP_" + name.upper()
        self.container = env.get(prefix + "_CONTAINER", "")
        self.databases = {role: env.get(prefix + "_" + role.upper() + "_DATABASE", "")
                          for role in ("accounts", "financials", "datalake")}
        if env.get("VWP_DATALAKE_DISPOSABLE") != "1":
            raise ValueError("set VWP_DATALAKE_DISPOSABLE=1 for isolated verification")
        if not re.fullmatch(r"vwp11-[a-z0-9-]+", self.container):
            raise ValueError("verification container must have the vwp11- prefix")
        if any(not re.fullmatch(r"vwp11_[a-z0-9_]+", db) for db in self.databases.values()):
            raise ValueError("all verification database names must have the vwp11_ prefix")
        if len(set(self.databases.values())) != 3:
            raise ValueError("source and target databases must be distinct")
        if not env.get(prefix + "_CONNECTION"):
            raise ValueError("missing engine connection environment")
        self.project = ROOT / "src" / "Datalake" / ("Vwp.Datalake.SqlServer" if name == "sqlserver" else "Vwp.Datalake.Postgres")

    def binary_sha256(self):
        binary = self.project / "bin" / self.configuration / "net10.0" / (self.project.name + ".dll")
        return hashlib.sha256(binary.read_bytes()).hexdigest()

    def command(self, action: str, extra: list[str] | None = None):
        return ["dotnet", "run", "--no-build", "--configuration", self.configuration,
                "--project", str(self.project), "--", action, *(extra or [])]

    def cli(self, action: str, extra: list[str] | None = None,
            settings: dict[str, str] | None = None, expected: int = 0, timeout: int = 180):
        started = time.monotonic()
        result = subprocess.run(self.command(action, extra), env=self.env | (settings or {}),
                                cwd=ROOT, capture_output=True, text=True, timeout=timeout)
        if result.returncode != expected:
            # Driver exception text may include connection details; never persist it.
            raise AssertionError(f"{self.name} {action}: expected exit {expected}, got {result.returncode}")
        return result, time.monotonic() - started

    def start(self, action: str, extra: list[str] | None = None, settings=None, pipe=False):
        return subprocess.Popen(self.command(action, extra), env=self.env | (settings or {}),
                                cwd=ROOT, stdout=subprocess.PIPE if pipe else subprocess.DEVNULL, stderr=subprocess.DEVNULL, text=True)

    def sql(self, role: str, statement: str, *, database: str | None = None) -> str:
        db = database or self.databases[role]
        if not re.fullmatch(r"vwp11_[a-z0-9_]+|postgres", db):
            raise ValueError("unexpected SQL database")
        if self.name == "postgres":
            args = ["docker", "exec", "-i", "-u", "postgres", self.container,
                    "psql", "-X", "-v", "ON_ERROR_STOP=1", "-At", "-d", db]
        else:
            args = ["docker", "exec", "-i", self.container, "sh", "-c",
                    'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -y 0 -d "$1"',
                    "sql", db]
            statement = "SET NOCOUNT ON;\n" + statement
        result = subprocess.run(args, input=statement, env=self.env, text=True,
                                capture_output=True, timeout=180)
        if result.returncode:
            raise AssertionError(f"{self.name} fixture/query failed (exit {result.returncode}); output withheld")
        return result.stdout.strip()

    def rows(self, role: str, table: str, *, target: bool = False):
        if self.name == "postgres":
            schema = "ingest_" + role if target else "public"
            statement = f"SELECT coalesce(json_agg(t),'[]'::json) FROM (SELECT * FROM {schema}.{table}) t;"
        else:
            statement = f"SELECT * FROM dbo.[{table}] FOR JSON PATH, INCLUDE_NULL_VALUES;"
        value = self.sql("datalake" if target else role, statement)
        if self.name == "sqlserver":
            value = "".join(value.splitlines())
        return normalized(json.loads(value or "[]"))

    def checkpoint(self):
        if self.name == "postgres":
            statement = "SELECT coalesce(json_agg(t),'[]'::json) FROM (SELECT lsn::text,rows_applied FROM sync.checkpoints WHERE source_name='financials') t;"
        else:
            statement = f"SELECT Version,SourceEpoch FROM etl.Checkpoints WHERE SourceName='{self.databases['financials']}' FOR JSON PATH;"
        return self.sql("datalake", statement)

    def observe(self):
        result, _ = self.cli("observe")
        for line in reversed(result.stdout.splitlines()):
            try:
                return json.loads(line)
            except json.JSONDecodeError:
                continue
        raise AssertionError("observe did not emit JSON")

    def sync(self, settings=None):
        return self.cli("sync", ["--seconds", "2"] if self.name == "postgres" else [], settings)

    def snapshot(self, settings=None):
        return self.cli("snapshot", ["--resnapshot"], settings) if self.name == "postgres" else self.cli("sync", ["--resnapshot"], settings)

    def projection(self):
        if self.name == "postgres":
            statement = "SELECT coalesce(json_agg(t),'[]'::json) FROM (SELECT operation_id AS id,account_id,account_name,account_status,description,amount,account_missing,transaction_total,transaction_count FROM reporting.account_operations) t;"
        else:
            statement = "SELECT Id AS id,AccountId AS account_id,AccountName AS account_name,AccountStatus AS account_status,Description AS description,Amount AS amount,AccountMissing AS account_missing FROM dbo.AccountOperations FOR JSON PATH, INCLUDE_NULL_VALUES;"
        value = self.sql("datalake", statement)
        if self.name == "sqlserver":
            value = "".join(value.splitlines())
        return normalized(json.loads(value or "[]"))

    def assert_equal(self):
        source = {}
        for role, tables in TABLES[self.name].items():
            for table in tables:
                rows = self.rows(role, table)
                mirror = self.rows(role, table, target=True)
                # Internal capture metadata is not part of the mirrored business contract.
                fields = set(rows[0]) if rows else set(mirror[0]) if mirror else set()
                mirror = [{key: value for key, value in row.items() if key in fields} for row in mirror]
                encode = lambda row: json.dumps(row, sort_keys=True)
                if sorted(map(encode, rows)) != sorted(map(encode, mirror)):
                    raise AssertionError(f"{self.name}: source/mirror mismatch for {table}")
                source[table.lower()] = rows
        accounts = {row["id"]: row for row in source["accounts"]}
        expected = []
        for operation in source["operations"]:
            account = accounts.get(operation.get("account_id", operation.get("accountid")))
            expected.append({"id": operation["id"], "account_id": operation.get("account_id", operation.get("accountid")),
                             "account_name": account["name"] if account else None,
                             "account_status": account["status"] if account else None,
                             "account_missing": account is None,
                             "description": operation["description"], "amount": operation["amount"]})
        if self.name == "postgres":
            for row in expected:
                transactions = [transaction for transaction in source["transactions"] if transaction["operation_id"] == row["id"]]
                row["transaction_total"] = sum(transaction["amount"] for transaction in transactions)
                row["transaction_count"] = len(transactions)
        encode = lambda row: json.dumps(row, sort_keys=True)
        if sorted(map(encode, expected)) != sorted(map(encode, self.projection())):
            raise AssertionError(f"{self.name}: joined physical reporting projection mismatch")

    def drain(self, settings=None, attempts=100):
        for attempt in range(attempts):
            self.sync(settings)
            try:
                self.assert_equal()
                return attempt + 1
            except AssertionError:
                if attempt == attempts - 1:
                    raise
        raise AssertionError("unreachable")

    def update_account(self, identifier: str, name: str):
        if self.name == "postgres":
            self.sql("accounts", f"UPDATE public.accounts SET name='{name}' WHERE id='{identifier}';")
        else:
            self.sql("accounts", f"UPDATE dbo.Accounts SET Name=N'{name}' WHERE Id='{identifier}';")

    def insert_operations(self, ids: list[str], account: str):
        values = ",\n".join(f"('{identifier}','{account}','synthetic operation',12.50)" for identifier in ids)
        if self.name == "postgres":
            self.sql("financials", f"INSERT INTO public.operations(id,account_id,description,amount) VALUES {values};")
        else:
            self.sql("financials", f"INSERT INTO dbo.Operations(Id,AccountId,Description,Amount) VALUES {values};")

    def fixtures(self):
        a, b, op, history, payment, transaction, order = [str(uuid.uuid4()) for _ in range(7)]
        if self.name == "postgres":
            self.sql("accounts", f"INSERT INTO public.accounts VALUES ('{a}','verification A','Active',now()),('{b}','verification B','Active',now()); INSERT INTO public.account_history VALUES ('{history}','{a}','existing historical row',now());")
            self.sql("financials", f"INSERT INTO public.operations(id,account_id,description,amount) VALUES ('{op}','{a}','verification original',10.25); INSERT INTO public.payment_means VALUES ('{payment}','synthetic payment'); INSERT INTO public.transactions(id,operation_id,payment_means_id,amount,occurred_at) VALUES ('{transaction}','{op}','{payment}',3.50,now()); INSERT INTO public.collection_orders VALUES ('{order}','{op}',now());")
        else:
            self.sql("accounts", f"INSERT INTO dbo.Accounts(Id,Name,Status,CreatedAt) VALUES ('{a}',N'verification A',N'Active',SYSDATETIMEOFFSET()),('{b}',N'verification B',N'Active',SYSDATETIMEOFFSET()); INSERT INTO dbo.AccountHistory(Id,AccountId,Description,OccurredAt) VALUES ('{history}','{a}',N'existing historical row',SYSDATETIMEOFFSET());")
            self.sql("financials", f"INSERT INTO dbo.Operations(Id,AccountId,Description,Amount) VALUES ('{op}','{a}',N'verification original',10.25); INSERT INTO dbo.PaymentMeans(Id,Name) VALUES ('{payment}',N'synthetic payment'); INSERT INTO dbo.Transactions(Id,OperationId,PaymentMeansId,Amount) VALUES ('{transaction}','{op}','{payment}',3.50); INSERT INTO dbo.CollectionOrders(Id,OperationId,DueAt) VALUES ('{order}','{op}',SYSDATETIMEOFFSET());")
        if self.name == "sqlserver":
            asset, contact = str(uuid.uuid4()), str(uuid.uuid4())
            self.sql("accounts", f"INSERT INTO dbo.Assets(Id,AccountId,Name,Kind,Value) VALUES ('{asset}','{a}',N'verification vehicle',N'vehicle',120.00); INSERT INTO dbo.ContactInformation(Id,AccountId,Email,Phone) VALUES ('{contact}','{a}',N'synthetic@example.test',N'0000000000');")
        return a, b, op, transaction, order


def verification(engine: Engine, record: dict):
    checks = record["checks"] = []
    def check(name, function):
        started = time.monotonic()
        try:
            detail = function()
        except Exception as error:
            checks.append({"name": name, "passed": False, "failure_type": type(error).__name__,
                           "detail": str(error) if isinstance(error, AssertionError) else "command output withheld"})
            raise
        checks.append({"name": name, "passed": True, "seconds": round(time.monotonic() - started, 3), "detail": detail})
        print(f"PASS {engine.name}: {name}", flush=True)

    engine.cli("bootstrap")
    a, b, op, transaction, order = engine.fixtures()

    def initial():
        settings = {"VWP_ETL_SNAPSHOT_HOLD_MS": "1200"} if engine.name == "postgres" else {"VWP_ETL_HOLD_SNAPSHOT_SECONDS": "2"}
        process = engine.start("snapshot", ["--resnapshot"], settings) if engine.name == "postgres" else engine.start("sync", ["--resnapshot"], settings)
        writes = 0
        deadline = time.monotonic() + 180
        while process.poll() is None:
            if time.monotonic() > deadline:
                process.kill()
                process.wait(timeout=10)
                raise AssertionError("concurrent snapshot exceeded its bounded deadline")
            engine.update_account(a, f"snapshot-concurrent-{writes}")
            writes += 1
            time.sleep(0.05)
        if process.returncode:
            raise AssertionError("concurrent snapshot failed")
        if writes < 2:
            raise AssertionError("snapshot finished before sufficient concurrent writes")
        engine.drain()
        return {"concurrent_source_writes": writes}
    check("initial snapshot with concurrent source writes and existing history", initial)

    def mutations():
        if engine.name == "sqlserver":
            engine.sql("accounts", f"UPDATE dbo.Assets SET Value=150.00 WHERE AccountId='{a}'; UPDATE dbo.ContactInformation SET Phone=N'1111111111' WHERE AccountId='{a}';")
        engine.update_account(a, "joined-account-update")
        engine.drain()
        if not any(row["id"] == op and row["account_name"] == "joined-account-update" for row in engine.projection()):
            raise AssertionError("unchanged operation did not reflect account update")
        if engine.name == "postgres":
            engine.sql("financials", f"UPDATE public.operations SET account_id='{b}',amount=99.25 WHERE id='{op}'; UPDATE public.transactions SET amount=4.50 WHERE id='{transaction}';")
        else:
            engine.sql("financials", f"UPDATE dbo.Operations SET AccountId='{b}',Amount=99.25 WHERE Id='{op}'; UPDATE dbo.Transactions SET Amount=4.50 WHERE Id='{transaction}';")
        engine.drain()
        second_op = str(uuid.uuid4())
        engine.insert_operations([second_op], a)
        if engine.name == "postgres":
            engine.sql("financials", f"UPDATE public.transactions SET operation_id='{second_op}' WHERE id='{transaction}';")
        else:
            engine.sql("financials", f"UPDATE dbo.Transactions SET OperationId='{second_op}' WHERE Id='{transaction}';")
        engine.drain()
        # A financial operation survives a missing Account; restore the join afterward.
        if engine.name == "postgres":
            engine.sql("accounts", f"DELETE FROM public.accounts WHERE id='{b}';")
        else:
            engine.sql("accounts", f"DELETE FROM dbo.Accounts WHERE Id='{b}';")
        engine.drain()
        if not any(row["id"] == op and row["account_missing"] for row in engine.projection()):
            raise AssertionError("operation disappeared when its Account was deleted")
        if engine.name == "postgres":
            engine.sql("accounts", f"INSERT INTO public.accounts VALUES ('{b}','restored account','Active',now());")
        else:
            engine.sql("accounts", f"INSERT INTO dbo.Accounts(Id,Name,Status,CreatedAt) VALUES ('{b}',N'restored account',N'Active',SYSDATETIMEOFFSET());")
        engine.drain()
        if engine.name == "postgres":
            engine.sql("financials", f"DELETE FROM public.collection_orders WHERE id='{order}'; DELETE FROM public.transactions WHERE id='{transaction}'; DELETE FROM public.operations WHERE id='{op}';")
        else:
            engine.sql("financials", f"DELETE FROM dbo.CollectionOrders WHERE Id='{order}'; DELETE FROM dbo.Transactions WHERE Id='{transaction}'; DELETE FROM dbo.Operations WHERE Id='{op}';")
        engine.drain()
        if any(row["id"] == op for row in engine.projection()):
            raise AssertionError("deleted operation remained in reporting")
        return {"account_join_change": True, "operation_reparent": True, "transaction_reparent": True, "dependent_delete": True}
    check("insert update delete reparent and joined-account invalidation", mutations)

    faults = ["before-target-commit", "after-target-commit-before-ack"] if engine.name == "postgres" else ["after-stage-commit", "before-apply-commit", "after-apply-commit", "before-checkpoint-commit", "after-checkpoint-commit"]
    def replay():
        for fault in faults:
            identifier = str(uuid.uuid4())
            engine.insert_operations([identifier], a)
            envkey = "VWP_ETL_FAILPOINT" if engine.name == "postgres" else "VWP_ETL_FAULT"
            before = engine.projection()
            engine.cli("sync", ["--seconds", "3"] if engine.name == "postgres" else [], {envkey: fault}, expected=1)
            if fault in ("before-target-commit", "before-apply-commit", "after-stage-commit") and sorted(engine.projection(), key=lambda row: row["id"]) != sorted(before, key=lambda row: row["id"]):
                raise AssertionError("uncommitted fault changed target projection")
            engine.drain()
            first = engine.projection()
            engine.sync()
            if sorted(first, key=lambda row: row["id"]) != sorted(engine.projection(), key=lambda row: row["id"]):
                raise AssertionError("replay changed already committed effects")
        return {"failpoints": faults}
    check("transaction faults restart and idempotent replay", replay)

    def bounded():
        ids = [str(uuid.uuid4()) for _ in range(12)]
        # Separate source commits allow PostgreSQL to honor transaction atomicity.
        for identifier in ids:
            engine.insert_operations([identifier], b)
        settings = {"VWP_ETL_MAX_ROWS": "2", "VWP_ETL_MAX_BYTES": "65536"}
        before_checkpoint = engine.checkpoint()
        if engine.name == "sqlserver":
            engine.sync(settings)
            observation = engine.observe()
            if observation.get("stagedRows") != 10 or observation.get("pendingWindows") != 1:
                raise AssertionError("two-row apply did not leave ten staged rows in its durable window")
            if engine.checkpoint() != before_checkpoint:
                raise AssertionError("partial window advanced its source checkpoint")
        batches = engine.drain(settings) + (1 if engine.name == "sqlserver" else 0)
        if engine.name == "sqlserver" and batches <= 1:
            raise AssertionError("two-row SQL batch did not require continuation")
        if engine.checkpoint() == before_checkpoint:
            raise AssertionError("completed batch did not advance financial checkpoint")
        return {"inserted_rows": len(ids), "row_limit": 2, "drain_invocations": batches}
    check("bounded batches continue to complete checkpoint", bounded)

    def budget_rejection():
        cases = ["byte"] + (["row"] if engine.name == "postgres" else [])
        for case in cases:
            before = sorted(engine.projection(), key=lambda row: row["id"])
            checkpoint = engine.checkpoint()
            if case == "row":
                engine.insert_operations([str(uuid.uuid4()) for _ in range(3)], a)
                settings = {"VWP_ETL_MAX_ROWS": "2", "VWP_ETL_MAX_BYTES": "65536"}
            else:
                identifier = str(uuid.uuid4())
                description = "é" * 500
                if engine.name == "postgres":
                    engine.sql("financials", f"INSERT INTO public.operations VALUES ('{identifier}','{a}','{description}',1.00);")
                else:
                    engine.sql("financials", f"INSERT INTO dbo.Operations VALUES ('{identifier}','{a}',N'{description}',1.00);")
                settings = {"VWP_ETL_MAX_BYTES": "1024"}
            rejected_commit = None
            if engine.name == "postgres":
                rejected_commit = engine.sql("financials", "SELECT pg_current_wal_lsn()::text;")
            engine.cli("sync", ["--seconds", "3"] if engine.name == "postgres" else [], settings, expected=1)
            after_checkpoint = engine.checkpoint()
            if engine.name == "sqlserver" and checkpoint != after_checkpoint:
                raise AssertionError("oversized source work advanced its durable checkpoint")
            if engine.name == "postgres":
                before_row, after_row = json.loads(checkpoint)[0], json.loads(after_checkpoint)[0]
                lsn = lambda value: (int(value.split("/")[0], 16) << 32) + int(value.split("/")[1], 16)
                if before_row["rows_applied"] != after_row["rows_applied"] or lsn(after_row["lsn"]) >= lsn(rejected_commit):
                    raise AssertionError("oversized source transaction advanced business progress past its commit")
                acknowledged = engine.sql("datalake", "SELECT confirmed_flush_lsn::text FROM pg_replication_slots WHERE slot_name=(SELECT slot_name FROM sync.checkpoints WHERE source_name='financials');")
                if lsn(acknowledged) >= lsn(rejected_commit):
                    raise AssertionError("oversized source transaction was acknowledged before target commit")
            if before != sorted(engine.projection(), key=lambda row: row["id"]):
                raise AssertionError("oversized source work partially changed reporting")
            engine.drain({"VWP_ETL_MAX_ROWS": "1000", "VWP_ETL_MAX_BYTES": "65536"})
        if engine.name == "postgres":
            engine.update_account(a, "fanout-budget-recovery")
            before = sorted(engine.projection(), key=lambda row: row["id"])
            engine.cli("sync", ["--seconds", "3"], {"VWP_ETL_MAX_PROJECTION_ROWS": "1"}, expected=1)
            if before != sorted(engine.projection(), key=lambda row: row["id"]):
                raise AssertionError("projection fanout rejection partially changed reporting")
            engine.drain({"VWP_ETL_MAX_PROJECTION_ROWS": "100000"})
            engine.update_account(a, "projection-byte-budget-recovery")
            before = sorted(engine.projection(), key=lambda row: row["id"])
            engine.cli("sync", ["--seconds", "3"], {"VWP_ETL_MAX_PROJECTION_BYTES": "1024"}, expected=1)
            if before != sorted(engine.projection(), key=lambda row: row["id"]):
                raise AssertionError("projection byte rejection partially changed reporting")
            engine.drain({"VWP_ETL_MAX_PROJECTION_BYTES": "1048576"})
        return {"atomic_oversized_rejections": cases, "larger_budget_recovered": True}
    check("oversized row byte and projection budgets fail atomically", budget_rejection)

    def writer():
        settings = {"VWP_ETL_LOCK_HOLD_MS": "4000"} if engine.name == "postgres" else {"VWP_ETL_HOLD_LOCK_SECONDS": "4"}
        process = engine.start("sync", ["--seconds", "6"] if engine.name == "postgres" else [], settings, pipe=True)
        try:
            deadline = time.monotonic() + 20
            held = False
            while time.monotonic() < deadline:
                readable, _, _ = select.select([process.stdout], [], [], 0.2)
                if readable:
                    line = process.stdout.readline()
                    if "writer-lock-held" in line:
                        held = True
                        break
                if process.poll() is not None:
                    break
            if not held:
                raise AssertionError("writer did not expose its held-lock checkpoint")
            engine.cli("sync", ["--seconds", "1"] if engine.name == "postgres" else [], expected=1)
            if process.wait(timeout=30):
                raise AssertionError("first writer failed")
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
        engine.drain()
        return {"second_writer_rejected": True}
    check("single writer excludes concurrent process", writer)

    def schema_drift():
        before = sorted(engine.projection(), key=lambda row: row["id"])
        checkpoint = engine.checkpoint()
        engine.sql("financials", "ALTER TABLE dbo.Operations ALTER COLUMN Amount decimal(18,3) NOT NULL;")
        identifier = str(uuid.uuid4())
        engine.sql("financials", f"INSERT INTO dbo.Operations VALUES ('{identifier}','{a}',N'precision guard',1.234);")
        engine.cli("sync", expected=1)
        if checkpoint != engine.checkpoint() or before != sorted(engine.projection(), key=lambda row: row["id"]):
            raise AssertionError("source precision drift advanced progress or published rounded data")
        # Restore the synthetic value explicitly before narrowing the source type.
        engine.sql("financials", f"UPDATE dbo.Operations SET Amount=1.23 WHERE Id='{identifier}'; ALTER TABLE dbo.Operations ALTER COLUMN Amount decimal(18,2) NOT NULL;")
        engine.snapshot()
        engine.drain()
        before = sorted(engine.projection(), key=lambda row: row["id"])
        checkpoint = engine.checkpoint()
        engine.sql("datalake", "ALTER TABLE dbo.Operations ALTER COLUMN Amount decimal(18,3) NOT NULL;")
        engine.cli("sync", expected=1)
        if checkpoint != engine.checkpoint() or before != sorted(engine.projection(), key=lambda row: row["id"]):
            raise AssertionError("target precision drift advanced progress or changed reporting")
        engine.sql("datalake", "ALTER TABLE dbo.Operations ALTER COLUMN Amount decimal(18,2) NOT NULL;")
        engine.snapshot()
        engine.drain()
        return {"source_and_target_precision_drift_rejected": True, "explicit_restore_resnapshot": True}
    if engine.name == "sqlserver":
        check("source and target precision drift fails before publishing", schema_drift)

    def postgres_mirror_precision_drift():
        checkpoint = engine.checkpoint()
        before = sorted(engine.projection(), key=lambda row: row["id"])
        engine.sql("datalake", "ALTER TABLE ingest_financials.operations ALTER COLUMN amount TYPE numeric(18,3);")
        engine.cli("sync", ["--seconds", "3"], expected=1)
        if checkpoint != engine.checkpoint() or before != sorted(engine.projection(), key=lambda row: row["id"]):
            raise AssertionError("mirror precision drift advanced progress or changed reporting")
        engine.sql("datalake", "ALTER TABLE ingest_financials.operations ALTER COLUMN amount TYPE numeric(18,2);")
        engine.snapshot()
        engine.drain()
        return {"mirror_precision_drift_rejected": True, "explicit_restore_resnapshot": True}
    if engine.name == "postgres":
        check("mirror precision drift fails before publishing", postgres_mirror_precision_drift)

    def reporting_precision_drift():
        if engine.name == "postgres":
            engine.sql("datalake", "ALTER TABLE reporting.account_operations ALTER COLUMN amount TYPE numeric(18,1);")
        else:
            engine.sql("datalake", "ALTER TABLE dbo.AccountOperations ALTER COLUMN Amount decimal(18,1) NOT NULL;")
        # The deliberate DDL may round existing fixture values itself. Save its
        # resulting report before checking that synchronization refuses new work.
        before = sorted(engine.projection(), key=lambda row: row["id"])
        checkpoint = engine.checkpoint()
        identifier = str(uuid.uuid4())
        if engine.name == "postgres":
            engine.sql("financials", f"INSERT INTO public.operations VALUES ('{identifier}','{a}','report precision guard',1.25);")
        else:
            engine.sql("financials", f"INSERT INTO dbo.Operations VALUES ('{identifier}','{a}',N'report precision guard',1.25);")
        engine.cli("sync", ["--seconds", "3"] if engine.name == "postgres" else [], expected=1)
        if checkpoint != engine.checkpoint() or before != sorted(engine.projection(), key=lambda row: row["id"]):
            raise AssertionError("report precision drift advanced progress or published rounded work")
        if engine.name == "postgres":
            engine.sql("datalake", "ALTER TABLE reporting.account_operations ALTER COLUMN amount TYPE numeric(18,2);")
        else:
            engine.sql("datalake", "ALTER TABLE dbo.AccountOperations ALTER COLUMN Amount decimal(18,2) NOT NULL;")
        engine.snapshot()
        engine.drain()
        return {"physical_report_precision_drift_rejected": True, "explicit_restore_resnapshot": True}
    check("reporting precision drift fails before publishing", reporting_precision_drift)

    def expiry():
        if engine.name == "postgres":
            slots = engine.sql("datalake", "SELECT slot_name FROM sync.checkpoints ORDER BY source_name;").splitlines()
            if not slots:
                raise AssertionError("no replication slots recorded")
            for slot in slots:
                if not re.fullmatch(r"[a-z0-9_]+", slot) or not slot.startswith(engine.env.get("VWP_POSTGRES_SLOT_PREFIX", "vwp_datalake")):
                    raise AssertionError("unexpected slot ownership")
            engine.sql("datalake", f"SELECT pg_drop_replication_slot('{slots[0]}');")
        else:
            engine.sql("accounts", "TRUNCATE TABLE dbo.AccountHistory;")
        engine.cli("sync", ["--seconds", "2"] if engine.name == "postgres" else [], expected=1)
        engine.snapshot()
        engine.drain()
        return {"missing_slot" if engine.name == "postgres" else "change_tracking_history_expiry": "detected; explicit resnapshot recovered"}
    check("source history identity unavailable explicit recovery", expiry)
    record["observation"] = engine.observe()
    record["passed"] = True
    return record


def parser(description=__doc__):
    result = argparse.ArgumentParser(description=description)
    result.add_argument("--engine", choices=("sqlserver", "postgres", "both"), default="both")
    result.add_argument("--env-file", type=Path)
    result.add_argument("--configuration", default="Release", choices=("Debug", "Release"))
    result.add_argument("--output", type=Path, required=True)
    return result


def main():
    args = parser().parse_args()
    destination = output_path(args.output)
    env = load_environment(args.env_file)
    result = {"schema_version": 1, "harness_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "started_utc": datetime.now(timezone.utc).isoformat(), "results": [],
              "scope": "isolated disposable native synchronization; original views require separate verification"}
    try:
        for name in ("sqlserver", "postgres") if args.engine == "both" else (args.engine,):
            engine = Engine(name, env, args.configuration)
            record = {"engine": name, "passed": False, "binary_sha256": engine.binary_sha256()}
            result["results"].append(record)
            verification(engine, record)
        result["passed"] = True
    except Exception as error:
        result["passed"] = False
        # Deliberately omit arbitrary exception messages and process output.
        result["failure_type"] = type(error).__name__
        if isinstance(error, AssertionError):
            result["failure_detail"] = str(error)
        print(f"FAIL: {type(error).__name__}; sensitive command output withheld", file=sys.stderr)
    destination.write_text(json.dumps(result, indent=2) + "\n")
    print(f"Evidence: {destination}")
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Exercise source writes, cross-service view reads, freshness, and read-only guards."""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid
from typing import Any


ACCOUNT_ID = os.environ.get("VWP_VERIFY_ACCOUNT_ID", "11111111-1111-4111-8111-111111111111")
ASSET_ID = os.environ.get("VWP_VERIFY_ASSET_ID", "22222222-2222-4222-8222-222222222222")
SERVICES = {
    "Financials": os.environ.get("FINANCIALS_URL", "http://127.0.0.1:5102"),
    "Cases": os.environ.get("CASES_URL", "http://127.0.0.1:5103"),
    "Notifications": os.environ.get("NOTIFICATIONS_URL", "http://127.0.0.1:5104"),
}
ACCOUNTS_URL = os.environ.get("ACCOUNTS_URL", "http://127.0.0.1:5101")


def request(
    base_url: str,
    path: str,
    *,
    method: str = "GET",
    payload: dict[str, Any] | None = None,
) -> tuple[int, dict[str, Any]]:
    body = None if payload is None else json.dumps(payload).encode("utf-8")
    request_message = urllib.request.Request(
        f"{base_url.rstrip('/')}{path}",
        data=body,
        method=method,
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request_message, timeout=10) as response:
            response_body = response.read()
            return response.status, json.loads(response_body) if response_body else {}
    except urllib.error.HTTPError as error:
        response_body = error.read()
        try:
            content = json.loads(response_body) if response_body else {}
        except json.JSONDecodeError:
            content = {"body": response_body.decode("utf-8", errors="replace")}
        return error.code, content


def expect_status(
    service: str,
    status: int,
    expected: int,
    body: dict[str, Any],
    operation: str,
) -> None:
    if status != expected:
        raise AssertionError(
            f"{service} {operation}: expected HTTP {expected}, got {status}: {body}"
        )


def expect_account(
    service: str,
    value: dict[str, Any],
    *,
    name: str,
    status: str,
    asset_name: str | None,
    asset_value: float | None,
    email: str | None,
    phone: str | None,
) -> None:
    if value.get("id") != ACCOUNT_ID:
        raise AssertionError(f"{service}: unexpected account id: {value}")
    if value.get("name") != name or value.get("status") != status:
        raise AssertionError(f"{service}: account fields are stale or mismatched: {value}")

    if asset_name is not None:
        assets = value.get("assets")
        if not assets:
            raise AssertionError(f"{service}: expected an asset in the view graph: {value}")
        asset = next((item for item in assets if item.get("id") == ASSET_ID), None)
        if asset is None or asset.get("name") != asset_name or asset.get("value") != asset_value:
            raise AssertionError(f"{service}: asset fields are stale or mismatched: {value}")

    if email is not None:
        contact = value.get("contactInformation")
        if contact is None or contact.get("email") != email or contact.get("phone") != phone:
            raise AssertionError(f"{service}: contact information is stale or mismatched: {value}")


def get_consumer_accounts() -> dict[str, dict[str, Any]]:
    result = {}
    for service, base_url in SERVICES.items():
        status, body = request(base_url, f"/accounts/{ACCOUNT_ID}")
        expect_status(service, status, 200, body, "view read")
        result[service] = body
    return result


def write_source_values(
    *,
    account_name: str,
    account_status: str,
    asset_name: str,
    asset_value: float,
    email: str,
    phone: str,
) -> None:
    writes = [
        (
            f"/accounts/{ACCOUNT_ID}",
            {"name": account_name, "status": account_status},
            "Account",
        ),
        (
            f"/accounts/{ACCOUNT_ID}/assets/{ASSET_ID}",
            {"name": asset_name, "kind": "vehicle", "value": asset_value},
            "Asset",
        ),
        (
            f"/accounts/{ACCOUNT_ID}/contact-information",
            {"email": email, "phone": phone},
            "ContactInformation",
        ),
    ]
    for path, payload, entity in writes:
        status, body = request(ACCOUNTS_URL, path, method="PUT", payload=payload)
        expect_status("Accounts", status, 200, body, f"{entity} write")


def main() -> None:
    uuid.UUID(ACCOUNT_ID)
    uuid.UUID(ASSET_ID)
    for service, base_url in {"Accounts": ACCOUNTS_URL, **SERVICES}.items():
        deadline = time.monotonic() + 30
        while True:
            try:
                status, body = request(base_url, "/health/ready")
                if status == 200:
                    break
            except (TimeoutError, OSError, urllib.error.URLError):
                pass
            if time.monotonic() >= deadline:
                raise RuntimeError(
                    f"{service} is not ready at {base_url}. Start scripts/local-forward.sh "
                    "and confirm scripts/local-up.sh completed."
                )
            time.sleep(1)

    write_source_values(
        account_name="VWP shared account v1",
        account_status="Active",
        asset_name="VWP shared asset v1",
        asset_value=120000.25,
        email="vwp-v1@example.test",
        phone="+54 11 5555-0101",
    )
    first_reads = get_consumer_accounts()
    for service, body in first_reads.items():
        expect_account(
            service,
            body,
            name="VWP shared account v1",
            status="Active",
            asset_name="VWP shared asset v1" if service in {"Financials", "Cases"} else None,
            asset_value=120000.25 if service in {"Financials", "Cases"} else None,
            email="vwp-v1@example.test" if service in {"Cases", "Notifications"} else None,
            phone="+54 11 5555-0101" if service in {"Cases", "Notifications"} else None,
        )

    write_source_values(
        account_name="VWP shared account v2",
        account_status="Verified",
        asset_name="VWP shared asset v2",
        asset_value=125000.75,
        email="vwp-v2@example.test",
        phone="+54 11 5555-0102",
    )
    second_reads = get_consumer_accounts()
    for service, body in second_reads.items():
        expect_account(
            service,
            body,
            name="VWP shared account v2",
            status="Verified",
            asset_name="VWP shared asset v2" if service in {"Financials", "Cases"} else None,
            asset_value=125000.75 if service in {"Financials", "Cases"} else None,
            email="vwp-v2@example.test" if service in {"Cases", "Notifications"} else None,
            phone="+54 11 5555-0102" if service in {"Cases", "Notifications"} else None,
        )

    for service, base_url in SERVICES.items():
        for operation in ("insert", "update", "delete"):
            path = f"/accounts/{ACCOUNT_ID}/write-probe/{operation}"
            status, body = request(base_url, path, method="POST", payload={})
            expect_status(service, status, 409, body, f"{operation} write probe")
            if body.get("blocked") is not True or body.get("guard") != "ef-save-changes":
                raise AssertionError(f"{service}: unexpected EF guard response: {body}")

        status, body = request(
            base_url,
            f"/accounts/{ACCOUNT_ID}/write-probe/execute-update",
            method="POST",
            payload={},
        )
        expect_status(service, status, 409, body, "bulk update permission probe")
        if body.get("blocked") is not True or body.get("guard") != "ef-view-mapping":
            raise AssertionError(f"{service}: unexpected EF view mapping response: {body}")

        for raw_operation in ("raw-update-view", "raw-update-source"):
            status, body = request(
                base_url,
                f"/accounts/{ACCOUNT_ID}/write-probe/{raw_operation}",
                method="POST",
                payload={},
            )
            expect_status(service, status, 409, body, f"{raw_operation} permission probe")
            if body.get("blocked") is not True or body.get("guard") != "sql-permissions":
                raise AssertionError(f"{service}: unexpected SQL permission response: {body}")

    after_probes = get_consumer_accounts()
    for service, body in after_probes.items():
        expect_account(
            service,
            body,
            name="VWP shared account v2",
            status="Verified",
            asset_name="VWP shared asset v2" if service in {"Financials", "Cases"} else None,
            asset_value=125000.75 if service in {"Financials", "Cases"} else None,
            email="vwp-v2@example.test" if service in {"Cases", "Notifications"} else None,
            phone="+54 11 5555-0102" if service in {"Cases", "Notifications"} else None,
        )

    print(
        "PASS: Accounts writes, first-read visibility, update visibility, EF INSERT/UPDATE/DELETE "
        "guards, EF view-mapping rejection, SQL raw-DML permissions, and unchanged consumer reads."
    )
    print(f"Account id: {ACCOUNT_ID}")
    print("Consumers: Financials, Cases, Notifications")


if __name__ == "__main__":
    try:
        main()
    except (AssertionError, RuntimeError, OSError, urllib.error.URLError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)

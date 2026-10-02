#!/usr/bin/env python3
"""Independent verifier for a PunchClock SQLite database.

Re-implements audit_hash from the spec in docs/design/audit-schema.md so an
auditor can check a copy of the database without the PunchClock app.

    python3 tools/verify_audit.py punchclock.db [--anchor AUDIT_ID:ROW_HASH ...]

Exit code 0 = all checks passed, 1 = tampering or inconsistency found.
"""
import argparse
import hashlib
import json
import sqlite3
import sys

GENESIS = "0" * 64

# Tables audited by triggers, mapped to their primary key column.
AUDITED = {
    "site_setting": "setting_id",
    "employee": "employee_id",
    "app_user": "user_id",
    "migration_batch": "batch_id",
    "legacy_row": "legacy_row_id",
    "migration_issue": "issue_id",
    "punch_correction": "correction_id",
    "punch": "punch_id",
    "app_event": "event_id",
}


def audit_hash(*args):
    """SHA-256 over a length-prefixed encoding of each argument (spec §5)."""
    h = hashlib.sha256()
    for a in args:
        if a is None:
            h.update(b"N;")
        elif isinstance(a, int):
            h.update(b"I" + str(a).encode() + b";")
        elif isinstance(a, str):
            b = a.encode("utf-8")
            h.update(b"T" + str(len(b)).encode() + b":" + b + b";")
        else:
            raise TypeError(f"audit_hash: unsupported type {type(a).__name__}")
    return h.hexdigest()


def register(conn):
    conn.create_function("audit_hash", -1, audit_hash, deterministic=True)


def verify(conn, anchors):
    errors, warnings = [], []
    rows = conn.execute(
        "SELECT audit_id, occurred_at_utc, txn_id, actor_kind, actor_user_id, actor_employee_id,"
        " device_id, app_version, table_name, operation, row_pk, before_json, after_json,"
        " prev_hash, row_hash FROM audit_log ORDER BY audit_id").fetchall()

    # 1. Chain: gapless ids, linked prev_hash, recomputed row_hash.
    prev, last_ts, by_id = GENESIS, None, {}
    for i, r in enumerate(rows, start=1):
        audit_id, ts, prev_hash, row_hash = r[0], r[1], r[13], r[14]
        if audit_id != i:
            errors.append(f"chain: expected audit_id {i}, found {audit_id} (rows deleted or inserted)")
            break
        if prev_hash != prev:
            errors.append(f"chain: audit_id {audit_id} prev_hash does not match row {i - 1}")
        if audit_hash(prev_hash, *r[:13]) != row_hash:
            errors.append(f"chain: audit_id {audit_id} content does not match its row_hash")
        if last_ts and ts < last_ts:
            warnings.append(f"clock: audit_id {audit_id} at {ts} is earlier than previous {last_ts}")
        prev, last_ts = row_hash, ts
        by_id[audit_id] = row_hash

    # 2. External anchors: the head the DB had when the anchor was taken must still be there.
    for a in anchors:
        aid, h = a.split(":", 1)
        if by_id.get(int(aid)) != h:
            errors.append(f"anchor: audit_id {aid} no longer has hash {h} (history rewritten or truncated)")

    # 3. Replay: last image in the log must equal every current row, and vice versa.
    for table in AUDITED:
        logged = dict(conn.execute(
            "SELECT row_pk, after_json FROM audit_log a WHERE table_name = ? AND audit_id ="
            " (SELECT MAX(audit_id) FROM audit_log b WHERE b.table_name = a.table_name"
            "  AND b.row_pk = a.row_pk)", (table,)).fetchall())
        current = dict(conn.execute(f"SELECT row_pk, j FROM audit_image_{table}").fetchall())
        for pk, j in current.items():
            if pk not in logged:
                errors.append(f"replay: {table} row {pk} exists but was never logged")
            elif logged[pk] != j:
                errors.append(f"replay: {table} row {pk} differs from its last logged image")
        for pk in logged.keys() - current.keys():
            errors.append(f"replay: {table} row {pk} was logged but is missing")

    # 4. Leftover context means a write path bypassed UnitOfWork.
    if conn.execute("SELECT COUNT(*) FROM audit_context").fetchone()[0]:
        errors.append("context: audit_context row left behind after commit")

    # 5. Schema fingerprint, to compare with the value shipped in the app build.
    ddl = conn.execute(
        "SELECT group_concat(name || ':' || sql, char(10)) FROM"
        " (SELECT name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type, name)").fetchone()[0]
    schema_fp = hashlib.sha256(ddl.encode()).hexdigest()
    head = (len(rows), prev)
    return errors, warnings, schema_fp, head


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("db")
    ap.add_argument("--anchor", action="append", default=[], help="AUDIT_ID:ROW_HASH from an external anchor")
    ap.add_argument("--schema-fp", help="expected schema fingerprint")
    args = ap.parse_args()
    conn = sqlite3.connect(f"file:{args.db}?mode=ro", uri=True)
    register(conn)
    errors, warnings, schema_fp, head = verify(conn, args.anchor)
    if args.schema_fp and schema_fp != args.schema_fp:
        errors.append(f"schema: fingerprint {schema_fp} != expected {args.schema_fp} (triggers or tables altered)")
    for w in warnings:
        print("WARN ", w)
    for e in errors:
        print("FAIL ", e)
    print(json.dumps({"ok": not errors, "head_audit_id": head[0], "head_hash": head[1], "schema_fp": schema_fp}))
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()

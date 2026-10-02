#!/usr/bin/env python3
"""Executable check of docs/database/schema.sql (Python 3.10+, stdlib only, SQLite >= 3.44).

Loads the schema into a scratch database, exercises every write rule, then plays the
tamper scenarios from the design doc and asserts the verifier catches each one.

    python3 docs/database/test_schema.py

Exit code 0 when every check passes.
"""
import datetime as dt
import hashlib
import os
import shutil
import sqlite3
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
SCHEMA = open(os.path.join(HERE, "schema.sql"), encoding="utf-8").read()
GENESIS = "0" * 64
VERIFY_VIEWS = ("verify_chain_v", "verify_drift_v", "verify_continuity_v")

failures = 0


def check(name, ok, detail=""):
    global failures
    print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail and not ok else ""))
    if not ok:
        failures += 1


# --- The two application-defined functions, as the app must register them -------------

def sha256_hex(text):
    return None if text is None else hashlib.sha256(text.encode("utf-8")).hexdigest()


class Session:
    """A connection with pc_sha256 and pc_ctx registered, like the app's connection factory."""

    def __init__(self, path, register=True):
        self.ctx = {}
        self.conn = sqlite3.connect(path, isolation_level=None)
        if register:
            self.conn.create_function("pc_sha256", 1, sha256_hex, deterministic=True)
            self.conn.create_function("pc_ctx", 1, self._ctx)
        self.conn.execute("PRAGMA foreign_keys = ON")

    def _ctx(self, name):
        if name in ("actor_kind", "actor_id", "client") and name not in self.ctx:
            raise ValueError(f"pc_ctx: {name} not set")
        return self.ctx.get(name)

    def act(self, kind, actor_id, reason=None):
        self.ctx = {"actor_kind": kind, "actor_id": actor_id, "client": "TEST-PC/1.0.0", "reason": reason}
        return self

    def x(self, sql, params=()):
        return self.conn.execute(sql, params)

    def one(self, sql, params=()):
        return self.conn.execute(sql, params).fetchone()

    def blocked(self, name, sql, params=(), expect=None):
        try:
            self.conn.execute(sql, params)
        except sqlite3.Error as e:
            check(name, expect is None or expect in str(e), str(e))
            return
        check(name, False, "write was accepted")

    def problems(self):
        return {v: self.conn.execute(f"SELECT * FROM {v}").fetchall() for v in VERIFY_VIEWS}

    def clean(self):
        return all(not rows for rows in self.problems().values())


def now_utc(delta_seconds=0):
    t = dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=delta_seconds)
    return t.strftime("%Y-%m-%dT%H:%M:%S.") + f"{t.microsecond // 1000:03d}Z"


def canonical(row):
    """Independent re-implementation of audit_canonical_v (no SQLite JSON involved)."""
    out = []
    for v in row:
        if v is None:
            out.append("N;")
        elif isinstance(v, int):
            out.append(f"I{v};")
        else:
            out.append(f"T{len(v.encode('utf-8'))}:{v};")
    return "".join(out)


def recompute_chain(s, patch):
    """What an attacker with the algorithm does: fix the audited image, rebuild every hash."""
    s.x("DROP TRIGGER audit_log_bu")
    table, row_id = patch
    s.x(f"UPDATE audit_log SET after_json = (SELECT j FROM {table}_snapshot_v WHERE id = ?)"
        " WHERE table_name = ? AND row_id = ?", (row_id, table, row_id))
    prev = GENESIS
    for row in s.x("SELECT seq, occurred_utc, actor_kind, actor_id, client, action, table_name, row_id,"
                   " before_json, after_json, reason FROM audit_log ORDER BY seq").fetchall():
        h = sha256_hex(canonical(list(row) + [prev]))
        s.x("UPDATE audit_log SET prev_hash = ?, row_hash = ? WHERE seq = ?", (prev, h, row[0]))
        prev = h


def main():
    print("SQLite", sqlite3.sqlite_version)
    work = tempfile.mkdtemp(prefix="punchclock-schema-")
    db = os.path.join(work, "punchclock.db")
    s = Session(db)
    s.x("PRAGMA journal_mode = WAL")

    # --- Bootstrap ---------------------------------------------------------------------
    s.act("user", 1, "initial schema")
    s.conn.executescript("BEGIN;\n" + SCHEMA + "\nCOMMIT;")
    check("schema applies in one transaction", s.one("PRAGMA user_version")[0] == 1)
    check("bootstrap is audited (2 service accounts + 2 settings)", s.one("SELECT count(*) FROM audit_log")[0] == 4)
    check("verifier clean after bootstrap", s.clean(), s.problems())

    s.act("user", 1).x("UPDATE site_setting SET value = 'Eastern Standard Time' WHERE key = 'time_zone_id'")
    s.x("INSERT INTO app_user (username, display_name, role, password_hash) VALUES ('owner', 'Owner', 'admin', 'pbkdf2-sha256$x')")
    admin = s.one("SELECT id FROM app_user WHERE username = 'owner'")[0]
    s.act("user", admin).x("INSERT INTO app_user (username, display_name, role, password_hash) VALUES ('mgr', 'Manager', 'manager', 'pbkdf2-sha256$y')")
    mgr = s.one("SELECT id FROM app_user WHERE username = 'mgr'")[0]
    s.blocked("second system account rejected",
              "INSERT INTO app_user (id, username, display_name, role) VALUES (9, 'sys2', 'x', 'system')")
    s.act("user", mgr).blocked("manager cannot create accounts",
                               "INSERT INTO app_user (username, display_name, role, password_hash) VALUES ('m2', 'x', 'manager', 'h')",
                               (), "not authorized")

    s.act("user", mgr, "new hire")
    s.x("INSERT INTO employee (first_name, last_name, pin_hash) VALUES ('Ana', 'Lopez', 'pbkdf2-sha256$a')")
    s.x("INSERT INTO employee (first_name, last_name, pin_hash) VALUES ('Ben', 'Okafor', 'pbkdf2-sha256$b')")
    s.x("INSERT INTO employee (first_name, last_name, pin_hash) VALUES ('Mia', 'Manager', 'pbkdf2-sha256$m')")
    ana, ben, mia = (r[0] for r in s.x("SELECT id FROM employee ORDER BY id"))
    s.act("user", admin).x("UPDATE app_user SET employee_id = ? WHERE id = ?", (mia, mgr))
    s.act("user", mgr).blocked("manager cannot promote self",
                               "UPDATE app_user SET role = 'admin' WHERE id = ?", (mgr,), "not authorized")
    s.act("user", admin).blocked("service-account role is immutable",
                                 "UPDATE app_user SET role = 'admin' WHERE id = 2", (), "immutable")

    # --- Kiosk punches -----------------------------------------------------------------
    t_in = now_utc()
    s.act("employee", ana).x("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source)"
                             " VALUES (?, 'IN', ?, -240, 'kiosk')", (ana, t_in))
    in_id = s.one("SELECT max(id) FROM punch")[0]
    check("kiosk IN accepted", in_id is not None)
    s.blocked("kiosk punch for someone else rejected",
              "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'IN', ?, -240, 'kiosk')",
              (ben, now_utc()), "punching employee")
    s.blocked("backdated kiosk punch rejected",
              "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'OUT', ?, -240, 'kiosk')",
              (ana, now_utc(-3600)), "current time")
    s.blocked("caller-supplied recorded_utc rejected",
              "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, recorded_utc) VALUES (?, 'OUT', ?, -240, 'kiosk', '2020-01-01T00:00:00.000Z')",
              (ana, now_utc()), "database clock")
    s.act("user", mgr).blocked("local (non-UTC) timestamp rejected",
                               "INSERT INTO punch_correction (action, employee_id, new_direction, new_occurred_utc, new_utc_offset_minutes, reason, actor_user_id)"
                               " VALUES ('add', ?, 'OUT', '2026-10-02 09:00:00', -240, 'local time entered by mistake', ?)",
                               (ana, mgr), "CHECK")
    s.act("employee", ana)
    s.x("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'OUT', ?, -240, 'kiosk')",
        (ana, now_utc()))
    out_id = s.one("SELECT max(id) FROM punch")[0]
    check("kiosk state from employee_status_v",
          s.one("SELECT last_direction FROM employee_status_v WHERE employee_id = ?", (ana,))[0] == "OUT")

    s.blocked("UPDATE punch rejected", "UPDATE punch SET occurred_utc = occurred_utc WHERE id = ?", (in_id,), "immutable")
    s.blocked("DELETE punch rejected", "DELETE FROM punch WHERE id = ?", (in_id,), "immutable")

    # --- Manager corrections -----------------------------------------------------------
    new_out = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=8)).strftime("%Y-%m-%dT%H:%M:%S.000Z")
    s.act("user", mgr)
    s.x("INSERT INTO punch_correction (action, employee_id, target_punch_id, new_direction, new_occurred_utc,"
        " new_utc_offset_minutes, reason, actor_user_id) VALUES ('adjust', ?, ?, 'OUT', ?, -240, ?, ?)",
        (ana, out_id, new_out, "Forgot to punch out; supervisor confirmed 8h shift", mgr))
    corr = s.one("SELECT id FROM punch_correction ORDER BY id DESC")[0]
    repl = s.one("SELECT id, occurred_utc, source FROM punch WHERE correction_id = ?", (corr,))
    check("adjust creates its replacement punch atomically", repl is not None and repl[1] == new_out and repl[2] == "correction")
    shift = s.one("SELECT duration_sec, status, out_punch_id FROM shift_v WHERE in_punch_id = ?", (in_id,))
    check("shift_v uses the corrected punch", shift[1] == "closed" and shift[2] == repl[0] and abs(shift[0] - 8 * 3600) < 5, shift)
    aud = s.one("SELECT before_json, after_json, reason, actor_kind, actor_id FROM audit_log"
                " WHERE table_name = 'punch_correction' AND row_id = ?", (corr,))
    check("correction audit row has before (target punch), after, reason and actor",
          f'"id":{out_id}' in aud[0] and new_out in aud[1] and aud[2].startswith("Forgot") and aud[3:] == ("user", mgr))
    check("replacement punch audit row carries the correction reason",
          s.one("SELECT reason FROM audit_log WHERE table_name = 'punch' AND row_id = ?", (repl[0],))[0].startswith("Forgot"))
    s.blocked("second correction of the same punch rejected",
              "INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'duplicate correction attempt', ?)",
              (ana, out_id, mgr), "UNIQUE")
    s.x("INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'Shift entered twice by mistake', ?)",
        (ana, repl[0], mgr))
    check("void of the replacement leaves the shift open",
          s.one("SELECT status FROM shift_v WHERE in_punch_id = ?", (in_id,))[0] == "open")
    s.x("INSERT INTO punch_correction (action, employee_id, new_direction, new_occurred_utc, new_utc_offset_minutes, reason, actor_user_id)"
        " VALUES ('add', ?, 'OUT', ?, -240, 'Missed punch, confirmed by timesheet', ?)", (ana, new_out, mgr))
    check("add closes the shift again", s.one("SELECT status FROM shift_v WHERE in_punch_id = ?", (in_id,))[0] == "closed")
    s.blocked("reason under 10 characters rejected",
              "INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'oops', ?)",
              (ana, in_id, mgr), "CHECK")
    s.blocked("actor_user_id must be the logged-in user",
              "INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'blame someone else', ?)",
              (ana, in_id, admin), "actor must be")
    s.act("employee", mia).x("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'IN', ?, -240, 'kiosk')",
                             (mia, now_utc()))
    mia_in = s.one("SELECT max(id) FROM punch")[0]
    s.act("user", mgr).blocked("manager cannot correct own punches",
                               "INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'not really here', ?)",
                               (mia, mia_in, mgr), "own punches")
    s.act("employee", ana).blocked("employee cannot issue corrections",
                                   "INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'self service edit', ?)",
                                   (ana, in_id, mgr), "actor must be")
    s.act("user", mgr).blocked("correction punch with other values rejected",
                               "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, correction_id) VALUES (?, 'OUT', ?, -240, 'correction', ?)",
                               (ana, now_utc(), corr), "does not match")
    s.blocked("second punch for one correction rejected",
              "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, correction_id) VALUES (?, 'OUT', ?, -240, 'correction', ?)",
              (ana, new_out, corr), "UNIQUE")

    # --- Employee self-service and deletes ---------------------------------------------
    s.act("employee", ben).x("UPDATE employee SET pin_hash = 'pbkdf2-sha256$b2' WHERE id = ?", (ben,))
    check("employee changes own PIN", s.one("SELECT pin_hash FROM employee WHERE id = ?", (ben,))[0].endswith("b2"))
    s.blocked("employee cannot rename self", "UPDATE employee SET last_name = 'X' WHERE id = ?", (ben,), "not authorized")
    s.blocked("employee cannot change another PIN", "UPDATE employee SET pin_hash = 'h' WHERE id = ?", (ana,), "not authorized")
    check("PIN hash never enters the log",
          s.one("SELECT count(*) FROM audit_log WHERE after_json LIKE '%pbkdf2%' OR before_json LIKE '%pbkdf2%'")[0] == 0)
    s.act("user", admin).blocked("DELETE employee rejected", "DELETE FROM employee WHERE id = ?", (ben,), "cannot be deleted")

    # --- Audit log protection ----------------------------------------------------------
    s.act("user", admin)
    s.blocked("UPDATE audit_log rejected", "UPDATE audit_log SET reason = 'x' WHERE seq = 5", (), "append-only")
    s.blocked("DELETE audit_log rejected", "DELETE FROM audit_log WHERE seq = (SELECT max(seq) FROM audit_log)", (), "append-only")
    s.blocked("forged hash on insert rejected",
              "INSERT INTO audit_log (actor_kind, actor_id, client, action, row_hash) VALUES ('user', ?, 'x', 'AUTH_LOGIN', 'abc')",
              (admin,), "seal")
    s.blocked("audit actor must match context",
              "INSERT INTO audit_log (actor_kind, actor_id, client, action) VALUES ('user', ?, 'x', 'AUTH_LOGIN')",
              (mgr,), "context")
    s.blocked("backdated audit row rejected",
              "INSERT INTO audit_log (occurred_utc, actor_kind, actor_id, client, action) VALUES ('2020-01-01T00:00:00.000Z', 'user', ?, 'x', 'AUTH_LOGIN')",
              (admin,), "database clock")
    s.blocked("explicit seq gap rejected",
              "INSERT INTO audit_log (seq, actor_kind, actor_id, client, action) VALUES (100000, 'user', ?, 'x', 'AUTH_LOGIN')",
              (admin,), "contiguous")
    s.x("INSERT INTO audit_log (actor_kind, actor_id, client, action, after_json) VALUES ('user', ?, 'TEST-PC/1.0.0', 'AUTH_LOGIN', json_object('username', 'owner'))",
        (admin,))
    check("app event is sealed on insert",
          s.one("SELECT row_hash IS NOT NULL FROM audit_log WHERE seq = (SELECT max(seq) FROM audit_log)")[0] == 1)

    # --- Legacy import (migration account) ---------------------------------------------
    s.act("user", 1).blocked("system account cannot import",
                             "INSERT INTO import_batch (source_file_name, source_sha256, manifest_sha256, source_time_zone_id, tool_version, manifest_employee_rows, manifest_shift_rows)"
                             " VALUES ('PunchClock.accdb', ?, ?, 'Eastern Standard Time', '1.0', 1, 2)", ("a" * 64, "b" * 64), "migration account")
    s.act("user", 2, "Legacy import from PunchClock.accdb")
    s.x("INSERT INTO import_batch (source_file_name, source_sha256, manifest_sha256, source_time_zone_id, tool_version, manifest_employee_rows, manifest_shift_rows)"
        " VALUES ('PunchClock.accdb', ?, ?, 'Eastern Standard Time', '1.0', 1, 2)", ("a" * 64, "b" * 64))
    batch = s.one("SELECT max(id) FROM import_batch")[0]
    ev = s.one("SELECT actor_id, after_json FROM audit_log WHERE table_name = 'import_batch' AND row_id = ?", (batch,))
    check("one import audit event carries source SHA-256 and manifest counts",
          ev[0] == 2 and "a" * 64 in ev[1] and '"manifest_shift_rows":2' in ev[1])
    s.x("INSERT INTO legacy_employee_raw (import_batch_id, legacy_employee_id, first_name, last_name, is_active, pin_digits) VALUES (?, 7, 'Old', 'Timer', 1, 3)", (batch,))
    s.x("INSERT INTO employee (legacy_id, first_name, last_name, pin_hash, pin_must_change) VALUES (7, 'Old', 'Timer', 'pbkdf2-sha256$o', 1)")
    old = s.one("SELECT id FROM employee WHERE legacy_id = 7")[0]
    s.x("INSERT INTO legacy_shift_raw (import_batch_id, legacy_shift_id, legacy_employee_id, time_in_local, time_in_oadate, time_out_local, time_out_oadate)"
        " VALUES (?, 41, 7, '2023-03-01T09:00:00.000', '44986.375', '2023-03-01T09:00:00.000', '44986.375')", (batch,))
    s.x("INSERT INTO migration_issue (import_batch_id, legacy_table, legacy_pk, code, disposition) VALUES (?, 'Shift', 41, 'DUMMY_SHIFT', 'SKIPPED')", (batch,))
    s.blocked("batch cannot close before raw rows match the manifest",
              "UPDATE import_batch SET completed_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = ?", (batch,), "manifest")
    s.x("INSERT INTO legacy_shift_raw (import_batch_id, legacy_shift_id, legacy_employee_id, time_in_local, time_in_oadate, time_out_local, time_out_oadate)"
        " VALUES (?, 42, 7, '2023-03-02T09:00:00.000', '44987.375', '2023-03-02T17:00:00.000', '44987.7083333333')", (batch,))
    for direction, ts in (("IN", "2023-03-02T14:00:00.000Z"), ("OUT", "2023-03-02T22:00:00.000Z")):
        s.x("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, import_batch_id, legacy_shift_id)"
            " VALUES (?, ?, ?, -300, 'legacy_import', ?, 42)", (old, direction, ts, batch))
    check("legacy shift keeps its ID and local wall time",
          s.one("SELECT in_local, out_local, duration_sec FROM shift_v WHERE employee_id = ?", (old,))
          == ("2023-03-02 09:00:00", "2023-03-02 17:00:00", 8 * 3600))
    s.x("UPDATE import_batch SET completed_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = ?", (batch,))
    check("batch closes once reconciled", s.one("SELECT completed_utc IS NOT NULL FROM import_batch WHERE id = ?", (batch,))[0] == 1)
    s.blocked("no imports into a closed batch",
              "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, import_batch_id, legacy_shift_id)"
              " VALUES (?, 'IN', '2023-03-03T14:00:00.000Z', -300, 'legacy_import', ?, 43)", (old, batch), "open batch")
    s.act("user", admin).blocked("admin cannot write legacy_import punches",
                                 "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, import_batch_id, legacy_shift_id)"
                                 " VALUES (?, 'IN', '2023-03-03T14:00:00.000Z', -300, 'legacy_import', ?, 43)", (old, batch), "migration account")
    s.x("UPDATE app_user SET is_active = 0 WHERE id = 2")
    check("migration account can be retired after import", s.one("SELECT is_active FROM app_user WHERE id = 2")[0] == 0)

    # --- Verifier on the intact database -----------------------------------------------
    check("verifier clean after a full day of activity", s.clean(), s.problems())
    rows = s.x("SELECT seq, occurred_utc, actor_kind, actor_id, client, action, table_name, row_id, before_json,"
               " after_json, reason, prev_hash, row_hash FROM audit_log ORDER BY seq").fetchall()
    prev, ok = GENESIS, True
    for r in rows:
        ok &= r[11] == prev and sha256_hex(canonical(r[:12])) == r[12]
        prev = r[12]
    check(f"chain of {len(rows)} rows recomputes in plain Python from the encoding spec", ok)
    fp = s.one("SELECT fingerprint FROM schema_fingerprint_v")[0]
    head_seq, head_hash = s.one("SELECT seq, row_hash FROM audit_log ORDER BY seq DESC LIMIT 1")
    s.act("user", admin).x("INSERT INTO audit_checkpoint (audit_seq, audit_hash, schema_fingerprint, key_id, signature) VALUES (?, ?, ?, 'k1', 'sig')",
                           (head_seq, head_hash, fp))
    s.blocked("checkpoint must match the chain",
              "INSERT INTO audit_checkpoint (audit_seq, audit_hash, schema_fingerprint, key_id, signature) VALUES (?, 'bad', ?, 'k1', 'sig')",
              (head_seq, fp), "does not match")
    anchor = (head_seq, head_hash)   # what was printed on the pay report / exported off the machine
    s.conn.close()

    # --- A tool without the app's functions --------------------------------------------
    raw = Session(db, register=False)
    check("generic tool can still read", raw.one("SELECT count(*) FROM punch")[0] > 0)
    raw.blocked("generic tool cannot write a punch",
                "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (1, 'IN', '2026-01-01T00:00:00.000Z', 0, 'kiosk')",
                (), "no such function")
    raw.blocked("generic tool cannot edit an employee", "UPDATE employee SET last_name = 'X' WHERE id = 1", (), "no such function")
    raw.conn.close()
    strict = Session(db)
    strict.act("user", admin).x("PRAGMA trusted_schema = OFF")
    strict.blocked("trusted_schema = OFF fails closed (app must set it ON)",
                   "UPDATE site_setting SET value = '12' WHERE key = 'max_shift_hours'", (), "unsafe use")
    strict.conn.close()

    # --- Tamper scenarios, each on a fresh copy -----------------------------------------
    def tampered(label):
        path = os.path.join(work, label + ".db")
        shutil.copy(db, path)
        t = Session(path)
        t.act("user", admin)
        return t

    def anchor_holds(t):
        r = t.one("SELECT row_hash FROM audit_log WHERE seq = ?", (anchor[0],))
        return r is not None and r[0] == anchor[1]

    t = tampered("edit_punch")
    t.x("DROP TRIGGER punch_bu")
    t.x("UPDATE punch SET occurred_utc = '2020-01-01T00:00:00.000Z' WHERE id = ?", (in_id,))
    check("edited punch (trigger dropped): schema fingerprint changes",
          t.one("SELECT fingerprint FROM schema_fingerprint_v")[0] != fp)
    check("edited punch (trigger dropped): drift detected",
          any(r[0] == "punch" and r[1] == in_id for r in t.problems()["verify_drift_v"]))

    t = tampered("edit_punch_restore_trigger")
    trig = t.one("SELECT sql FROM sqlite_schema WHERE name = 'punch_bu'")[0]
    t.x("DROP TRIGGER punch_bu")
    t.x("UPDATE punch SET occurred_utc = '2020-01-01T00:00:00.000Z' WHERE id = ?", (in_id,))
    t.x(trig)
    check("trigger restored: fingerprint matches again (so it alone is not enough)",
          t.one("SELECT fingerprint FROM schema_fingerprint_v")[0] == fp)
    check("trigger restored: drift still detected", bool(t.problems()["verify_drift_v"]))

    t = tampered("edit_audit")
    t.x("DROP TRIGGER audit_log_bu")
    t.x("UPDATE audit_log SET reason = 'Approved by owner' WHERE table_name = 'punch_correction'")
    check("edited audit row: chain break detected", bool(t.problems()["verify_chain_v"]))

    t = tampered("insert_bypass")
    t.x("DROP TRIGGER punch_bi")
    t.x("DROP TRIGGER punch_ai")
    t.x("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'IN', '2026-01-01T00:00:00.000Z', 0, 'kiosk')",
        (ben,))
    check("row inserted with audit trigger dropped: detected",
          any(r[2] == "row has no audit record" for r in t.problems()["verify_drift_v"]))

    t = tampered("launder")
    t.x("DROP TRIGGER employee_bu")
    t.x("DROP TRIGGER employee_au")
    t.x("UPDATE employee SET is_active = 0 WHERE id = ?", (ben,))
    t.x(s_trigger("employee_bu"))
    t.x(s_trigger("employee_au"))
    t.act("user", mgr).x("UPDATE employee SET first_name = 'Benjamin' WHERE id = ?", (ben,))
    p = t.problems()
    check("out-of-band edit laundered by a later real edit: drift misses it",
          not p["verify_drift_v"])
    check("out-of-band edit laundered by a later real edit: continuity catches it",
          any(r[2] == ben for r in p["verify_continuity_v"]))

    t = tampered("rewrite_chain")
    t.x("DROP TRIGGER punch_bu")
    t.x("UPDATE punch SET occurred_utc = '2020-01-01T00:00:00.000Z' WHERE id = ?", (in_id,))
    t.x(trig)
    recompute_chain(t, patch=("punch", in_id))
    check("chain rewrite: stored checkpoint no longer matches",
          any("checkpoint" in r[1] for r in t.problems()["verify_chain_v"]))

    t = tampered("rewrite_chain_full")
    t.x("DROP TRIGGER punch_bu")
    t.x("UPDATE punch SET occurred_utc = '2020-01-01T00:00:00.000Z' WHERE id = ?", (in_id,))
    t.x(trig)
    t.x("DROP TRIGGER audit_checkpoint_bd")
    t.x("DROP TRIGGER audit_log_bd")
    t.x("DELETE FROM audit_log WHERE table_name = 'audit_checkpoint'")
    t.x("DELETE FROM audit_checkpoint")
    t.x(s_trigger("audit_checkpoint_bd"))
    t.x(s_trigger("audit_log_bd"))
    recompute_chain(t, patch=("punch", in_id))
    t.x(s_trigger("audit_log_bu"))
    check("full rewrite incl. checkpoints: every internal check passes", t.clean(), t.problems())
    check("full rewrite incl. checkpoints: schema fingerprint unchanged",
          t.one("SELECT fingerprint FROM schema_fingerprint_v")[0] == fp)
    check("full rewrite incl. checkpoints: only the external anchor catches it", not anchor_holds(t))

    t = tampered("truncate_tail")
    t.x("DROP TRIGGER audit_log_bd")
    t.x("DROP TRIGGER audit_checkpoint_bd")
    t.x("DELETE FROM audit_checkpoint")
    t.x("DELETE FROM audit_log WHERE seq >= ?", (anchor[0],))
    check("tail truncation: anchor catches it", not anchor_holds(t))

    t = tampered("untouched")
    check("untouched copy: verifier clean and anchor holds", t.clean() and anchor_holds(t))

    shutil.rmtree(work, ignore_errors=True)
    print(f"\n{'OK' if not failures else str(failures) + ' FAILED'}")
    return 1 if failures else 0


def s_trigger(name):
    """Original trigger SQL from schema.sql, as an attacker would recreate it."""
    start = SCHEMA.index(f"CREATE TRIGGER {name} ")
    end = SCHEMA.index("\nEND;", start) + len("\nEND;")
    return SCHEMA[start:end]


if __name__ == "__main__":
    sys.exit(main())

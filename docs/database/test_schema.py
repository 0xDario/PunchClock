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
import zoneinfo

HERE = os.path.dirname(os.path.abspath(__file__))
SCHEMA = open(os.path.join(HERE, "schema.sql"), encoding="utf-8").read()
GENESIS = "0" * 64
VERIFY_VIEWS = ("verify_chain_v", "verify_drift_v", "verify_continuity_v", "verify_history_v", "verify_rules_v")
# Windows zone ids the tests use, mapped to IANA for zoneinfo. .NET resolves the Windows id directly.
WINDOWS_ZONES = {"Eastern Standard Time": "America/Toronto"}

failures = 0


def check(name, ok, detail=""):
    global failures
    print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail and not ok else ""))
    if not ok:
        failures += 1


# --- The three application-defined functions, as the app must register them -----------

def sha256_hex(text):
    return None if text is None else hashlib.sha256(text.encode("utf-8")).hexdigest()


def utc_offset(zone_id, utc):
    """pc_utc_offset: minutes east of UTC for a Windows zone id at a UTC instant; NULL if unknown."""
    if zone_id not in WINDOWS_ZONES or utc is None:
        return None
    t = dt.datetime.strptime(utc, "%Y-%m-%dT%H:%M:%S.%fZ").replace(tzinfo=dt.timezone.utc)
    return int(t.astimezone(zoneinfo.ZoneInfo(WINDOWS_ZONES[zone_id])).utcoffset().total_seconds() // 60)


def site_offset(utc):
    return utc_offset("Eastern Standard Time", utc)


class Session:
    """A connection with pc_sha256, pc_ctx and pc_utc_offset registered, like the app's connection factory."""

    def __init__(self, path, register=True):
        self.ctx = {}
        self.conn = sqlite3.connect(path, isolation_level=None)
        if register:
            self.conn.create_function("pc_sha256", 1, sha256_hex, deterministic=True)
            self.conn.create_function("pc_ctx", 1, self._ctx)
            self.conn.create_function("pc_utc_offset", 2, utc_offset)
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

    s.act("user", 1).blocked("unknown time zone id rejected",
                             "UPDATE site_setting SET value = 'Mars/Olympus_Mons' WHERE key = 'time_zone_id'", (), "known Windows time zone")
    s.x("UPDATE site_setting SET value = 'Eastern Standard Time' WHERE key = 'time_zone_id'")
    for bad in ("0", "abc", "100", "08", "-4", "12.5"):
        s.blocked(f"max_shift_hours '{bad}' rejected",
                  "UPDATE site_setting SET value = ? WHERE key = 'max_shift_hours'", (bad,), "whole number from 1 to 48")
    s.x("UPDATE site_setting SET value = '14' WHERE key = 'max_shift_hours'")
    check("max_shift_hours accepts a valid value", s.one("SELECT value FROM site_setting WHERE key = 'max_shift_hours'")[0] == "14")
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
    s.blocked("last active admin cannot deactivate self", "UPDATE app_user SET is_active = 0 WHERE id = ?", (admin,), "last active admin")
    s.blocked("last active admin cannot demote self", "UPDATE app_user SET role = 'manager' WHERE id = ?", (admin,), "last active admin")
    s.x("INSERT INTO app_user (username, display_name, role, password_hash) VALUES ('admin2', 'Second Admin', 'admin', 'pbkdf2-sha256$z')")
    admin2 = s.one("SELECT id FROM app_user WHERE username = 'admin2'")[0]
    s.x("UPDATE app_user SET is_active = 0 WHERE id = ?", (admin2,))
    check("an admin can be deactivated while another stays active", s.one("SELECT is_active FROM app_user WHERE id = ?", (admin2,))[0] == 0)

    # --- Kiosk punches -----------------------------------------------------------------
    t_in = now_utc()
    OFF = site_offset(t_in)
    s.act("employee", ana).x("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source)"
                             f" VALUES (?, 'IN', ?, {OFF}, 'kiosk')", (ana, t_in))
    in_id = s.one("SELECT max(id) FROM punch")[0]
    check("kiosk IN accepted", in_id is not None)
    s.blocked("kiosk punch for someone else rejected",
              f"INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'IN', ?, {OFF}, 'kiosk')",
              (ben, now_utc()), "punching employee")
    s.blocked("backdated kiosk punch rejected",
              f"INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'OUT', ?, {OFF}, 'kiosk')",
              (ana, now_utc(-3600)), "current time")
    s.blocked("caller-supplied recorded_utc rejected",
              f"INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, recorded_utc) VALUES (?, 'OUT', ?, {OFF}, 'kiosk', '2020-01-01T00:00:00.000Z')",
              (ana, now_utc()), "database clock")
    s.act("user", mgr).blocked("local (non-UTC) timestamp rejected",
                               "INSERT INTO punch_correction (action, employee_id, new_direction, new_occurred_utc, new_utc_offset_minutes, reason, actor_user_id)"
                               " VALUES ('add', ?, 'OUT', '2026-10-02 09:00:00', -240, 'local time entered by mistake', ?)",
                               (ana, mgr), "CHECK")
    s.act("employee", ana)
    s.x(f"INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'OUT', ?, {OFF}, 'kiosk')",
        (ana, now_utc()))
    out_id = s.one("SELECT max(id) FROM punch")[0]
    check("kiosk state from employee_status_v",
          s.one("SELECT last_direction FROM employee_status_v WHERE employee_id = ?", (ana,))[0] == "OUT")

    s.blocked("UPDATE punch rejected", "UPDATE punch SET occurred_utc = occurred_utc WHERE id = ?", (in_id,), "immutable")
    s.blocked("DELETE punch rejected", "DELETE FROM punch WHERE id = ?", (in_id,), "immutable")

    # --- Manager corrections -----------------------------------------------------------
    new_in = (dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=8)).strftime("%Y-%m-%dT%H:%M:%S.000Z")
    off_in = site_offset(new_in)
    s.act("user", mgr)
    s.x("INSERT INTO punch_correction (action, employee_id, target_punch_id, new_direction, new_occurred_utc,"
        " new_utc_offset_minutes, reason, actor_user_id) VALUES ('adjust', ?, ?, 'IN', ?, ?, ?, ?)",
        (ana, in_id, new_in, off_in, "Punched in late at the kiosk; supervisor confirmed 8h shift", mgr))
    corr = s.one("SELECT id FROM punch_correction ORDER BY id DESC")[0]
    repl = s.one("SELECT id, occurred_utc, source FROM punch WHERE correction_id = ?", (corr,))
    check("adjust creates its replacement punch atomically", repl is not None and repl[1] == new_in and repl[2] == "correction")
    shift = s.one("SELECT duration_sec, status, out_punch_id FROM shift_v WHERE in_punch_id = ?", (repl[0],))
    check("shift_v uses the corrected punch", shift[1] == "closed" and shift[2] == out_id and abs(shift[0] - 8 * 3600) < 5, shift)
    aud = s.one("SELECT before_json, after_json, reason, actor_kind, actor_id FROM audit_log"
                " WHERE table_name = 'punch_correction' AND row_id = ?", (corr,))
    check("correction audit row has before (target punch), after, reason and actor",
          f'"id":{in_id}' in aud[0] and new_in in aud[1] and aud[2].startswith("Punched") and aud[3:] == ("user", mgr))
    check("replacement punch audit row carries the correction reason",
          s.one("SELECT reason FROM audit_log WHERE table_name = 'punch' AND row_id = ?", (repl[0],))[0].startswith("Punched"))
    s.blocked("second correction of the same punch rejected",
              "INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'duplicate correction attempt', ?)",
              (ana, in_id, mgr), "UNIQUE")
    s.x("INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'Shift entered twice by mistake', ?)",
        (ana, repl[0], mgr))
    check("void of the replacement leaves the OUT without an IN",
          s.one("SELECT count(*) FROM punch_exception_v WHERE punch_id = ? AND kind = 'missing_in'", (out_id,))[0] == 1)
    s.x("INSERT INTO punch_correction (action, employee_id, new_direction, new_occurred_utc, new_utc_offset_minutes, reason, actor_user_id)"
        " VALUES ('add', ?, 'IN', ?, ?, 'Missed punch, confirmed by timesheet', ?)", (ana, new_in, off_in, mgr))
    check("add closes the shift again", s.one("SELECT status FROM shift_v WHERE out_punch_id = ?", (out_id,))[0] == "closed")
    future = now_utc(3600)
    s.blocked("correction into the future rejected",
              "INSERT INTO punch_correction (action, employee_id, new_direction, new_occurred_utc, new_utc_offset_minutes, reason, actor_user_id)"
              " VALUES ('add', ?, 'OUT', ?, ?, 'pre-entering tomorrow''s shift', ?)", (ana, future, site_offset(future), mgr), "in the future")
    s.blocked("correction with an offset that is not the site zone rejected",
              "INSERT INTO punch_correction (action, employee_id, new_direction, new_occurred_utc, new_utc_offset_minutes, reason, actor_user_id)"
              " VALUES ('add', ?, 'OUT', ?, ?, 'offset typed by hand wrongly', ?)", (ana, new_in, off_in + 60, mgr), "time zone")
    s.blocked("reason under 10 characters rejected",
              "INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'oops', ?)",
              (ana, out_id, mgr), "CHECK")
    s.blocked("actor_user_id must be the logged-in user",
              "INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'blame someone else', ?)",
              (ana, out_id, admin), "actor must be")
    s.act("employee", mia).x(f"INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'IN', ?, {OFF}, 'kiosk')",
                             (mia, now_utc()))
    mia_in = s.one("SELECT max(id) FROM punch")[0]
    s.blocked("kiosk punch with an offset that is not the site zone rejected",
              "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'OUT', ?, ?, 'kiosk')",
              (mia, now_utc(), OFF + 60), "time zone")
    s.blocked("kiosk punch in the future rejected",
              "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'OUT', ?, ?, 'kiosk')",
              (mia, now_utc(60), OFF), "in the future")
    s.act("user", mgr).blocked("manager cannot correct own punches",
                               "INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'not really here', ?)",
                               (mia, mia_in, mgr), "own punches")
    s.act("employee", ana).blocked("employee cannot issue corrections",
                                   "INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id) VALUES ('void', ?, ?, 'self service edit', ?)",
                                   (ana, out_id, mgr), "actor must be")
    s.act("user", mgr).blocked("correction punch with other values rejected",
                               "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, correction_id) VALUES (?, 'OUT', ?, ?, 'correction', ?)",
                               (ana, now_utc(), OFF, corr), "does not match")
    s.blocked("second punch for one correction rejected",
              "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, correction_id) VALUES (?, 'IN', ?, ?, 'correction', ?)",
              (ana, new_in, off_in, corr), "UNIQUE")

    # --- Employee self-service and deletes ---------------------------------------------
    s.act("employee", ben).x("UPDATE employee SET pin_hash = 'pbkdf2-sha256$b2' WHERE id = ?", (ben,))
    check("employee changes own PIN", s.one("SELECT pin_hash FROM employee WHERE id = ?", (ben,))[0].endswith("b2"))
    s.blocked("employee self-service must change the PIN",
              "UPDATE employee SET pin_must_change = 0 WHERE id = ?", (ben,), "not authorized")
    s.blocked("employee cannot rename self", "UPDATE employee SET last_name = 'X' WHERE id = ?", (ben,), "not authorized")
    s.blocked("employee cannot change another PIN", "UPDATE employee SET pin_hash = 'h' WHERE id = ?", (ana,), "not authorized")
    check("PIN hash never enters the log",
          s.one("SELECT count(*) FROM audit_log WHERE after_json LIKE '%pbkdf2%' OR before_json LIKE '%pbkdf2%'")[0] == 0)
    s.act("user", mgr).x("INSERT INTO employee (first_name, last_name, pin_hash) VALUES ('Zoe', 'Former', 'pbkdf2-sha256$z')")
    zoe = s.one("SELECT max(id) FROM employee")[0]
    s.x("UPDATE employee SET is_active = 0 WHERE id = ?", (zoe,))
    s.act("employee", zoe).blocked("deactivated employee cannot change own PIN",
                                   "UPDATE employee SET pin_hash = 'pbkdf2-sha256$z2' WHERE id = ?", (zoe,), "not authorized")
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
    s.blocked("audit client must match context",
              "INSERT INTO audit_log (actor_kind, actor_id, client, action) VALUES ('user', ?, 'OTHER-PC/9.9', 'AUTH_LOGIN')",
              (admin,), "client does not match")
    s.blocked("backdated audit row rejected",
              "INSERT INTO audit_log (occurred_utc, actor_kind, actor_id, client, action) VALUES ('2020-01-01T00:00:00.000Z', 'user', ?, 'TEST-PC/1.0.0', 'AUTH_LOGIN')",
              (admin,), "database clock")
    s.blocked("explicit seq gap rejected",
              "INSERT INTO audit_log (seq, actor_kind, actor_id, client, action) VALUES (100000, 'user', ?, 'TEST-PC/1.0.0', 'AUTH_LOGIN')",
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
        " VALUES ('PunchClock.accdb', ?, ?, 'Eastern Standard Time', '1.0', 1, 4)", ("a" * 64, "b" * 64))
    batch = s.one("SELECT max(id) FROM import_batch")[0]
    ev = s.one("SELECT actor_id, after_json FROM audit_log WHERE table_name = 'import_batch' AND row_id = ?", (batch,))
    check("one import audit event carries source SHA-256 and manifest counts",
          ev[0] == 2 and "a" * 64 in ev[1] and '"manifest_shift_rows":4' in ev[1])
    s.x("INSERT INTO legacy_employee_raw (import_batch_id, legacy_employee_id, first_name, last_name, is_active, pin_digits) VALUES (?, 7, 'Old', 'Timer', 1, 3)", (batch,))
    s.blocked("imported employee must start with a forced PIN reset",
              "INSERT INTO employee (legacy_id, first_name, last_name, pin_hash) VALUES (7, 'Old', 'Timer', 'pbkdf2-sha256$o')",
              (), "pin_must_change = 1")
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
          s.one("SELECT in_local, out_local, duration_sec FROM shift_v WHERE employee_id = ?"
                " AND in_punch_id IN (SELECT id FROM punch WHERE legacy_shift_id = 42)", (old,))
          == ("2023-03-02 09:00:00", "2023-03-02 17:00:00", 8 * 3600))
    imp = ("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, import_batch_id, legacy_shift_id)"
           " VALUES (?, ?, ?, ?, 'legacy_import', ?, ?)")
    s.blocked("import punch for a shift not in the raw export rejected", imp,
              (old, "IN", "2023-03-05T14:00:00.000Z", -300, batch, 99), "raw legacy shift")
    s.blocked("import punch assigned to the wrong employee rejected", imp,
              (ana, "IN", "2023-03-02T14:00:00.000Z", -300, batch, 41), "raw legacy shift")
    s.blocked("import punch at a time other than the raw shift rejected", imp,
              (old, "IN", "2023-03-01T15:00:00.000Z", -300, batch, 41), "raw legacy shift")
    s.blocked("import punch with an offset that is not the source zone rejected", imp,
              (old, "IN", "2023-03-01T13:00:00.000Z", -240, batch, 41), "time zone")
    s.x("INSERT INTO legacy_shift_raw (import_batch_id, legacy_shift_id, legacy_employee_id, time_in_local, time_in_oadate, time_out_local, time_out_oadate)"
        " VALUES (?, 43, 7, '2023-03-12T02:30:00.000', '44997.1041666667', '2023-03-12T10:00:00.000', '44997.4166666667')", (batch,))
    s.x("INSERT INTO legacy_shift_raw (import_batch_id, legacy_shift_id, legacy_employee_id, time_in_local, time_in_oadate, time_out_local, time_out_oadate)"
        " VALUES (?, 44, 7, '2023-11-05T01:30:00.000', '45235.0625', '2023-11-05T09:00:00.000', '45235.375')", (batch,))
    s.blocked("batch cannot close while a raw shift has no punches or SKIPPED issue",
              "UPDATE import_batch SET completed_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = ?", (batch,), "SKIPPED issue")
    s.blocked("spring-forward shift moved one hour needs its DST_INVALID issue first", imp,
              (old, "IN", "2023-03-12T07:30:00.000Z", -240, batch, 43), "raw legacy shift")
    s.x("INSERT INTO migration_issue (import_batch_id, legacy_table, legacy_pk, code, disposition) VALUES (?, 'Shift', 43, 'DST_INVALID', 'IMPORTED_FLAGGED')", (batch,))
    s.x(imp, (old, "IN", "2023-03-12T07:30:00.000Z", -240, batch, 43))
    s.x(imp, (old, "OUT", "2023-03-12T14:00:00.000Z", -240, batch, 43))
    s.blocked("ambiguous fall-back time must take the earlier (daylight) occurrence", imp,
              (old, "IN", "2023-11-05T06:30:00.000Z", -300, batch, 44), "earlier (daylight)")
    s.x("INSERT INTO migration_issue (import_batch_id, legacy_table, legacy_pk, code, disposition) VALUES (?, 'Shift', 44, 'DST_AMBIGUOUS', 'IMPORTED_FLAGGED')", (batch,))
    s.x(imp, (old, "IN", "2023-11-05T05:30:00.000Z", -240, batch, 44))
    s.x(imp, (old, "OUT", "2023-11-05T14:00:00.000Z", -300, batch, 44))
    check("ambiguous fall-back shift imports at the daylight occurrence (8.5 h)",
          s.one("SELECT duration_sec FROM shift_v WHERE in_punch_id IN (SELECT id FROM punch WHERE legacy_shift_id = 44)")[0]
          == 8.5 * 3600)
    check("spring-forward shift imports one hour later once DST_INVALID is recorded",
          s.one("SELECT in_local FROM shift_v WHERE in_punch_id IN (SELECT id FROM punch WHERE legacy_shift_id = 43)")[0]
          == "2023-03-12 03:30:00")
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
    s.act("employee", old).blocked("PIN reset flag cannot be cleared without a new PIN",
                                   "UPDATE employee SET pin_must_change = 0 WHERE id = ?", (old,), "not authorized")
    s.x("UPDATE employee SET pin_hash = 'pbkdf2-sha256$new', pin_must_change = 0 WHERE id = ?", (old,))
    check("PIN reset completes with a new PIN", s.one("SELECT pin_must_change FROM employee WHERE id = ?", (old,))[0] == 0)
    s.act("user", 1).x("UPDATE app_user SET employee_id = ? WHERE id = ?", (old, admin))
    s.act("user", admin).blocked("admin cannot unlink own employee record",
                                 "UPDATE app_user SET employee_id = NULL WHERE id = ?", (admin,), "own employee link")
    s.blocked("admin cannot correct own punches",
              "INSERT INTO punch_correction (action, employee_id, new_direction, new_occurred_utc, new_utc_offset_minutes, reason, actor_user_id)"
              " VALUES ('add', ?, 'OUT', '2023-03-03T22:00:00.000Z', -300, 'adding my own missed punch', ?)", (old, admin), "own punches")
    s.act("user", mgr).blocked("caller-supplied created_utc rejected",
                               "INSERT INTO employee (first_name, last_name, pin_hash, created_utc) VALUES ('Back', 'Dated', 'h', '2020-01-01T00:00:00.000Z')",
                               (), "database clock")

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
    s.blocked("checkpoint of an older position rejected",
              "INSERT INTO audit_checkpoint (audit_seq, audit_hash, schema_fingerprint, key_id, signature)"
              " SELECT seq, row_hash, ?, 'k1', 'sig' FROM audit_log WHERE seq = 5", (fp,), "current chain head")
    s.blocked("checkpoint with a stale schema fingerprint rejected",
              "INSERT INTO audit_checkpoint (audit_seq, audit_hash, schema_fingerprint, key_id, signature)"
              " SELECT seq, row_hash, ?, 'k1', 'sig' FROM audit_log ORDER BY seq DESC LIMIT 1", ("0" * 64,), "current schema")
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

    # --- A fresh install before the site time zone is set -------------------------------
    fresh = Session(os.path.join(work, "fresh.db"))
    fresh.act("user", 1, "initial schema")
    fresh.conn.executescript("BEGIN;\n" + SCHEMA + "\nCOMMIT;")
    fresh.x("INSERT INTO employee (first_name, last_name, pin_hash) VALUES ('Early', 'Bird', 'h')")
    fresh.act("employee", 1).blocked("no punches until the site time zone is configured",
                                     "INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (1, 'IN', ?, 0, 'kiosk')",
                                     (now_utc(),), "not configured")
    fresh.conn.close()

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
    check("out-of-band edit: drift flags it", any(r[1] == ben for r in t.problems()["verify_drift_v"]))
    t.act("user", mgr).blocked("out-of-band edit cannot be laundered by a later real edit",
                               "UPDATE employee SET first_name = 'Benjamin' WHERE id = ?", (ben,), "previous audited image")
    t.x("DROP TRIGGER audit_log_bi")
    t.x("UPDATE employee SET first_name = 'Benjamin' WHERE id = ?", (ben,))
    t.x(s_trigger("audit_log_bi"))
    p = t.problems()
    check("laundered with the guard dropped: drift misses it", not p["verify_drift_v"])
    check("laundered with the guard dropped: continuity catches it",
          any(r[2] == ben for r in p["verify_continuity_v"]))

    # Codex review: fabricated table events and row-id reuse.
    t = tampered("forge_update")
    t.x("DROP TRIGGER punch_bu")
    t.x("UPDATE punch SET occurred_utc = '2020-01-01T00:00:00.000Z' WHERE id = ?", (in_id,))
    t.x(trig)
    prev_img = t.one("SELECT after_json FROM audit_log WHERE table_name = 'punch' AND row_id = ?", (in_id,))[0]
    t.blocked("fabricated UPDATE event on a punch rejected",
              "INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json)"
              " SELECT 'user', ?, 'TEST-PC/1.0.0', 'UPDATE', 'punch', ?, ?, j FROM punch_snapshot_v WHERE id = ?",
              (admin, in_id, prev_img, in_id), "only for mutable tables")

    t = tampered("forge_insert")
    t.x("DROP TRIGGER punch_bi")
    t.x("DROP TRIGGER punch_ai")
    t.x("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, recorded_utc)"
        " VALUES (?, 'IN', '2026-01-01T13:00:00.000Z', -300, 'kiosk', '2026-01-01T13:00:00.000Z')", (ben,))
    forged = t.one("SELECT max(id) FROM punch")[0]
    t.x(s_trigger("punch_bi"))
    t.x(s_trigger("punch_ai"))
    t.act("employee", ben).blocked("fabricated INSERT event for a backdated punch rejected",
                                   "INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, after_json)"
                                   " SELECT 'employee', ?, 'TEST-PC/1.0.0', 'INSERT', 'punch', id, j FROM punch_snapshot_v WHERE id = ?",
                                   (ben, forged), "not created in this statement")

    t = tampered("forge_immutable")
    t.x("DROP TRIGGER employee_bu")
    t.x("DROP TRIGGER employee_au")
    before_img = t.one("SELECT j FROM employee_snapshot_v WHERE id = ?", (ben,))[0]
    t.x("UPDATE employee SET created_utc = '2020-01-01T00:00:00.000Z' WHERE id = ?", (ben,))
    t.x(s_trigger("employee_bu"))
    t.x(s_trigger("employee_au"))
    t.blocked("fabricated UPDATE changing an immutable field rejected",
              "INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json)"
              " SELECT 'user', ?, 'TEST-PC/1.0.0', 'UPDATE', 'employee', id, ?, j FROM employee_snapshot_v WHERE id = ?",
              (admin, before_img, ben), "immutable field")
    t.blocked("fabricated event with an image that is not the row rejected",
              "INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json)"
              " VALUES ('user', ?, 'TEST-PC/1.0.0', 'UPDATE', 'employee', ?, ?, '{}')", (admin, ben, before_img), "current image")

    t = tampered("reuse_id")
    last = t.one("SELECT max(id) FROM punch")[0]
    owner = t.one("SELECT employee_id FROM punch WHERE id = ?", (last,))[0]
    t.x("DROP TRIGGER punch_bd")
    t.x("DELETE FROM punch WHERE id = ?", (last,))
    t.x(s_trigger("punch_bd"))
    check("deleted punch: drift flags the missing row",
          any(r[0] == "punch" and r[1] == last for r in t.problems()["verify_drift_v"]))
    t.act("employee", owner).blocked("legitimate insert cannot reuse the deleted row id and hide it",
                                     f"INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'OUT', ?, {OFF}, 'kiosk')",
                                     (owner, now_utc()), "already has history")

    # Codex review: an admission trigger dropped for one statement and recreated identically.
    t = tampered("admission_bypass")
    t.x("DROP TRIGGER punch_bi")
    t.act("employee", ana)
    t.x("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'IN', ?, ?, 'kiosk')",
        (ben, now_utc(), OFF))
    for_ben = t.one("SELECT max(id) FROM punch")[0]
    t.x("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source) VALUES (?, 'IN', ?, ?, 'kiosk')",
        (ana, now_utc(-3600), OFF))
    backdated = t.one("SELECT max(id) FROM punch")[0]
    t.act("user", 2)
    t.x("INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, import_batch_id, legacy_shift_id)"
        " VALUES (?, 'IN', '2023-03-05T14:00:00.000Z', -300, 'legacy_import', ?, 99)", (old, batch))
    phantom = t.one("SELECT max(id) FROM punch")[0]
    t.x(s_trigger("punch_bi"))
    t.act("user", admin)
    t.x("DROP TRIGGER punch_correction_bi")
    t.x("INSERT INTO punch_correction (action, employee_id, new_direction, new_occurred_utc, new_utc_offset_minutes, reason, actor_user_id)"
        " VALUES ('add', ?, 'OUT', ?, ?, 'adding my own missed punch', ?)", (old, new_in, off_in, admin))
    self_fix = t.one("SELECT max(id) FROM punch_correction")[0]
    t.x(s_trigger("punch_correction_bi"))
    p = t.problems()
    rules = {(r[0], r[1]) for r in p["verify_rules_v"]}
    check("admission bypass: fingerprint matches and the other views stay clean",
          t.one("SELECT fingerprint FROM schema_fingerprint_v")[0] == fp
          and not any(p[v] for v in VERIFY_VIEWS if v != "verify_rules_v"), p)
    check("admission bypass: kiosk punch for another employee flagged", ("punch", for_ben) in rules)
    check("admission bypass: backdated kiosk punch flagged", ("punch", backdated) in rules)
    check("admission bypass: phantom import punch by a retired account into a closed batch flagged",
          ("punch", phantom) in rules
          and len([r for r in p["verify_rules_v"] if r[1] == phantom]) == 2)
    check("admission bypass: admin self-correction flagged", ("punch_correction", self_fix) in rules)

    t = tampered("inactive_pin")
    t.x("DROP TRIGGER employee_bu")
    t.act("employee", zoe).x("UPDATE employee SET pin_hash = 'pbkdf2-sha256$z3' WHERE id = ?", (zoe,))
    t.act("user", admin).x(s_trigger("employee_bu"))
    check("deactivated employee's PIN change with the rule dropped: flagged",
          ("employee", zoe) in {(r[0], r[1]) for r in t.problems()["verify_rules_v"]})

    t = tampered("import_gaps")
    t.act("user", 2)
    t.x("DROP TRIGGER employee_bi")
    t.x("INSERT INTO employee (legacy_id, first_name, last_name, pin_hash) VALUES (99, 'Leaked', 'Pin', 'h')")
    leaked = t.one("SELECT id FROM employee WHERE legacy_id = 99")[0]
    t.x(s_trigger("employee_bi"))
    t.x("DROP TRIGGER legacy_shift_raw_bi")
    t.x("INSERT INTO legacy_shift_raw (import_batch_id, legacy_shift_id, legacy_employee_id, time_in_local) VALUES (?, 45, 7, '2023-03-06T09:00:00.000')", (batch,))
    t.x(s_trigger("legacy_shift_raw_bi"))
    rules = {(r[0], r[1]) for r in t.problems()["verify_rules_v"]}
    check("imported employee without a forced PIN reset flagged", ("employee", leaked) in rules)
    check("closed batch with a raw shift that never became punches flagged", ("import_batch", batch) in rules)

    t = tampered("rewrite_chain")
    t.x("DROP TRIGGER punch_bu")
    t.x("UPDATE punch SET occurred_utc = '2020-01-01T00:00:00.000Z' WHERE id = ?", (in_id,))
    t.x(trig)
    recompute_chain(t, patch=("punch", in_id))
    check("chain rewrite: stored checkpoint no longer matches",
          any("checkpoint" in r[1] for r in t.problems()["verify_chain_v"]))

    check("chain rewrite: a punch moved outside its admission rules is flagged by the rules view",
          any(r[1] == in_id for r in t.problems()["verify_rules_v"]))

    # The strongest internal-only attack: an edit that still satisfies every admission rule
    # (kiosk time moved 100 s, inside the 120 s window), with checkpoints removed and the
    # whole chain recomputed.
    t = tampered("rewrite_chain_full")
    t.x("DROP TRIGGER punch_bu")
    t.x("UPDATE punch SET occurred_utc = strftime('%Y-%m-%dT%H:%M:%fZ', recorded_utc, '-100 seconds') WHERE id = ?", (in_id,))
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

# PunchClock rebuild: database and audit-log design

Status: draft for review. Target: .NET 8+ desktop app on Windows 10/11, SQLite (Microsoft.Data.Sqlite, bundled e_sqlite3 ≥ 3.37 for `STRICT` tables).

Files:
- [`schema.sql`](schema.sql): complete DDL (tables, triggers, views). Executable as-is once `audit_hash` is registered.
- [`../../tools/verify_audit.py`](../../tools/verify_audit.py): independent verifier an auditor can run on a copy of the database without the app.

## 1. Decisions in one screen

| Concern | Decision |
|---|---|
| Punches | Immutable events (`IN`/`OUT`), one row each. Never updated, never deleted. Shifts are a view. |
| Manager edits | A `punch_correction` row (actor, reason, target = before) plus, for ADD/ADJUST, a new `punch` row (after). The original stays forever. |
| Audit capture | `AFTER INSERT/UPDATE` triggers on every table write a before/after JSON image into `audit_log`. Not optional, not app-side. |
| Who did it | The app inserts one `audit_context` row at the start of each write transaction; triggers copy it into every audit row and refuse to write without it. |
| Tamper evidence | `audit_log` is a SHA-256 hash chain. The verifier recomputes the chain, replays the log against live tables, and checks external anchors. |
| Deletes | Blocked on every table. Employees and users are deactivated. |
| Time | UTC ISO-8601 text with milliseconds and `Z`, enforced by `CHECK`. Local offset stored per punch. |
| Migration | Every legacy row kept verbatim in `legacy_row`; every anomaly recorded in `migration_issue`; source file SHA-256 in `migration_batch`. The chain starts at import. |

## 2. Legacy model and what replaces it

Legacy (`PunchClock/PunchClockDataSet1.xsd`):

```
Employee(EmployeeID int AutoNumber, FirstName text(255), LastName text(255), PinCode int, IsActive int)
Shift   (ShiftID int AutoNumber, EmployeeID int, TimeIn datetime, TimeOut datetime NULL = open)
```

| Legacy | New | Why |
|---|---|---|
| `Employee` | `employee` (+ `legacy_employee_id`) | PIN becomes a salted hash (`pin_hash`), not a plaintext int. |
| `Shift` row, `UPDATE ... SET TimeOut` on punch-out | two `punch` rows (`IN`, `OUT`) | A punch-out no longer mutates history. |
| Edits made directly in Access | `punch_correction` + replacement `punch` | Edits become attributable, reasoned, reversible. |
| Punch state = last row by `ShiftID` | `v_employee_status` = last effective punch by time | Fixes out-of-order state flips (legacy bug #5). |
| Dummy shift on employee creation | removed; `v_employee_status` handles "no punches" | Fixes NULL-cast crash without fake data. |
| none | `app_user` (admin/manager) | Corrections need an authenticated actor. |
| none | `audit_log`, `audit_context`, `app_event` | Legal traceability. |

## 3. Tables

Full DDL is in `schema.sql`; this is the shape.

**Reference**
- `site_setting(setting_id, key, value)`: `site_tz_id` (Windows zone id), `max_shift_minutes`.
- `employee(employee_id, legacy_employee_id, first_name, last_name, pin_hash, pin_must_change, is_active, created_at_utc)`
- `app_user(user_id, username, display_name, password_hash, role ∈ {admin, manager}, employee_id?, is_active, created_at_utc)`

**Time data**
- `punch(punch_id, employee_id, punch_type, punched_at_utc, utc_offset_min, source ∈ {kiosk, correction, migration}, correction_id?, migration_batch_id?, legacy_shift_id?, device_id, recorded_at_utc)`
  - `punched_at_utc` is the time the punch counts for; `recorded_at_utc` is when the row was written. They differ for corrections and migration.
  - `CHECK`s tie `source` to exactly one provenance column.
- `punch_correction(correction_id, action ∈ {ADD, ADJUST, VOID}, employee_id, target_punch_id?, reason, actor_user_id, created_at_utc)`
  - `target_punch_id` is `UNIQUE`: a punch is corrected at most once. Correcting a correction means targeting the replacement punch, so history is a linear chain per punch.
  - `reason` must be at least 10 non-blank characters.

**Provenance**
- `migration_batch(batch_id, source_file_name, source_sha256, source_tz_id, source_counts_json, tool_version, started_at_utc)`
- `legacy_row(legacy_row_id, batch_id, source_table, source_pk, raw_json)`
- `migration_issue(issue_id, batch_id, legacy_row_id, issue_code, disposition, detail_json)`
- `app_event(event_id, event_type, detail_json, occurred_at_utc)`: logins, failed PINs, report runs, exports, verification results, anchors, schema upgrades. These are actions with no table row of their own, so they get one, and the trigger chains it like any other write.

**Audit**
- `audit_context(id = 1, txn_id, actor_kind, actor_user_id?, actor_employee_id?, device_id, app_version)`
- `audit_log(audit_id, occurred_at_utc, txn_id, actor_*, device_id, app_version, table_name, operation, row_pk, before_json, after_json, prev_hash, row_hash)`

## 4. Write path

Every write goes through one `UnitOfWork`:

```csharp
using var tx = conn.BeginTransaction(deferred: false);           // BEGIN IMMEDIATE
Exec(tx, "INSERT INTO audit_context VALUES (1, $txn, $kind, $user, $emp, $device, $ver)", ...);
work(tx);                                                       // punches, corrections, etc.
Exec(tx, "DELETE FROM audit_context");
tx.Commit();
```

Properties this gives, all enforced in the database rather than by convention:

- **No context, no write.** The `audit_sink` trigger raises `audit: no audit_context row` if any audited table is written without it. A leftover row makes the next `INSERT` fail on the `id = 1` key, and the verifier flags it.
- **Authorization in triggers.** Kiosk punches must be made by that employee (`actor_kind = 'employee'`, same `employee_id`, active). Corrections must be made by an active manager/admin who is the actor in context. Only admins create accounts or change roles. Employees can change only their own PIN. Migration rows need `actor_kind = 'migration'`. The app still checks all of this for UX; the triggers make a buggy or bypassed UI unable to break the rules.
- **Single writer.** SQLite serializes writers, so `MAX(audit_id) + 1` and "previous `row_hash`" inside the trigger are race-free. Use `BEGIN IMMEDIATE` to fail fast instead of on upgrade.

A correction (ADJUST) is two inserts in one unit of work:

```sql
-- actor_kind='user', actor_user_id=2 in audit_context
INSERT INTO punch_correction (action, employee_id, target_punch_id, reason, actor_user_id)
VALUES ('ADJUST', 1, 2, 'Forgot to punch out at 17:30, confirmed by supervisor', 2);
INSERT INTO punch (employee_id, punch_type, punched_at_utc, utc_offset_min, source, correction_id, device_id)
VALUES (1, 'OUT', '2026-10-01T21:30:00.000Z', -240, 'correction', last_insert_rowid(), 'OFFICE-PC');
```

Resulting audit rows: the correction (before image = target punch, after image = correction with actor and reason), then the replacement punch. Before/after is exact by construction: punches are immutable, so the referenced "before" can never drift. VOID is the correction row alone. ADD has no target.

Policy option, not enforced in v1: block managers from correcting their own punches (`app_user.employee_id = punch_correction.employee_id`) unless admin. One extra `RAISE` in `punch_correction_bi`. Worth turning on if the business has more than one manager.

## 5. Hash chain

Each `audit_log` row stores:

```
prev_hash = row_hash of audit_id - 1   (64 zeros for audit_id 1)
row_hash  = audit_hash(prev_hash, audit_id, occurred_at_utc, txn_id, actor_kind, actor_user_id,
                       actor_employee_id, device_id, app_version, table_name, operation,
                       row_pk, before_json, after_json)
```

`audit_hash` is an application-defined SQLite function (SQLite has no built-in SHA-256 in the bundled build). Encoding, so any auditor can reimplement it:

- Hash = lowercase hex SHA-256 over the concatenation of each argument's encoding, in order.
- `NULL` → `N;`
- integer → `I<decimal>;`
- text → `T<utf8 byte length>:<utf8 bytes>;`

Length prefixes make the encoding unambiguous; no delimiter in a reason or name can shift field boundaries.

```csharp
conn.CreateFunction("audit_hash", (object[] args) =>
{
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var a in args)
    {
        byte[] chunk = a switch
        {
            null or DBNull => "N;"u8.ToArray(),
            long l   => Encoding.ASCII.GetBytes($"I{l};"),
            string s => [.. Encoding.ASCII.GetBytes($"T{Encoding.UTF8.GetByteCount(s)}:"),
                         .. Encoding.UTF8.GetBytes(s), (byte)';'],
            _ => throw new InvalidOperationException($"audit_hash: {a.GetType()}")
        };
        sha.AppendData(chunk);
    }
    return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
}, isDeterministic: true);
```

Side effect worth keeping: a generic SQLite tool (DB Browser, the `sqlite3` CLI) has no `audit_hash`, so any write it attempts to an audited table fails with `no such function`. Casual edits outside the app, which is how the legacy app was "corrected", stop working.

Credential hashes are never copied into the log. Images carry `pin_fp` / `password_fp` (first 16 hex chars of `audit_hash(hash)`), which proves a credential changed and lets replay detect a swapped hash without duplicating it.

## 6. Timestamps

- Storage: `TEXT` `YYYY-MM-DDTHH:MM:SS.SSSZ`, always UTC. Every timestamp column has `CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', col) IS col)`, which rejects local times, missing `Z`, missing milliseconds and impossible dates. Lexical order = chronological order, so indexes and `ORDER BY` work on the text.
- App writes `DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)`. Never `DateTime.Now`.
- `punch.utc_offset_min` records the site offset at the punch instant (from `TimeZoneInfo.FindSystemTimeZoneById(site_tz_id)`). Reports show local wall time exactly as the employee saw it, and a DST night shift computes its true duration (`v_shift.minutes` is UTC arithmetic).
- Audit `occurred_at_utc` comes from SQLite `'now'`, which is the OS clock. Clock rollback is not prevented; the verifier warns when an audit row is earlier than its predecessor, and `audit_id` order remains authoritative.

## 7. Tamper detection

What the verifier (`tools/verify_audit.py`, mirrored in the app's startup check) does:

| Check | Catches |
|---|---|
| 1. `audit_id` is gapless 1..n, each `prev_hash` equals the previous `row_hash`, each `row_hash` recomputes | Edited, deleted or inserted audit rows in the middle of the log |
| 2. External anchors `(audit_id, row_hash)` still match | Truncation of the tail, or a full rewrite with a recomputed chain |
| 3. Replay: last logged image per row equals the current row image (`audit_image_*` views), and every row has been logged | Data edited with triggers dropped or disabled; rows inserted outside the app |
| 4. `audit_context` empty | A write path that bypassed `UnitOfWork` |
| 5. Schema fingerprint (SHA-256 of `sqlite_master` DDL) equals the value shipped with the app build | Dropped or altered triggers, constraints, views |

Tested against a scratch database (see §11): editing a punch with its trigger dropped fails check 3; deleting the last audit row fails checks 2 and 3; editing an `after_json` fails checks 1 and 3; an untouched copy passes.

**Anchors are what make this legally meaningful.** Anyone with write access to the file and knowledge of the algorithm can rebuild a self-consistent chain. Only a copy of the chain head held somewhere they cannot reach makes that detectable. Plan:

- At each pay-period close, report export, and daily at first launch, the app writes an `ANCHOR` `app_event` with `(audit_id, row_hash)` signed with an Ed25519 key, and pushes the same anchor out of the machine: printed in the footer of every pay report, appended to a file on a network share or USB backup, and optionally emailed to the owner.
- Signing key: generated on install, private part protected with DPAPI (machine scope), public part exported with the first anchor. A forged anchor without the key fails signature check.
- Given an anchor from date D, the verifier proves nothing before D was changed. Records after the last anchor are protected by the chain and replay checks only.

**Threat model, stated plainly.**
- Protected: app bugs, a manager editing through the UI without a reason, anyone using Access/DB Browser style tools, deleting or altering past records when an external anchor exists.
- Not prevented: an administrator with the file, the DPAPI key and every anchor copy rewriting history. Mitigation is organisational (anchors held by someone other than the manager, e.g. payroll or the owner).
- Confidentiality is out of scope for this doc. If the database must be unreadable to someone who copies the file, add SQLCipher; it does not change this design.

## 8. Read model

- `v_effective_punch`: punches not targeted by any correction.
- `v_shift`: each effective `IN` paired with the next effective punch by `(punched_at_utc, punch_id)`. Status `OK`, `OPEN`, `MISSING_OUT` (next punch is another `IN`), `MISSING_IN` (orphan `OUT`). Payroll only sums `OK`; the others are a manager work queue, resolved by corrections.
- `v_employee_status`: last effective punch per employee, used by the kiosk to decide IN vs OUT. Open shifts older than `max_shift_minutes` should prompt "Did you forget to punch out?" rather than silently closing a multi-day shift (legacy bug #5). That is app behaviour; the schema only reports it.

## 9. Access migration

Runs once, on Windows (ACE OLEDB + the database password), as a single unit of work with `actor_kind = 'migration'` so the import is either complete or absent.

1. Hash the `.accdb` (SHA-256) and count rows per table. Insert `migration_batch` with the hash, counts, `source_tz_id` and tool version.
2. Copy every `Employee` and `Shift` row verbatim into `legacy_row.raw_json` (local datetimes kept as read, no conversion).
3. Employees: insert with `legacy_employee_id`. PIN: hash the legacy integer as its decimal string and set `pin_must_change = 1` for everyone, since leading zeros were lost (`PIN_UNRECOVERABLE` issue for PINs under 3 digits, which cannot be valid originals).
4. Shifts: convert `TimeIn`/`TimeOut` from `source_tz_id` local time to UTC with `TimeZoneInfo`. Each shift becomes an `IN` punch and, if `TimeOut` is set, an `OUT` punch, both with `legacy_shift_id`. Issues, all recorded with a disposition:

| Condition | Code | Disposition |
|---|---|---|
| `TimeIn = TimeOut` (NewStaffForm dummy) | `DUMMY_SHIFT` | SKIPPED |
| `TimeOut` NULL | `OPEN_SHIFT` | IMPORTED_FLAGGED (latest per employee is legitimately open) |
| `TimeIn` NULL | `NULL_TIME_IN` | SKIPPED |
| `TimeOut < TimeIn` | `NEGATIVE_DURATION` | IMPORTED_FLAGGED |
| duration > `max_shift_minutes` | `LONG_SHIFT` | IMPORTED_FLAGGED |
| overlaps another shift of same employee | `OVERLAPPING_SHIFT` | IMPORTED_FLAGGED |
| local time in fall-back hour | `DST_AMBIGUOUS` | IMPORTED_FLAGGED, earlier (daylight) offset chosen |
| local time in spring-forward gap | `DST_INVALID` | IMPORTED_FLAGGED, shifted forward |
| `EmployeeID` not in `Employee` | `ORPHAN_EMPLOYEE` | SKIPPED |

5. Write a `MIGRATION_COMPLETE` `app_event` with the reconciliation (`legacy rows read = imported + skipped`), then take the first external anchor. Flagged punches are fixed afterwards through normal corrections, so every post-migration change is itself audited.

## 10. Operations

- Connection: `foreign_keys = ON`, `journal_mode = WAL`, `synchronous = FULL`, register `audit_hash` before any statement.
- Schema upgrades: DDL cannot fire triggers, so each upgrade writes a `SCHEMA_UPGRADE` `app_event` with old/new `user_version` and new schema fingerprint in the same transaction.
- Backups: `VACUUM INTO` or the online backup API, logged as `BACKUP` with the head anchor. A restored backup verifies against anchors up to its head.
- Data access: Dapper with explicit SQL fits a schema this size and keeps the SQL reviewable. If EF Core is used, declare the triggers in the model (`ToTable(t => t.HasTrigger(...))`) so EF does not rely on `RETURNING` semantics around them, and never let it generate `UPDATE`/`DELETE` on append-only tables.
- Growth: about 3 audit rows per punch pair. 50 employees × 2 punches/day × 365 is under 100k rows a year; size is a non-issue for a decade.

## 11. Validation done

`schema.sql` was loaded into SQLite 3.45 with `audit_hash` registered (Python), then exercised: admin bootstrap, manager and employee creation, kiosk IN/OUT, an ADJUST correction, an employee PIN change. Confirmed blocked: writes without context, `UPDATE`/`DELETE` on punches, employee creating staff, non-UTC timestamp, employee issuing a correction, a second correction of the same punch, a reason under 10 chars, a manager promoting themself, deleting audit rows. `v_shift` returned one 510-minute `OK` shift reflecting the correction. Tamper results as in §7.

## 12. Open decisions

1. Site time zone id for the legacy data (needed for step 4 of the migration).
2. Whether managers may correct their own punches (§4 policy option).
3. Where external anchors go: printed report footer only, or also a network share / email.
4. PIN policy after migration: force reset for everyone (default here) or only for PINs that look truncated.

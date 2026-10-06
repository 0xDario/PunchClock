# PunchClock database and audit-log design

Status: draft for review. Target: .NET desktop app on Windows 10/11, one local SQLite file.

| File | What it is |
|---|---|
| [`schema.sql`](schema.sql) | Complete DDL: tables, triggers, hash-chain seal, read views, verifier views, seed. |
| [`test_schema.py`](test_schema.py) | Executable proof: loads the schema, exercises every rule, plays the tamper scenarios. 84 checks. `python3 docs/database/test_schema.py` |

## 1. Decisions

| Concern | Decision |
|---|---|
| Punches | Immutable `IN`/`OUT` events, one row each. Never updated, never deleted. Shifts are a view. |
| Manager edits | One `punch_correction` row holds actor, reason, the target punch (before) and the new values (after). Its trigger inserts the replacement punch in the same statement, so a correction can never exist without its result. |
| Audit capture | Database triggers on every table write a before/after JSON image to `audit_log`. The app cannot skip it. |
| Who did it | Per-connection context function `pc_ctx()`. Every audit row's actor must equal it, and every write rule checks it. |
| Tamper evidence | `audit_log` is a SHA-256 hash chain, sealed by its own trigger. Verifier views check chain, drift, continuity, checkpoints and schema fingerprint. Signed checkpoints plus a copy off the machine make a full rewrite detectable. |
| Deletes | Blocked everywhere. People and accounts are deactivated. |
| Time | UTC text `YYYY-MM-DDTHH:MM:SS.sssZ`, format enforced by `CHECK`. Site offset stored with every punch. Record times come from the database clock, not the caller. |
| Migration | Dedicated `migration` account, `legacy_import` punches, legacy IDs kept, raw legacy rows kept (minus PINs), every anomaly in `migration_issue`, batch cannot close until row counts match the export manifest. |

## 2. Legacy model and its replacement

Legacy, from `PunchClock/PunchClockDataSet1.xsd`:

```
Employee(EmployeeID AutoNumber, FirstName, LastName, PinCode int, IsActive int)
Shift   (ShiftID AutoNumber, EmployeeID, TimeIn datetime, TimeOut datetime NULL = punched in)
```

| Legacy | New | Why |
|---|---|---|
| `Employee` | `employee` + `legacy_id` | PIN becomes a salted hash; leading zeros were lost in the int, so imported PINs must be reset. |
| `Shift`, punch-out = `UPDATE Shift SET TimeOut` | two `punch` rows | Punching out no longer rewrites history. |
| Edits made by opening the `.accdb` in Access | `punch_correction` | Attributable, reasoned, reversible, and impossible from a generic tool (section 5). |
| Punch state = last row by `ShiftID` | `employee_status_v`: last effective punch by time | Fixes state flips after out-of-order rows. |
| Dummy zero-length shift per new employee | none | `employee_status_v` handles "no punches yet". |
| `DateTime.Now`, no zone | UTC + `utc_offset_minutes` | DST nights compute true durations; reports show the wall time the employee saw. |
| no users, no roles | `app_user` with `admin`, `manager`, plus `system` and `migration` service accounts | Corrections need an authenticated actor. |

## 3. Tables

| Table | Mutability | Notes |
|---|---|---|
| `site_setting` | admin edits, audited | `time_zone_id` (Windows zone id), `max_shift_hours`. |
| `employee` | manager edits, audited | `legacy_id`, `pin_hash`, `pin_must_change`, `is_active`. Employee may change only their own PIN, and clearing `pin_must_change` requires a new PIN. |
| `app_user` | admin edits, audited | Roles `system` (id 1), `migration` (id 2), `admin`, `manager`. `employee_id` links a manager who also punches. |
| `punch` | insert-only | `direction`, `occurred_utc`, `utc_offset_minutes`, `recorded_utc`, `source` ∈ {`kiosk`, `correction`, `legacy_import`}, plus `correction_id` or `import_batch_id` + `legacy_shift_id`. |
| `punch_correction` | insert-only | `action` ∈ {`add`, `adjust`, `void`}, `target_punch_id` (UNIQUE), `new_direction`, `new_occurred_utc`, `new_utc_offset_minutes`, `reason` (≥ 10 chars), `actor_user_id`. |
| `import_batch` | insert, then close once | Source `.accdb` SHA-256, manifest SHA-256, manifest row counts, zone, tool version. |
| `legacy_employee_raw`, `legacy_shift_raw` | insert-only | Exported rows verbatim, including OADate values. **PinCode is not stored**: these tables are immutable and chained, so anything here is kept forever. |
| `migration_issue` | insert-only | Code + disposition per anomaly (section 9). |
| `audit_checkpoint` | insert-only | Signed `(seq, hash, schema fingerprint)`. |
| `audit_log` | append-only | Sections 6 and 8. |

`punch_correction` rules, all enforced by triggers:

- Actor is the active manager or admin in context, and `actor_user_id` must equal that actor.
- A manager cannot correct their own punches (`app_user.employee_id`). No account can change its own `employee_id`, so an admin cannot unlink, correct and relink.
- The target belongs to the same employee. A punch is superseded at most once (`UNIQUE`), so the history of any punch is a linear chain; re-correcting means targeting the latest replacement.
- `adjust` and `add` insert the replacement punch from the correction's `new_*` values inside the correction's own trigger. A `correction` punch whose values differ from its correction is rejected.

A punch is effective unless a correction targets it (`punch_effective_v`). `shift_v` pairs each effective `IN` with the next effective punch by `(occurred_utc, id)` and reports `closed`, `open` or `missing_out`; `punch_exception_v` adds `missing_in`, `long_shift` and `long_open_shift`. That view is the manager's pre-payroll queue, and it is cleared only through corrections.

## 4. Write path

The app registers two functions on every connection, right after `Open()`:

```csharp
conn.CreateFunction("pc_sha256", (string? s) =>
    s is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant(),
    isDeterministic: true);
conn.CreateFunction("pc_ctx", (string name) => audit.Get(name));   // throws if actor/client unset
```

`audit` is the unit of work's context: `actor_kind` (`employee` at the kiosk after PIN check, `user` after login), `actor_id`, `client` (machine name + app version), optional `reason`. Then each write is ordinary SQL inside `BEGIN IMMEDIATE ... COMMIT`.

What this buys:

- **No context, no write.** `pc_ctx` throws when the actor is unset, so the trigger fails and the statement rolls back. There is no context row to leave behind.
- **Generic tools cannot write.** DB Browser or the `sqlite3` CLI do not have `pc_sha256`/`pc_ctx`, so every insert or update fails with `no such function`. The legacy habit of fixing data in Access stops working. Reads still work.
- **Authorization in the database.** Kiosk punches only by that employee, only active, only at the current time (±120 s). Corrections only by managers/admins, never on themselves. Account changes only by admins. Legacy writes only by the `migration` account into an open batch. The app checks the same rules for UX; the triggers make a buggy UI unable to break them.

Required connection settings: `foreign_keys = ON`, `synchronous = FULL`, `busy_timeout`, and **`trusted_schema = ON`**. With `trusted_schema = OFF` SQLite refuses app functions inside triggers ("unsafe use of pc_sha256()"), so every write fails closed. `journal_mode = WAL` is set once at creation.

## 5. Corrections: before and after

One manager action, one statement:

```sql
-- pc_ctx: actor_kind = 'user', actor_id = 3
INSERT INTO punch_correction (action, employee_id, target_punch_id, new_direction,
                              new_occurred_utc, new_utc_offset_minutes, reason, actor_user_id)
VALUES ('adjust', 1, 2, 'OUT', '2026-10-01T21:30:00.000Z', -240,
        'Forgot to punch out; supervisor confirmed 17:30', 3);
```

Audit rows produced, in order:

| seq | table | before_json | after_json | reason |
|---|---|---|---|---|
| n | `punch_correction` | the target punch, exactly | the correction, including new values and actor | the correction's reason |
| n+1 | `punch` | NULL | the replacement punch | the correction's reason |

Before/after cannot drift: punches are immutable, so the before-image is the punch itself, forever.

## 6. Hash chain

`audit_log` columns: `seq` (contiguous from 1), `occurred_utc`, `actor_kind`, `actor_id`, `client`, `action`, `table_name`, `row_id`, `before_json`, `after_json`, `reason`, `prev_hash`, `row_hash`.

```
prev_hash(n) = row_hash(n-1), or 64 zeros for n = 1
row_hash(n)  = SHA-256( canonical(seq, occurred_utc, actor_kind, actor_id, client, action,
                                  table_name, row_id, before_json, after_json, reason, prev_hash) )
```

Canonical encoding, field by field, concatenated, then UTF-8 bytes hashed:

- NULL → `N;`
- integer → `I<decimal>;`
- text → `T<UTF-8 byte length>:<text>;`

Length prefixes make field boundaries unambiguous. The encoding is defined once, in `audit_canonical_v`, and is reproducible in any language without SQLite's JSON serializer (the test re-derives every hash in plain Python). That matters over a 10-year retention period: a SQLite upgrade cannot invalidate old hashes.

The seal is enforced by the database, not trusted to the writer:

- `BEFORE INSERT` rejects any caller-supplied `prev_hash`/`row_hash`, an actor different from `pc_ctx`, and an `occurred_utc` other than the database clock.
- It also rejects any table event (`INSERT`/`UPDATE`) the table's own trigger could not have written, so a caller cannot fabricate one to cover an edit made with triggers dropped:
  - `after_json` must equal the row's current image;
  - an `INSERT` must be the row id's first event (no reuse of a deleted row's id) and the row's own creation time must equal the event time, i.e. same statement;
  - `UPDATE` exists only for the four mutable tables, its `before_json` must equal the previous audited image, and it cannot change an immutable field (`id`, `created_utc`, `legacy_id`, batch provenance, service-account roles, a set `completed_utc`).
- `AFTER INSERT` rejects a seq gap, then sets `prev_hash`, then `row_hash`.
- `BEFORE UPDATE` allows only those two NULL-to-value writes. `BEFORE DELETE` always aborts.

Non-data events (`AUTH_LOGIN`, `AUTH_LOGIN_FAILED`, `AUTH_PIN_FAILED`, `REPORT_EXPORT`, `BACKUP`, `VERIFY`, `APP_START`, `SCHEMA_MIGRATE`, ...) are plain inserts into `audit_log` by the app and are sealed the same way.

Credentials never enter the log. Images carry `pin_hash_digest` / `password_hash_digest`, which proves a change and lets drift checks catch a swapped hash without storing a crackable PIN hash forever.

## 7. Time

- Storage: UTC `TEXT`, `YYYY-MM-DDTHH:MM:SS.sssZ`. `CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', col) IS col)` rejects local times, missing `Z`, missing milliseconds and impossible dates. Fixed width, so text order is time order and indexes work.
- App: `DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)`. Never `DateTime.Now`.
- `utc_offset_minutes` is the site offset at that instant, from `TimeZoneInfo.FindSystemTimeZoneById(time_zone_id).GetUtcOffset(utc)`. Reports render `occurred_utc + offset`, never the machine's current zone, so a later DST change or a zone fix cannot move history. Durations are UTC arithmetic: a night shift across fall-back is 9 hours, not 8.
- `work_date` (in `shift_v`) is the local date of the `IN` punch. Payroll periods group by it.
- `recorded_utc`, `punch_correction.created_utc` and `audit_log.occurred_utc` must equal the database clock at insert. `occurred_utc` is the time the punch counts for; at the kiosk it must be within 120 s of the clock, for corrections and imports it is the asserted time.
- Clock rollback cannot be prevented by software on the same machine. `verify_clock_v` flags any audit row more than 60 s earlier than its predecessor; `seq` order stays authoritative.

## 8. Tamper detection

Each `verify_*` view returns zero rows on an intact file. The app runs them at startup and on demand, writes a `VERIFY` audit event with the result, and refuses to export payroll while any returns rows.

| View / check | Catches |
|---|---|
| `verify_chain_v` | Edited, inserted or deleted audit rows; seq gaps; stored checkpoints that no longer match the chain. |
| `verify_drift_v` | Table rows changed, inserted or deleted with triggers dropped: current snapshot vs last audited image, both directions. |
| `verify_continuity_v` | An out-of-band edit later "laundered" by a legitimate edit: each `UPDATE`'s before-image must equal the previous after-image. Drift alone misses this. (The insert guard already refuses such an edit; this catches a log written while the guard was dropped.) |
| `verify_history_v` | Events the trigger path cannot produce: a second `INSERT` for one row id, `UPDATE` on an immutable table, a row whose creation time differs from its `INSERT` event, a checkpoint that did not anchor the head, a closed import batch that does not reconcile. |
| `schema_fingerprint_v` vs value compiled into the app | Dropped or altered triggers, views, constraints. |
| `verify_clock_v` (warning) | System clock moved backwards. |
| External anchor | Everything above done consistently by someone who knows the algorithm. |

All of these are demonstrated in `test_schema.py`, including one result worth stating plainly: an attacker who drops a trigger, edits a punch, recreates the identical trigger, patches the audit image, deletes stored checkpoints and recomputes the whole chain passes every internal check and the fingerprint. **Only an anchor held outside the file catches that.** A hash chain inside a file the attacker can write proves consistency, not authenticity.

Anchoring plan:

1. On pay-period close, payroll export, backup, and first launch each day, the app takes the head `(seq, row_hash)` and schema fingerprint, signs them, and inserts an `audit_checkpoint`.
2. The same values leave the machine: printed in the footer of every pay report (a signed paper report is the strongest anchor a small business has), appended to a file on a network share or backup drive, optionally emailed to the owner.
3. Signing key: ECDSA P-256 created through Windows CNG in the Microsoft Platform Crypto Provider (TPM-backed, non-exportable) where a TPM exists, DPAPI-protected software key otherwise. Public key exported with the first checkpoint.

Threat model:

| Actor | Outcome |
|---|---|
| Manager acting through the app | Allowed, fully attributed: who, when, why, before, after. Cannot touch own punches. |
| Anyone with DB Browser / Access-style tools | Writes fail (`no such function`). |
| Someone with a script that registers the functions | Writes pass the rules as the actor they claim; the audit row records that claim. Writes with triggers dropped are caught by drift, continuity, history and fingerprint, and fabricated audit events that would hide them are refused at insert. What remains is identity: a mutable-table change the claimed actor could have made legitimately. |
| Admin on the box rewriting history consistently | Caught only against an off-machine anchor. Organisational control: anchors held by someone other than the manager (owner, accountant). |

Confidentiality is out of scope: anyone who copies the file can read it. If that matters, add SQLCipher; nothing here changes.

## 9. Legacy import

Pairs with the exporter in `migration/legacy-export/` (PR #4): `Employee.csv`, `Shift.csv`, `manifest.json` with source SHA-256 and row counts. Runs once, on the target machine, as `pc_ctx` actor `('user', 2)`, the `migration` account, in one transaction so the import is complete or absent.

1. Insert `import_batch` with source `.accdb` SHA-256, manifest SHA-256, manifest row counts, `source_time_zone_id`, tool version. **Its `INSERT` audit row is the single import event** the legal chain starts from: it carries the source hash and the counts.
2. Copy every exported row into `legacy_employee_raw` / `legacy_shift_raw` (PIN replaced by its digit count).
3. Employees: insert with `legacy_id` = `EmployeeID`, PIN hashed from its decimal string, `pin_must_change = 1` for everyone. The plaintext PINs sat in a file whose password is in the public README; every one of them must be considered known.
4. Shifts: convert `TimeIn`/`TimeOut` from `source_time_zone_id` to UTC with `TimeZoneInfo`. Each shift becomes an `IN` punch and, if `TimeOut` is set, an `OUT` punch, both `source = 'legacy_import'` with `legacy_shift_id` = `ShiftID`.
5. Record every anomaly in `migration_issue`:

| Condition | Code | Disposition |
|---|---|---|
| `TimeIn = TimeOut` (NewStaffForm dummy) | `DUMMY_SHIFT` | SKIPPED |
| `TimeOut` NULL | `OPEN_SHIFT` | IMPORTED_FLAGGED |
| `TimeIn` NULL | `NULL_TIME_IN` | SKIPPED |
| `TimeOut < TimeIn` | `NEGATIVE_DURATION` | IMPORTED_FLAGGED |
| longer than `max_shift_hours` | `LONG_SHIFT` | IMPORTED_FLAGGED |
| overlaps another shift of the same employee | `OVERLAPPING_SHIFT` | IMPORTED_FLAGGED |
| local time in the fall-back hour | `DST_AMBIGUOUS` | IMPORTED_FLAGGED, daylight (earlier) offset |
| local time in the spring-forward gap | `DST_INVALID` | IMPORTED_FLAGGED, shifted forward one hour |
| `EmployeeID` with no employee | `ORPHAN_EMPLOYEE` | SKIPPED (raw row kept) |
| NULL first or last name | `NULL_NAME` | IMPORTED_FLAGGED, stored as empty string |
| every imported PIN | `PIN_RESET_REQUIRED` | IMPORTED |
| `ShiftID` order disagrees with `TimeIn` order for the same employee (legacy state came from ID order) | `OUT_OF_ORDER_ID` | IMPORTED_FLAGGED |
| shift spans a DST transition (duration differs from wall-clock difference) | `CROSSES_DST` | IMPORTED_FLAGGED |

6. Close the batch (`completed_utc`). The trigger refuses unless the raw tables hold exactly the manifest's row counts.
7. Take the first checkpoint and print it on the migration sign-off sheet. An admin then deactivates the `migration` account. Flagged punches are fixed afterwards through ordinary corrections, so every post-migration change is itself audited.

## 10. Notes for the app scaffold

The scaffold's initial migration uses the same column names (`occurred_utc`, `utc_offset_minutes`, `recorded_utc`, `created_utc`, `legacy_id`). To adopt this design:

- Use `schema.sql` as the initial migration (minus the two header `PRAGMA`s if the runner owns versioning). The runner's `schema_migrations` table is excluded from the fingerprint and is not audited; the runner should write a `SCHEMA_MIGRATE` audit event carrying the script checksum and the new fingerprint.
- **SQLite 3.44 minimum.** `ORDER BY` inside an aggregate (`schema_fingerprint_v`) needs 3.44; older builds reject the schema at load. Assert `sqlite_version() >= 3.44` at startup. Do not pin an older native bundle over what the `Microsoft.Data.Sqlite` package ships. The test suite passes on 3.45.1, 3.46.1 and 3.53.3 (the scaffold's build) and fails to load on 3.41.2.
- The connection factory must register `pc_sha256` and `pc_ctx` and set `trusted_schema = ON` on every open, **before the schema migration runs**: the seed inserts in `schema.sql` fire audit triggers that call both. The migration runs with actor `('user', 1)`.
- `punch.source` values are `kiosk`, `correction`, `legacy_import`. The kiosk must not insert `correction` punches; it inserts `punch_correction` rows.
- Prefer Dapper or plain ADO.NET over EF Core for writes. If EF Core is used, call `ToTable(t => t.UseSqlReturningClause(false))` on every entity: since EF Core 7, saving to SQLite tables with `AFTER` triggers via `RETURNING` fails ([EF Core 7 breaking changes](https://learn.microsoft.com/ef/core/what-is-new/ef-core-7.0/breaking-changes#high-impact-changes)).

## 11. Validation

`test_schema.py` on SQLite 3.45.1, 3.46.1 and 3.53.3: 84 checks, all passing. Covered: bootstrap; every authorization rule in both directions; kiosk impersonation, backdating, caller-supplied record times; local timestamps; immutability of punches, corrections and the log; adjust/void/add with shift recomputation; double correction; self-correction, including an admin unlinking their own employee record; forged correction punches; employee PIN self-service and forced PIN reset; caller-supplied creation times; checkpoints at an old position; credential hashes absent from the log; forged hashes, actor mismatch, backdated and gapped audit inserts; import with reconciliation and closed-batch lock; generic-tool writes; `trusted_schema = OFF`; an independent Python re-derivation of the whole chain. Tamper scenarios: punch edit with trigger dropped, the same with the trigger restored, audit row edit, insert bypassing the audit trigger, laundered edit (refused, and caught when the guard is dropped), fabricated `UPDATE` and `INSERT` events, an immutable-field change behind a fabricated event, a deleted row whose id is reused, chain rewrite with stored checkpoints, full rewrite including checkpoints, tail truncation, and an untouched control copy.

## 12. Open decisions

1. **Site time zone** for the legacy data and the new app (`time_zone_id`). Needed before import.
2. **Self-correction ban.** Enforced for every role, admins included. A sole owner who is also on the clock would need a second admin to fix their own punches. Keep strict, or allow admins?
3. **Where anchors go**: report footer only, or also a network share or email.
4. **Pay-period locking.** Not in v1. A `pay_period` table with `closed_utc` and a rule that corrections into a closed period need an admin is the natural next step once payroll exports exist.

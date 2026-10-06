-- PunchClock schema v1 (SQLite >= 3.44, UTF-8 database encoding)
--
-- Design doc: docs/database/schema-design.md
--
-- Three application-defined functions must be registered on EVERY connection before
-- any write. A connection without them can read every table but cannot write,
-- because every write fires a trigger that calls them.
--   pc_sha256(text) -> lowercase hex SHA-256 of the UTF-8 bytes; NULL -> NULL
--   pc_utc_offset(zone_id, utc) -> INTEGER minutes east of UTC for that Windows time
--                      zone id at that UTC instant (TimeZoneInfo.GetUtcOffset);
--                      NULL if the zone id is unknown or either argument is NULL
--   pc_ctx(name)    -> the connection's audit context:
--                        'actor_kind' TEXT  'employee' | 'user'
--                        'actor_id'   INTEGER employee.id or app_user.id
--                        'client'     TEXT  machine name + app version
--                        'reason'     TEXT  or NULL
--                      Raises an error if actor_kind, actor_id or client is unset.
--
-- Per-connection pragmas, set by the app on open (SQLite does not persist them):
--   foreign_keys = ON, synchronous = FULL, busy_timeout = 5000, trusted_schema = ON
-- Persistent, set once at creation outside any transaction: journal_mode = WAL
--
-- Timestamps: UTC TEXT, fixed width YYYY-MM-DDTHH:MM:SS.sssZ, so text order is time order.
-- Apply this file in one transaction with pc_ctx actor = ('user', 1), the system account.

PRAGMA application_id = 1346587723;  -- 0x5043434B, 'PCCK'
PRAGMA user_version = 1;

-- ===========================================================================
-- Tables
-- ===========================================================================

CREATE TABLE site_setting (
  id    INTEGER PRIMARY KEY,
  key   TEXT NOT NULL UNIQUE,
  value TEXT NOT NULL
) STRICT;

CREATE TABLE employee (
  id              INTEGER PRIMARY KEY,
  legacy_id       INTEGER UNIQUE,                 -- Access Employee.EmployeeID
  first_name      TEXT NOT NULL,
  last_name       TEXT NOT NULL,
  pin_hash        TEXT NOT NULL,                  -- pbkdf2-sha256$iterations$salt$hash
  pin_must_change INTEGER NOT NULL DEFAULT 0 CHECK (pin_must_change IN (0, 1)),
  is_active       INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
  created_utc     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                  CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_utc) IS created_utc)
) STRICT;

CREATE TABLE app_user (
  id                   INTEGER PRIMARY KEY,
  username             TEXT NOT NULL UNIQUE COLLATE NOCASE,
  display_name         TEXT NOT NULL,
  role                 TEXT NOT NULL CHECK (role IN ('system', 'migration', 'admin', 'manager')),
  password_hash        TEXT,                      -- NULL only for the system and migration accounts
  employee_id          INTEGER UNIQUE REFERENCES employee(id),  -- set when the manager also punches
  must_change_password INTEGER NOT NULL DEFAULT 1 CHECK (must_change_password IN (0, 1)),
  is_active            INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
  created_utc          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                       CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_utc) IS created_utc),
  CHECK ((role IN ('system', 'migration')) = (password_hash IS NULL))
) STRICT;

-- One run of the legacy importer over one export folder.
CREATE TABLE import_batch (
  id                  INTEGER PRIMARY KEY,
  source_file_name    TEXT NOT NULL,
  source_sha256       TEXT NOT NULL CHECK (length(source_sha256) = 64 AND source_sha256 NOT GLOB '*[^0-9a-f]*'),
  manifest_sha256     TEXT NOT NULL CHECK (length(manifest_sha256) = 64 AND manifest_sha256 NOT GLOB '*[^0-9a-f]*'),
  source_time_zone_id TEXT NOT NULL,              -- zone the legacy wall-clock times were written in
  tool_version        TEXT NOT NULL,
  manifest_employee_rows INTEGER NOT NULL,        -- row counts from manifest.json; the batch
  manifest_shift_rows    INTEGER NOT NULL,        -- cannot close until the raw tables match them
  started_utc         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  completed_utc       TEXT CHECK (completed_utc IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', completed_utc) IS completed_utc)
) STRICT;

-- Legacy rows exactly as exported, kept as evidence. PinCode is deliberately NOT copied:
-- this table is immutable and hash-chained, so anything stored here is stored forever.
CREATE TABLE legacy_employee_raw (
  id                 INTEGER PRIMARY KEY,
  import_batch_id    INTEGER NOT NULL REFERENCES import_batch(id),
  legacy_employee_id INTEGER NOT NULL,
  first_name         TEXT,
  last_name          TEXT,
  is_active          INTEGER,
  pin_digits         INTEGER,                     -- digit count of the legacy integer PIN, NULL if NULL
  UNIQUE (import_batch_id, legacy_employee_id)
) STRICT;

CREATE TABLE legacy_shift_raw (
  id                 INTEGER PRIMARY KEY,
  import_batch_id    INTEGER NOT NULL REFERENCES import_batch(id),
  legacy_shift_id    INTEGER NOT NULL,
  legacy_employee_id INTEGER,
  time_in_local      TEXT,                        -- yyyy-MM-ddTHH:mm:ss.fff site wall clock, as exported
  time_in_oadate     TEXT,                        -- the stored OLE Automation double, as exported
  time_out_local     TEXT,
  time_out_oadate    TEXT,
  UNIQUE (import_batch_id, legacy_shift_id)
) STRICT;

-- Every anomaly the importer found and what it did about it.
CREATE TABLE migration_issue (
  id              INTEGER PRIMARY KEY,
  import_batch_id INTEGER NOT NULL REFERENCES import_batch(id),
  legacy_table    TEXT NOT NULL CHECK (legacy_table IN ('Employee', 'Shift')),
  legacy_pk       INTEGER NOT NULL,
  code            TEXT NOT NULL CHECK (code IN (
                    'DUMMY_SHIFT', 'OPEN_SHIFT', 'NULL_TIME_IN', 'NEGATIVE_DURATION', 'LONG_SHIFT',
                    'OVERLAPPING_SHIFT', 'DST_AMBIGUOUS', 'DST_INVALID', 'ORPHAN_EMPLOYEE',
                    'NULL_NAME', 'PIN_RESET_REQUIRED', 'OUT_OF_ORDER_ID', 'CROSSES_DST')),
  disposition     TEXT NOT NULL CHECK (disposition IN ('IMPORTED', 'IMPORTED_FLAGGED', 'SKIPPED')),
  detail_json     TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(detail_json))
) STRICT;

-- Append-only punch events. Never updated, never deleted.
CREATE TABLE punch (
  id                 INTEGER PRIMARY KEY,
  employee_id        INTEGER NOT NULL REFERENCES employee(id),
  direction          TEXT NOT NULL CHECK (direction IN ('IN', 'OUT')),
  occurred_utc       TEXT NOT NULL CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', occurred_utc) IS occurred_utc),
  utc_offset_minutes INTEGER NOT NULL CHECK (utc_offset_minutes BETWEEN -840 AND 840),
  recorded_utc       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                     CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', recorded_utc) IS recorded_utc),
  source             TEXT NOT NULL CHECK (source IN ('kiosk', 'correction', 'legacy_import')),
  correction_id      INTEGER UNIQUE REFERENCES punch_correction(id),
  import_batch_id    INTEGER REFERENCES import_batch(id),
  legacy_shift_id    INTEGER,
  CHECK ((source = 'correction') = (correction_id IS NOT NULL)),
  CHECK ((source = 'legacy_import') = (import_batch_id IS NOT NULL AND legacy_shift_id IS NOT NULL)),
  UNIQUE (import_batch_id, legacy_shift_id, direction)
) STRICT;

CREATE INDEX punch_employee_time ON punch (employee_id, occurred_utc, id);

-- A manager's change to the punch record. The row carries both sides:
--   add:    target_punch_id NULL, new_* set  -> trigger inserts the replacement punch
--   adjust: target_punch_id set,  new_* set  -> target superseded, trigger inserts replacement
--   void:   target_punch_id set,  new_* NULL -> target superseded, nothing replaces it
-- UNIQUE(target_punch_id): a punch is superseded at most once, so the history of any
-- punch is a linear chain. Re-correcting means targeting the latest replacement.
CREATE TABLE punch_correction (
  id                     INTEGER PRIMARY KEY,
  action                 TEXT NOT NULL CHECK (action IN ('add', 'adjust', 'void')),
  employee_id            INTEGER NOT NULL REFERENCES employee(id),
  target_punch_id        INTEGER UNIQUE REFERENCES punch(id),
  new_direction          TEXT CHECK (new_direction IN ('IN', 'OUT')),
  new_occurred_utc       TEXT CHECK (new_occurred_utc IS NULL
                                     OR strftime('%Y-%m-%dT%H:%M:%fZ', new_occurred_utc) IS new_occurred_utc),
  new_utc_offset_minutes INTEGER CHECK (new_utc_offset_minutes BETWEEN -840 AND 840),
  reason                 TEXT NOT NULL CHECK (length(trim(reason)) >= 10),
  actor_user_id          INTEGER NOT NULL REFERENCES app_user(id),
  created_utc            TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  CHECK ((action = 'add') = (target_punch_id IS NULL)),
  CHECK (CASE action
           WHEN 'void' THEN new_direction IS NULL AND new_occurred_utc IS NULL AND new_utc_offset_minutes IS NULL
           ELSE new_direction IS NOT NULL AND new_occurred_utc IS NOT NULL AND new_utc_offset_minutes IS NOT NULL
         END)
) STRICT;

CREATE INDEX punch_correction_employee ON punch_correction (employee_id, created_utc);

-- Signed chain-head checkpoints. The same (seq, hash) must also leave the machine.
CREATE TABLE audit_checkpoint (
  id                 INTEGER PRIMARY KEY,
  created_utc        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  audit_seq          INTEGER NOT NULL,
  audit_hash         TEXT NOT NULL,
  schema_fingerprint TEXT NOT NULL,
  key_id             TEXT NOT NULL,
  signature          TEXT NOT NULL                -- base64 signature over the canonical tuple
) STRICT;

-- ===========================================================================
-- Audit log: append-only, hash-chained, sealed by its own trigger
-- ===========================================================================

CREATE TABLE audit_log (
  seq          INTEGER PRIMARY KEY,               -- contiguous 1..n
  occurred_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  actor_kind   TEXT NOT NULL CHECK (actor_kind IN ('employee', 'user')),
  actor_id     INTEGER NOT NULL,
  client       TEXT NOT NULL,
  action       TEXT NOT NULL CHECK (action IN (
                 'INSERT', 'UPDATE',
                 'AUTH_LOGIN', 'AUTH_LOGIN_FAILED', 'AUTH_LOGOUT', 'AUTH_PIN_FAILED',
                 'REPORT_EXPORT', 'BACKUP', 'VERIFY', 'APP_START', 'APP_STOP', 'SCHEMA_MIGRATE')),
  table_name   TEXT,
  row_id       INTEGER,
  before_json  TEXT CHECK (before_json IS NULL OR json_valid(before_json)),
  after_json   TEXT CHECK (after_json IS NULL OR json_valid(after_json)),
  reason       TEXT,
  prev_hash    TEXT,                              -- written only by the seal
  row_hash     TEXT,                              -- written only by the seal
  CHECK ((action IN ('INSERT', 'UPDATE')) = (table_name IS NOT NULL AND row_id IS NOT NULL))
) STRICT;

CREATE INDEX audit_log_row ON audit_log (table_name, row_id, seq);

-- The one definition of what row_hash covers, used by the seal and the verifier.
-- Each field is encoded as N; (NULL), I<decimal>; (integer) or T<utf8 byte length>:<text>;
-- so the hash can be recomputed in any language without SQLite's JSON serializer.
CREATE VIEW audit_canonical_v AS
SELECT seq,
       'I' || seq || ';'
    || 'T' || length(CAST(occurred_utc AS BLOB)) || ':' || occurred_utc || ';'
    || 'T' || length(CAST(actor_kind AS BLOB)) || ':' || actor_kind || ';'
    || 'I' || actor_id || ';'
    || 'T' || length(CAST(client AS BLOB)) || ':' || client || ';'
    || 'T' || length(CAST(action AS BLOB)) || ':' || action || ';'
    || CASE WHEN table_name  IS NULL THEN 'N;' ELSE 'T' || length(CAST(table_name AS BLOB)) || ':' || table_name || ';' END
    || CASE WHEN row_id      IS NULL THEN 'N;' ELSE 'I' || row_id || ';' END
    || CASE WHEN before_json IS NULL THEN 'N;' ELSE 'T' || length(CAST(before_json AS BLOB)) || ':' || before_json || ';' END
    || CASE WHEN after_json  IS NULL THEN 'N;' ELSE 'T' || length(CAST(after_json AS BLOB)) || ':' || after_json || ';' END
    || CASE WHEN reason      IS NULL THEN 'N;' ELSE 'T' || length(CAST(reason AS BLOB)) || ':' || reason || ';' END
    || CASE WHEN prev_hash   IS NULL THEN 'N;' ELSE 'T' || length(CAST(prev_hash AS BLOB)) || ':' || prev_hash || ';' END
       AS canonical
FROM audit_log;

CREATE TRIGGER audit_log_bi BEFORE INSERT ON audit_log BEGIN
  SELECT RAISE(ABORT, 'audit_log: prev_hash and row_hash are written by the seal')
   WHERE NEW.prev_hash IS NOT NULL OR NEW.row_hash IS NOT NULL;
  SELECT RAISE(ABORT, 'audit_log: actor does not match the connection context')
   WHERE NEW.actor_kind IS NOT pc_ctx('actor_kind') OR NEW.actor_id IS NOT pc_ctx('actor_id');
  SELECT RAISE(ABORT, 'audit_log: client does not match the connection context')
   WHERE NEW.client IS NOT pc_ctx('client');
  SELECT RAISE(ABORT, 'audit_log: occurred_utc is the database clock, not caller-supplied')
   WHERE NEW.occurred_utc IS NOT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
  -- Table events must be exactly what the table's own trigger would write, so a caller
  -- cannot fabricate one to cover an edit made with triggers dropped.
  SELECT RAISE(ABORT, 'audit_log: after_json must be the current image of the row')
   WHERE NEW.action IN ('INSERT', 'UPDATE')
     AND NEW.after_json IS NOT (SELECT j FROM all_snapshot_v
                                 WHERE table_name = NEW.table_name AND row_id = NEW.row_id);
  SELECT RAISE(ABORT, 'audit_log: row id already has history (deleted row reused)')
   WHERE NEW.action = 'INSERT'
     AND EXISTS (SELECT 1 FROM audit_log WHERE table_name = NEW.table_name AND row_id = NEW.row_id);
  SELECT RAISE(ABORT, 'audit_log: row was not created in this statement')
   WHERE NEW.action = 'INSERT'
     AND COALESCE(json_extract(NEW.after_json, '$.created_utc'), json_extract(NEW.after_json, '$.recorded_utc'),
                  json_extract(NEW.after_json, '$.started_utc'), NEW.occurred_utc) IS NOT NEW.occurred_utc;
  SELECT RAISE(ABORT, 'audit_log: UPDATE events exist only for mutable tables')
   WHERE NEW.action = 'UPDATE' AND NEW.table_name NOT IN ('site_setting', 'employee', 'app_user', 'import_batch');
  SELECT RAISE(ABORT, 'audit_log: before_json must be the previous audited image')
   WHERE NEW.action = 'UPDATE'
     AND NEW.before_json IS NOT (SELECT after_json FROM audit_log
                                  WHERE table_name = NEW.table_name AND row_id = NEW.row_id
                                    AND action IN ('INSERT', 'UPDATE')
                                  ORDER BY seq DESC LIMIT 1);
  SELECT RAISE(ABORT, 'audit_log: UPDATE changes an immutable field')
   WHERE NEW.action = 'UPDATE'
     AND (EXISTS (SELECT 1 FROM json_each('["$.id","$.key","$.legacy_id","$.created_utc","$.started_utc",
                                          "$.source_file_name","$.source_sha256","$.manifest_sha256",
                                          "$.source_time_zone_id","$.tool_version",
                                          "$.manifest_employee_rows","$.manifest_shift_rows"]') f
                   WHERE json_extract(NEW.before_json, f.value) IS NOT json_extract(NEW.after_json, f.value))
          OR json_extract(NEW.before_json, '$.completed_utc') IS NOT NULL
             AND json_extract(NEW.before_json, '$.completed_utc') IS NOT json_extract(NEW.after_json, '$.completed_utc')
          OR (json_extract(NEW.before_json, '$.role') IN ('system', 'migration')
              OR json_extract(NEW.after_json, '$.role') IN ('system', 'migration'))
             AND json_extract(NEW.before_json, '$.role') IS NOT json_extract(NEW.after_json, '$.role'));
END;

-- Seal: refuse gaps, link to the predecessor, then hash the canonical form.
CREATE TRIGGER audit_log_ai AFTER INSERT ON audit_log BEGIN
  SELECT RAISE(ABORT, 'audit_log: seq must be contiguous')
   WHERE NEW.seq < 1
      OR (NEW.seq > 1 AND NOT EXISTS (SELECT 1 FROM audit_log WHERE seq = NEW.seq - 1))
      OR EXISTS (SELECT 1 FROM audit_log WHERE seq > NEW.seq);
  UPDATE audit_log
     SET prev_hash = COALESCE((SELECT row_hash FROM audit_log WHERE seq = NEW.seq - 1),
                              '0000000000000000000000000000000000000000000000000000000000000000')
   WHERE seq = NEW.seq;
  UPDATE audit_log
     SET row_hash = (SELECT pc_sha256(canonical) FROM audit_canonical_v WHERE seq = NEW.seq)
   WHERE seq = NEW.seq;
END;

-- Only the seal may update, and only NULL -> value: prev_hash first, then row_hash.
CREATE TRIGGER audit_log_bu BEFORE UPDATE ON audit_log BEGIN
  SELECT RAISE(ABORT, 'audit_log is append-only')
   WHERE NOT (
         OLD.row_hash IS NULL
     AND NEW.prev_hash IS NOT NULL
     AND (OLD.prev_hash IS NULL OR NEW.prev_hash IS OLD.prev_hash)
     AND (OLD.prev_hash IS NOT NULL OR NEW.row_hash IS NULL)
     AND NEW.seq IS OLD.seq AND NEW.occurred_utc IS OLD.occurred_utc
     AND NEW.actor_kind IS OLD.actor_kind AND NEW.actor_id IS OLD.actor_id
     AND NEW.client IS OLD.client AND NEW.action IS OLD.action
     AND NEW.table_name IS OLD.table_name AND NEW.row_id IS OLD.row_id
     AND NEW.before_json IS OLD.before_json AND NEW.after_json IS OLD.after_json
     AND NEW.reason IS OLD.reason);
END;

CREATE TRIGGER audit_log_bd BEFORE DELETE ON audit_log BEGIN
  SELECT RAISE(ABORT, 'audit_log is append-only');
END;

-- ===========================================================================
-- Row snapshots: the one definition of each row's audited image, used by the
-- AFTER triggers (after_json) and by the drift verifier. Credential hashes appear
-- only as a digest, so the log proves a change without keeping the credential.
-- ===========================================================================

CREATE VIEW site_setting_snapshot_v AS
SELECT id, json_object('id', id, 'key', key, 'value', value) AS j
FROM site_setting;

CREATE VIEW employee_snapshot_v AS
SELECT id, json_object('id', id, 'legacy_id', legacy_id, 'first_name', first_name, 'last_name', last_name,
                       'pin_hash_digest', pc_sha256(pin_hash), 'pin_must_change', pin_must_change,
                       'is_active', is_active, 'created_utc', created_utc) AS j
FROM employee;

CREATE VIEW app_user_snapshot_v AS
SELECT id, json_object('id', id, 'username', username, 'display_name', display_name, 'role', role,
                       'password_hash_digest', pc_sha256(password_hash), 'employee_id', employee_id,
                       'must_change_password', must_change_password, 'is_active', is_active,
                       'created_utc', created_utc) AS j
FROM app_user;

CREATE VIEW import_batch_snapshot_v AS
SELECT id, json_object('id', id, 'source_file_name', source_file_name, 'source_sha256', source_sha256,
                       'manifest_sha256', manifest_sha256, 'source_time_zone_id', source_time_zone_id,
                       'tool_version', tool_version, 'manifest_employee_rows', manifest_employee_rows,
                       'manifest_shift_rows', manifest_shift_rows, 'started_utc', started_utc,
                       'completed_utc', completed_utc) AS j
FROM import_batch;

CREATE VIEW legacy_employee_raw_snapshot_v AS
SELECT id, json_object('id', id, 'import_batch_id', import_batch_id, 'legacy_employee_id', legacy_employee_id,
                       'first_name', first_name, 'last_name', last_name, 'is_active', is_active,
                       'pin_digits', pin_digits) AS j
FROM legacy_employee_raw;

CREATE VIEW legacy_shift_raw_snapshot_v AS
SELECT id, json_object('id', id, 'import_batch_id', import_batch_id, 'legacy_shift_id', legacy_shift_id,
                       'legacy_employee_id', legacy_employee_id,
                       'time_in_local', time_in_local, 'time_in_oadate', time_in_oadate,
                       'time_out_local', time_out_local, 'time_out_oadate', time_out_oadate) AS j
FROM legacy_shift_raw;

CREATE VIEW migration_issue_snapshot_v AS
SELECT id, json_object('id', id, 'import_batch_id', import_batch_id, 'legacy_table', legacy_table,
                       'legacy_pk', legacy_pk, 'code', code, 'disposition', disposition,
                       'detail_json', detail_json) AS j
FROM migration_issue;

CREATE VIEW punch_snapshot_v AS
SELECT id, json_object('id', id, 'employee_id', employee_id, 'direction', direction,
                       'occurred_utc', occurred_utc, 'utc_offset_minutes', utc_offset_minutes,
                       'recorded_utc', recorded_utc, 'source', source, 'correction_id', correction_id,
                       'import_batch_id', import_batch_id, 'legacy_shift_id', legacy_shift_id) AS j
FROM punch;

CREATE VIEW punch_correction_snapshot_v AS
SELECT id, json_object('id', id, 'action', action, 'employee_id', employee_id,
                       'target_punch_id', target_punch_id, 'new_direction', new_direction,
                       'new_occurred_utc', new_occurred_utc, 'new_utc_offset_minutes', new_utc_offset_minutes,
                       'reason', reason, 'actor_user_id', actor_user_id, 'created_utc', created_utc) AS j
FROM punch_correction;

CREATE VIEW audit_checkpoint_snapshot_v AS
SELECT id, json_object('id', id, 'created_utc', created_utc, 'audit_seq', audit_seq, 'audit_hash', audit_hash,
                       'schema_fingerprint', schema_fingerprint, 'key_id', key_id,
                       'signature', signature) AS j
FROM audit_checkpoint;

CREATE VIEW all_snapshot_v AS
          SELECT 'site_setting'        AS table_name, id AS row_id, j FROM site_setting_snapshot_v
UNION ALL SELECT 'employee',            id, j FROM employee_snapshot_v
UNION ALL SELECT 'app_user',            id, j FROM app_user_snapshot_v
UNION ALL SELECT 'import_batch',        id, j FROM import_batch_snapshot_v
UNION ALL SELECT 'legacy_employee_raw', id, j FROM legacy_employee_raw_snapshot_v
UNION ALL SELECT 'legacy_shift_raw',    id, j FROM legacy_shift_raw_snapshot_v
UNION ALL SELECT 'migration_issue',     id, j FROM migration_issue_snapshot_v
UNION ALL SELECT 'punch',               id, j FROM punch_snapshot_v
UNION ALL SELECT 'punch_correction',    id, j FROM punch_correction_snapshot_v
UNION ALL SELECT 'audit_checkpoint',    id, j FROM audit_checkpoint_snapshot_v;

-- What each raw legacy shift may become: an IN at its TimeIn and an OUT at its TimeOut,
-- in local wall time. A shift recorded as DST_INVALID may instead be one hour later
-- (moved out of the spring-forward gap); the importer records that issue first.
CREATE VIEW legacy_punch_source_v AS
SELECT d.import_batch_id, d.legacy_shift_id, d.legacy_employee_id, d.direction, d.wall_local,
       CASE WHEN EXISTS (SELECT 1 FROM migration_issue i
                          WHERE i.import_batch_id = d.import_batch_id AND i.legacy_table = 'Shift'
                            AND i.legacy_pk = d.legacy_shift_id AND i.code = 'DST_INVALID')
            THEN strftime('%Y-%m-%dT%H:%M:%f', d.wall_local, '+60 minutes') END AS wall_local_dst_shifted
  FROM (SELECT import_batch_id, legacy_shift_id, legacy_employee_id, 'IN' AS direction, time_in_local AS wall_local
          FROM legacy_shift_raw
        UNION ALL
        SELECT import_batch_id, legacy_shift_id, legacy_employee_id, 'OUT', time_out_local
          FROM legacy_shift_raw) d
 WHERE d.wall_local IS NOT NULL;

-- Raw rows the import has not accounted for: every raw employee becomes an employee with
-- that legacy_id and every raw shift becomes its punches (IN, plus OUT when TimeOut is
-- set), unless a SKIPPED migration_issue says why not. A batch cannot close while any remain.
CREATE VIEW import_unreconciled_v AS
SELECT r.import_batch_id, 'Employee' AS legacy_table, r.legacy_employee_id AS legacy_pk,
       'no employee with this legacy_id' AS problem
  FROM legacy_employee_raw r
 WHERE NOT EXISTS (SELECT 1 FROM employee e WHERE e.legacy_id = r.legacy_employee_id)
   AND NOT EXISTS (SELECT 1 FROM migration_issue i
                    WHERE i.import_batch_id = r.import_batch_id AND i.legacy_table = 'Employee'
                      AND i.legacy_pk = r.legacy_employee_id AND i.disposition = 'SKIPPED')
UNION ALL
SELECT v.import_batch_id, 'Shift', v.legacy_shift_id, 'no ' || v.direction || ' punch for this shift'
  FROM legacy_punch_source_v v
 WHERE NOT EXISTS (SELECT 1 FROM punch p
                    WHERE p.source = 'legacy_import' AND p.import_batch_id = v.import_batch_id
                      AND p.legacy_shift_id = v.legacy_shift_id AND p.direction = v.direction)
   AND NOT EXISTS (SELECT 1 FROM migration_issue i
                    WHERE i.import_batch_id = v.import_batch_id AND i.legacy_table = 'Shift'
                      AND i.legacy_pk = v.legacy_shift_id AND i.disposition = 'SKIPPED');

-- The connection's actor when it is an active app_user; empty otherwise.
CREATE VIEW ctx_user_v AS
SELECT u.* FROM app_user u
WHERE pc_ctx('actor_kind') = 'user' AND u.id = pc_ctx('actor_id') AND u.is_active = 1;

-- ===========================================================================
-- Write rules and audit triggers, per table
-- ===========================================================================

-- site_setting: admin or system; audited updates; no deletes.
CREATE TRIGGER site_setting_bi BEFORE INSERT ON site_setting BEGIN
  SELECT RAISE(ABORT, 'site_setting: admin only')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role IN ('system', 'admin'));
  SELECT RAISE(ABORT, 'site_setting: time_zone_id must be a known Windows time zone id')
   WHERE NEW.key = 'time_zone_id' AND NEW.value IS NOT 'UNSET'
     AND pc_utc_offset(NEW.value, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')) IS NULL;
  SELECT RAISE(ABORT, 'site_setting: max_shift_hours must be a whole number from 1 to 48')
   WHERE NEW.key = 'max_shift_hours'
     AND NOT (NEW.value GLOB '[1-9]*' AND NEW.value NOT GLOB '*[^0-9]*' AND length(NEW.value) <= 2
              AND CAST(NEW.value AS INTEGER) BETWEEN 1 AND 48);
END;
CREATE TRIGGER site_setting_ai AFTER INSERT ON site_setting BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'INSERT', 'site_setting', NEW.id,
         NULL, s.j, pc_ctx('reason')
    FROM site_setting_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER site_setting_bu BEFORE UPDATE ON site_setting BEGIN
  SELECT RAISE(ABORT, 'site_setting: admin only')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role IN ('system', 'admin'));
  SELECT RAISE(ABORT, 'site_setting: id and key are immutable')
   WHERE NEW.id IS NOT OLD.id OR NEW.key IS NOT OLD.key;
  SELECT RAISE(ABORT, 'site_setting: time_zone_id must be a known Windows time zone id')
   WHERE NEW.key = 'time_zone_id'
     AND pc_utc_offset(NEW.value, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')) IS NULL;
  SELECT RAISE(ABORT, 'site_setting: max_shift_hours must be a whole number from 1 to 48')
   WHERE NEW.key = 'max_shift_hours'
     AND NOT (NEW.value GLOB '[1-9]*' AND NEW.value NOT GLOB '*[^0-9]*' AND length(NEW.value) <= 2
              AND CAST(NEW.value AS INTEGER) BETWEEN 1 AND 48);
END;
CREATE TRIGGER site_setting_au AFTER UPDATE ON site_setting BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'UPDATE', 'site_setting', NEW.id,
         json_object('id', OLD.id, 'key', OLD.key, 'value', OLD.value), s.j, pc_ctx('reason')
    FROM site_setting_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER site_setting_bd BEFORE DELETE ON site_setting BEGIN
  SELECT RAISE(ABORT, 'site_setting rows cannot be deleted');
END;

-- employee: managers, admins and the system account create and edit;
-- an employee may change only their own PIN. Never deleted.
CREATE TRIGGER employee_bi BEFORE INSERT ON employee BEGIN
  SELECT RAISE(ABORT, 'employee: created_utc is the database clock, not caller-supplied')
   WHERE NEW.created_utc IS NOT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
  SELECT RAISE(ABORT, 'employee: manager, admin or migration only')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role IN ('system', 'migration', 'admin', 'manager'));
  SELECT RAISE(ABORT, 'employee: an imported employee must start with pin_must_change = 1')
   WHERE NEW.legacy_id IS NOT NULL AND NEW.pin_must_change IS NOT 1;
END;
CREATE TRIGGER employee_ai AFTER INSERT ON employee BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'INSERT', 'employee', NEW.id,
         NULL, s.j, pc_ctx('reason')
    FROM employee_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER employee_bu BEFORE UPDATE ON employee BEGIN
  SELECT RAISE(ABORT, 'employee: id, legacy_id and created_utc are immutable')
   WHERE NEW.id IS NOT OLD.id OR NEW.legacy_id IS NOT OLD.legacy_id OR NEW.created_utc IS NOT OLD.created_utc;
  SELECT RAISE(ABORT, 'employee: not authorized')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role IN ('system', 'admin', 'manager'))
     AND NOT (pc_ctx('actor_kind') = 'employee' AND pc_ctx('actor_id') = OLD.id
              AND NEW.first_name IS OLD.first_name AND NEW.last_name IS OLD.last_name
              AND NEW.is_active IS OLD.is_active AND NEW.pin_must_change = 0
              AND NEW.pin_hash IS NOT OLD.pin_hash);
END;
CREATE TRIGGER employee_au AFTER UPDATE ON employee BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'UPDATE', 'employee', NEW.id,
         json_object('id', OLD.id, 'legacy_id', OLD.legacy_id, 'first_name', OLD.first_name,
                     'last_name', OLD.last_name, 'pin_hash_digest', pc_sha256(OLD.pin_hash),
                     'pin_must_change', OLD.pin_must_change, 'is_active', OLD.is_active,
                     'created_utc', OLD.created_utc),
         s.j, pc_ctx('reason')
    FROM employee_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER employee_bd BEFORE DELETE ON employee BEGIN
  SELECT RAISE(ABORT, 'employee rows cannot be deleted; set is_active = 0');
END;

-- app_user: admins and the system account manage people's accounts; a user may change
-- only their own password. The two service accounts are created once, at bootstrap:
-- system (id 1) and migration (id 2, used only by the legacy importer).
CREATE TRIGGER app_user_bi BEFORE INSERT ON app_user BEGIN
  SELECT RAISE(ABORT, 'app_user: created_utc is the database clock, not caller-supplied')
   WHERE NEW.created_utc IS NOT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
  SELECT RAISE(ABORT, 'app_user: not authorized')
   WHERE NOT (
         (NEW.role IN ('admin', 'manager')
          AND EXISTS (SELECT 1 FROM ctx_user_v WHERE role IN ('system', 'admin')))
      OR (NEW.role = 'system' AND NEW.id = 1 AND NOT EXISTS (SELECT 1 FROM app_user))
      OR (NEW.role = 'migration' AND NEW.id = 2
          AND EXISTS (SELECT 1 FROM ctx_user_v WHERE role = 'system')));
END;
CREATE TRIGGER app_user_ai AFTER INSERT ON app_user BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'INSERT', 'app_user', NEW.id,
         NULL, s.j, pc_ctx('reason')
    FROM app_user_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER app_user_bu BEFORE UPDATE ON app_user BEGIN
  SELECT RAISE(ABORT, 'app_user: id, created_utc and service-account roles are immutable')
   WHERE NEW.id IS NOT OLD.id OR NEW.created_utc IS NOT OLD.created_utc
      OR ((OLD.role IN ('system', 'migration') OR NEW.role IN ('system', 'migration')) AND NEW.role IS NOT OLD.role);
  SELECT RAISE(ABORT, 'app_user: the last active admin cannot be deactivated or demoted')
   WHERE OLD.role = 'admin' AND OLD.is_active = 1
     AND (NEW.role IS NOT 'admin' OR NEW.is_active IS NOT 1)
     AND NOT EXISTS (SELECT 1 FROM app_user WHERE role = 'admin' AND is_active = 1 AND id <> OLD.id);
  SELECT RAISE(ABORT, 'app_user: an account cannot change its own employee link')
   WHERE pc_ctx('actor_kind') = 'user' AND pc_ctx('actor_id') = OLD.id
     AND NEW.employee_id IS NOT OLD.employee_id;
  SELECT RAISE(ABORT, 'app_user: not authorized')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role IN ('system', 'admin'))
     AND NOT (pc_ctx('actor_kind') = 'user' AND pc_ctx('actor_id') = OLD.id AND OLD.is_active = 1
              AND NEW.username IS OLD.username AND NEW.display_name IS OLD.display_name
              AND NEW.role IS OLD.role AND NEW.employee_id IS OLD.employee_id
              AND NEW.is_active IS OLD.is_active AND NEW.must_change_password = 0
              AND NEW.password_hash IS NOT OLD.password_hash);
END;
CREATE TRIGGER app_user_au AFTER UPDATE ON app_user BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'UPDATE', 'app_user', NEW.id,
         json_object('id', OLD.id, 'username', OLD.username, 'display_name', OLD.display_name,
                     'role', OLD.role, 'password_hash_digest', pc_sha256(OLD.password_hash),
                     'employee_id', OLD.employee_id, 'must_change_password', OLD.must_change_password,
                     'is_active', OLD.is_active, 'created_utc', OLD.created_utc),
         s.j, pc_ctx('reason')
    FROM app_user_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER app_user_bd BEFORE DELETE ON app_user BEGIN
  SELECT RAISE(ABORT, 'app_user rows cannot be deleted; set is_active = 0');
END;

-- import_batch: migration account only. Its INSERT audit row is the import's anchor event:
-- it carries the source .accdb SHA-256, the manifest SHA-256 and the manifest row counts.
-- The one permitted update closes an open batch, and only once the raw tables reconcile.
CREATE TRIGGER import_batch_bi BEFORE INSERT ON import_batch BEGIN
  SELECT RAISE(ABORT, 'import_batch: migration account only')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role = 'migration');
  SELECT RAISE(ABORT, 'import_batch: must start open') WHERE NEW.completed_utc IS NOT NULL;
  SELECT RAISE(ABORT, 'import_batch: started_utc is the database clock, not caller-supplied')
   WHERE NEW.started_utc IS NOT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
END;
CREATE TRIGGER import_batch_ai AFTER INSERT ON import_batch BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'INSERT', 'import_batch', NEW.id,
         NULL, s.j, pc_ctx('reason')
    FROM import_batch_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER import_batch_bu BEFORE UPDATE ON import_batch BEGIN
  SELECT RAISE(ABORT, 'import_batch: only closing an open batch is allowed')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role = 'migration')
      OR OLD.completed_utc IS NOT NULL OR NEW.completed_utc IS NULL
      OR NEW.id IS NOT OLD.id OR NEW.source_file_name IS NOT OLD.source_file_name
      OR NEW.source_sha256 IS NOT OLD.source_sha256 OR NEW.manifest_sha256 IS NOT OLD.manifest_sha256
      OR NEW.source_time_zone_id IS NOT OLD.source_time_zone_id
      OR NEW.tool_version IS NOT OLD.tool_version OR NEW.started_utc IS NOT OLD.started_utc
      OR NEW.manifest_employee_rows IS NOT OLD.manifest_employee_rows
      OR NEW.manifest_shift_rows IS NOT OLD.manifest_shift_rows;
  SELECT RAISE(ABORT, 'import_batch: completed_utc is the database clock, not caller-supplied')
   WHERE NEW.completed_utc IS NOT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
  SELECT RAISE(ABORT, 'import_batch: raw row counts do not match the manifest')
   WHERE (SELECT count(*) FROM legacy_employee_raw WHERE import_batch_id = OLD.id) <> OLD.manifest_employee_rows
      OR (SELECT count(*) FROM legacy_shift_raw WHERE import_batch_id = OLD.id) <> OLD.manifest_shift_rows;
  SELECT RAISE(ABORT, 'import_batch: a raw row has neither its imported result nor a SKIPPED issue')
   WHERE EXISTS (SELECT 1 FROM import_unreconciled_v WHERE import_batch_id = OLD.id);
END;
CREATE TRIGGER import_batch_au AFTER UPDATE ON import_batch BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'UPDATE', 'import_batch', NEW.id,
         json_object('id', OLD.id, 'source_file_name', OLD.source_file_name, 'source_sha256', OLD.source_sha256,
                     'manifest_sha256', OLD.manifest_sha256, 'source_time_zone_id', OLD.source_time_zone_id,
                     'tool_version', OLD.tool_version, 'manifest_employee_rows', OLD.manifest_employee_rows,
                     'manifest_shift_rows', OLD.manifest_shift_rows, 'started_utc', OLD.started_utc,
                     'completed_utc', OLD.completed_utc),
         s.j, pc_ctx('reason')
    FROM import_batch_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER import_batch_bd BEFORE DELETE ON import_batch BEGIN
  SELECT RAISE(ABORT, 'import_batch rows cannot be deleted');
END;

-- Import evidence tables: insert-only, migration account, open batch.
CREATE TRIGGER legacy_employee_raw_bi BEFORE INSERT ON legacy_employee_raw BEGIN
  SELECT RAISE(ABORT, 'legacy_employee_raw: migration account and open batch required')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role = 'migration')
      OR NOT EXISTS (SELECT 1 FROM import_batch WHERE id = NEW.import_batch_id AND completed_utc IS NULL);
END;
CREATE TRIGGER legacy_employee_raw_ai AFTER INSERT ON legacy_employee_raw BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'INSERT', 'legacy_employee_raw', NEW.id,
         NULL, s.j, pc_ctx('reason')
    FROM legacy_employee_raw_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER legacy_employee_raw_bu BEFORE UPDATE ON legacy_employee_raw BEGIN
  SELECT RAISE(ABORT, 'legacy_employee_raw is immutable');
END;
CREATE TRIGGER legacy_employee_raw_bd BEFORE DELETE ON legacy_employee_raw BEGIN
  SELECT RAISE(ABORT, 'legacy_employee_raw is immutable');
END;

CREATE TRIGGER legacy_shift_raw_bi BEFORE INSERT ON legacy_shift_raw BEGIN
  SELECT RAISE(ABORT, 'legacy_shift_raw: migration account and open batch required')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role = 'migration')
      OR NOT EXISTS (SELECT 1 FROM import_batch WHERE id = NEW.import_batch_id AND completed_utc IS NULL);
END;
CREATE TRIGGER legacy_shift_raw_ai AFTER INSERT ON legacy_shift_raw BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'INSERT', 'legacy_shift_raw', NEW.id,
         NULL, s.j, pc_ctx('reason')
    FROM legacy_shift_raw_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER legacy_shift_raw_bu BEFORE UPDATE ON legacy_shift_raw BEGIN
  SELECT RAISE(ABORT, 'legacy_shift_raw is immutable');
END;
CREATE TRIGGER legacy_shift_raw_bd BEFORE DELETE ON legacy_shift_raw BEGIN
  SELECT RAISE(ABORT, 'legacy_shift_raw is immutable');
END;

CREATE TRIGGER migration_issue_bi BEFORE INSERT ON migration_issue BEGIN
  SELECT RAISE(ABORT, 'migration_issue: migration account and open batch required')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role = 'migration')
      OR NOT EXISTS (SELECT 1 FROM import_batch WHERE id = NEW.import_batch_id AND completed_utc IS NULL);
END;
CREATE TRIGGER migration_issue_ai AFTER INSERT ON migration_issue BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'INSERT', 'migration_issue', NEW.id,
         NULL, s.j, pc_ctx('reason')
    FROM migration_issue_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER migration_issue_bu BEFORE UPDATE ON migration_issue BEGIN
  SELECT RAISE(ABORT, 'migration_issue is immutable');
END;
CREATE TRIGGER migration_issue_bd BEFORE DELETE ON migration_issue BEGIN
  SELECT RAISE(ABORT, 'migration_issue is immutable');
END;

-- punch: insert-only. Each source has its own admission rule.
CREATE TRIGGER punch_bi BEFORE INSERT ON punch BEGIN
  SELECT RAISE(ABORT, 'punch: recorded_utc is the database clock, not caller-supplied')
   WHERE NEW.recorded_utc IS NOT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
  SELECT RAISE(ABORT, 'punch: kiosk punches must be made by the punching employee')
   WHERE NEW.source = 'kiosk'
     AND NOT (pc_ctx('actor_kind') = 'employee' AND pc_ctx('actor_id') = NEW.employee_id);
  SELECT RAISE(ABORT, 'punch: employee is inactive')
   WHERE NEW.source = 'kiosk'
     AND NOT EXISTS (SELECT 1 FROM employee WHERE id = NEW.employee_id AND is_active = 1);
  SELECT RAISE(ABORT, 'punch: occurred_utc is in the future')
   WHERE NEW.occurred_utc > NEW.recorded_utc;
  SELECT RAISE(ABORT, 'punch: kiosk time must be the current time')
   WHERE NEW.source = 'kiosk'
     AND (julianday(NEW.recorded_utc) - julianday(NEW.occurred_utc)) * 86400 > 120;
  SELECT RAISE(ABORT, 'punch: site time zone is not configured')
   WHERE NEW.source IN ('kiosk', 'correction')
     AND (SELECT value FROM site_setting WHERE key = 'time_zone_id') IS 'UNSET';
  SELECT RAISE(ABORT, 'punch: utc_offset_minutes does not match the time zone at that instant')
   WHERE NEW.utc_offset_minutes IS NOT pc_utc_offset(
           CASE WHEN NEW.source = 'legacy_import'
                THEN (SELECT source_time_zone_id FROM import_batch WHERE id = NEW.import_batch_id)
                ELSE (SELECT value FROM site_setting WHERE key = 'time_zone_id') END,
           NEW.occurred_utc);
  SELECT RAISE(ABORT, 'punch: correction punch does not match its correction')
   WHERE NEW.source = 'correction'
     AND NOT EXISTS (SELECT 1 FROM punch_correction c
                      WHERE c.id = NEW.correction_id AND c.action <> 'void'
                        AND c.employee_id = NEW.employee_id AND c.new_direction = NEW.direction
                        AND c.new_occurred_utc = NEW.occurred_utc
                        AND c.new_utc_offset_minutes = NEW.utc_offset_minutes);
  SELECT RAISE(ABORT, 'punch: legacy_import punches need the migration account and an open batch')
   WHERE NEW.source = 'legacy_import'
     AND (NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role = 'migration')
          OR NOT EXISTS (SELECT 1 FROM import_batch WHERE id = NEW.import_batch_id AND completed_utc IS NULL));
  SELECT RAISE(ABORT, 'punch: legacy_import punch does not match its raw legacy shift')
   WHERE NEW.source = 'legacy_import'
     AND NOT EXISTS (SELECT 1 FROM legacy_punch_source_v v
                      WHERE v.import_batch_id = NEW.import_batch_id AND v.legacy_shift_id = NEW.legacy_shift_id
                        AND v.direction = NEW.direction
                        AND v.legacy_employee_id = (SELECT legacy_id FROM employee WHERE id = NEW.employee_id)
                        AND strftime('%Y-%m-%dT%H:%M:%f', NEW.occurred_utc, NEW.utc_offset_minutes || ' minutes')
                            IN (v.wall_local, v.wall_local_dst_shifted));
  -- A fall-back wall time occurs twice; section 9 takes the earlier, daylight occurrence.
  -- The later one is recognisable: one hour before it the same wall time already happened.
  SELECT RAISE(ABORT, 'punch: ambiguous local time must use the earlier (daylight) occurrence')
   WHERE NEW.source = 'legacy_import'
     AND pc_utc_offset((SELECT source_time_zone_id FROM import_batch WHERE id = NEW.import_batch_id),
                       strftime('%Y-%m-%dT%H:%M:%fZ', NEW.occurred_utc, '-60 minutes'))
         IS NEW.utc_offset_minutes + 60;
END;
CREATE TRIGGER punch_ai AFTER INSERT ON punch BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'INSERT', 'punch', NEW.id, NULL, s.j,
         COALESCE((SELECT reason FROM punch_correction WHERE id = NEW.correction_id), pc_ctx('reason'))
    FROM punch_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER punch_bu BEFORE UPDATE ON punch BEGIN
  SELECT RAISE(ABORT, 'punch rows are immutable; insert a punch_correction');
END;
CREATE TRIGGER punch_bd BEFORE DELETE ON punch BEGIN
  SELECT RAISE(ABORT, 'punch rows are immutable; insert a punch_correction');
END;

-- punch_correction: insert-only, by the active manager/admin in context, never on
-- their own punches. The replacement punch is created here, in the same statement,
-- so a correction can never exist without its "after".
CREATE TRIGGER punch_correction_bi BEFORE INSERT ON punch_correction BEGIN
  SELECT RAISE(ABORT, 'punch_correction: created_utc is the database clock, not caller-supplied')
   WHERE NEW.created_utc IS NOT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
  SELECT RAISE(ABORT, 'punch_correction: actor must be the active manager or admin in context')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role IN ('admin', 'manager') AND id = NEW.actor_user_id);
  SELECT RAISE(ABORT, 'punch_correction: managers cannot correct their own punches')
   WHERE EXISTS (SELECT 1 FROM app_user WHERE id = NEW.actor_user_id AND employee_id = NEW.employee_id);
  SELECT RAISE(ABORT, 'punch_correction: target punch belongs to another employee')
   WHERE NEW.target_punch_id IS NOT NULL
     AND NOT EXISTS (SELECT 1 FROM punch WHERE id = NEW.target_punch_id AND employee_id = NEW.employee_id);
END;
CREATE TRIGGER punch_correction_ai AFTER INSERT ON punch_correction BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'INSERT', 'punch_correction', NEW.id,
         (SELECT j FROM punch_snapshot_v WHERE id = NEW.target_punch_id), s.j, NEW.reason
    FROM punch_correction_snapshot_v s WHERE s.id = NEW.id;
  INSERT INTO punch (employee_id, direction, occurred_utc, utc_offset_minutes, source, correction_id)
  SELECT NEW.employee_id, NEW.new_direction, NEW.new_occurred_utc, NEW.new_utc_offset_minutes, 'correction', NEW.id
   WHERE NEW.action <> 'void';
END;
CREATE TRIGGER punch_correction_bu BEFORE UPDATE ON punch_correction BEGIN
  SELECT RAISE(ABORT, 'punch_correction rows are immutable; correct the replacement punch instead');
END;
CREATE TRIGGER punch_correction_bd BEFORE DELETE ON punch_correction BEGIN
  SELECT RAISE(ABORT, 'punch_correction rows are immutable');
END;

-- audit_checkpoint: insert-only, system or admin, must anchor the current chain head.
CREATE TRIGGER audit_checkpoint_bi BEFORE INSERT ON audit_checkpoint BEGIN
  SELECT RAISE(ABORT, 'audit_checkpoint: system or admin only')
   WHERE NOT EXISTS (SELECT 1 FROM ctx_user_v WHERE role IN ('system', 'admin'));
  SELECT RAISE(ABORT, 'audit_checkpoint: created_utc is the database clock, not caller-supplied')
   WHERE NEW.created_utc IS NOT strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
  SELECT RAISE(ABORT, 'audit_checkpoint: does not match the audit chain')
   WHERE NOT EXISTS (SELECT 1 FROM audit_log WHERE seq = NEW.audit_seq AND row_hash = NEW.audit_hash);
  SELECT RAISE(ABORT, 'audit_checkpoint: must anchor the current chain head')
   WHERE NEW.audit_seq IS NOT (SELECT max(seq) FROM audit_log);
  SELECT RAISE(ABORT, 'audit_checkpoint: schema_fingerprint must be the current schema')
   WHERE NEW.schema_fingerprint IS NOT (SELECT fingerprint FROM schema_fingerprint_v);
END;
CREATE TRIGGER audit_checkpoint_ai AFTER INSERT ON audit_checkpoint BEGIN
  INSERT INTO audit_log (actor_kind, actor_id, client, action, table_name, row_id, before_json, after_json, reason)
  SELECT pc_ctx('actor_kind'), pc_ctx('actor_id'), pc_ctx('client'), 'INSERT', 'audit_checkpoint', NEW.id,
         NULL, s.j, pc_ctx('reason')
    FROM audit_checkpoint_snapshot_v s WHERE s.id = NEW.id;
END;
CREATE TRIGGER audit_checkpoint_bu BEFORE UPDATE ON audit_checkpoint BEGIN
  SELECT RAISE(ABORT, 'audit_checkpoint is immutable');
END;
CREATE TRIGGER audit_checkpoint_bd BEFORE DELETE ON audit_checkpoint BEGIN
  SELECT RAISE(ABORT, 'audit_checkpoint is immutable');
END;

-- ===========================================================================
-- Read model
-- ===========================================================================

-- A punch is effective unless a correction targets it.
CREATE VIEW punch_effective_v AS
SELECT p.* FROM punch p
WHERE NOT EXISTS (SELECT 1 FROM punch_correction c WHERE c.target_punch_id = p.id);

-- Kiosk state: the latest effective punch by time (not by id, which was legacy bug #5).
CREATE VIEW employee_status_v AS
SELECT e.id AS employee_id, e.first_name, e.last_name, e.is_active,
       lp.direction AS last_direction, lp.occurred_utc AS last_punch_utc
  FROM employee e
  LEFT JOIN (SELECT employee_id, direction, occurred_utc,
                    row_number() OVER (PARTITION BY employee_id ORDER BY occurred_utc DESC, id DESC) AS rn
               FROM punch_effective_v) lp
         ON lp.employee_id = e.id AND lp.rn = 1;

-- Each effective IN paired with the next effective punch of the same employee.
-- Local times use the offset recorded with each punch, never the machine's current
-- zone, so a DST change cannot move history. Durations are UTC arithmetic.
CREATE VIEW shift_v AS
WITH ordered AS (
  SELECT p.*,
         lead(id)                 OVER w AS next_id,
         lead(direction)          OVER w AS next_direction,
         lead(occurred_utc)       OVER w AS next_occurred_utc,
         lead(utc_offset_minutes) OVER w AS next_utc_offset_minutes
    FROM punch_effective_v p
  WINDOW w AS (PARTITION BY employee_id ORDER BY occurred_utc, id)
)
SELECT employee_id,
       id AS in_punch_id,
       occurred_utc AS in_utc,
       strftime('%Y-%m-%d %H:%M:%S', occurred_utc, utc_offset_minutes || ' minutes') AS in_local,
       date(occurred_utc, utc_offset_minutes || ' minutes') AS work_date,
       CASE WHEN next_direction = 'OUT' THEN next_id END AS out_punch_id,
       CASE WHEN next_direction = 'OUT' THEN next_occurred_utc END AS out_utc,
       CASE WHEN next_direction = 'OUT'
            THEN strftime('%Y-%m-%d %H:%M:%S', next_occurred_utc, next_utc_offset_minutes || ' minutes') END AS out_local,
       CASE WHEN next_direction = 'OUT'
            THEN CAST(round((julianday(next_occurred_utc) - julianday(occurred_utc)) * 86400) AS INTEGER) END AS duration_sec,
       CASE WHEN next_direction = 'OUT' THEN 'closed'
            WHEN next_direction = 'IN'  THEN 'missing_out'
            ELSE 'open' END AS status
  FROM ordered
 WHERE direction = 'IN';

-- The manager's review queue before payroll. Fixed only by corrections.
CREATE VIEW punch_exception_v AS
WITH ordered AS (
  SELECT p.*, lag(direction) OVER (PARTITION BY employee_id ORDER BY occurred_utc, id) AS prev_direction
    FROM punch_effective_v p
),
max_shift AS (
  SELECT COALESCE((SELECT CAST(value AS INTEGER) FROM site_setting WHERE key = 'max_shift_hours'), 16) * 3600 AS sec
)
SELECT employee_id, id AS punch_id, occurred_utc, 'missing_in' AS kind
  FROM ordered WHERE direction = 'OUT' AND prev_direction IS NOT 'IN'
UNION ALL
SELECT employee_id, in_punch_id, in_utc, 'missing_out' FROM shift_v WHERE status = 'missing_out'
UNION ALL
SELECT employee_id, in_punch_id, in_utc, 'long_shift' FROM shift_v, max_shift WHERE duration_sec > max_shift.sec
UNION ALL
SELECT employee_id, in_punch_id, in_utc, 'long_open_shift' FROM shift_v, max_shift
 WHERE status = 'open' AND (julianday('now') - julianday(in_utc)) * 86400 > max_shift.sec;

-- ===========================================================================
-- Verifier: on an intact database every verify_* view returns zero rows
-- ===========================================================================

CREATE VIEW verify_chain_v AS
SELECT a.seq, 'prev_hash does not match the predecessor' AS problem
  FROM audit_log a
 WHERE a.prev_hash IS NOT COALESCE((SELECT row_hash FROM audit_log p WHERE p.seq = a.seq - 1),
                                   CASE WHEN a.seq = 1 THEN '0000000000000000000000000000000000000000000000000000000000000000' END)
UNION ALL
SELECT a.seq, 'row_hash does not match the row content'
  FROM audit_log a JOIN audit_canonical_v c ON c.seq = a.seq
 WHERE a.row_hash IS NOT pc_sha256(c.canonical)
UNION ALL
SELECT (SELECT max(seq) FROM audit_log), 'seq is not contiguous from 1'
 WHERE (SELECT count(*) FROM audit_log) <> COALESCE((SELECT max(seq) FROM audit_log), 0)
UNION ALL
SELECT k.audit_seq, 'checkpoint ' || k.id || ' no longer matches the chain'
  FROM audit_checkpoint k
 WHERE NOT EXISTS (SELECT 1 FROM audit_log a WHERE a.seq = k.audit_seq AND a.row_hash = k.audit_hash);

-- Current rows vs their last audited image, in both directions.
CREATE VIEW verify_drift_v AS
WITH latest AS (
  SELECT table_name, row_id, after_json, max(seq) AS seq
    FROM audit_log WHERE action IN ('INSERT', 'UPDATE')
   GROUP BY table_name, row_id
)
SELECT s.table_name, s.row_id, 'row differs from its last audited image' AS problem
  FROM all_snapshot_v s JOIN latest l ON l.table_name = s.table_name AND l.row_id = s.row_id
 WHERE s.j IS NOT l.after_json
UNION ALL
SELECT s.table_name, s.row_id, 'row has no audit record'
  FROM all_snapshot_v s LEFT JOIN latest l ON l.table_name = s.table_name AND l.row_id = s.row_id
 WHERE l.seq IS NULL
UNION ALL
SELECT l.table_name, l.row_id, 'audited row is missing from its table'
  FROM latest l LEFT JOIN all_snapshot_v s ON s.table_name = l.table_name AND s.row_id = l.row_id
 WHERE s.row_id IS NULL;

-- An audited UPDATE whose before-image differs from the previous after-image means the
-- row was changed outside the audited path in between, then laundered by a real edit.
CREATE VIEW verify_continuity_v AS
SELECT u.seq, u.table_name, u.row_id, 'before image differs from the previous audited image' AS problem
  FROM audit_log u
 WHERE u.action = 'UPDATE'
   AND u.before_json IS NOT (SELECT p.after_json FROM audit_log p
                              WHERE p.table_name = u.table_name AND p.row_id = u.row_id
                                AND p.seq < u.seq AND p.action IN ('INSERT', 'UPDATE')
                              ORDER BY p.seq DESC LIMIT 1);

-- Events the trigger path cannot produce.
CREATE VIEW verify_history_v AS
SELECT a.seq, a.table_name, a.row_id, 'row id has more than one INSERT event' AS problem
  FROM audit_log a
 WHERE a.action = 'INSERT'
   AND EXISTS (SELECT 1 FROM audit_log b WHERE b.action = 'INSERT' AND b.table_name = a.table_name
                                          AND b.row_id = a.row_id AND b.seq < a.seq)
UNION ALL
SELECT seq, table_name, row_id, 'UPDATE event on an immutable table'
  FROM audit_log
 WHERE action = 'UPDATE' AND table_name NOT IN ('site_setting', 'employee', 'app_user', 'import_batch')
UNION ALL
SELECT seq, table_name, row_id, 'row creation time differs from its INSERT event'
  FROM audit_log
 WHERE action = 'INSERT'
   AND COALESCE(json_extract(after_json, '$.created_utc'), json_extract(after_json, '$.recorded_utc'),
                json_extract(after_json, '$.started_utc'), occurred_utc) IS NOT occurred_utc
UNION ALL
SELECT a.seq, a.table_name, a.row_id, 'checkpoint did not anchor the head when created'
  FROM audit_log a
 WHERE a.action = 'INSERT' AND a.table_name = 'audit_checkpoint'
   AND json_extract(a.after_json, '$.audit_seq') IS NOT a.seq - 1
UNION ALL
SELECT NULL, 'import_batch', b.id, 'closed batch does not reconcile with its manifest'
  FROM import_batch b
 WHERE b.completed_utc IS NOT NULL
   AND ((SELECT count(*) FROM legacy_employee_raw WHERE import_batch_id = b.id) <> b.manifest_employee_rows
     OR (SELECT count(*) FROM legacy_shift_raw WHERE import_batch_id = b.id) <> b.manifest_shift_rows);

-- Admission rules for payroll and import records, re-derived from the stored rows and their INSERT
-- events. A BEFORE trigger can be dropped for one statement and recreated identically,
-- which the fingerprint cannot see; the data it let in still breaks these rules. Actor
-- state, the site zone and batch state are read as of the event, from the log itself.
CREATE VIEW verify_rules_v AS
WITH pe AS (
  SELECT p.*, a.seq AS event_seq, a.actor_kind, a.actor_id,
         (SELECT json_extract(h.after_json, '$.value') FROM audit_log h
           WHERE h.table_name = 'site_setting' AND h.action IN ('INSERT', 'UPDATE') AND h.seq < a.seq
             AND json_extract(h.after_json, '$.key') = 'time_zone_id'
           ORDER BY h.seq DESC LIMIT 1) AS site_zone_then,
         (SELECT h.after_json FROM audit_log h
           WHERE h.table_name = 'employee' AND h.row_id = p.employee_id
             AND h.action IN ('INSERT', 'UPDATE') AND h.seq < a.seq
           ORDER BY h.seq DESC LIMIT 1) AS employee_then,
         (SELECT h.after_json FROM audit_log h
           WHERE a.actor_kind = 'user' AND h.table_name = 'app_user' AND h.row_id = a.actor_id
             AND h.action IN ('INSERT', 'UPDATE') AND h.seq < a.seq
           ORDER BY h.seq DESC LIMIT 1) AS actor_then
    FROM punch p
    JOIN audit_log a ON a.table_name = 'punch' AND a.row_id = p.id AND a.action = 'INSERT'
),
ce AS (
  SELECT c.*, a.seq AS event_seq, a.actor_kind, a.actor_id,
         (SELECT h.after_json FROM audit_log h
           WHERE a.actor_kind = 'user' AND h.table_name = 'app_user' AND h.row_id = a.actor_id
             AND h.action IN ('INSERT', 'UPDATE') AND h.seq < a.seq
           ORDER BY h.seq DESC LIMIT 1) AS actor_then
    FROM punch_correction c
    JOIN audit_log a ON a.table_name = 'punch_correction' AND a.row_id = c.id AND a.action = 'INSERT'
)
SELECT 'punch' AS table_name, id AS row_id, 'kiosk punch not made by the punching employee' AS problem
  FROM pe WHERE source = 'kiosk' AND NOT (actor_kind = 'employee' AND actor_id = employee_id)
UNION ALL
SELECT 'punch', id, 'kiosk punch by an inactive employee'
  FROM pe WHERE source = 'kiosk' AND json_extract(employee_then, '$.is_active') IS NOT 1
UNION ALL
SELECT 'punch', id, 'kiosk time is not the time it was recorded'
  FROM pe WHERE source = 'kiosk' AND (julianday(recorded_utc) - julianday(occurred_utc)) * 86400 > 120
UNION ALL
SELECT 'punch', id, 'punch time is later than when it was recorded'
  FROM pe WHERE occurred_utc > recorded_utc
UNION ALL
SELECT 'punch', id, 'offset does not match the time zone in effect'
  FROM pe
 WHERE utc_offset_minutes IS NOT pc_utc_offset(
         CASE WHEN source = 'legacy_import'
              THEN (SELECT source_time_zone_id FROM import_batch WHERE id = pe.import_batch_id)
              ELSE site_zone_then END,
         occurred_utc)
UNION ALL
SELECT 'punch', id, 'correction punch does not match its correction'
  FROM pe
 WHERE source = 'correction'
   AND NOT EXISTS (SELECT 1 FROM punch_correction c
                    WHERE c.id = pe.correction_id AND c.action <> 'void'
                      AND c.employee_id = pe.employee_id AND c.new_direction = pe.direction
                      AND c.new_occurred_utc = pe.occurred_utc
                      AND c.new_utc_offset_minutes = pe.utc_offset_minutes)
UNION ALL
SELECT 'punch', id, 'import punch not made by the active migration account into an open batch'
  FROM pe
 WHERE source = 'legacy_import'
   AND (json_extract(actor_then, '$.role') IS NOT 'migration' OR json_extract(actor_then, '$.is_active') IS NOT 1
        OR EXISTS (SELECT 1 FROM audit_log h
                    WHERE h.table_name = 'import_batch' AND h.row_id = pe.import_batch_id
                      AND h.action = 'UPDATE' AND h.seq < pe.event_seq))
UNION ALL
SELECT 'punch', id, 'import punch does not match its raw legacy shift'
  FROM pe
 WHERE source = 'legacy_import'
   AND NOT EXISTS (SELECT 1 FROM legacy_punch_source_v v
                    WHERE v.import_batch_id = pe.import_batch_id AND v.legacy_shift_id = pe.legacy_shift_id
                      AND v.direction = pe.direction
                      AND v.legacy_employee_id = json_extract(pe.employee_then, '$.legacy_id')
                      AND strftime('%Y-%m-%dT%H:%M:%f', pe.occurred_utc, pe.utc_offset_minutes || ' minutes')
                          IN (v.wall_local, v.wall_local_dst_shifted))
UNION ALL
SELECT 'punch', id, 'import punch took the later occurrence of an ambiguous local time'
  FROM pe
 WHERE source = 'legacy_import'
   AND pc_utc_offset((SELECT source_time_zone_id FROM import_batch WHERE id = pe.import_batch_id),
                     strftime('%Y-%m-%dT%H:%M:%fZ', occurred_utc, '-60 minutes'))
       IS utc_offset_minutes + 60
UNION ALL
SELECT 'employee', a.row_id, 'imported employee created without a forced PIN reset'
  FROM audit_log a
 WHERE a.table_name = 'employee' AND a.action = 'INSERT'
   AND json_extract(a.after_json, '$.legacy_id') IS NOT NULL
   AND json_extract(a.after_json, '$.pin_must_change') IS NOT 1
UNION ALL
SELECT 'import_batch', b.id, 'closed batch has unreconciled raw rows'
  FROM import_batch b
 WHERE b.completed_utc IS NOT NULL
   AND EXISTS (SELECT 1 FROM import_unreconciled_v u WHERE u.import_batch_id = b.id)
UNION ALL
SELECT 'punch_correction', id, 'correction actor is not the user who made it'
  FROM ce WHERE NOT (actor_kind = 'user' AND actor_id = actor_user_id)
UNION ALL
SELECT 'punch_correction', id, 'correction by an account that was not an active manager or admin'
  FROM ce
 WHERE json_extract(actor_then, '$.role') NOT IN ('admin', 'manager') IS NOT 0
    OR json_extract(actor_then, '$.is_active') IS NOT 1
UNION ALL
SELECT 'punch_correction', id, 'correction of the actor''s own punches'
  FROM ce WHERE json_extract(actor_then, '$.employee_id') = employee_id
UNION ALL
SELECT 'punch_correction', id, 'correction target belongs to another employee'
  FROM ce
 WHERE target_punch_id IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM punch WHERE id = ce.target_punch_id AND employee_id = ce.employee_id);

-- Warning, not proof: the wall clock moved back more than 60 s between audit rows.
CREATE VIEW verify_clock_v AS
SELECT a.seq, a.occurred_utc, p.occurred_utc AS previous_occurred_utc, 'clock moved backwards' AS problem
  FROM audit_log a JOIN audit_log p ON p.seq = a.seq - 1
 WHERE (julianday(p.occurred_utc) - julianday(a.occurred_utc)) * 86400 > 60;

-- Hash of every schema object. Compared with the value compiled into the app for this
-- user_version; a dropped or edited trigger changes it.
CREATE VIEW schema_fingerprint_v AS
SELECT pc_sha256(json_group_array(json_array(type, name, tbl_name, sql) ORDER BY type, name)) AS fingerprint
  FROM sqlite_schema
 WHERE name NOT LIKE 'sqlite_%' AND name <> 'schema_migrations';

-- ===========================================================================
-- Seed
-- ===========================================================================

INSERT INTO app_user (id, username, display_name, role, password_hash, must_change_password)
VALUES (1, 'system', 'System', 'system', NULL, 0);
INSERT INTO app_user (id, username, display_name, role, password_hash, must_change_password)
VALUES (2, 'migration', 'Legacy importer', 'migration', NULL, 0);

INSERT INTO site_setting (key, value) VALUES ('time_zone_id', 'UNSET');
INSERT INTO site_setting (key, value) VALUES ('max_shift_hours', '16');

-- PunchClock rebuild: SQLite schema v1 (design reference, see audit-schema.md)
--
-- Requirements on every connection the app opens:
--   PRAGMA foreign_keys = ON;
--   PRAGMA journal_mode = WAL;      -- persistent, set once at creation
--   PRAGMA synchronous = FULL;      -- a committed punch must survive power loss
--   register application function audit_hash(...)  (spec in audit-schema.md §5)
--
-- Rules enforced below:
--   * Every table except audit_log and audit_context is audited by triggers.
--   * No row in any table can be DELETEd.
--   * punch, punch_correction, app_event, migration_*, legacy_row, audit_log are append-only.
--   * Every write must happen inside a transaction that first inserts the single
--     audit_context row (who/where), and deletes it before COMMIT.
--   * All timestamps are UTC ISO-8601 text: YYYY-MM-DDTHH:MM:SS.SSSZ

PRAGMA application_id = 0x50434C4B;  -- 'PCLK'
PRAGMA user_version   = 1;

-- ---------------------------------------------------------------------------
-- Reference data
-- ---------------------------------------------------------------------------

CREATE TABLE site_setting (
    setting_id     INTEGER PRIMARY KEY,
    key            TEXT    NOT NULL UNIQUE,
    value          TEXT    NOT NULL
) STRICT;
-- Seeded keys: 'site_tz_id' (Windows zone id, e.g. 'Eastern Standard Time'),
--              'max_shift_minutes' (open-shift alert threshold).

CREATE TABLE employee (
    employee_id        INTEGER PRIMARY KEY,
    legacy_employee_id INTEGER UNIQUE,                     -- Access Employee.EmployeeID
    first_name         TEXT    NOT NULL CHECK (length(first_name) BETWEEN 1 AND 255),
    last_name          TEXT    NOT NULL CHECK (length(last_name)  BETWEEN 1 AND 255),
    pin_hash           TEXT    NOT NULL,                   -- PHC string (PBKDF2/Argon2id, per-row salt)
    pin_must_change    INTEGER NOT NULL DEFAULT 0 CHECK (pin_must_change IN (0,1)),
    is_active          INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    created_at_utc     TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                       CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at_utc) IS created_at_utc)
) STRICT;

CREATE TABLE app_user (
    user_id        INTEGER PRIMARY KEY,
    username       TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    display_name   TEXT    NOT NULL,
    password_hash  TEXT    NOT NULL,                       -- PHC string
    role           TEXT    NOT NULL CHECK (role IN ('admin','manager')),
    employee_id    INTEGER REFERENCES employee(employee_id), -- set if the manager also punches
    is_active      INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    created_at_utc TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                   CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at_utc) IS created_at_utc)
) STRICT;

-- ---------------------------------------------------------------------------
-- Migration provenance
-- ---------------------------------------------------------------------------

CREATE TABLE migration_batch (
    batch_id            INTEGER PRIMARY KEY,
    source_file_name    TEXT    NOT NULL,
    source_sha256       TEXT    NOT NULL CHECK (length(source_sha256) = 64),
    source_tz_id        TEXT    NOT NULL,                  -- zone used to read legacy local times
    source_counts_json  TEXT    NOT NULL CHECK (json_valid(source_counts_json)),
    tool_version        TEXT    NOT NULL,
    started_at_utc      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', started_at_utc) IS started_at_utc)
) STRICT;

-- Verbatim copy of every legacy row read, so any imported value can be traced
-- back to the source cell.
CREATE TABLE legacy_row (
    legacy_row_id  INTEGER PRIMARY KEY,
    batch_id       INTEGER NOT NULL REFERENCES migration_batch(batch_id),
    source_table   TEXT    NOT NULL CHECK (source_table IN ('Employee','Shift')),
    source_pk      INTEGER NOT NULL,
    raw_json       TEXT    NOT NULL CHECK (json_valid(raw_json)),
    UNIQUE (batch_id, source_table, source_pk)
) STRICT;

CREATE TABLE migration_issue (
    issue_id       INTEGER PRIMARY KEY,
    batch_id       INTEGER NOT NULL REFERENCES migration_batch(batch_id),
    legacy_row_id  INTEGER NOT NULL REFERENCES legacy_row(legacy_row_id),
    issue_code     TEXT    NOT NULL CHECK (issue_code IN (
                       'DUMMY_SHIFT',        -- TimeIn = TimeOut (created by NewStaffForm)
                       'OPEN_SHIFT',         -- TimeOut NULL
                       'NULL_TIME_IN',
                       'NEGATIVE_DURATION',
                       'LONG_SHIFT',         -- > max_shift_minutes
                       'OVERLAPPING_SHIFT',
                       'DST_AMBIGUOUS',      -- local time in fall-back hour
                       'DST_INVALID',        -- local time in spring-forward gap
                       'ORPHAN_EMPLOYEE',    -- Shift.EmployeeID has no Employee row
                       'PIN_UNRECOVERABLE')),-- leading zeros lost in legacy int PIN
    disposition    TEXT    NOT NULL CHECK (disposition IN ('IMPORTED','IMPORTED_FLAGGED','SKIPPED')),
    detail_json    TEXT    NOT NULL DEFAULT '{}' CHECK (json_valid(detail_json))
) STRICT;

-- ---------------------------------------------------------------------------
-- Time data
-- ---------------------------------------------------------------------------

-- Corrections are created before the punch that carries the corrected value,
-- so punch.correction_id can reference it. A punch is never edited; it is
-- superseded (ADJUST) or cancelled (VOID) by exactly one correction.
CREATE TABLE punch_correction (
    correction_id   INTEGER PRIMARY KEY,
    action          TEXT    NOT NULL CHECK (action IN ('ADD','ADJUST','VOID')),
    employee_id     INTEGER NOT NULL REFERENCES employee(employee_id),
    target_punch_id INTEGER UNIQUE REFERENCES punch(punch_id),  -- the "before"
    reason          TEXT    NOT NULL CHECK (length(trim(reason)) >= 10),
    actor_user_id   INTEGER NOT NULL REFERENCES app_user(user_id),
    created_at_utc  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                    CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at_utc) IS created_at_utc),
    CHECK ((action = 'ADD') = (target_punch_id IS NULL))
) STRICT;

CREATE TABLE punch (
    punch_id           INTEGER PRIMARY KEY,
    employee_id        INTEGER NOT NULL REFERENCES employee(employee_id),
    punch_type         TEXT    NOT NULL CHECK (punch_type IN ('IN','OUT')),
    punched_at_utc     TEXT    NOT NULL                    -- the time the punch counts for
                       CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', punched_at_utc) IS punched_at_utc),
    utc_offset_min     INTEGER NOT NULL CHECK (utc_offset_min BETWEEN -840 AND 840),
    source             TEXT    NOT NULL CHECK (source IN ('kiosk','correction','migration')),
    correction_id      INTEGER UNIQUE REFERENCES punch_correction(correction_id), -- the "after"
    migration_batch_id INTEGER REFERENCES migration_batch(batch_id),
    legacy_shift_id    INTEGER,
    device_id          TEXT    NOT NULL,
    recorded_at_utc    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                       CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', recorded_at_utc) IS recorded_at_utc),
    CHECK ((source = 'correction') = (correction_id IS NOT NULL)),
    CHECK ((source = 'migration')  = (migration_batch_id IS NOT NULL)),
    CHECK (legacy_shift_id IS NULL OR source = 'migration'),
    UNIQUE (legacy_shift_id, punch_type)
) STRICT;

CREATE INDEX ix_punch_employee_time ON punch (employee_id, punched_at_utc, punch_id);
CREATE INDEX ix_correction_employee ON punch_correction (employee_id);

-- Non-data actions the law cares about: logins, failed PINs, report runs,
-- exports, verification results, anchors, schema upgrades, migration completion.
CREATE TABLE app_event (
    event_id        INTEGER PRIMARY KEY,
    event_type      TEXT    NOT NULL CHECK (event_type IN (
                        'APP_START','APP_STOP',
                        'LOGIN_OK','LOGIN_FAIL','LOGOUT','PIN_FAIL',
                        'REPORT_RUN','EXPORT',
                        'VERIFY_OK','VERIFY_FAIL','ANCHOR',
                        'SCHEMA_UPGRADE','MIGRATION_COMPLETE','BACKUP')),
    detail_json     TEXT    NOT NULL DEFAULT '{}' CHECK (json_valid(detail_json)),
    occurred_at_utc TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                    CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', occurred_at_utc) IS occurred_at_utc)
) STRICT;

-- ---------------------------------------------------------------------------
-- Audit infrastructure
-- ---------------------------------------------------------------------------

-- One row, present only inside a write transaction. Triggers copy it into every
-- audit row. Not audited itself; its content is captured in audit_log.
CREATE TABLE audit_context (
    id                INTEGER PRIMARY KEY CHECK (id = 1),
    txn_id            TEXT    NOT NULL,                    -- UUID per unit of work
    actor_kind        TEXT    NOT NULL CHECK (actor_kind IN ('employee','user','system','migration')),
    actor_user_id     INTEGER REFERENCES app_user(user_id),
    actor_employee_id INTEGER REFERENCES employee(employee_id),
    device_id         TEXT    NOT NULL,
    app_version       TEXT    NOT NULL,
    CHECK (actor_kind <> 'user'     OR actor_user_id IS NOT NULL),
    CHECK (actor_kind <> 'employee' OR actor_employee_id IS NOT NULL)
) STRICT;

CREATE TABLE audit_log (
    audit_id          INTEGER PRIMARY KEY,                 -- gapless 1..n
    occurred_at_utc   TEXT    NOT NULL,
    txn_id            TEXT    NOT NULL,
    actor_kind        TEXT    NOT NULL,
    actor_user_id     INTEGER,                             -- no FKs: the log outlives everything
    actor_employee_id INTEGER,
    device_id         TEXT    NOT NULL,
    app_version       TEXT    NOT NULL,
    table_name        TEXT    NOT NULL,
    operation         TEXT    NOT NULL CHECK (operation IN ('INSERT','UPDATE')),
    row_pk            INTEGER NOT NULL,
    before_json       TEXT,
    after_json        TEXT    NOT NULL,
    prev_hash         TEXT    NOT NULL CHECK (length(prev_hash) = 64),
    row_hash          TEXT    NOT NULL UNIQUE CHECK (length(row_hash) = 64)
) STRICT;

CREATE INDEX ix_audit_table_row ON audit_log (table_name, row_pk, audit_id);

-- Write-only funnel: table triggers INSERT here; the INSTEAD OF trigger chains it.
CREATE VIEW audit_sink AS
    SELECT table_name, operation, row_pk, before_json, after_json FROM audit_log WHERE 0;

CREATE TRIGGER audit_sink_insert INSTEAD OF INSERT ON audit_sink
BEGIN
    SELECT RAISE(ABORT, 'audit: no audit_context row; write through UnitOfWork')
     WHERE NOT EXISTS (SELECT 1 FROM audit_context);
    INSERT INTO audit_log (audit_id, occurred_at_utc, txn_id, actor_kind, actor_user_id,
                           actor_employee_id, device_id, app_version, table_name, operation,
                           row_pk, before_json, after_json, prev_hash, row_hash)
    SELECT h.audit_id, h.ts, c.txn_id, c.actor_kind, c.actor_user_id,
           c.actor_employee_id, c.device_id, c.app_version, NEW.table_name, NEW.operation,
           NEW.row_pk, NEW.before_json, NEW.after_json, h.prev_hash,
           audit_hash(h.prev_hash, h.audit_id, h.ts, c.txn_id, c.actor_kind, c.actor_user_id,
                      c.actor_employee_id, c.device_id, c.app_version, NEW.table_name,
                      NEW.operation, NEW.row_pk, NEW.before_json, NEW.after_json)
      FROM audit_context c,
           (SELECT COALESCE(MAX(audit_id), 0) + 1 AS audit_id,
                   COALESCE((SELECT row_hash FROM audit_log ORDER BY audit_id DESC LIMIT 1),
                            '0000000000000000000000000000000000000000000000000000000000000000') AS prev_hash,
                   strftime('%Y-%m-%dT%H:%M:%fZ','now') AS ts
              FROM audit_log) h;
END;

CREATE TRIGGER audit_log_no_update BEFORE UPDATE ON audit_log
BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
CREATE TRIGGER audit_log_no_delete BEFORE DELETE ON audit_log
BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;

-- ---------------------------------------------------------------------------
-- Canonical row images. Used by the triggers (via NEW/OLD, same column list)
-- and by the verifier to replay the log against current state.
-- Credential hashes appear only as a fingerprint.
-- ---------------------------------------------------------------------------

CREATE VIEW audit_image_site_setting AS SELECT setting_id AS row_pk,
    json_object('setting_id',setting_id,'key',key,'value',value) AS j FROM site_setting;
CREATE VIEW audit_image_employee AS SELECT employee_id AS row_pk,
    json_object('employee_id',employee_id,'legacy_employee_id',legacy_employee_id,
                'first_name',first_name,'last_name',last_name,
                'pin_fp',substr(audit_hash(pin_hash),1,16),'pin_must_change',pin_must_change,
                'is_active',is_active,'created_at_utc',created_at_utc) AS j FROM employee;
CREATE VIEW audit_image_app_user AS SELECT user_id AS row_pk,
    json_object('user_id',user_id,'username',username,'display_name',display_name,
                'password_fp',substr(audit_hash(password_hash),1,16),'role',role,
                'employee_id',employee_id,'is_active',is_active,
                'created_at_utc',created_at_utc) AS j FROM app_user;
CREATE VIEW audit_image_migration_batch AS SELECT batch_id AS row_pk,
    json_object('batch_id',batch_id,'source_file_name',source_file_name,
                'source_sha256',source_sha256,'source_tz_id',source_tz_id,
                'source_counts_json',source_counts_json,'tool_version',tool_version,
                'started_at_utc',started_at_utc) AS j FROM migration_batch;
CREATE VIEW audit_image_legacy_row AS SELECT legacy_row_id AS row_pk,
    json_object('legacy_row_id',legacy_row_id,'batch_id',batch_id,'source_table',source_table,
                'source_pk',source_pk,'raw_json',raw_json) AS j FROM legacy_row;
CREATE VIEW audit_image_migration_issue AS SELECT issue_id AS row_pk,
    json_object('issue_id',issue_id,'batch_id',batch_id,'legacy_row_id',legacy_row_id,
                'issue_code',issue_code,'disposition',disposition,
                'detail_json',detail_json) AS j FROM migration_issue;
CREATE VIEW audit_image_punch_correction AS SELECT correction_id AS row_pk,
    json_object('correction_id',correction_id,'action',action,'employee_id',employee_id,
                'target_punch_id',target_punch_id,'reason',reason,
                'actor_user_id',actor_user_id,'created_at_utc',created_at_utc) AS j
    FROM punch_correction;
CREATE VIEW audit_image_punch AS SELECT punch_id AS row_pk,
    json_object('punch_id',punch_id,'employee_id',employee_id,'punch_type',punch_type,
                'punched_at_utc',punched_at_utc,'utc_offset_min',utc_offset_min,
                'source',source,'correction_id',correction_id,
                'migration_batch_id',migration_batch_id,'legacy_shift_id',legacy_shift_id,
                'device_id',device_id,'recorded_at_utc',recorded_at_utc) AS j FROM punch;
CREATE VIEW audit_image_app_event AS SELECT event_id AS row_pk,
    json_object('event_id',event_id,'event_type',event_type,'detail_json',detail_json,
                'occurred_at_utc',occurred_at_utc) AS j FROM app_event;

-- ---------------------------------------------------------------------------
-- Audit triggers. Pattern per table:
--   AFTER INSERT  -> audit row with after image
--   AFTER UPDATE  -> audit row with before + after image (mutable tables only)
--   BEFORE UPDATE -> abort (append-only tables)
--   BEFORE DELETE -> abort (all tables)
-- ---------------------------------------------------------------------------

-- site_setting (mutable)
CREATE TRIGGER site_setting_bi BEFORE INSERT ON site_setting BEGIN
    SELECT RAISE(ABORT, 'site_setting: admin, system or migration actor required')
     WHERE NOT EXISTS (SELECT 1 FROM audit_context c
         WHERE c.actor_kind IN ('system','migration')
            OR (c.actor_kind = 'user' AND EXISTS (SELECT 1 FROM app_user u
                 WHERE u.user_id = c.actor_user_id AND u.role = 'admin' AND u.is_active = 1)));
END;
CREATE TRIGGER site_setting_ai AFTER INSERT ON site_setting BEGIN
    INSERT INTO audit_sink VALUES ('site_setting','INSERT',NEW.setting_id,NULL,
        (SELECT j FROM audit_image_site_setting WHERE row_pk = NEW.setting_id));
END;
CREATE TRIGGER site_setting_bu BEFORE UPDATE ON site_setting BEGIN
    SELECT RAISE(ABORT, 'site_setting: key and id are immutable')
     WHERE NEW.setting_id IS NOT OLD.setting_id OR NEW.key IS NOT OLD.key;
    SELECT RAISE(ABORT, 'site_setting: admin actor required')
     WHERE NOT EXISTS (SELECT 1 FROM audit_context c JOIN app_user u ON u.user_id = c.actor_user_id
         WHERE c.actor_kind = 'user' AND u.role = 'admin' AND u.is_active = 1);
END;
CREATE TRIGGER site_setting_au AFTER UPDATE ON site_setting BEGIN
    INSERT INTO audit_sink VALUES ('site_setting','UPDATE',NEW.setting_id,
        json_object('setting_id',OLD.setting_id,'key',OLD.key,'value',OLD.value),
        (SELECT j FROM audit_image_site_setting WHERE row_pk = NEW.setting_id));
END;
CREATE TRIGGER site_setting_bd BEFORE DELETE ON site_setting
BEGIN SELECT RAISE(ABORT, 'site_setting: delete not allowed'); END;

-- employee (mutable: names, PIN, active flag; never deleted)
CREATE TRIGGER employee_bi BEFORE INSERT ON employee BEGIN
    SELECT RAISE(ABORT, 'employee: only a manager/admin or the migration may create employees')
     WHERE NOT EXISTS (
        SELECT 1 FROM audit_context c
         WHERE c.actor_kind = 'migration'
            OR (c.actor_kind = 'user' AND EXISTS (SELECT 1 FROM app_user u
                 WHERE u.user_id = c.actor_user_id AND u.is_active = 1
                   AND u.role IN ('manager','admin'))));
END;
CREATE TRIGGER employee_ai AFTER INSERT ON employee BEGIN
    INSERT INTO audit_sink VALUES ('employee','INSERT',NEW.employee_id,NULL,
        (SELECT j FROM audit_image_employee WHERE row_pk = NEW.employee_id));
END;
CREATE TRIGGER employee_bu BEFORE UPDATE ON employee BEGIN
    SELECT RAISE(ABORT, 'employee: id, legacy id and created_at are immutable')
     WHERE NEW.employee_id IS NOT OLD.employee_id
        OR NEW.legacy_employee_id IS NOT OLD.legacy_employee_id
        OR NEW.created_at_utc IS NOT OLD.created_at_utc;
    SELECT RAISE(ABORT, 'employee: only a manager/admin, or the employee changing their own PIN')
     WHERE NOT EXISTS (
        SELECT 1 FROM audit_context c
         WHERE (c.actor_kind = 'user' AND EXISTS (SELECT 1 FROM app_user u
                 WHERE u.user_id = c.actor_user_id AND u.is_active = 1
                   AND u.role IN ('manager','admin')))
            OR (c.actor_kind = 'employee' AND c.actor_employee_id = OLD.employee_id
                AND NEW.first_name IS OLD.first_name AND NEW.last_name IS OLD.last_name
                AND NEW.is_active IS OLD.is_active));
END;
CREATE TRIGGER employee_au AFTER UPDATE ON employee BEGIN
    INSERT INTO audit_sink VALUES ('employee','UPDATE',NEW.employee_id,
        json_object('employee_id',OLD.employee_id,'legacy_employee_id',OLD.legacy_employee_id,
                    'first_name',OLD.first_name,'last_name',OLD.last_name,
                    'pin_fp',substr(audit_hash(OLD.pin_hash),1,16),
                    'pin_must_change',OLD.pin_must_change,
                    'is_active',OLD.is_active,'created_at_utc',OLD.created_at_utc),
        (SELECT j FROM audit_image_employee WHERE row_pk = NEW.employee_id));
END;
CREATE TRIGGER employee_bd BEFORE DELETE ON employee
BEGIN SELECT RAISE(ABORT, 'employee: delete not allowed, set is_active = 0'); END;

-- app_user (mutable; only an admin may create or change accounts)
CREATE TRIGGER app_user_bi BEFORE INSERT ON app_user BEGIN
    SELECT RAISE(ABORT, 'app_user: only an admin (or first-run system setup) may create accounts')
     WHERE NOT EXISTS (
        SELECT 1 FROM audit_context c
         WHERE (c.actor_kind = 'system' AND NOT EXISTS (SELECT 1 FROM app_user))
            OR (c.actor_kind = 'user' AND EXISTS (SELECT 1 FROM app_user u
                 WHERE u.user_id = c.actor_user_id AND u.role = 'admin' AND u.is_active = 1)));
END;
CREATE TRIGGER app_user_ai AFTER INSERT ON app_user BEGIN
    INSERT INTO audit_sink VALUES ('app_user','INSERT',NEW.user_id,NULL,
        (SELECT j FROM audit_image_app_user WHERE row_pk = NEW.user_id));
END;
CREATE TRIGGER app_user_bu BEFORE UPDATE ON app_user BEGIN
    SELECT RAISE(ABORT, 'app_user: id and created_at are immutable')
     WHERE NEW.user_id IS NOT OLD.user_id OR NEW.created_at_utc IS NOT OLD.created_at_utc;
    SELECT RAISE(ABORT, 'app_user: only an admin may change roles or other accounts')
     WHERE NOT EXISTS (
        SELECT 1 FROM audit_context c
         WHERE c.actor_kind = 'user'
           AND (EXISTS (SELECT 1 FROM app_user u WHERE u.user_id = c.actor_user_id
                         AND u.role = 'admin' AND u.is_active = 1)
                OR (c.actor_user_id = OLD.user_id AND NEW.role IS OLD.role
                    AND NEW.is_active IS OLD.is_active)));
END;
CREATE TRIGGER app_user_au AFTER UPDATE ON app_user BEGIN
    INSERT INTO audit_sink VALUES ('app_user','UPDATE',NEW.user_id,
        json_object('user_id',OLD.user_id,'username',OLD.username,'display_name',OLD.display_name,
                    'password_fp',substr(audit_hash(OLD.password_hash),1,16),'role',OLD.role,
                    'employee_id',OLD.employee_id,'is_active',OLD.is_active,
                    'created_at_utc',OLD.created_at_utc),
        (SELECT j FROM audit_image_app_user WHERE row_pk = NEW.user_id));
END;
CREATE TRIGGER app_user_bd BEFORE DELETE ON app_user
BEGIN SELECT RAISE(ABORT, 'app_user: delete not allowed, set is_active = 0'); END;

-- migration_batch, legacy_row, migration_issue (append-only, migration actor only)
CREATE TRIGGER migration_batch_bi BEFORE INSERT ON migration_batch BEGIN
    SELECT RAISE(ABORT, 'migration_batch: migration actor required')
     WHERE NOT EXISTS (SELECT 1 FROM audit_context WHERE actor_kind = 'migration');
END;
CREATE TRIGGER migration_batch_ai AFTER INSERT ON migration_batch BEGIN
    INSERT INTO audit_sink VALUES ('migration_batch','INSERT',NEW.batch_id,NULL,
        (SELECT j FROM audit_image_migration_batch WHERE row_pk = NEW.batch_id));
END;
CREATE TRIGGER migration_batch_bu BEFORE UPDATE ON migration_batch
BEGIN SELECT RAISE(ABORT, 'migration_batch is append-only'); END;
CREATE TRIGGER migration_batch_bd BEFORE DELETE ON migration_batch
BEGIN SELECT RAISE(ABORT, 'migration_batch is append-only'); END;

CREATE TRIGGER legacy_row_ai AFTER INSERT ON legacy_row BEGIN
    INSERT INTO audit_sink VALUES ('legacy_row','INSERT',NEW.legacy_row_id,NULL,
        (SELECT j FROM audit_image_legacy_row WHERE row_pk = NEW.legacy_row_id));
END;
CREATE TRIGGER legacy_row_bu BEFORE UPDATE ON legacy_row
BEGIN SELECT RAISE(ABORT, 'legacy_row is append-only'); END;
CREATE TRIGGER legacy_row_bd BEFORE DELETE ON legacy_row
BEGIN SELECT RAISE(ABORT, 'legacy_row is append-only'); END;

CREATE TRIGGER migration_issue_ai AFTER INSERT ON migration_issue BEGIN
    INSERT INTO audit_sink VALUES ('migration_issue','INSERT',NEW.issue_id,NULL,
        (SELECT j FROM audit_image_migration_issue WHERE row_pk = NEW.issue_id));
END;
CREATE TRIGGER migration_issue_bu BEFORE UPDATE ON migration_issue
BEGIN SELECT RAISE(ABORT, 'migration_issue is append-only'); END;
CREATE TRIGGER migration_issue_bd BEFORE DELETE ON migration_issue
BEGIN SELECT RAISE(ABORT, 'migration_issue is append-only'); END;

-- punch_correction (append-only; manager/admin acting as themselves)
CREATE TRIGGER punch_correction_bi BEFORE INSERT ON punch_correction BEGIN
    SELECT RAISE(ABORT, 'punch_correction: actor must be the logged-in, active manager or admin')
     WHERE NOT EXISTS (
        SELECT 1 FROM audit_context c JOIN app_user u ON u.user_id = c.actor_user_id
         WHERE c.actor_kind = 'user' AND c.actor_user_id = NEW.actor_user_id
           AND u.is_active = 1 AND u.role IN ('manager','admin'));
    SELECT RAISE(ABORT, 'punch_correction: target punch belongs to another employee')
     WHERE NEW.target_punch_id IS NOT NULL
       AND (SELECT employee_id FROM punch WHERE punch_id = NEW.target_punch_id)
           IS NOT NEW.employee_id;
END;
CREATE TRIGGER punch_correction_ai AFTER INSERT ON punch_correction BEGIN
    INSERT INTO audit_sink VALUES ('punch_correction','INSERT',NEW.correction_id,
        (SELECT j FROM audit_image_punch WHERE row_pk = NEW.target_punch_id),  -- before image
        (SELECT j FROM audit_image_punch_correction WHERE row_pk = NEW.correction_id));
END;
CREATE TRIGGER punch_correction_bu BEFORE UPDATE ON punch_correction
BEGIN SELECT RAISE(ABORT, 'punch_correction is append-only'); END;
CREATE TRIGGER punch_correction_bd BEFORE DELETE ON punch_correction
BEGIN SELECT RAISE(ABORT, 'punch_correction is append-only'); END;

-- punch (append-only; source must match the actor)
CREATE TRIGGER punch_bi BEFORE INSERT ON punch BEGIN
    SELECT RAISE(ABORT, 'punch: kiosk punch must be made by that active employee')
     WHERE NEW.source = 'kiosk' AND NOT EXISTS (
        SELECT 1 FROM audit_context c JOIN employee e ON e.employee_id = c.actor_employee_id
         WHERE c.actor_kind = 'employee' AND c.actor_employee_id = NEW.employee_id
           AND e.is_active = 1);
    SELECT RAISE(ABORT, 'punch: correction punch needs an ADD/ADJUST correction for the same employee by the current actor')
     WHERE NEW.source = 'correction' AND NOT EXISTS (
        SELECT 1 FROM punch_correction pc JOIN audit_context c ON c.actor_user_id = pc.actor_user_id
         WHERE pc.correction_id = NEW.correction_id AND pc.action IN ('ADD','ADJUST')
           AND pc.employee_id = NEW.employee_id AND c.actor_kind = 'user');
    SELECT RAISE(ABORT, 'punch: migration punch needs the migration actor')
     WHERE NEW.source = 'migration'
       AND NOT EXISTS (SELECT 1 FROM audit_context WHERE actor_kind = 'migration');
END;
CREATE TRIGGER punch_ai AFTER INSERT ON punch BEGIN
    INSERT INTO audit_sink VALUES ('punch','INSERT',NEW.punch_id,NULL,
        (SELECT j FROM audit_image_punch WHERE row_pk = NEW.punch_id));
END;
CREATE TRIGGER punch_bu BEFORE UPDATE ON punch
BEGIN SELECT RAISE(ABORT, 'punch is append-only: use punch_correction'); END;
CREATE TRIGGER punch_bd BEFORE DELETE ON punch
BEGIN SELECT RAISE(ABORT, 'punch is append-only: use punch_correction VOID'); END;

-- app_event (append-only)
CREATE TRIGGER app_event_ai AFTER INSERT ON app_event BEGIN
    INSERT INTO audit_sink VALUES ('app_event','INSERT',NEW.event_id,NULL,
        (SELECT j FROM audit_image_app_event WHERE row_pk = NEW.event_id));
END;
CREATE TRIGGER app_event_bu BEFORE UPDATE ON app_event
BEGIN SELECT RAISE(ABORT, 'app_event is append-only'); END;
CREATE TRIGGER app_event_bd BEFORE DELETE ON app_event
BEGIN SELECT RAISE(ABORT, 'app_event is append-only'); END;

-- ---------------------------------------------------------------------------
-- Read model
-- ---------------------------------------------------------------------------

-- A punch counts unless a correction targets it (ADJUST replaces, VOID cancels).
CREATE VIEW v_effective_punch AS
SELECT p.* FROM punch p
 WHERE NOT EXISTS (SELECT 1 FROM punch_correction c WHERE c.target_punch_id = p.punch_id);

-- Pair IN with the next effective punch by time (not by id: fixes legacy bug #5).
CREATE VIEW v_shift AS
WITH o AS (
    SELECT employee_id, punch_id, punch_type, punched_at_utc, utc_offset_min,
           LEAD(punch_type)     OVER w AS next_type,
           LEAD(punch_id)       OVER w AS next_id,
           LEAD(punched_at_utc) OVER w AS next_at,
           LAG(punch_type)      OVER w AS prev_type
      FROM v_effective_punch
    WINDOW w AS (PARTITION BY employee_id ORDER BY punched_at_utc, punch_id)
)
SELECT employee_id,
       punch_id                                            AS in_punch_id,
       CASE WHEN next_type = 'OUT' THEN next_id END        AS out_punch_id,
       punched_at_utc                                      AS in_utc,
       CASE WHEN next_type = 'OUT' THEN next_at END        AS out_utc,
       utc_offset_min                                      AS in_offset_min,
       CASE WHEN next_type = 'OUT'
            THEN CAST(round((julianday(next_at) - julianday(punched_at_utc)) * 1440) AS INTEGER)
       END                                                 AS minutes,
       CASE WHEN next_type IS NULL THEN 'OPEN'
            WHEN next_type = 'IN'  THEN 'MISSING_OUT'
            ELSE 'OK' END                                  AS status
  FROM o WHERE punch_type = 'IN'
UNION ALL
SELECT employee_id, NULL, punch_id, NULL, punched_at_utc, utc_offset_min, NULL, 'MISSING_IN'
  FROM o WHERE punch_type = 'OUT' AND (prev_type IS NULL OR prev_type = 'OUT');

-- Current state for the kiosk: last effective punch by time.
CREATE VIEW v_employee_status AS
SELECT e.employee_id, e.first_name, e.last_name, e.is_active,
       (SELECT p.punch_type FROM v_effective_punch p WHERE p.employee_id = e.employee_id
         ORDER BY p.punched_at_utc DESC, p.punch_id DESC LIMIT 1) AS last_punch_type,
       (SELECT p.punched_at_utc FROM v_effective_punch p WHERE p.employee_id = e.employee_id
         ORDER BY p.punched_at_utc DESC, p.punch_id DESC LIMIT 1) AS last_punch_utc
  FROM employee e;

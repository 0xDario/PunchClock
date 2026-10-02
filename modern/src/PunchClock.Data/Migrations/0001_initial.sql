-- Minimal operational schema for the kiosk. The audit design (hash-chained
-- audit table, correction rows, actors) lands in later migrations.

CREATE TABLE employee (
    id          INTEGER PRIMARY KEY,
    legacy_id   INTEGER UNIQUE,                 -- Access Employee.EmployeeID, NULL for new staff
    first_name  TEXT    NOT NULL,
    last_name   TEXT    NOT NULL,
    pin_hash    TEXT    NOT NULL,
    is_active   INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_utc TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
) STRICT;

-- One row per punch event. A shift is an In followed by the next Out.
CREATE TABLE punch (
    id                 INTEGER PRIMARY KEY,
    employee_id        INTEGER NOT NULL REFERENCES employee (id),
    direction          TEXT    NOT NULL CHECK (direction IN ('IN', 'OUT')),
    occurred_utc       TEXT    NOT NULL,        -- ISO 8601 UTC, fixed width so it sorts as text
    utc_offset_minutes INTEGER NOT NULL,        -- local offset at punch time, for wall-clock reporting
    source             TEXT    NOT NULL,
    recorded_utc       TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
) STRICT;

CREATE INDEX ix_punch_employee_time ON punch (employee_id, occurred_utc, id);

-- Punches are append-only. Corrections will be new rows, never edits.
CREATE TRIGGER punch_no_update BEFORE UPDATE ON punch
BEGIN
    SELECT RAISE(ABORT, 'punch rows are append-only');
END;

CREATE TRIGGER punch_no_delete BEFORE DELETE ON punch
BEGIN
    SELECT RAISE(ABORT, 'punch rows are append-only');
END;

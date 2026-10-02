-- Minimal operational schema for the kiosk. The audit log (hash-chained audit
-- table, correction rows, actors) is designed separately and lands as later
-- migrations on top of this one.

CREATE TABLE employee (
    id             INTEGER PRIMARY KEY,
    first_name     TEXT    NOT NULL CHECK (length(trim(first_name)) > 0),
    last_name      TEXT    NOT NULL CHECK (length(trim(last_name)) > 0),
    pin_hash       TEXT    NOT NULL,
    is_active      INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at_utc TEXT    NOT NULL
) STRICT;

-- One row per clock event. A shift is an IN followed by the next OUT by time;
-- shifts are derived, never stored.
-- Timestamps are fixed-width ISO 8601 UTC (yyyy-MM-ddTHH:mm:ss.fffffffZ) so
-- text order equals time order; the GLOB check keeps it that way.
CREATE TABLE punch (
    id                 INTEGER PRIMARY KEY,
    employee_id        INTEGER NOT NULL REFERENCES employee (id),
    direction          TEXT    NOT NULL CHECK (direction IN ('IN', 'OUT')),
    occurred_at_utc    TEXT    NOT NULL CHECK (occurred_at_utc GLOB
        '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'),
    utc_offset_minutes INTEGER NOT NULL CHECK (utc_offset_minutes BETWEEN -1080 AND 1080),
    recorded_at_utc    TEXT    NOT NULL,
    source             TEXT    NOT NULL CHECK (source IN ('KIOSK', 'IMPORT'))
) STRICT;

CREATE INDEX ix_punch_employee_time ON punch (employee_id, occurred_at_utc, id);

-- Punches are append-only. Corrections will be new rows, never edits.
CREATE TRIGGER punch_no_update BEFORE UPDATE ON punch
BEGIN
    SELECT RAISE(ABORT, 'punch rows are append-only');
END;

CREATE TRIGGER punch_no_delete BEFORE DELETE ON punch
BEGIN
    SELECT RAISE(ABORT, 'punch rows are append-only');
END;

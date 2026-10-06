# PunchClock

> **v2 rewrite in progress.** The new app lives in `src/` and `tests/` (`PunchClock.Modern.sln`): .NET 10 WPF on a local SQLite database. Everything below describes the legacy v1 WinForms + Access app in `PunchClock/`, which is kept unchanged until the data migration is done. See [v2 rewrite](#v2-rewrite) at the end.

_A basic Windows Forms Punch Clock application that allows staff to Punch In/Out of work._

* Staff select their name from a dropdown list and enter their unique 3-6 digit pin code in order to punch in/out of their shift.

* Action to Punch In/Out is automatically determined by the last action a staff member performed. If they last **'punched in'**, their next action in the system would be to **'punch out'**

* Data is saved to a Microsoft Access Database **[PunchClock.accdb]**. Reporting can be generated to allow for easy calculation of the pay owed an employee.

* Credentials for Microsoft Access Database:
    |username|password|
    |----|-------|
    |Admin|admin123|

Application in Action:
![Punch Clock Windows Form Demo](https://github.com/0xDario/PunchClock/blob/master/PunchClock.gif)

## Dependencies
- [Microsoft.ACE.OLEDB12.0](https://www.microsoft.com/en-us/download/details.aspx?id=13255)

## v2 rewrite

| Project | Role |
|---|---|
| `src/PunchClock.Core` | Domain and rules: explicit punch in/out, PIN policy and PBKDF2 hashing. No I/O. |
| `src/PunchClock.Data.Sqlite` | SQLite storage, embedded SQL migrations, database path resolution. |
| `src/PunchClock.App` | WPF kiosk (`net10.0-windows`). |
| `tests/PunchClock.Tests` | xUnit v3 tests against real SQLite files. |

Requires the .NET 10 SDK. On Windows: `dotnet run --project src/PunchClock.App`. Anywhere: `dotnet build PunchClock.Modern.sln` and `dotnet test --solution PunchClock.Modern.sln` (the WPF project builds on Linux/macOS but only runs on Windows).

- **Database**: `%ProgramData%\PunchClock\punchclock.db`, or the path in `PUNCHCLOCK_DB`. Created and migrated on startup. To try the kiosk with demo staff (PINs `1234` and `0042`), run a Debug build with `PUNCHCLOCK_DB` pointing at a scratch file and `PUNCHCLOCK_DEMO=1`; nothing is seeded otherwise.
- **Schema** is the audit-log design in `docs/database/` (PR #6), shipped as migration `0001_audit_schema.sql`. Database triggers write a hash-chained before/after record of every change, check who may make it, and refuse any write from a connection without an actor. Generic SQLite tools can read the file but not write it.
- **Code that opens the database** (the kiosk, the importer) goes through `PunchClockDatabase.OpenAndMigrateAsync()` and `SqliteDatabase.OpenAsync(AuditContext)`, which register `pc_sha256`/`pc_utc_offset`/`pc_ctx` and set the required pragmas. Startup also runs the schema's `verify_*` views (chain, drift, continuity, history, payroll rules) and refuses to open a database that fails them. Set `AuditContext.Actor` before writing.
- **Punches** are immutable IN/OUT events stored in UTC (millisecond precision) with the site's UTC offset; shifts are derived. The employee chooses In or Out and the app refuses a choice that contradicts their current state, so a forgotten punch-out is surfaced instead of silently becoming a multi-day shift. Employees whose PIN someone else knows (imported staff, new staff given a starting PIN, and PIN resets) must choose a new PIN before their next punch is recorded. Five wrong PINs within 15 minutes lock the employee out of punching and PIN changes until the window passes or a manager resets the PIN.
- **Admin** button on the kiosk: the first admin account can only be created by a Windows administrator running PunchClock elevated (Run as administrator), so a kiosk user cannot claim it; after that the button signs in. Five failed sign-ins for a username within 15 minutes lock it until the window passes. Punching stays disabled until an admin sets the site time zone, because every punch's offset is checked against it. Admins set the site time zone, add and deactivate employees, and manage accounts: create a named admin or manager account for each person (with a temporary password they must replace at first sign-in), link it to the employee that person punches as so they cannot correct their own punches, reset a forgotten password, and deactivate accounts (for example the `migration` account after the import). Anyone signed in can change their own password. Admins and managers correct punches on the Corrections tab (add a missing punch, change or void one, with a reason of at least 10 characters; the review list flags missing punch-outs and long shifts; a changed punch in the repeated hour when clocks go back keeps the occurrence it was in), and can reset a forgotten PIN to a temporary one, with an audited reason; the employee must replace it before their next punch is recorded.
- **Reports** tab (admins and managers): pick a pay period to see hours per employee by the old app's report rule, so totals match it during the switch. A shift counts in the period its IN falls in if its OUT is before the day after the period ends, and minutes are Access `DateDiff("n")` on site wall-clock times. Elapsed hours are shown beside them (they differ by an hour for a shift across a daylight-saving change), and shifts that start in the period but are not paid (no punch-out yet, or ending after the period) are listed with the reason instead of being dropped. The pay report, every punch (superseded ones included, with the correction that replaced them) and every correction (who, why, before and after) export to CSV. Each export is recorded as `REPORT_EXPORT` with its row count and SHA-256, and text a person typed is defused so a spreadsheet cannot run it as a formula.
- **Backup** (Site tab, admins): `VACUUM INTO` a new file while the kiosk keeps running, then open the copy read-only and check its schema fingerprint and every `verify_*` view before keeping it. A `BACKUP` audit event records the file's SHA-256 and the audit entry the copy ends at. The Site tab also lists records to review from `verify_clock_v` and `verify_offset_v` (a PC clock that ran backwards, an offset that differs from today's time zone rules); these never stop the app.
- **Migrations** are `src/PunchClock.Data.Sqlite/Migrations/NNNN_name.sql`, applied in order in one transaction, checksummed, and audited as `SCHEMA_MIGRATE`. Never edit an applied migration; add a new one. Startup and every write transaction compare the live schema with the fingerprint this build's scripts produce, so a trigger dropped with a SQLite tool stops the app instead of silently removing a protection.

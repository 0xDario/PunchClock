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
- **Admin** button on the kiosk: the first click creates the admin account; after that it signs in. Punching stays disabled until an admin sets the site time zone, because every punch's offset is checked against it. Admins set the site time zone, deactivate accounts (for example the `migration` account after the import), and add or deactivate employees. Admins and managers can reset a forgotten PIN to a temporary one, with an audited reason; the employee must replace it before their next punch is recorded.
- **Migrations** are `src/PunchClock.Data.Sqlite/Migrations/NNNN_name.sql`, applied in order in one transaction, checksummed, and audited as `SCHEMA_MIGRATE`. Never edit an applied migration; add a new one. Startup and every write transaction compare the live schema with the fingerprint this build's scripts produce, so a trigger dropped with a SQLite tool stops the app instead of silently removing a protection.

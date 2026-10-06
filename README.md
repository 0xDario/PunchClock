# PunchClock

> **v2 rewrite in progress.** The new app lives in `src/` and `tests/` (`PunchClock.Modern.sln`): .NET 10 WPF on a local SQLite database. Everything below describes the legacy v1 WinForms + Access app in `PunchClock/`, which is kept unchanged until the data migration is done. See [v2 rewrite](#v2-rewrite) at the end, and [Moving from the old PunchClock app](#moving-from-the-old-punchclock-app) for the switchover.

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
| `src/PunchClock.Migration` | Legacy import: verifies the exporter's CSVs and manifest, flags anomalies, writes the audit-logged import. |
| `src/PunchClock.Import` | `PunchClock.Import.exe`, the one-time import program shipped in the installer. |
| `tests/PunchClock.Tests` | xUnit v3 tests against real SQLite files. |
| `tests/PunchClock.Migration.Tests` | Importer tests against a database created by the app's own migrations. |

Requires the .NET 10 SDK. On Windows: `dotnet run --project src/PunchClock.App`. Anywhere: `dotnet build PunchClock.Modern.sln` and `dotnet test --solution PunchClock.Modern.sln` (the WPF project builds on Linux/macOS but only runs on Windows).

- **Database**: `%ProgramData%\PunchClock\punchclock.db`, or the path in `PUNCHCLOCK_DB`. Created and migrated on startup. To try the kiosk with demo staff (PINs `1234` and `0042`), run a Debug build with `PUNCHCLOCK_DB` pointing at a scratch file and `PUNCHCLOCK_DEMO=1`; nothing is seeded otherwise.
- **Schema** is the audit-log design in `docs/database/` (PR #6), shipped as migration `0001_audit_schema.sql`. Database triggers write a hash-chained before/after record of every change, check who may make it, and refuse any write from a connection without an actor. Generic SQLite tools can read the file but not write it.
- **Code that opens the database** (the kiosk, the importer) goes through `PunchClockDatabase.OpenAndMigrateAsync()` and `SqliteDatabase.OpenAsync(AuditContext)`, which register `pc_sha256`/`pc_utc_offset`/`pc_ctx` and set the required pragmas. Startup also runs the schema's `verify_*` views (chain, drift, continuity, history, payroll rules) and refuses to open a database that fails them. Set `AuditContext.Actor` before writing.
- **Punches** are immutable IN/OUT events stored in UTC (millisecond precision) with the site's UTC offset; shifts are derived. The employee chooses In or Out and the app refuses a choice that contradicts their current state, so a forgotten punch-out is surfaced instead of silently becoming a multi-day shift. Employees whose PIN someone else knows (imported staff, new staff given a starting PIN, and PIN resets) must choose a new PIN before their next punch is recorded. Five wrong PINs within 15 minutes lock the employee out of punching and PIN changes until the window passes or a manager resets the PIN.
- **Admin** button on the kiosk: the first admin account can only be created by a Windows administrator running PunchClock elevated (Run as administrator), so a kiosk user cannot claim it; after that the button signs in. Five failed sign-ins for a username within 15 minutes lock it until the window passes. Punching stays disabled until an admin sets the site time zone, because every punch's offset is checked against it. Admins set the site time zone, add and deactivate employees, and manage accounts: create a named admin or manager account for each person (with a temporary password they must replace at first sign-in), link it to the employee that person punches as so they cannot correct their own punches, reset a forgotten password, and deactivate accounts (for example the `migration` account after the import). Anyone signed in can change their own password. Admins and managers correct punches on the Corrections tab (add a missing punch, change or void one, with a reason of at least 10 characters; the review list flags missing punch-outs and long shifts; a changed punch in the repeated hour when clocks go back keeps the occurrence it was in), and can reset a forgotten PIN to a temporary one, with an audited reason; the employee must replace it before their next punch is recorded.
- **Migrations** are `src/PunchClock.Data.Sqlite/Migrations/NNNN_name.sql`, applied in order in one transaction, checksummed, and audited as `SCHEMA_MIGRATE`. Never edit an applied migration; add a new one. Startup and every write transaction compare the live schema with the fingerprint this build's scripts produce, so a trigger dropped with a SQLite tool stops the app instead of silently removing a protection.

## Moving from the old PunchClock app

One-time switchover of a PC running the old app (v1.x, Access database) to v2. Do it when nobody needs to punch, and do every step on that PC. Nothing below changes or deletes the old database: until someone punches in the new app, going back means simply starting the old app again.

**Before you switch:** v2 does not have a pay-period hours report or a data export yet, and the old app cannot read v2's punches. Wait for a release whose notes list them, unless you can run payroll another way until then.

**You need** the v2 installer, `PunchClock-Setup-<version>.exe`, from [Releases](https://github.com/0xDario/PunchClock/releases), and a USB stick for the backup.

1. **Stop the old app.** Pick a time when nobody is on shift, close PunchClock, and check Task Manager that `PunchClock.exe` is gone.

2. **Back up the database.** Right-click the old PunchClock shortcut, choose *Open file location*, and find `PunchClock.accdb` in that folder (the old app keeps it next to `PunchClock.exe`). Copy it to the USB stick and to one more place, renamed `PunchClock-backup-<date>.accdb`. Do not open the original in Access.

3. **Print the old hours report** for the last complete month, the way you normally run it (if that means opening the file in Access, open a copy of the backup, never the original). You will compare it with the new app in step 7.

4. **Install v2 and create the admin.** Run the installer. It is not code-signed yet, so Windows shows *Windows protected your PC*: click **More info**, then **Run anyway**. It installs to Program Files, creates `C:\ProgramData\PunchClock` for the database, and adds a Start menu folder *PunchClock > Move data from the old app*. Then create the admin right away: right-click *PunchClock* in the Start menu, choose *More > Run as administrator*, click **Admin** and create the admin account (the first admin can only be created from an elevated window). On the **Site** tab, set the time zone the old app's punches were recorded in, normally this PC's own zone. Close PunchClock and start it normally from now on. Do not add employees or punch yet, or the import will refuse to run.

5. **Export the old data.** Start menu > *PunchClock > Move data from the old app > 1. Export old data*, then pick the original `PunchClock.accdb` from step 2. The exporter works on a copy, checks every row count with Access, records this PC's time zone, and writes `legacy-export-<timestamp>.zip` to the Desktop. If it says the database is open, the old app is still running; close it and try again.

6. **Import into v2.** Start menu > *2. Import into PunchClock*. Drag the zip from the Desktop onto the window and press Enter. The importer verifies the export, shows how many employees, shifts and hours it found and which time zone it will use (press Enter if that is where the punches were recorded), and opens a check report in Notepad. Read its *Needs review* list, then type `IMPORT`. The importer stops without writing anything if the time zone it would use differs from the one set on the Site tab in step 4. It writes everything in one step and opens the import report: **print it** and keep it with the backup. Its *Audit log head* line is the fingerprint that proves later that the imported history was not altered.

7. **Reconcile hours.** Open `hours-by-month.csv` (next to the zip on the Desktop, in the `-import` folder). For the month you printed in step 3, each employee's `legacy_report_hours` must equal the old report's total exactly. Employee and shift counts are already checked by the importer, which refuses to finish if any row is missing. If the hours do not match, keep using the old app and send the zip and both report folders to whoever maintains PunchClock; nothing in the old database has changed.

8. **Finish setup as admin.** In PunchClock click **Admin** and sign in. Work through the report's *Needs review* list on the **Corrections** tab: add the missing punch-outs, fix overlaps and negative shifts, and fix anyone whose punched in/out state differs from the old app. Deactivate the `migration` account with a reason such as "Cutover done", so nothing can import into this database again. Check that every employee is listed.

9. **Switch over.** Anyone still punched in at the export shows as punched in; anyone the report flagged with a different in/out state should have been fixed in step 8. PINs carry over, but the old app stored them as numbers, so a PIN that started with 0 lost that digit (`0123` is now `123`). An employee who had no PIN gets a temporary one, printed under *Temporary PINs* in the import report; hand it to them in person. Because the old file kept PINs in plain text, every imported employee must choose a new PIN before their next punch is recorded. If someone forgets their PIN later, an admin can reset it from **Admin**.

10. **Retire the old app.** Delete its Desktop and Startup shortcuts and rename its folder to `PunchClock-v1-retired` so nobody starts it by habit; a punch there would be missing from v2. Keep the `.accdb` backup, the export zip and the printed reports for as long as payroll records must be kept where you are. The zip holds every old PIN in plain text and the `-import` report folder holds any temporary PINs, so store them on the USB stick, not on the shared PC, and delete the Desktop copies (the `legacy-export-<timestamp>` folder, its zip and both report folders). A copy of the import report and manifest also stays in `C:\ProgramData\PunchClock\imports`.

What the import does with the old data, in short:

- Every employee and shift keeps its old ID, and every exported row is stored unchanged in the new database as evidence (except PINs, which are stored only as hashes).
- Times are converted from the PC's local time to UTC with the recorded time zone, so shifts across a daylight-saving change get their true length. Times in the repeated or skipped hour are flagged.
- Skipped, with the raw row kept and the reason recorded: the zero-length shift the old app added for every new employee, shifts whose employee no longer exists (the old report never counted them either), and shifts with no punch-in time.
- Imported and flagged for a manager to fix through the new app's audited corrections: missed punch-outs, punch-outs before punch-ins, shifts over 16 hours, overlaps, rows added out of order (a sign of hand edits in Access).
- The whole import is one audited event as the `migration` account, carrying the old file's SHA-256 and row counts. It is all or nothing, it refuses to run twice, and it refuses a database that already has employees or punches.

What it does not protect against yet: anyone who can sign in to the PC can copy, replace or script-edit `C:\ProgramData\PunchClock\punchclock.db` (every Windows account needs write access so it can punch). The app refuses to start if the audit chain, the row history or the admission rules do not verify, so edits made behind its back are caught. It does not save checkpoints yet, though, and only a fingerprint kept off the PC, like the *Audit log head* on the printed import report, catches someone rewriting the whole file. Keep that printout, and back up the database regularly to a drive you keep elsewhere.

From a command prompt, the same importer runs unattended:

```
"C:\Program Files\PunchClock\Migration\PunchClock.Import.exe" check  legacy-export-<timestamp>.zip
"C:\Program Files\PunchClock\Migration\PunchClock.Import.exe" import legacy-export-<timestamp>.zip --yes [--time-zone "Eastern Standard Time"] [--db <path>]
```

## Releasing

1. Set `<Version>` in `src/Directory.Build.props` and merge to `master`.
2. Tag it: `git tag v2.0.0 && git push origin v2.0.0`. v1.x tags are the legacy app.
3. `.github/workflows/release.yml` runs the tests, publishes the app and the importer self-contained for `win-x64`, bundles the exporter, builds `PunchClock-Setup-<version>.exe` (Inno Setup, `installer/PunchClock.iss`) and `SHA256SUMS.txt`, and attaches them to a **draft** release. Check the draft, then publish it.

Pull requests that touch packaging build the same installer as a workflow artifact, without a release.

# PunchClock (modern)

Rebuild of the legacy WinForms + Access app in `../PunchClock`, which stays untouched until the migration is done.

| Project | Purpose |
|---|---|
| `src/PunchClock.Core` | Domain: employees, punch events, punch rules, PIN hashing, services. No I/O. |
| `src/PunchClock.Data` | SQLite stores and the migration runner (`Microsoft.Data.Sqlite`, plain SQL). |
| `src/PunchClock.App` | WPF kiosk window (`net8.0-windows`). |
| `tests/PunchClock.Tests` | xUnit tests against a real SQLite file. |

## Build and test

Requires the .NET 8 SDK.

```
cd modern
dotnet test
dotnet run --project src/PunchClock.App   # Windows only
```

The app builds on Linux/macOS (`EnableWindowsTargeting`) but only runs on Windows. CI runs on `windows-latest` (`.github/workflows/modern-ci.yml`).

## Database

- File: `%ProgramData%\PunchClock\punchclock.db`, override with `PUNCHCLOCK_DB`.
- Schema changes are numbered scripts in `src/PunchClock.Data/Migrations/NNNN_name.sql`, embedded in the assembly and applied at startup in order, one transaction each. `schema_migrations` records each script's SHA-256; the app refuses to start if an applied script was edited or the database is newer than the build. Never edit an applied script, add a new one.
- Debug builds seed `Demo Employee` / PIN `1234` into an empty database.

## Behaviour changes from the legacy app

- **Punch direction is explicit.** Separate Punch In and Punch Out buttons. A Punch In while already in (or Out while out) is refused with a message, not silently toggled. State comes from the latest punch by time, not by row id.
- **Punches are append-only events** (`IN`/`OUT` rows), enforced by triggers. No placeholder shift is created for new employees.
- **PINs are hashed** (PBKDF2-SHA256, per-PIN salt, 210k iterations) and treated as digit strings, so leading zeros count. 3 to 8 digits; 3 is kept for legacy PINs.
- **Timestamps are UTC plus the local offset** at punch time, so DST changes do not corrupt durations.
- **State check and insert are one `BEGIN IMMEDIATE` transaction**, so a double click or a second kiosk cannot record two punch-ins.
- The confirmation message is shown only after the write commits.

## Not in this scaffold

Audit log, manager accounts and punch corrections, reports, and the Access import. The schema here is deliberately minimal so the audit design can replace or extend it through new migrations.

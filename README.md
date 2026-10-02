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

- **Database**: `%ProgramData%\PunchClock\punchclock.db`, or the path in `PUNCHCLOCK_DB`. Created and migrated on startup. Debug builds seed two demo employees (PINs `1234` and `0042`).
- **Punches** are immutable IN/OUT events stored in UTC with the site's UTC offset; shifts are derived. The employee chooses In or Out and the app refuses a choice that contradicts their current state, so a forgotten punch-out is surfaced instead of silently becoming a multi-day shift.
- **Migrations** are `src/PunchClock.Data.Sqlite/Migrations/NNNN_name.sql`, applied in order in one transaction and checksummed. Never edit an applied migration; add a new one.

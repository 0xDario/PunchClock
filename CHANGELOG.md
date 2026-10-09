# Changelog

Each GitHub release takes its notes from its section below. Before tagging a release, add a `## vX.Y.Z - YYYY-MM-DD` section for it, using the same headings (Features Added, Bugs Fixed). The release build stops if the section is missing. v1.x is the legacy WinForms app; its notes are on the [Releases](https://github.com/0xDario/PunchClock/releases) page.

## v2.0.2 - 2026-10-07

The first v2 release ready for the switchover from the old app. Use it instead of 2.0.0 and 2.0.1, whose export tool cannot read the old database. The app itself is the same as in 2.0.0.

Features Added (compared with v1.1, the old app):
- Rebuilt from scratch: .NET 10 on a local SQLite database, replacing WinForms and Access. Installs with one setup program on Windows 10/11 (64-bit)
- Audit log of every change to the database: who, when, why, and the before and after values. Entries are hash-chained, and the app refuses to start if the log, the punch history or its rules fail verification
- Employees choose Punch In or Punch Out, and the app refuses a choice that contradicts their current state, so a forgotten punch-out shows up instead of becoming a multi-day shift
- PINs are stored hashed, never in plain text. Imported staff, new staff and PIN resets must choose a new PIN before their next punch. Five wrong PINs within 15 minutes lock the employee out
- Named admin and manager accounts with their own passwords. The first admin can only be created by a Windows administrator, and an account linked to the employee its owner punches as cannot correct that employee's punches
- Corrections tab: add a missing punch, change or void one, always with a reason. A review list flags missing punch-outs and long shifts
- Reports tab: hours per employee for a pay period, using the old report's rule so totals match it. Shifts that are not paid yet are listed with the reason
- CSV export of the pay report, every punch and every correction, each one recorded in the audit log
- Backup button that copies the database while the kiosk keeps running and checks the copy before keeping it
- Switchover tools in the installer: an export tool for the old Access database and an importer that checks every row, flags anything that needs a manager's review, and prints a report. See [Moving from the old PunchClock app](https://github.com/0xDario/PunchClock/blob/v2.0.2/README.md#moving-from-the-old-punchclock-app)

Bugs Fixed (compared with v2.0.1):
- Export tool: "Could not find installable ISAM" when opening the old database
- Export tool: "The parameter is incorrect" while reading the database's column list
- Export tool: "Argument types do not match" while writing the export's manifest
- Export tool: "The term 'Get-FileHash' is not recognized" when started from a PowerShell 7 window
- Export tool: with only the 32-bit Access engine installed, the export stopped because Access still held its working copy's lock file
- Export tool: an unexpected error now names the failing line and includes the details needed to diagnose it
- Every release build now runs the export tool on a real Access database with both the 64-bit and the 32-bit Access engine and checks the result with the importer, so an export tool that cannot read Access can no longer ship

## v2.0.1 - 2026-10-07

**Do not use this release.** Its export tool still cannot read the old Access database ("Could not find installable ISAM"). Use [v2.0.2](https://github.com/0xDario/PunchClock/releases/tag/v2.0.2) or later.

Bugs Fixed:
- Export tool: "Unable to cast object of type 'System.Management.Automation.PSObject' to type 'System.IConvertible'" before reading any rows. This fix exposed the next error, fixed in v2.0.2

## v2.0.0 - 2026-10-07

**Do not use this release.** Its export tool cannot read the old Access database, so the switchover stops at the export step. Use [v2.0.2](https://github.com/0xDario/PunchClock/releases/tag/v2.0.2) or later.

Features Added:
- First release of PunchClock v2, rebuilt from scratch on .NET 10 and SQLite, with an audit log of every change, In/Out punching, hashed PINs, admin and manager accounts, punch corrections, pay-period reports, CSV export, backups, and tools to move the data from the old app. [v2.0.2](https://github.com/0xDario/PunchClock/releases/tag/v2.0.2) lists them in full

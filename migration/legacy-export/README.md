# Legacy Access export

Step one of the migration: get every row out of the legacy `PunchClock.accdb` with
legacy IDs, raw timestamps, and enough evidence to prove later that nothing was lost
or altered.

| Tool | Runs on | Reads via | Role |
|---|---|---|---|
| `run-export.cmd` / `Export-LegacyData.ps1` | Windows, the machine that runs the old app | ACE OLEDB, the engine the app uses | Primary export |
| `jackcess/LegacyExport.java` | Any OS | [Jackcess](https://jackcess.sourceforge.io/) + jackcess-encrypt, no Access install | Independent second read of the same snapshot |
| `validate_export.py` | Any OS, Python 3.9+, stdlib only | the export folder or zip | Integrity check, cross-check, data-quality report |

## Running the export (Windows)

1. Close PunchClock on every machine that uses the database file.
2. Copy this folder to the machine that holds the production `PunchClock.accdb`.
3. Double-click `run-export.cmd` and pick the database in the file dialog, or:

   ```bat
   run-export.cmd -Source "C:\path\to\PunchClock.accdb"
   ```

4. Hand back `Desktop\legacy-export-<timestamp>.zip`.

`run-export.cmd` starts 64-bit Windows PowerShell and retries in 32-bit if the Access
Database Engine is only installed for 32-bit (the legacy app ships x86 builds). Options:

| Parameter | Default | |
|---|---|---|
| `-Source` | file dialog | the production `.accdb`, not the empty copy in this repo |
| `-Password` | `admin123` | the legacy default |
| `-OutDir` | `Desktop\legacy-export-<timestamp>` | must be empty or absent |
| `-SiteTimeZone` | this machine's zone | Windows zone ID of the site, e.g. `Eastern Standard Time`, if exporting elsewhere |
| `-Force` | off | run despite a `.laccdb` lock file, only if it is stale |
| `-NoZip` | off | skip the zip |

Exit codes: 0 ok, 2 bad input or password, 3 no ACE provider in either bitness
(install the *Microsoft Access Database Engine 2016 Redistributable*), 4 reconciliation
failed, 5 database open, 6 file changed during copy.

## What a run produces

```
legacy-export-<timestamp>/
  source/PunchClock.accdb   byte-exact snapshot the export was read from
  Employee.csv
  Shift.csv
  <any other user table>.csv
  queries.csv               saved query SQL (the pay report's record source, if saved)
  manifest.json             hashes, row counts, ID ranges, Access control totals, site time zone
  SHA256SUMS.txt            every file above
legacy-export-<timestamp>.zip
```

The output holds every employee's PIN in plain text and a full copy of the database.
Share it only through the private project and never commit it; `.gitignore`
excludes `legacy-export-*`.

## Guarantees

- **Source untouched.** The lock file is checked, the source is hashed and copied, and
  the copy must hash identically. The export opens only the copy. Both the copy and the
  source are re-hashed at the end; any change fails the run.
- **Row counts verified by Access, not trusted.** One aggregate query, separate from the
  row export, returns `COUNT(*)`, per-column non-NULL counts, sums of numeric columns and
  date bounds. The run fails if the CSV row count disagrees; the validator checks the rest.
  The row count Access caches in table headers is ignored: it is stale in every committed
  copy (header says 5 employees and 12 shifts, both tables are empty).
- **IDs kept.** Rows are ordered by primary key; `EmployeeID` and `ShiftID` are the
  legacy AutoNumber values.
- **Raw timestamps kept.** Each date column is written twice:
  - `TimeIn`: `yyyy-MM-ddTHH:mm:ss.fff`, site-local wall clock, no offset. The app wrote
    `DateTime.Now`; `manifest.json` records the site time zone so the importer can
    convert to UTC.
  - `TimeIn_OADate`: the stored OLE Automation double (`CDbl()` in Access), shortest
    round-trip decimal. If a stored value has sub-millisecond precision the manifest
    warns, and `_OADate` is the exact one.
- **Two engines agree.** `Employee.csv` and `Shift.csv` from ACE and from Jackcess are
  byte-identical for the same file (`tests/Test-CsvFormat.ps1` checks the formatter
  against Jackcess output of every committed copy of the database).

## CSV format

UTF-8 without BOM, CRLF line endings, header row, RFC 4180 quoting. NULL is an empty
unquoted field (an open shift has empty `TimeOut` and `TimeOut_OADate`); an empty
string is `""`. Numbers are culture-invariant, Yes/No is `true`/`false`, binary is base64.

## Validating a returned export

```sh
python3 validate_export.py legacy-export-<timestamp>.zip --site-tz America/Toronto --out report.md

# Independent second read of the snapshot, then cross-check:
cd jackcess && mvn -q dependency:copy-dependencies -DoutputDirectory=lib
java -cp 'lib/*' LegacyExport.java ../export/source/PunchClock.accdb ../jackcess-export
python3 validate_export.py ../export --compare ../jackcess-export
```

Integrity failures exit 1. Data-quality findings are reported, not failed, because each
one is a migration decision: orphan `Shift.EmployeeID` values (the relationship exists
but referential integrity is off), zero-length dummy shifts created with each new
employee, open shifts and open shifts the legacy app can never close, shifts over 16 h,
overlaps, ShiftID order disagreeing with time order, PINs that lost leading zeros or are
shared, and DST-ambiguous or nonexistent local times. It ends with hours per employee,
for comparison against the legacy pay report.

## Tests

```sh
python3 -m unittest discover tests
pwsh tests/Test-CsvFormat.ps1 -JackcessExport <jackcess export folder>
```

## Reconciliation the importer must meet

- `row_count`, `min_id`, `max_id` and control totals per table
- every legacy ID present exactly once in the new database
- the source `sha256` recorded as the first audit event, so the legal chain starts at
  the exact file that was migrated

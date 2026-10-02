# Legacy Access export

Step one of the migration: get every `Employee` and `Shift` row out of the legacy
`PunchClock.accdb` with legacy IDs, raw timestamps, and enough evidence to prove
later that nothing was lost or altered.

Two exporters produce the same output format. Run either; run both on the same
file if you want an independent cross-check.

| Tool | Runs on | Reads via | Needs |
|---|---|---|---|
| `Export-LegacyData.ps1` | Windows (the machine running the old app) | ACE OLEDB, same engine as the app | Windows PowerShell 5.1, Access Runtime / Access Database Engine already installed for the app |
| `jackcess/LegacyExport.java` | Windows, macOS, Linux | [Jackcess](https://jackcess.sourceforge.io/) + jackcess-encrypt (pure Java, no Access install) | Java 21, Maven to fetch the jars |

## Running

Close the PunchClock app first so the file is not written mid-copy.

**Windows, ACE OLEDB**

```powershell
powershell -ExecutionPolicy Bypass -File .\Export-LegacyData.ps1 -Source 'C:\path\to\PunchClock.accdb'
```

If it reports no ACE provider, the Access Runtime is the other bitness. Run the same
command from `C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe` (32-bit).
`-Password` defaults to `admin123`; pass it if the production file uses another one.

**Any OS, Jackcess**

```sh
cd jackcess
mvn -q dependency:copy-dependencies -DoutputDirectory=lib
java -cp 'lib/*' LegacyExport.java /path/to/PunchClock.accdb [outDir] [password]
```

## What each run produces

```
legacy-export-<yyyyMMdd-HHmmss>/
  source/PunchClock.accdb   byte-exact snapshot the export was read from
  Employee.csv
  Shift.csv
  manifest.json             source SHA-256, row counts, ID ranges, CSV SHA-256s
```

The output contains every employee's PIN in plain text (the legacy schema stores it
that way). Treat the folder as sensitive and never commit it; `.gitignore` excludes
`legacy-export-*/`.

## Guarantees

- **Source untouched.** The source is hashed, copied, and the copy is hashed again
  before export. The export opens only the copy, read-only, and re-hashes it after.
  Any hash mismatch aborts the run.
- **Row counts are verified, not trusted.** Rows written are checked against an
  independent count (`COUNT(*)` on ACE, a physical table scan on Jackcess). The
  row count Access stores in each table header is deliberately ignored: it is only
  refreshed on compact, and every committed copy of the file has it wrong (header
  says 5 employees and 12 shifts; both tables are actually empty).
- **IDs kept.** Rows are ordered by primary key; `EmployeeID` and `ShiftID` are the
  legacy AutoNumber values, unchanged.
- **Raw timestamps kept.** Each date column is written twice:
  - `TimeIn` as `yyyy-MM-ddTHH:mm:ss.fff`, site-local wall-clock time, no offset
    (the app wrote `DateTime.Now`; there is no time zone information to recover).
  - `TimeIn_OADate` as the OLE Automation double Access stores, shortest
    round-trip decimal. Both engines read at millisecond precision, and the app
    wrote millisecond-precision values, so this is the stored value.

## CSV format

UTF-8 without BOM, CRLF line endings, header row, RFC 4180 quoting. NULL is an empty
unquoted field (an open shift has empty `TimeOut` and `TimeOut_OADate`); an empty
string is `""`. Both tools emit byte-identical CSVs for the same source file, so the
`sha256` values in the two manifests match.

## Reconciliation

The importer must end with the same numbers this manifest records:

- `row_count`, `min_id`, `max_id` per table
- every legacy ID present exactly once in the new database
- the source `sha256` recorded as the first audit event, so the legal chain starts
  at the exact file that was migrated

Known data issues the importer has to handle (seen in the repo's git history copies,
expect them in production): orphan `Shift.EmployeeID` values with no employee (the
`EmployeeShift` relationship exists but referential integrity is off), zero-length
dummy shifts created with each new employee (`TimeIn = TimeOut`), and open shifts
(`TimeOut` NULL).

#!/usr/bin/env python3
"""Validate a legacy PunchClock export and report data-quality findings.

Usage:
    python3 validate_export.py <export-dir-or-zip> [--compare <other-export>]
                               [--site-tz America/Toronto] [--out report.md]

Integrity checks (any failure exits 1):
  - SHA256SUMS.txt, CSV and snapshot hashes match the manifest
  - every CSV parses as strict RFC 4180 with the manifest's header
  - row counts, ID ranges and Access control totals match; IDs are unique and ascending
  - each ISO timestamp equals its *_OADate double rounded to the millisecond
  - with --compare, the same tables in both exports hold the same rows

Data-quality findings never fail the run; they are what the importer must decide on.
Works with output of Export-LegacyData.ps1 and jackcess/LegacyExport.java. Stdlib only.
"""

import argparse
import collections
import datetime as dt
import decimal
import hashlib
import json
import pathlib
import shutil
import sys
import tempfile
import zipfile

OA_EPOCH = dt.datetime(1899, 12, 30)
MS_PER_DAY = 86_400_000
LONG_SHIFT = dt.timedelta(hours=16)
# NewStaffForm inserts the dummy shift with two separate DateTime.Now calls, so it can be a few ms long.
NEAR_ZERO = dt.timedelta(seconds=1)
REQUIRED_TABLES = ("Employee", "Shift")


# --- parsing -----------------------------------------------------------------

def parse_csv(text):
    """Strict RFC 4180. Unquoted empty field -> None (NULL); quoted empty -> ''."""
    rows, row, i, n = [], [], 0, len(text)
    while i < n:
        if text[i] == '"':
            i += 1
            buf = []
            while True:
                j = text.find('"', i)
                if j < 0:
                    raise ValueError("unterminated quoted field")
                buf.append(text[i:j])
                if j + 1 < n and text[j + 1] == '"':
                    buf.append('"')
                    i = j + 2
                    continue
                i = j + 1
                break
            value = "".join(buf)
        else:
            j = i
            while j < n and text[j] not in ',\r\n':
                if text[j] == '"':
                    raise ValueError(f"bare quote at offset {j}")
                j += 1
            value = text[i:j] if j > i else None
            i = j
        row.append(value)
        if i >= n:
            raise ValueError("last line does not end with CRLF")
        if text[i] == ',':
            i += 1
        elif text.startswith('\r\n', i):
            rows.append(row)
            row = []
            i += 2
        else:
            raise ValueError(f"expected , or CRLF at offset {i}")
    return rows


def oadate_to_datetime(d):
    """Same arithmetic as .NET DateTime.FromOADate (rounds to the millisecond)."""
    ms = int(d * MS_PER_DAY + (0.5 if d >= 0 else -0.5))
    if ms < 0:
        ms -= (ms % -MS_PER_DAY) * 2  # C# % keeps the dividend's sign
    return OA_EPOCH + dt.timedelta(milliseconds=ms)


def parse_iso(s):
    return dt.datetime.strptime(s, "%Y-%m-%dT%H:%M:%S.%f")


def fmt_iso(t):
    return t.strftime("%Y-%m-%dT%H:%M:%S.") + f"{t.microsecond // 1000:03d}"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def hours(td):
    return td.total_seconds() / 3600


class Export:
    def __init__(self, path):
        path = pathlib.Path(path)
        if path.suffix.lower() == ".zip":
            self._tmp = tempfile.TemporaryDirectory()
            root = pathlib.Path(self._tmp.name)
            with zipfile.ZipFile(path) as z:
                for info in z.infolist():
                    # Windows PowerShell 5.1 Compress-Archive writes backslash separators.
                    rel = pathlib.PurePosixPath(info.filename.replace("\\", "/"))
                    if info.is_dir() or rel.is_absolute() or ".." in rel.parts:
                        continue
                    dest = root.joinpath(*rel.parts)
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    # Stream: the snapshot can be as large as the production database.
                    with z.open(info) as src, open(dest, "wb") as out:
                        shutil.copyfileobj(src, out, 1 << 20)
            # Compress-Archive puts files at the root; tolerate one wrapping folder.
            if not (root / "manifest.json").exists():
                subs = [p for p in root.iterdir() if p.is_dir() and (p / "manifest.json").exists()]
                if len(subs) == 1:
                    root = subs[0]
            path = root
        self.dir = path
        self.manifest = json.loads((path / "manifest.json").read_text(encoding="utf-8-sig"))
        self.tables = {}  # name -> (header, rows)


# --- validation ----------------------------------------------------------------

class Report:
    def __init__(self):
        self.failures, self.lines = [], []

    def fail(self, msg):
        self.failures.append(msg)

    def add(self, line=""):
        self.lines.append(line)


def check_integrity(ex, rep):
    m = ex.manifest
    for f in m.get("failures") or []:
        rep.fail(f"exporter reported: {f}")

    sums = ex.dir / "SHA256SUMS.txt"
    if sums.exists():
        listed = set()
        for line in sums.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            digest, name = line.split("  ", 1)
            listed.add(name)
            p = ex.dir / name
            if not p.exists():
                rep.fail(f"SHA256SUMS lists missing file {name}")
            elif sha256(p) != digest:
                rep.fail(f"SHA256SUMS mismatch for {name}")
        extra = [str(p.relative_to(ex.dir)).replace("\\", "/") for p in ex.dir.rglob("*")
                 if p.is_file() and p.name != "SHA256SUMS.txt"]
        for name in sorted(set(extra) - listed):
            rep.fail(f"file not covered by SHA256SUMS: {name}")

    # Without the snapshot the CSVs cannot be tied back to the hashed source bytes.
    src = m.get("source", {})
    snap = src.get("snapshot")
    if not snap:
        rep.fail("manifest declares no source snapshot")
    elif not (ex.dir / snap).exists():
        rep.fail(f"snapshot {snap} named in the manifest is missing")
    elif sha256(ex.dir / snap) != src.get("sha256"):
        rep.fail("snapshot hash differs from the source hash in the manifest")

    names = {t["name"] for t in m["tables"]}
    for name in REQUIRED_TABLES:
        if name not in names:
            rep.fail(f"{name} table missing from export {ex.dir}")
    for t in m["tables"]:
        check_table(ex, t, rep)


def check_table(ex, t, rep):
    name = t["name"]
    path = ex.dir / t["file"]
    if not path.exists():
        rep.fail(f"{name}: {t['file']} missing")
        return
    if t.get("sha256") and sha256(path) != t["sha256"]:
        rep.fail(f"{name}: CSV hash differs from manifest")
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        rep.fail(f"{name}: CSV has a BOM")
    try:
        rows = parse_csv(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError) as e:
        rep.fail(f"{name}: CSV does not parse: {e}")
        return
    if not rows:
        rep.fail(f"{name}: CSV is empty (no header)")
        return
    header, body = rows[0], rows[1:]
    if header != t["columns"]:
        rep.fail(f"{name}: header {header} != manifest columns {t['columns']}")
        return
    for k, r in enumerate(body, 2):
        if len(r) != len(header):
            rep.fail(f"{name}: line {k} has {len(r)} fields, header has {len(header)}")
            return
    # Only structurally sound tables reach the cross-check and data-quality analysis.
    ex.tables[name] = (header, body)
    if len(body) != t["row_count"]:
        rep.fail(f"{name}: {len(body)} rows in CSV, manifest says {t['row_count']}")

    pk = t.get("primary_key")
    if isinstance(pk, str) and pk in header:
        k = header.index(pk)
        keys = [r[k] for r in body]
        if None in keys:
            rep.fail(f"{name}: NULL {pk}")
            return
        if len(set(keys)) != len(keys):
            rep.fail(f"{name}: duplicate {pk} values")
        try:
            ids = [int(x) for x in keys]
        except ValueError:
            ids = None  # text or GUID key: Access collation decides the order, so only uniqueness is checked
        if ids is not None:
            if ids != sorted(ids):
                rep.fail(f"{name}: rows not ordered by {pk}")
            lo, hi = (min(ids), max(ids)) if ids else (None, None)
            if (t.get("min_id"), t.get("max_id")) != (lo, hi):
                rep.fail(f"{name}: {pk} range {lo}..{hi} != manifest {t.get('min_id')}..{t.get('max_id')}")

    # ISO column must equal the stored double rounded to the millisecond.
    for c in header:
        if not c.endswith("_OADate") or c[:-7] not in header:
            continue
        ki, ko = header.index(c[:-7]), header.index(c)
        bad = 0
        for r in body:
            if (r[ki] is None) != (r[ko] is None):
                bad += 1
            elif r[ki] is not None and fmt_iso(oadate_to_datetime(float(r[ko]))) != r[ki]:
                bad += 1
        if bad:
            rep.fail(f"{name}.{c[:-7]}: {bad} values disagree with {c}")

    totals = t.get("control_totals")
    if totals:
        for col, ct in totals.items():
            if col not in header:
                rep.fail(f"{name}: control totals name unknown column {col}")
                continue
            vals = [r[header.index(col)] for r in body if r[header.index(col)] is not None]
            if len(vals) != ct["non_null"]:
                rep.fail(f"{name}.{col}: {len(vals)} non-NULL values, Access counted {ct['non_null']}")
            if "sum" in ct:
                got = sum((decimal.Decimal(v) for v in vals), decimal.Decimal(0))
                want = decimal.Decimal(ct["sum"]) if ct["sum"] is not None else decimal.Decimal(0)
                if got != want:
                    rep.fail(f"{name}.{col}: sum {got} != Access SUM {want}")
            for agg, fn in (("min", min), ("max", max)):
                if agg in ct and (fn(vals) if vals else None) != ct[agg]:
                    rep.fail(f"{name}.{col}: {agg} {fn(vals) if vals else None} != Access {agg.upper()} {ct[agg]}")


def compare(a, b, rep):
    rep.add("## Cross-check against second export")
    rep.add()
    ta = {t["name"]: t for t in a.manifest["tables"]}
    tb = {t["name"]: t for t in b.manifest["tables"]}
    if a.manifest["source"].get("sha256") != b.manifest["source"].get("sha256"):
        rep.fail("compared exports were read from different source files (sha256 differs)")
    for name in sorted(set(ta) & set(tb)):
        if name not in a.tables or name not in b.tables:
            continue
        if (a.dir / ta[name]["file"]).read_bytes() == (b.dir / tb[name]["file"]).read_bytes():
            rep.add(f"- {name}: byte-identical ({len(a.tables[name][1])} rows)")
            continue
        ha, ra = a.tables[name]
        hb, rb = b.tables[name]
        if ha != hb:
            rep.fail(f"{name}: columns differ between exports: {ha} vs {hb}")
            continue
        diffs = [(x, y) for x, y in zip(ra, rb) if x != y]
        if len(ra) != len(rb) or diffs:
            rep.fail(f"{name}: {len(diffs)} differing rows, {len(ra)} vs {len(rb)} rows")
            for x, y in diffs[:5]:
                rep.add(f"  - `{x}` vs `{y}`")
        else:
            rep.add(f"- {name}: same values, different encoding")
    only = sorted(set(ta) ^ set(tb))
    if only:
        rep.add(f"- Tables in only one export: {', '.join(only)}")
    rep.add()


# --- data quality ------------------------------------------------------------------

def col(header, row, name):
    lower = [h.lower() for h in header]
    return row[lower.index(name.lower())] if name.lower() in lower else None


def is_near_zero(shift):
    return shift[3] is not None and dt.timedelta(0) <= shift[3] - shift[2] < NEAR_ZERO


def quality(ex, site_tz, rep):
    if "Employee" not in ex.tables or "Shift" not in ex.tables:
        return
    eh, erows = ex.tables["Employee"]
    sh, srows = ex.tables["Shift"]
    rep.add("## Data-quality findings")
    rep.add()
    rep.add("These do not fail validation. Each one is a migration decision.")
    rep.add()

    employees = {}
    for r in erows:
        employees[int(col(eh, r, "EmployeeID"))] = r
    rep.add(f"### Employee ({len(erows)} rows)")
    if any(h.lower() == "isactive" for h in eh):
        act = collections.Counter(col(eh, r, "IsActive") for r in erows)
        rep.add(f"- IsActive values: {dict(act)}")
    else:
        rep.add("- No IsActive column (file predates the 2023-10 schema change).")
    blank = [i for i, r in employees.items()
             if not (col(eh, r, "FirstName") or "").strip() or not (col(eh, r, "LastName") or "").strip()]
    if blank:
        rep.add(f"- Blank first or last name: EmployeeID {blank}")
    names = collections.Counter(((col(eh, r, "FirstName") or "").strip().lower(),
                                 (col(eh, r, "LastName") or "").strip().lower()) for r in erows)
    dupes = [f"{f} {l}" for (f, l), n in names.items() if n > 1]
    if dupes:
        rep.add(f"- Duplicate names: {dupes}")
    pins = {i: col(eh, r, "PinCode") for i, r in employees.items()}
    nulls = [i for i, p in pins.items() if p is None]
    if nulls:
        rep.add(f"- NULL PIN: EmployeeID {nulls}")
    lengths = collections.Counter(len(p) for p in pins.values() if p is not None)
    rep.add(f"- PIN digit lengths: {dict(sorted(lengths.items()))}")
    short = [i for i, p in pins.items() if p is not None and len(p) < 3]
    if short:
        rep.add(f"- PIN under 3 digits (leading zeros were lost, or the 3-digit rule was bypassed): EmployeeID {short}")
    shared = collections.defaultdict(list)
    for i, p in pins.items():
        if p is not None:
            shared[p].append(i)
    shared = [ids for ids in shared.values() if len(ids) > 1]
    if shared:
        rep.add(f"- Employees sharing a PIN: {shared}")
    rep.add()

    shifts = []
    bad_rows = []
    for r in srows:
        sid = int(col(sh, r, "ShiftID"))
        emp = col(sh, r, "EmployeeID")
        tin, tout = col(sh, r, "TimeIn"), col(sh, r, "TimeOut")
        if emp is None or tin is None:
            bad_rows.append(sid)
            continue
        shifts.append((sid, int(emp), parse_iso(tin), parse_iso(tout) if tout else None))

    rep.add(f"### Shift ({len(srows)} rows)")
    if shifts:
        rep.add(f"- TimeIn range: {fmt_iso(min(s[2] for s in shifts))} to {fmt_iso(max(s[2] for s in shifts))}")
    if bad_rows:
        rep.add(f"- NULL EmployeeID or TimeIn: ShiftID {bad_rows}")
    orphans = collections.Counter(s[1] for s in shifts if s[1] not in employees)
    if orphans:
        rep.add(f"- Orphan shifts (EmployeeID not in Employee): {sum(orphans.values())} rows, "
                f"EmployeeID {dict(sorted(orphans.items()))}")
    first_shift = {}
    for s in shifts:
        if s[1] not in first_shift or s[0] < first_shift[s[1]]:
            first_shift[s[1]] = s[0]
    dummy = [s[0] for s in shifts if is_near_zero(s)]
    exact = sum(1 for s in shifts if s[3] is not None and s[3] == s[2])
    initial = sum(1 for sid in dummy if sid in first_shift.values())
    rep.add(f"- Zero-length or under-1 s shifts: {len(dummy)} ({exact} exactly zero; {initial} are the "
            "employee's first shift, i.e. the new-employee dummy row; the rest look like double punches)")
    negative = [s[0] for s in shifts if s[3] is not None and s[3] < s[2]]
    if negative:
        rep.add(f"- TimeOut before TimeIn: ShiftID {negative}")
    long = [(s[0], round(hours(s[3] - s[2]), 1)) for s in shifts if s[3] is not None and s[3] - s[2] > LONG_SHIFT]
    if long:
        rep.add(f"- Shifts over {hours(LONG_SHIFT):.0f} h (likely forgotten punch-out): {len(long)}, "
                f"e.g. {long[:10]}")

    by_emp = collections.defaultdict(list)
    for s in shifts:
        by_emp[s[1]].append(s)
    open_rows, multi_open, stale_open, inversions, overlaps = [], [], [], [], []
    for emp, ss in by_emp.items():
        ss.sort(key=lambda s: s[0])
        opens = [s[0] for s in ss if s[3] is None]
        open_rows += opens
        if len(opens) > 1:
            multi_open.append((emp, opens))
        # The legacy app only ever closes the highest ShiftID, so every other open row is stuck.
        stuck = [o for o in opens if o != ss[-1][0]]
        if stuck:
            stale_open.append((emp, stuck))
        for prev, cur in zip(ss, ss[1:]):
            if cur[2] < prev[2]:
                inversions.append((prev[0], cur[0]))
        # Compare each shift with the latest-ending earlier shift, so a long shift
        # containing several short ones reports every contained shift. An open shift
        # never ends, so it overlaps everything after it.
        latest = None  # (end, ShiftID)
        for cur in sorted(ss, key=lambda s: s[2]):
            if latest is not None and cur[2] < latest[0]:
                overlaps.append((latest[1], cur[0]))
            end = cur[3] if cur[3] is not None else dt.datetime.max
            if latest is None or end > latest[0]:
                latest = (end, cur[0])
    rep.add(f"- Open shifts (TimeOut NULL): {len(open_rows)}" + (f", ShiftID {open_rows[:20]}" if open_rows else ""))
    if multi_open:
        rep.add(f"- Employees with more than one open shift: {multi_open}")
    if stale_open:
        rep.add(f"- Open shifts the legacy app can never close (not the employee's last ShiftID): {stale_open}")
    if inversions:
        rep.add(f"- ShiftID order disagrees with TimeIn order (manual edits?): {len(inversions)}, e.g. {inversions[:10]}")
    if overlaps:
        rep.add(f"- Overlapping shifts for the same employee: {len(overlaps)}, e.g. {overlaps[:10]}")

    tz = None
    if site_tz:
        from zoneinfo import ZoneInfo, ZoneInfoNotFoundError
        try:
            tz = ZoneInfo(site_tz)
        except ZoneInfoNotFoundError:
            # Windows Python ships no IANA database.
            rep.add(f"- DST check skipped: time zone `{site_tz}` not found. On Windows run "
                    "`python -m pip install tzdata` and retry.")
    if tz:
        gap, fold = [], []
        for s in shifts:
            for t in (s[2], s[3]):
                if t is None:
                    continue
                a, b = t.replace(tzinfo=tz, fold=0), t.replace(tzinfo=tz, fold=1)
                if a.utcoffset() != b.utcoffset():
                    # Nonexistent times round-trip differently through UTC.
                    rt = a.astimezone(dt.timezone.utc).astimezone(tz).replace(tzinfo=None)
                    (gap if rt != t else fold).append(s[0])
        rep.add(f"- DST ({site_tz}): {len(set(fold))} shifts with an ambiguous local time, "
                f"{len(set(gap))} with a time that does not exist" +
                (f"; ShiftID {sorted(set(fold) | set(gap))[:20]}" if fold or gap else ""))
    elif not site_tz:
        rep.add("- DST check skipped; pass --site-tz with the site's IANA zone (manifest records "
                f"`{ex.manifest.get('site_time_zone', {}).get('id', 'unknown')}`).")
    rep.add()

    rep.add("### Hours per employee (closed shifts, under-1 s shifts excluded)")
    rep.add()
    rep.add("| EmployeeID | Name | Shifts | Open | Hours | First TimeIn | Last TimeIn |")
    rep.add("|---:|---|---:|---:|---:|---|---|")
    for emp in sorted(by_emp):
        ss = by_emp[emp]
        r = employees.get(emp)
        nm = f"{col(eh, r, 'FirstName') or ''} {col(eh, r, 'LastName') or ''}".strip() if r else "(orphan)"
        closed = [s for s in ss if s[3] is not None and s[3] > s[2] and not is_near_zero(s)]
        total = sum(hours(s[3] - s[2]) for s in closed)
        rep.add(f"| {emp} | {nm} | {len(ss)} | {sum(1 for s in ss if s[3] is None)} | {total:.2f} | "
                f"{fmt_iso(min(s[2] for s in ss))} | {fmt_iso(max(s[2] for s in ss))} |")
    rep.add()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("export")
    ap.add_argument("--compare", help="second export of the same file (e.g. the Jackcess one)")
    ap.add_argument("--site-tz", help="IANA time zone of the site, e.g. America/Toronto")
    ap.add_argument("--out", help="write the report here instead of stdout")
    args = ap.parse_args()

    rep = Report()
    ex = Export(args.export)
    m = ex.manifest
    check_integrity(ex, rep)
    other = None
    if args.compare:
        other = Export(args.compare)
        check_integrity(other, rep)

    out = ["# Legacy export validation", ""]
    src = m.get("source", {})
    out.append(f"- Export: `{args.export}` ({m.get('tool')}, {m.get('exported_at_utc')})")
    out.append(f"- Source: `{src.get('path')}`, {src.get('size_bytes')} bytes, sha256 `{src.get('sha256')}`")
    for t in m["tables"]:
        out.append(f"- {t['name']}: {t['row_count']} rows, IDs {t.get('min_id')}..{t.get('max_id')}")
    if m.get("warnings"):
        out += [f"- Exporter warning: {w}" for w in m["warnings"]]
    out.append("")

    body = rep.lines
    rep.lines = []
    if other:
        compare(ex, other, rep)
    quality(ex, args.site_tz, rep)

    verdict = ["## Integrity", ""]
    if rep.failures:
        verdict += [f"- FAIL: {f}" for f in rep.failures]
    else:
        verdict.append("- PASS: hashes, row counts, ID ranges, control totals and timestamps all reconcile.")
    verdict += body + [""]
    text = "\n".join(out + verdict + rep.lines) + "\n"
    if args.out:
        pathlib.Path(args.out).write_text(text, encoding="utf-8")
    else:
        sys.stdout.write(text)
    sys.exit(1 if rep.failures else 0)


if __name__ == "__main__":
    main()

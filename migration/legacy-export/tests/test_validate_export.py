"""Tests for validate_export.py on a synthetic export. Run: python3 -m unittest discover tests"""

import datetime as dt
import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
import zipfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import validate_export as v  # noqa: E402

EMPLOYEE = [(1, "Ann", "Lee", "1234", "1"), (2, "Bo", "Ng", "12", "1"), (3, "Cy", 'O"Neil, Jr', "1234", "0")]
SHIFT = [  # ShiftID, EmployeeID, TimeIn, TimeOut
    (10, 1, "2021-11-06T08:00:00.000", "2021-11-06T16:30:00.250"),
    (11, 1, "2021-11-07T01:30:00.000", "2021-11-07T09:00:00.000"),  # ambiguous in America/Toronto
    (12, 2, "2021-03-14T02:30:00.000", "2021-03-14T02:30:00.000"),  # nonexistent, zero-length
    (13, 9, "2021-03-15T08:00:00.000", None),                        # orphan, open
    (14, 2, "2021-03-16T08:00:00.000", "2021-03-17T08:00:00.000"),  # 24 h
]
EXTRA_SHIFTS = [  # added by tests that need them; manifest totals are recomputed
    (20, 3, "2022-01-03T09:00:00.000", "2022-01-03T09:00:00.004"),  # dummy split across two DateTime.Now calls
    (21, 1, "2022-02-01T08:00:00.000", "2022-02-01T17:00:00.000"),
    (22, 1, "2022-02-01T09:00:00.000", "2022-02-01T10:00:00.000"),  # inside 21
    (23, 1, "2022-02-01T11:00:00.000", "2022-02-01T12:00:00.000"),  # inside 21, not adjacent to it
    (24, 3, "2022-03-01T08:00:00.000", None),                        # stuck: 25 is also open and higher
    (25, 3, "2022-03-02T08:00:00.000", None),
]


def oadate(iso):
    t = v.parse_iso(iso)
    return repr((t - v.OA_EPOCH) // dt.timedelta(milliseconds=1) / v.MS_PER_DAY)


def field(x):
    if x is None:
        return ""
    s = str(x)
    return '"' + s.replace('"', '""') + '"' if any(c in s for c in ',"\r\n') or s == "" else s


def write_export(root, shifts=SHIFT):
    root.mkdir(parents=True, exist_ok=True)
    emp = ["EmployeeID,FirstName,LastName,PinCode,IsActive"] + [",".join(field(x) for x in r) for r in EMPLOYEE]
    sh = ["ShiftID,EmployeeID,TimeIn,TimeIn_OADate,TimeOut,TimeOut_OADate"] + [
        ",".join(field(x) for x in (s, e, i, oadate(i), o, oadate(o) if o else None)) for s, e, i, o in shifts]
    (root / "Employee.csv").write_bytes(("\r\n".join(emp) + "\r\n").encode())
    (root / "Shift.csv").write_bytes(("\r\n".join(sh) + "\r\n").encode())
    (root / "source").mkdir(exist_ok=True)
    (root / "source" / "PunchClock.accdb").write_bytes(b"fake accdb")
    sha = lambda p: hashlib.sha256((root / p).read_bytes()).hexdigest()  # noqa: E731
    manifest = {
        "tool": "test", "exported_at_utc": "2026-10-02T00:00:00.000Z",
        "source": {"path": "C:\\PunchClock.accdb", "size_bytes": 10, "sha256": sha("source/PunchClock.accdb"),
                   "snapshot": "source/PunchClock.accdb"},
        "site_time_zone": {"id": "Eastern Standard Time"},
        "tables": [
            {"name": "Employee", "file": "Employee.csv", "row_count": 3, "primary_key": "EmployeeID",
             "min_id": 1, "max_id": 3, "columns": emp[0].split(","), "sha256": sha("Employee.csv"),
             "control_totals": {"EmployeeID": {"non_null": 3, "sum": "6"}, "FirstName": {"non_null": 3},
                                "LastName": {"non_null": 3}, "PinCode": {"non_null": 3, "sum": "2480"},
                                "IsActive": {"non_null": 3, "sum": "2"}}},
            {"name": "Shift", "file": "Shift.csv", "row_count": len(shifts), "primary_key": "ShiftID",
             "min_id": min(x[0] for x in shifts), "max_id": max(x[0] for x in shifts),
             "columns": sh[0].split(","), "sha256": sha("Shift.csv"),
             "control_totals": {
                 "ShiftID": {"non_null": len(shifts), "sum": str(sum(x[0] for x in shifts))},
                 "EmployeeID": {"non_null": len(shifts), "sum": str(sum(x[1] for x in shifts))},
                 "TimeIn": {"non_null": len(shifts), "min": min(x[2] for x in shifts), "max": max(x[2] for x in shifts)},
                 "TimeOut": {"non_null": sum(1 for x in shifts if x[3]), "min": min(x[3] for x in shifts if x[3]),
                             "max": max(x[3] for x in shifts if x[3])}}},
        ],
        "warnings": [], "failures": [],
    }
    (root / "manifest.json").write_text(json.dumps(manifest, indent=2))
    files = sorted(p for p in root.rglob("*") if p.is_file())
    (root / "SHA256SUMS.txt").write_text(
        "".join(f"{sha(p.relative_to(root))}  {p.relative_to(root).as_posix()}\n" for p in files))
    return root


def run(*args):
    p = subprocess.run([sys.executable, str(HERE.parent / "validate_export.py"), *map(str, args)],
                       capture_output=True, text=True)
    return p.returncode, p.stdout


class ValidateExportTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = write_export(pathlib.Path(self.tmp.name) / "export")

    def tearDown(self):
        self.tmp.cleanup()

    def test_clean_export_passes_and_reports_findings(self):
        rc, out = run(self.root, "--site-tz", "America/Toronto")
        self.assertEqual(rc, 0, out)
        self.assertIn("PASS", out)
        self.assertIn("Orphan shifts (EmployeeID not in Employee): 1 rows", out)
        self.assertIn("New-employee dummy shifts (employee's lowest ShiftID, under 2 s): 1", out)
        self.assertIn("Other shifts under 1 s (double punches, or the dummy row of a deleted employee): 0", out)
        self.assertIn("PIN under 3 digits", out)
        self.assertIn("Employees sharing a PIN: [[1, 3]]", out)
        self.assertIn("Shifts over 16 h", out)
        self.assertIn("1 shifts with an ambiguous local time, 1 with a time that does not exist", out)

    def test_zip_input(self):
        z = self.root.with_suffix(".zip")
        with zipfile.ZipFile(z, "w") as zf:
            for p in self.root.rglob("*"):
                zf.write(p, p.relative_to(self.root))
        self.assertEqual(run(z)[0], 0)

    def test_tampered_csv_fails(self):
        p = self.root / "Shift.csv"
        p.write_bytes(p.read_bytes().replace(b"16:30:00.250", b"17:30:00.250"))
        rc, out = run(self.root)
        self.assertEqual(rc, 1)
        self.assertIn("SHA256SUMS mismatch for Shift.csv", out)
        self.assertIn("Shift: CSV hash differs from manifest", out)
        self.assertIn("Shift.TimeOut: 1 values disagree with TimeOut_OADate", out)

    def test_control_total_mismatch_fails(self):
        m = json.loads((self.root / "manifest.json").read_text())
        m["tables"][1]["control_totals"]["TimeOut"]["non_null"] = 5
        m["tables"][1]["row_count"] = 6
        (self.root / "manifest.json").write_text(json.dumps(m))
        rc, out = run(self.root)
        self.assertEqual(rc, 1)
        self.assertIn("Shift.TimeOut: 4 non-NULL values, Access counted 5", out)
        self.assertIn("5 rows in CSV, manifest says 6", out)

    def test_compare_identical_exports(self):
        other = write_export(pathlib.Path(self.tmp.name) / "other")
        rc, out = run(self.root, "--compare", other)
        self.assertEqual(rc, 0, out)
        self.assertIn("Shift: byte-identical (5 rows)", out)

    def test_lock_file_overlap_and_dummy_findings(self):
        root = write_export(pathlib.Path(self.tmp.name) / "extra", SHIFT + EXTRA_SHIFTS)
        rc, out = run(root)
        self.assertEqual(rc, 0, out)
        self.assertIn("New-employee dummy shifts (employee's lowest ShiftID, under 2 s): 2", out)
        self.assertIn("Overlapping shifts for the same employee: 3", out)
        self.assertIn("(21, 22), (21, 23)", out)
        self.assertIn("(24, 25)", out)  # an open shift overlaps everything after it
        self.assertIn("never close (not the employee's last ShiftID): [(3, [24])]", out)

    def test_missing_snapshot_fails(self):
        (self.root / "source" / "PunchClock.accdb").unlink()
        sums = self.root / "SHA256SUMS.txt"
        sums.write_text("".join(l + "\n" for l in sums.read_text().splitlines() if "source/" not in l))
        rc, out = run(self.root)
        self.assertEqual(rc, 1)
        self.assertIn("snapshot source/PunchClock.accdb named in the manifest is missing", out)

    def test_undeclared_snapshot_fails(self):
        m = json.loads((self.root / "manifest.json").read_text())
        del m["source"]["snapshot"]
        (self.root / "manifest.json").write_text(json.dumps(m))
        rc, out = run(self.root)
        self.assertEqual(rc, 1)
        self.assertIn("manifest declares no source snapshot", out)

    def test_text_primary_key_table(self):
        (self.root / "Lookup.csv").write_bytes(b"Code,Label\r\nB,Bee\r\nA,Ay\r\n")
        m = json.loads((self.root / "manifest.json").read_text())
        m["tables"].append({"name": "Lookup", "file": "Lookup.csv", "row_count": 2, "primary_key": "Code",
                            "min_id": None, "max_id": None, "columns": ["Code", "Label"],
                            "sha256": hashlib.sha256((self.root / "Lookup.csv").read_bytes()).hexdigest()})
        (self.root / "manifest.json").write_text(json.dumps(m))
        (self.root / "SHA256SUMS.txt").unlink()
        rc, out = run(self.root)
        self.assertEqual(rc, 0, out)

    def test_digit_text_key_uses_declared_type(self):
        (self.root / "Codes.csv").write_bytes(b"Code,Label\r\n2,Two\r\n1,One\r\n")
        m = json.loads((self.root / "manifest.json").read_text())
        m["tables"].append({"name": "Codes", "file": "Codes.csv", "row_count": 2, "primary_key": "Code",
                            "min_id": None, "max_id": None, "columns": ["Code", "Label"],
                            "sha256": hashlib.sha256((self.root / "Codes.csv").read_bytes()).hexdigest(),
                            "column_types": [{"name": "Code", "category": "text"}, {"name": "Label", "category": "text"}]})
        (self.root / "manifest.json").write_text(json.dumps(m))
        (self.root / "SHA256SUMS.txt").unlink()
        rc, out = run(self.root)
        self.assertEqual(rc, 0, out)

    def test_missing_csv_hash_fails(self):
        m = json.loads((self.root / "manifest.json").read_text())
        del m["tables"][0]["sha256"]
        (self.root / "manifest.json").write_text(json.dumps(m))
        rc, out = run(self.root)
        self.assertEqual(rc, 1)
        self.assertIn("Employee: manifest declares no CSV hash", out)

    def test_malformed_oadate_reports_instead_of_crashing(self):
        p = self.root / "Shift.csv"
        lines = p.read_bytes().split(b"\r\n")
        f = lines[1].split(b",")
        f[3] = b"garbage"
        lines[1] = b",".join(f)
        p.write_bytes(b"\r\n".join(lines))
        rc, out = run(self.root)
        self.assertEqual(rc, 1)
        self.assertIn("Shift.TimeIn: 1 values disagree with TimeIn_OADate", out)
        self.assertNotIn("Traceback", out)

    def test_float_sum_tolerates_rounding(self):
        (self.root / "Rates.csv").write_bytes(b"ID,Rate\r\n1,0.1\r\n2,0.2\r\n")
        m = json.loads((self.root / "manifest.json").read_text())
        m["tables"].append({"name": "Rates", "file": "Rates.csv", "row_count": 2, "primary_key": "ID",
                            "min_id": 1, "max_id": 2, "columns": ["ID", "Rate"],
                            "sha256": hashlib.sha256((self.root / "Rates.csv").read_bytes()).hexdigest(),
                            "column_types": [{"name": "ID", "category": "integer"}, {"name": "Rate", "category": "float"}],
                            "control_totals": {"ID": {"non_null": 2, "sum": "3"},
                                               "Rate": {"non_null": 2, "sum": "0.30000000000000004"}}})
        (self.root / "manifest.json").write_text(json.dumps(m))
        (self.root / "SHA256SUMS.txt").unlink()
        rc, out = run(self.root)
        self.assertEqual(rc, 0, out)
        m["tables"][-1]["control_totals"]["Rate"]["sum"] = "0.4"
        (self.root / "manifest.json").write_text(json.dumps(m))
        rc, out = run(self.root)
        self.assertEqual(rc, 1)
        self.assertIn("Rates.Rate: sum", out)

    def test_unknown_time_zone_is_reported_not_raised(self):
        rc, out = run(self.root, "--site-tz", "Nowhere/Atlantis")
        self.assertEqual(rc, 0, out)
        self.assertIn("DST check skipped: time zone `Nowhere/Atlantis` not found", out)

    def test_compare_requires_core_tables(self):
        other = write_export(pathlib.Path(self.tmp.name) / "other")
        m = json.loads((other / "manifest.json").read_text())
        m["tables"] = [t for t in m["tables"] if t["name"] != "Shift"]
        (other / "manifest.json").write_text(json.dumps(m))
        rc, out = run(self.root, "--compare", other)
        self.assertEqual(rc, 1)
        self.assertIn("Shift table missing from export", out)

    def test_broken_structure_reports_instead_of_crashing(self):
        p = self.root / "Employee.csv"
        p.write_bytes(p.read_bytes().replace(b",IsActive\r\n", b"\r\n", 1))
        rc, out = run(self.root)
        self.assertEqual(rc, 1)
        self.assertIn("Employee: header", out)
        self.assertNotIn("Traceback", out)

    def test_dummy_rule_matches_importer(self):
        shifts = SHIFT + [
            (30, 3, "2022-04-01T08:00:00.900", "2022-04-01T08:00:02.100"),  # first for 3, 1.2 s: dummy
            (31, 3, "2022-04-02T08:00:00.000", "2022-04-02T08:00:01.500"),  # not first, 1.5 s: a real row
            (32, 3, "2022-04-03T08:00:00.000", "2022-04-03T08:00:00.300"),  # not first, under 1 s: double punch
            (33, 7, "2022-04-04T08:00:00.000", "2022-04-04T08:00:00.500"),  # orphan's first: never a dummy
            (34, 1, "2022-04-05T08:00:00.000", "2022-04-05T08:00:01.000"),  # exactly 1 s, not first: real
        ]
        root = write_export(pathlib.Path(self.tmp.name) / "dummy", shifts)
        rc, out = run(root)
        self.assertEqual(rc, 0, out)
        self.assertIn("New-employee dummy shifts (employee's lowest ShiftID, under 2 s): 2", out)  # 12 and 30
        self.assertIn("Other shifts under 1 s (double punches, or the dummy row of a deleted employee): 2, ShiftID [32, 33]", out)

    def test_report_hours_follow_access_datediff(self):
        t = v.parse_iso
        self.assertEqual(v.access_minutes(t("2022-01-03T08:00:30.000"), t("2022-01-03T08:10:20.000")), 10)
        self.assertEqual(v.access_minutes(t("2022-01-03T08:00:59.600"), t("2022-01-03T08:09:00.000")), 8)
        self.assertEqual(v.access_minutes(t("2022-01-03T08:00:59.400"), t("2022-01-03T08:01:00.000")), 1)
        self.assertEqual(v.access_minutes(t("2022-01-03T09:00:00.000"), t("2022-01-03T08:58:59.000")), -2)
        rc, out = run(self.root)
        self.assertEqual(rc, 0, out)
        # ShiftID 10 is 8 h 30 min 0.25 s: 510 report minutes, 8.50 exact hours (11 adds 450 minutes)
        self.assertIn("| 1 | Ann Lee | 2 | 0 | 960 | 16.00 | 16.00 |", out)

    def test_csv_parser_null_vs_empty(self):
        self.assertEqual(v.parse_csv('a,,"",""""\r\n"x\r\ny",1\r\n'), [["a", None, "", '"'], ["x\r\ny", "1"]])
        with self.assertRaises(ValueError):
            v.parse_csv("a,b\n")

    def test_oadate_matches_dotnet(self):
        self.assertEqual(v.fmt_iso(v.oadate_to_datetime(44564.136225138885)), "2022-01-03T03:16:09.852")
        self.assertEqual(v.oadate_to_datetime(-1.25), dt.datetime(1899, 12, 29, 6, 0))


if __name__ == "__main__":
    unittest.main()

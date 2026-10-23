"""Invoke the actual packaged COM server with its real, encrypted user profile."""
import ctypes, hashlib, json, os, pathlib, subprocess, sys, time, uuid, zipfile

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(__file__).resolve().parents[1]
WORK = pathlib.Path((ROOT / "verification/efs-fix-latest.txt").read_text(encoding="utf-8-sig").strip())
AREA = WORK / ("packaged-" + uuid.uuid4().hex[:8])
AREA.mkdir()
PROBE = WORK / "HzipShellProbeEfs.exe"
APP = ROOT / "release/Hzip"
PROFILE = pathlib.Path(os.environ["LOCALAPPDATA"]) / "Packages/Hzip.ContextMenu_fss7vx0rtmayp/LocalCache/Local/Hzip"
JOBS = PROFILE / "ShellJobs"
assert PROFILE.exists() and (PROFILE / "Temp").stat().st_file_attributes & 0x4000
env = os.environ.copy()
env.pop("HZIP_DATA_DIR", None)
env.pop("HZIP_ENGINE", None)
original = pathlib.Path(sys.argv[sys.argv.index("--archive") + 1]) if "--archive" in sys.argv else APP / "examples/Hzip示例.zip"
source_hash = hashlib.file_digest(original.open("rb"), "sha256").hexdigest()
text = AREA / "notes.txt"; text.write_text("package activation fixture")
folder = AREA / "普通文件夹"; folder.mkdir()
(folder / "说明.txt").write_text("archive content for packaged activation", encoding="utf-8")
results = []

def invoke(flag, archive, expected_failure=False):
    before = set(JOBS.glob("*.json"))
    p = subprocess.run([str(PROBE), str(APP / "shell/Hzip.Shell.v05r2.dll"), str(archive), str(text), str(folder), flag], env=env, capture_output=True, timeout=20)
    assert p.returncode == 0, (p.stdout, p.stderr)
    proof = json.loads(p.stdout)
    assert proof["registered_activation"] and proof["invoked"]
    end = time.monotonic() + 30
    report = None
    while time.monotonic() < end:
        for path in set(JOBS.glob("*.json")) - before:
            try: current = json.loads(path.read_text(encoding="utf-8-sig"))
            except (OSError, json.JSONDecodeError): continue
            if current["Status"] == "running": continue
            def contains(value):
                if isinstance(value, str): return str(AREA) in value
                if isinstance(value, dict): return any(contains(v) for v in value.values())
                if isinstance(value, list): return any(contains(v) for v in value)
                return False
            matched = contains(current.get("Error")) or contains(current["Completed"])
            if matched: report = current; break
        if report: break
        time.sleep(.05)
    assert report, "Packaged command did not produce a terminal receipt"
    if expected_failure:
        assert report["Status"] == "failed" and "加密" in report["Error"], report
        dismiss = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "tools/ui.ps1"), "-TargetProcessId", str(report["ProcessId"]), "-Action", "invoke", "-Id", "DismissShellError", "-WindowTitle", "右键任务未完成"], capture_output=True, timeout=20)
        assert dismiss.returncode == 0, (dismiss.stdout, dismiss.stderr)
    else:
        assert report["Status"] == "completed", report
        inspect = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "tools/ui.ps1"), "-TargetProcessId", str(report["ProcessId"]), "-Action", "list", "-WindowTitle", "Hzip"], capture_output=True, timeout=20)
        assert inspect.returncode == 0, (inspect.stdout, inspect.stderr)
        controls = json.loads(inspect.stdout.decode("utf-8-sig"))
        assert any(x["Id"] == "ShellTotalPercent" and "100%" in x["Name"] for x in controls)
        assert not any(x["Id"] in {"EntriesGrid", "CreateArchiveButton", "RecentArchives"} for x in controls)
        close = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "tools/ui.ps1"), "-TargetProcessId", str(report["ProcessId"]), "-Action", "invoke", "-Id", "ShellCancelCloseButton", "-WindowTitle", "Hzip"], capture_output=True, timeout=20)
        assert close.returncode == 0, (close.stdout, close.stderr)
    return report

if "--expect-failure" in sys.argv:
    case = AREA / "before"; case.mkdir()
    archive = case / original.name; archive.write_bytes(original.read_bytes())
    report = invoke("--registered-invoke-direct", archive, expected_failure=True)
    results.append({"BeforeFix": "Actual packaged activation reproduces screenshot encryption error", "Receipt": report})
else:
    for flag, name in [("--registered-invoke-direct", "direct"), ("--registered-invoke-extract", "smart"), ("--registered-invoke-subfolder", "subfolder")]:
        case = AREA / name; case.mkdir()
        archive = case / original.name; archive.write_bytes(original.read_bytes())
        report = invoke(flag, archive)
        destination = pathlib.Path(report["Completed"][0]["Destination"])
        with zipfile.ZipFile(original) as z:
            for item in z.infolist():
                if item.is_dir(): continue
                output = destination / item.filename
                assert output.read_bytes() == z.read(item)
                assert not (output.stat().st_file_attributes & 0x4000), "Output inherited EFS"
        results.append({"Action": flag, "Passed": True, "Receipt": report, "OutputUnencrypted": True})
    report = invoke("--registered-invoke-zip", archive)
    output = pathlib.Path(report["Completed"][0]["Outputs"][0])
    with zipfile.ZipFile(output) as z: assert z.read(folder.name + "/说明.txt") == (folder / "说明.txt").read_bytes()
    assert not (output.stat().st_file_attributes & 0x4000)
    results.append({"Action": "packaged quick ZIP", "Passed": True, "OutputUnencrypted": True, "Receipt": report})
assert hashlib.file_digest(original.open("rb"), "sha256").hexdigest() == source_hash
data = {"Failures": 0, "SourceArchiveSHA256": source_hash, "OriginalArchiveUnchanged": True, "RealPackagedProfile": str(PROFILE), "ProfileTempEncrypted": True, "Results": results}
(AREA / "results.json").write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
(ROOT / "verification/packaged-efs-latest.txt").write_text(str(AREA), encoding="utf-8")
print(json.dumps({"passed": len(results), "area": str(AREA)}, ensure_ascii=False))

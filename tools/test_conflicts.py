"""Exercise actual WPF conflict dialogs and verify files after every choice."""
import datetime, json, os, pathlib, subprocess, sys, time, uuid, zipfile
from types import SimpleNamespace
sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "release/Hzip"
WORK = pathlib.Path((ROOT / "verification/conflict-latest.txt").read_text(encoding="utf-8-sig").strip())
AREA = WORK / ("ui-" + uuid.uuid4().hex[:8]); AREA.mkdir()
(ROOT / "verification/conflict-ui-latest.txt").write_text(str(AREA), encoding="utf-8")
ENV = os.environ.copy(); ENV["HZIP_DATA_DIR"] = str(AREA / "profile"); ENV["HZIP_ENGINE"] = str(ROOT / "release/Hzip/engine/7z.exe")
results = []
def run(args, env=ENV):
    p = subprocess.run(list(map(str, args)), env=env, capture_output=True, timeout=30)
    assert p.returncode == 0, (args, p.stdout, p.stderr)
    return p.stdout.decode("utf-8-sig").strip()
def ui(p, action, **options):
    args = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ROOT / "tools/ui.ps1", "-TargetProcessId", p.pid, "-Action", action, "-WindowTitle", options.pop("WindowTitle", "Hzip")]
    for k, v in options.items(): args.extend(["-" + k, v])
    return run(args)
def wait(predicate, timeout=25):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        value = predicate()
        if value: return value
        time.sleep(.05)
    raise TimeoutError("Conflict workflow timed out")
def controls(p): return json.loads(ui(p, "list"))
def receipt(p, jobs=None):
    for path in (jobs or AREA / "profile/ShellJobs").glob("*.json"):
        try: value = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError): continue
        if value["ProcessId"] == p.pid: return value
def terminal(p, jobs=None):
    value = receipt(p, jobs)
    return value if value and value["Status"] != "running" else None
def fixture(name, members=("a.txt", "b.txt", "c.txt")):
    case = AREA / name; case.mkdir(); archive = case / "sample.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        for member in members:
            entry = zipfile.ZipInfo(member, (2026, 10, 18, 2, 6, 4)); z.writestr(entry, "new:" + member if not member.endswith("/") else "")
    for member in members:
        if "/" not in member:
            path = case / member; path.write_text("old:" + member); os.utime(path, (1735787046, 1735787046))
    return case, archive
def start(args): return subprocess.Popen([str(APP / "Hzip.exe"), *map(str, args)], env=ENV)
def conflict(p):
    def inspect():
        values = controls(p)
        return values if any(x["Id"] == "ConflictDestinationPath" for x in values) else None
    return wait(inspect)
def choose(p, action, screenshot=None):
    values = conflict(p)
    assert not any(x["Id"] == "ArchiveEntries" for x in values)
    for id in ["ConflictIncomingMetadata", "ConflictExistingMetadata"]:
        assert any(x["Id"] == id and "修改时间" in x["Name"] for x in values)
    target = pathlib.Path(next(x["Name"] for x in values if x["Id"] == "ConflictDestinationPath"))
    if screenshot: ui(p, "screenshot", OutputPath=AREA / screenshot)
    ui(p, "invoke", Id="Conflict" + action)
    return target
def finish(p, status="completed", jobs=None):
    report = wait(lambda: terminal(p, jobs))
    assert report["Status"] == status, report
    values = controls(p)
    assert any(x["Id"] == "ShellCancelCloseButton" and x["Name"] == "关闭" for x in values)
    if status == "completed": assert any(x["Id"] == "ShellTotalProgress" and x["RangeValue"] == 100 for x in values)
    else: assert not any(x["Id"] == "ShellTotalProgress" and x["RangeValue"] == 100 for x in values)
    ui(p, "invoke", Id="ShellCancelCloseButton")
    if isinstance(p, subprocess.Popen): assert p.wait(timeout=10) == (0 if status == "completed" else 1)
    return report
def check(name, action):
    if "--case" in sys.argv and sys.argv[sys.argv.index("--case") + 1].lower() not in name.lower(): return
    action(); results.append({"Name": name, "Passed": True}); print("PASS", name, flush=True)
    (AREA / "results.json").write_text(json.dumps({"Failures": 0, "Results": results}, ensure_ascii=False, indent=2), encoding="utf-8")

for action in ["Replace", "Skip", "Rename"]:
    def single(action=action):
        case, archive = fixture("single-" + action, ("a.txt", "b.txt")); p = start(["--extract-direct", archive])
        target = choose(p, action, "file-comparison.png" if action == "Replace" else None)
        assert target.name in {"a.txt", "b.txt"}
        other = choose(p, "Skip"); assert target != other
        finish(p)
        assert target.read_text() == ("new:" if action == "Replace" else "old:") + target.name
        assert other.read_text() == "old:" + other.name
        if action == "Rename": assert target.with_name(target.stem + " (2)" + target.suffix).read_text() == "new:" + target.name
    check("Single " + action + " affects only the chosen file", single)
for action in ["ReplaceAll", "SkipAll", "RenameAll"]:
    def all_files(action=action):
        case, archive = fixture("all-" + action); p = start(["--extract-direct", archive]); choose(p, action); report = finish(p)
        assert report["Completed"][0]["Files"] == (0 if action == "SkipAll" else 3)
        for name in ["a.txt", "b.txt", "c.txt"]:
            assert (case / name).read_text() == ("new:" if action == "ReplaceAll" else "old:") + name
            if action == "RenameAll": assert (case / (name[0] + " (2).txt")).read_text() == "new:" + name
    check(action + " processes subsequent conflicts without another dialog", all_files)
def cancel():
    case, archive = fixture("cancel"); p = start(["--extract-direct", archive]); choose(p, "Replace"); choose(p, "Cancel"); finish(p, "cancelled")
    assert all((case / name).read_text() == "old:" + name for name in ["a.txt", "b.txt", "c.txt"])
check("Cancel rolls back earlier replacements", cancel)
def close_dialog():
    case, archive = fixture("close", ("a.txt",)); p = start(["--extract-direct", archive]); conflict(p); ui(p, "close"); finish(p, "cancelled")
    assert (case / "a.txt").read_text() == "old:a.txt"
check("Closing conflict dialog cancels without replacing anything", close_dialog)
def file_folder():
    case, archive = fixture("file-folder", ("folder.txt",)); (case / "folder.txt").unlink(); (case / "folder.txt").mkdir(); (case / "folder.txt/keep").write_text("keep")
    p = start(["--extract-direct", archive]); values = conflict(p)
    assert all(any(x["Id"] == id and not x["Enabled"] for x in values) for id in ["ConflictReplace", "ConflictReplaceAll"])
    choose(p, "Rename", "folder-comparison.png"); finish(p)
    assert (case / "folder.txt/keep").read_text() == "keep" and (case / "folder (2).txt").read_text() == "new:folder.txt"
check("Folder collision disables replacement and preserves children", file_folder)
def equal_metadata():
    case, archive = fixture("equal-metadata", ("a.txt",)); timestamp = datetime.datetime(2026, 10, 18, 2, 6, 4).timestamp(); os.utime(case / "a.txt", (timestamp, timestamp))
    p = start(["--extract-direct", archive]); values = conflict(p)
    assert any("大小和修改时间相同，不代表文件内容相同" in x["Name"] for x in values)
    choose(p, "Skip", "equal-metadata.png"); finish(p); assert (case / "a.txt").read_text() == "old:a.txt"
check("Equal size and date never silently assume identical content", equal_metadata)
def batch():
    case, archive = fixture("batch", ("a.txt",)); second = case / "second.zip"; second.write_bytes(archive.read_bytes())
    p = start(["--extract-direct", archive, second]); choose(p, "SkipAll"); report = finish(p)
    assert len(report["Completed"]) == 2 and all(x["Files"] == 0 for x in report["Completed"])
    p = start(["--extract-direct", archive]); choose(p, "Skip"); finish(p)
    assert (case / "a.txt").read_text() == "old:a.txt"
check("Apply-all spans an archive batch and resets for a new operation", batch)
def main_window():
    case, archive = fixture("main", ("a.txt",)); p = start([archive])
    wait(lambda: any(x["Id"] == "ExtractButton" and x["Enabled"] for x in controls(p)))
    ui(p, "invoke", Id="ExtractButton")
    values = controls(p); assert any(x["Id"] == "ExtractOverwritePolicy" for x in values)
    ui(p, "toggle", Name="完成后打开目标文件夹")
    ui(p, "invoke", Id="StartExtractButton"); choose(p, "Replace", "main-conflict.png")
    wait(lambda: (case / "a.txt").read_text() == "new:a.txt")
    wait(lambda: any(x["Id"] == "ArchiveEntries" for x in controls(p)))
    ui(p, "close"); assert p.wait(timeout=10) == 0
check("Main interface extraction defaults to asking and uses the same dialog", main_window)

if APP.resolve() == (ROOT / "release/Hzip").resolve():
    def registered():
        case, archive = fixture("registered", ("a.txt",)); text = case / "notes.txt"; text.write_text("ordinary"); folder = case / "ordinary-folder"; folder.mkdir()
        efs = pathlib.Path((ROOT / "verification/efs-fix-latest.txt").read_text(encoding="utf-8-sig").strip())
        profile = pathlib.Path(os.environ["LOCALAPPDATA"]) / "Packages/Hzip.ContextMenu_fss7vx0rtmayp/LocalCache/Local/Hzip"; jobs = profile / "ShellJobs"; before = set(jobs.glob("*.json"))
        env = os.environ.copy(); env.pop("HZIP_DATA_DIR", None); env.pop("HZIP_ENGINE", None)
        probe = json.loads(run([efs / "HzipShellProbeEfs.exe", ROOT / "release/Hzip/shell/Hzip.Shell.v05r2.dll", archive, text, folder, "--registered-invoke-direct"], env))
        assert probe["registered_activation"] and probe["invoked"]
        def pid():
            for path in set(jobs.glob("*.json")) - before:
                try: return json.loads(path.read_text(encoding="utf-8-sig"))["ProcessId"]
                except (OSError, json.JSONDecodeError): pass
        p = SimpleNamespace(pid=wait(pid)); choose(p, "Rename", "registered-conflict.png"); report = finish(p, jobs=jobs)
        assert (case / "a (2).txt").read_text() == "new:a.txt" and (case / "a.txt").read_text() == "old:a.txt"
        assert not ((case / "a (2).txt").stat().st_file_attributes & 0x4000)
        (AREA / "registered-results.json").write_text(json.dumps({"Passed": True, "Probe": probe, "Receipt": report, "OutputUnencrypted": True}, ensure_ascii=False, indent=2), encoding="utf-8")
    check("Actual installed COM right-click opens only progress and conflict dialogs", registered)
print(json.dumps({"passed": len(results), "area": str(AREA)}, ensure_ascii=False))

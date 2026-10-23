"""Check that the published GUI integrity action exposes external GZIP data."""
import json, os, pathlib, subprocess, sys, time
sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(__file__).resolve().parents[1]
AREA = pathlib.Path((ROOT / "verification/gzip-tail-latest.txt").read_text(encoding="utf-8-sig").strip())
archive = json.loads((AREA / "analysis.json").read_text(encoding="utf-8-sig"))["Source"]
env = os.environ.copy(); env["HZIP_DATA_DIR"] = str(AREA / "gui-profile"); env.pop("HZIP_ENGINE", None)
p = subprocess.Popen([str(ROOT / "release/Hzip/Hzip.exe"), archive], env=env)
def ui(action, **options):
    args = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "tools/ui.ps1"), "-TargetProcessId", str(p.pid), "-Action", action, "-WindowTitle", "Hzip"]
    for key, value in options.items(): args.extend(["-" + key, str(value)])
    done = subprocess.run(args, capture_output=True, timeout=20)
    assert done.returncode == 0, (done.stdout, done.stderr)
    return done.stdout.decode("utf-8-sig")
end = time.monotonic() + 20
while True:
    try:
        values = json.loads(ui("list"))
        if any(x["Id"] == "TestArchiveButton" and x["Enabled"] for x in values): break
    except AssertionError:
        if time.monotonic() >= end: raise
    assert time.monotonic() < end, "Archive GUI did not load"
    time.sleep(.1)
ui("invoke", Id="TestArchiveButton")
end = time.monotonic() + 20
while True:
    values = json.loads(ui("list"))
    message = "\n".join(x["Name"] for x in values)
    if "262" in message and "附加数据" in message: break
    assert time.monotonic() < end, values
    time.sleep(.1)
ui("screenshot", OutputPath=AREA / "gui-test-warning.png")
ui("close")
end = time.monotonic() + 10
while p.poll() is None:
    try:
        if any(x["Id"] == "TestArchiveButton" for x in json.loads(ui("list"))):
            ui("close"); break
    except AssertionError:
        if p.poll() is not None: break
    assert time.monotonic() < end, "Warning dialog did not close"
    time.sleep(.1)
assert p.wait(timeout=10) == 0
(AREA / "gui-test-warning.json").write_text(json.dumps({"Passed": True, "PublishedGUIIntegrityAction": True, "GzipTailWarningShown": True, "Message": message}, ensure_ascii=False, indent=2), encoding="utf-8")
print("PASS published GUI GZIP integrity warning")

"""Real shell progress UI, output contents, pause/resume and cancellation checks."""
import base64, ctypes, hashlib, json, os, pathlib, subprocess, sys, time, uuid, zipfile

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "release/Hzip"
WORK = pathlib.Path((ROOT / "verification/shell-progress-latest.txt").read_text(encoding="utf-8-sig").strip())
AREA = WORK / ("ui-" + uuid.uuid4().hex[:8]); AREA.mkdir()
ENV = os.environ.copy(); ENV["HZIP_DATA_DIR"] = str(AREA / "profile"); ENV.pop("HZIP_ENGINE", None)
(AREA / "profile").mkdir(); (AREA / "profile/settings.json").write_text(json.dumps({"CompressionLevel": 9, "Threads": 1}))
results, processes = [], []

def tool(args):
    p = subprocess.run(list(map(str, args)), env=ENV, capture_output=True, timeout=45)
    assert p.returncode == 0, (args, p.stdout, p.stderr)
    return p.stdout.decode("utf-8-sig").strip()

def ui(p, action, **options):
    args = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ROOT / "tools/ui.ps1", "-TargetProcessId", p.pid, "-Action", action]
    if "WindowTitle" not in options: options["WindowTitle"] = " · Hzip"
    for k, v in options.items(): args += ["-" + k, v]
    return tool(args)

def cli(args): return tool([ROOT / "release/Hzip/hz.exe", *args])

def wait(predicate, timeout=60):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        value = predicate()
        if value: return value
        time.sleep(.05)
    raise TimeoutError("Shell progress operation timed out")

def start(args):
    p = subprocess.Popen([str(APP / "Hzip.exe"), *map(str, args)], env=ENV); processes.append(p)
    return p

def receipt(p):
    for file in (AREA / "profile/ShellJobs").glob("*.json"):
        try: value = json.loads(file.read_text())
        except (OSError, json.JSONDecodeError): continue
        if value["ProcessId"] == p.pid: return value

def terminal(p):
    value = receipt(p)
    return value if value and value["Status"] != "running" else None

def controls(p):
    values = json.loads(ui(p, "list"))
    assert not any(x["Id"] in {"EntriesGrid", "CreateArchiveButton", "OpenArchiveButton", "RecentArchives"} for x in values), values
    return values

def finish(p, status="completed", screenshot=None):
    last_prompt_check = [0]
    def completed():
        report = terminal(p)
        if report: return report
        if status == "completed" and time.monotonic() - last_prompt_check[0] > 1:
            last_prompt_check[0] = time.monotonic()
            try: ui(p, "invoke", WindowTitle="同名文件处理", Id="ConflictRenameAll")
            except AssertionError: pass
        return None
    report = wait(completed, 150)
    assert report["Status"] == status, report
    values = controls(p)
    assert p.poll() is None, "Progress window vanished before the user could review the result"
    assert any(x["Id"] == "ShellCancelCloseButton" and x["Name"] == "关闭" for x in values)
    if status == "completed":
        assert any(x["Id"] == "ShellCurrentProgress" and x["RangeValue"] == 100 for x in values)
        assert any(x["Id"] == "ShellTotalProgress" and x["RangeValue"] == 100 for x in values)
    else:
        assert any(x["Id"] == "ShellProgressState" and ("取消" if status == "cancelled" else "未完成") in x["Name"] for x in values)
    if screenshot: ui(p, "screenshot", OutputPath=AREA / screenshot)
    ui(p, "invoke", Id="ShellCancelCloseButton")
    assert p.wait(timeout=10) == {"completed": 0, "cancelled": 1, "failed": 2}[status]
    return report

def check(name, action):
    action(); results.append({"Name": name, "Passed": True}); print("PASS", name, flush=True)

source = AREA / "普通文件夹"; source.mkdir(); (source / "说明.txt").write_text("shell progress verified 中文", encoding="utf-8")
archive = AREA / "sample.zip"
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z: z.writestr("sample.txt", "sample body"); z.writestr("nested/说明.txt", "中文内容")

def quick(fmt):
    p = start(["--quick-" + fmt, source]); finish(p, screenshot="compression-completed.png" if fmt == "zip" else None)
    out = AREA / ("out-" + fmt); cli(["extract", AREA / (source.name + "." + fmt), out, "--layout", "here"])
    assert (out / source.name / "说明.txt").read_bytes() == (source / "说明.txt").read_bytes()

def extraction(flag, stem, multiple=False):
    path = AREA / (stem + ".zip"); path.write_bytes(archive.read_bytes())
    inputs = [path]
    if multiple:
        second = AREA / (stem + "-2.zip"); second.write_bytes(archive.read_bytes()); inputs.append(second)
    p = start([flag, *inputs]); report = finish(p, screenshot="extraction-completed.png" if flag == "--extract-direct" else None)
    assert len(report["Completed"]) == len(inputs)
    for item in report["Completed"]:
        dest = pathlib.Path(item["Destination"])
        assert (dest / "sample.txt").read_text() == "sample body"
        assert (dest / "nested/说明.txt").read_text(encoding="utf-8") == "中文内容"

def settings(create=True, cancel=False):
    p = start(["--create", source] if create else ["--extract-to", archive])
    time.sleep(.4); values = controls(p)
    assert not any(x["Id"] == "ShellPauseButton" for x in values), "Progress appeared before settings confirmation"
    if cancel:
        ui(p, "invoke", Name="取消"); assert p.wait(timeout=10) == 1; return
    if create: ui(p, "set", Id="CreateOutputPath", Value=AREA / "custom.zip")
    else: ui(p, "set", Id="ExtractDestination", Value=AREA / "custom-extracted")
    ui(p, "invoke", Id="StartCreateButton" if create else "StartExtractButton")
    finish(p)
    if create:
        with zipfile.ZipFile(AREA / "custom.zip") as z: assert z.read(source.name + "/说明.txt") == (source / "说明.txt").read_bytes()
    else: assert (AREA / "custom-extracted/sample/sample.txt").read_text() == "sample body"

def damaged():
    path = AREA / "bad.zip"; path.write_bytes(b"not a zip")
    p = start(["--extract-direct", path]); finish(p, "failed", "failure-result.png"); assert path.read_bytes() == b"not a zip"

def encrypted(cancel=False):
    from ctypes import wintypes as w
    class Blob(ctypes.Structure):
        _fields_ = [("size", w.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]
    secret = "Progress-secret-2026"; data = ctypes.create_string_buffer(secret.encode()); protected = Blob()
    original = Blob(len(secret), ctypes.cast(data, ctypes.POINTER(ctypes.c_ubyte)))
    assert ctypes.windll.crypt32.CryptProtectData(ctypes.byref(original), "Hzip test", None, None, None, 1, ctypes.byref(protected))
    try: ciphertext = base64.b64encode(ctypes.string_at(protected.data, protected.size)).decode()
    finally: ctypes.windll.kernel32.LocalFree(ctypes.cast(protected.data, ctypes.c_void_p))
    (AREA / "profile/passwords.json").write_text(json.dumps([{"Id": uuid.uuid4().hex, "Label": "progress-secret", "Ciphertext": ciphertext, "Created": "2026-10-20T00:00:00+00:00"}]))
    archive = AREA / ("encrypted-cancel.7z" if cancel else "encrypted.7z")
    cli(["create", archive, source, "--format", "7z", "--password", secret])
    p = start(["--extract-subfolder", archive]); time.sleep(.5)
    password_controls = json.loads(ui(p, "list", WindowTitle="输入归档密码"))
    assert any(x["Id"] == "ChooseArchivePassword" for x in password_controls)
    if cancel: ui(p, "invoke", Name="取消", WindowTitle="输入归档密码")
    else:
        ui(p, "expand", Id="ChooseArchivePassword", WindowTitle="输入归档密码")
        ui(p, "select", Name="progress-secret", WindowTitle="")
        ui(p, "invoke", Id="SubmitArchivePassword", WindowTitle="输入归档密码")
    report = finish(p, "cancelled" if cancel else "completed")
    if not cancel:
        out = pathlib.Path(report["Completed"][0]["Destination"])
        assert (out / source.name / "说明.txt").read_bytes() == (source / "说明.txt").read_bytes()

def long_task(cancel=False, close=False):
    folder = AREA / ("close-large" if close else "cancel-large" if cancel else "resume-large"); folder.mkdir()
    payload = folder / "payload.bin"
    with payload.open("wb") as out:
        for i in range(96): out.write(os.urandom(1024 * 1024))
    p = start(["--quick-7z", folder]); time.sleep(.5); controls(p)
    before = float(ui(p, "range", Id="ShellTotalProgress")); assert 0 <= before < 100
    ui(p, "invoke", Id="ShellPauseButton")
    wait(lambda: any(x["Id"] == "ShellProgressState" and x["Name"] == "已暂停" for x in controls(p)), 20)
    cpu = tool(["powershell.exe", "-NoProfile", "-Command", f"$worker=Get-CimInstance Win32_Process -Filter 'ParentProcessId = {p.pid}' | Where-Object Name -eq '7z.exe' | Select-Object -First 1; if(-not $worker){{throw 'No owned engine'}}; $first=(Get-Process -Id $worker.ProcessId).TotalProcessorTime.TotalSeconds; Start-Sleep -Milliseconds 600; $second=(Get-Process -Id $worker.ProcessId).TotalProcessorTime.TotalSeconds; [pscustomobject]@{{Worker=$worker.ProcessId;CpuDelta=$second-$first}} | ConvertTo-Json -Compress"])
    assert json.loads(cpu)["CpuDelta"] <= .02, cpu
    (AREA / ("cancel-pause.json" if cancel else "resume-pause.json")).write_text(cpu)
    ui(p, "screenshot", OutputPath=AREA / ("cancel-paused.png" if cancel else "paused.png"))
    if close:
        ui(p, "close"); assert p.wait(timeout=30) == 1
        assert receipt(p)["Status"] == "cancelled"
    elif cancel:
        ui(p, "invoke", Id="ShellCancelCloseButton"); finish(p, "cancelled", "cancelled-result.png")
    else:
        ui(p, "invoke", Id="ShellPauseButton"); finish(p, screenshot="large-completed.png")
        out = AREA / "resumed-decoded"; cli(["extract", AREA / (folder.name + ".7z"), out, "--layout", "here"])
        with payload.open("rb") as a, (out / folder.name / payload.name).open("rb") as b: assert hashlib.file_digest(a, "sha256").digest() == hashlib.file_digest(b, "sha256").digest()
    if cancel: assert not (AREA / (folder.name + ".7z")).exists()

try:
    check("quick ZIP progress, retained 100% result, real archive", lambda: quick("zip"))
    check("quick 7Z progress and real archive", lambda: quick("7z"))
    for flag, stem in [("--extract-direct", "direct"), ("--extract-many-here", "smart"), ("--extract-subfolder", "subfolder")]:
        check(flag + " progress and decoded contents", lambda f=flag, s=stem: extraction(f, s))
    check("batch extraction total progress and all outputs", lambda: extraction("--extract-subfolder", "batch", True))
    check("create settings only, then progress only", settings)
    check("extract settings only, then progress only", lambda: settings(False))
    check("cancel creation settings without starting task", lambda: settings(True, True))
    check("cancel extraction settings without starting task", lambda: settings(False, True))
    check("failure is shown in progress without false success", damaged)
    check("encrypted extraction uses password dialog owned by progress", encrypted)
    check("cancel password shows cancelled progress without output", lambda: encrypted(True))
    check("real native pause freezes CPU, resume completes with matching hash", long_task)
    check("cancel paused task rolls back output", lambda: long_task(True))
    check("closing active progress cancels safely and exits", lambda: long_task(True, True))
except BaseException as error:
    results.append({"Passed": False, "Error": repr(error)}); raise
finally:
    (AREA / "results.json").write_text(json.dumps({"Failures": sum(not x["Passed"] for x in results), "Results": results, "Application": str(APP)}, ensure_ascii=False, indent=2), encoding="utf-8")
    (ROOT / "verification/shell-progress-ui-latest.txt").write_text(str(AREA), encoding="utf-8")
    for p in processes:
        if p.poll() is None:
            try: ui(p, "close"); p.wait(timeout=15)
            except BaseException: p.kill()
print(json.dumps({"passed": len(results), "area": str(AREA)}, ensure_ascii=False))

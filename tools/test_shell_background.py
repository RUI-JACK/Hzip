"""Exercise the real shell startup route, recording top-level window show events."""
import base64, ctypes as c, hashlib, json, os, pathlib, subprocess, sys, threading, time, uuid, zipfile
from ctypes import wintypes as w

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "release/Hzip"
AREA = ROOT / "verification" / ("shell-ui-" + uuid.uuid4().hex[:8])
AREA.mkdir()
ENV = os.environ.copy()
ENV["HZIP_DATA_DIR"] = str(AREA / "profile")
ENV.pop("HZIP_ENGINE", None)
USER = c.windll.user32
USER.GetAncestor.argtypes = [w.HWND, w.UINT]
USER.GetAncestor.restype = w.HWND
USER.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
USER.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
USER.PostMessageW.argtypes = [w.HWND, w.UINT, w.WPARAM, w.LPARAM]
USER.PeekMessageW.argtypes = [c.POINTER(w.MSG), w.HWND, w.UINT, w.UINT, w.UINT]
USER.DispatchMessageW.argtypes = [c.POINTER(w.MSG)]
USER.DispatchMessageW.restype = w.LPARAM
processes, events, results = [], [], []
active_ids = set()
CALLBACK = c.WINFUNCTYPE(None, w.HANDLE, w.DWORD, w.HWND, c.c_long, c.c_long, w.DWORD, w.DWORD)

def window_info(hwnd):
    pid = w.DWORD()
    USER.GetWindowThreadProcessId(hwnd, c.byref(pid))
    title = c.create_unicode_buffer(2048)
    USER.GetWindowTextW(hwnd, title, len(title))
    return pid.value, title.value

@CALLBACK
def shown(hook, event, hwnd, object_id, child, thread, stamp):
    if object_id == 0 and child == 0 and hwnd and USER.GetAncestor(hwnd, 2) == hwnd:
        pid, title = window_info(hwnd)
        if pid in active_ids:
            events.append({"pid": pid, "title": title, "event_time": stamp})

USER.SetWinEventHook.argtypes = [w.DWORD, w.DWORD, w.HMODULE, CALLBACK, w.DWORD, w.DWORD, w.DWORD]
USER.SetWinEventHook.restype = w.HANDLE
hook = USER.SetWinEventHook(0x8002, 0x8002, None, shown, 0, 0, 0)
assert hook, "Cannot install window-show observer"

def pump():
    message = w.MSG()
    while USER.PeekMessageW(c.byref(message), None, 0, 0, 1):
        USER.TranslateMessage(c.byref(message))
        USER.DispatchMessageW(c.byref(message))

def until(predicate, timeout=40):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        pump()
        value = predicate()
        if value:
            return value
        time.sleep(.015)
    raise TimeoutError("Timed out waiting for shell operation")

def tool(args, expected=0):
    p = subprocess.Popen(list(map(str, args)), env=ENV, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    captured = {}
    reader = threading.Thread(target=lambda: captured.update(output=p.communicate()), daemon=True)
    reader.start()
    try: until(lambda: not reader.is_alive())
    except BaseException:
        p.kill(); reader.join(timeout=5); raise
    stdout, stderr = captured["output"]
    assert p.returncode == expected, (args, p.returncode, stdout, stderr)
    return stdout.decode("utf-8-sig")

def cli(args):
    return tool([ROOT / "release/Hzip/hz.exe", *args])

def ui(p, action, **options):
    args = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ROOT / "tools/ui.ps1", "-TargetProcessId", p.pid, "-Action", action]
    for key, value in options.items():
        args += ["-" + key, value]
    return tool(args)

ENUM = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
USER.EnumWindows.argtypes = [ENUM, w.LPARAM]
def windows(p):
    found = []
    @ENUM
    def collect(hwnd, value):
        pid, title = window_info(hwnd)
        if pid == p.pid and USER.IsWindowVisible(hwnd):
            found.append((hwnd, title))
        return True
    USER.EnumWindows(collect, 0)
    return found

def start(args):
    p = subprocess.Popen([str(APP / "Hzip.exe"), *map(str, args)], env=ENV)
    active_ids.add(p.pid)
    processes.append(p)
    return p

def receipt(p):
    files = (AREA / "profile/ShellJobs").glob("*.json")
    return next((v for f in files if (v := json.loads(f.read_text()))["ProcessId"] == p.pid), None)

def finish(p, status="completed", expected=0, silent=False):
    until(lambda: p.poll() is not None)
    pump()
    assert p.returncode == expected, (p.returncode, receipt(p))
    report = receipt(p)
    assert report and report["Status"] == status, report
    if silent:
        assert not [e for e in events if e["pid"] == p.pid], events
    return report

def dialog(p, title):
    until(lambda: any(t == title for _, t in windows(p)))
    current = windows(p)
    assert len(current) == 1 and current[0][1] == title, current
    controls = json.loads(ui(p, "list"))
    forbidden = {"EntriesGrid", "CreateArchiveButton", "OpenArchiveButton", "RecentArchives"}
    assert not any(x["Id"] in forbidden for x in controls), controls
    return controls

def allowed(p, titles):
    assert all(e["title"] in titles for e in events if e["pid"] == p.pid), [e for e in events if e["pid"] == p.pid]

def password_choice(p, label):
    ui(p, "expand", Id="ChooseArchivePassword")
    ui(p, "select", Name=label)
    ui(p, "invoke", Id="SubmitArchivePassword", WindowTitle="输入归档密码")

def sha(path):
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()

def check(name, action):
    action()
    results.append({"name": name, "passed": True})
    print("PASS", name, flush=True)

folder = AREA / "Clash Verge"
folder.mkdir()
(folder / "中文 & notes.txt").write_text("source remains unchanged", encoding="utf-8")
sample = AREA / "sample.zip"
with zipfile.ZipFile(sample, "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr("sample.txt", "sample data")
    z.writestr("nested/说明.txt", "Unicode content")
source_hash, archive_hash = sha(folder / "中文 & notes.txt"), sha(sample)

def seed_passwords():
    class Blob(c.Structure):
        _fields_ = [("size", w.DWORD), ("data", c.POINTER(c.c_ubyte))]
    entries = []
    for label, value in [("wrong", "incorrect"), ("correct", "Shell-secret-2026")]:
        data = c.create_string_buffer(value.encode())
        original = Blob(len(value.encode()), c.cast(data, c.POINTER(c.c_ubyte)))
        encrypted = Blob()
        assert c.windll.crypt32.CryptProtectData(c.byref(original), "Hzip test", None, None, None, 1, c.byref(encrypted))
        try:
            entries.append({"Id": uuid.uuid4().hex, "Label": label, "Ciphertext": base64.b64encode(c.string_at(encrypted.data, encrypted.size)).decode(), "Created": "2026-10-20T00:00:00+00:00"})
        finally:
            c.windll.kernel32.LocalFree(c.cast(encrypted.data, c.c_void_p))
    (AREA / "profile").mkdir(exist_ok=True)
    (AREA / "profile/passwords.json").write_text(json.dumps(entries))

def quick(fmt):
    p = start(["--quick-" + fmt, folder]); finish(p, silent=True)
    target = AREA / (folder.name + "." + fmt)
    out = AREA / ("decoded-" + fmt)
    cli(["extract", target, out, "--layout", "here"])
    assert sha(out / folder.name / "中文 & notes.txt") == source_hash

def collision():
    original = sha(AREA / "Clash Verge.zip")
    p = start(["--quick-zip", folder]); finish(p, silent=True)
    assert (AREA / "Clash Verge (2).zip").exists() and sha(AREA / "Clash Verge.zip") == original

def extract(flag, stem, layout):
    path = AREA / (stem + ".zip"); path.write_bytes(sample.read_bytes())
    p = start([flag, path]); finish(p, silent=True)
    dest = AREA if layout == "here" else AREA / stem
    assert (dest / "sample.txt").read_text() == "sample data"
    assert (dest / "nested/说明.txt").read_text() == "Unicode content"

def custom_create(cancel=False):
    p = start(["--create", folder]); dialog(p, "创建归档 · Hzip")
    if not cancel: ui(p, "screenshot", OutputPath=AREA / "create-settings-only.png")
    target = AREA / ("cancelled.zip" if cancel else "custom.zip")
    ui(p, "set", Id="CreateOutputPath", Value=target)
    ui(p, "invoke", Name="取消") if cancel else ui(p, "invoke", Id="StartCreateButton")
    finish(p, "cancelled" if cancel else "completed", 1 if cancel else 0)
    allowed(p, {"创建归档 · Hzip"})
    if cancel: assert not target.exists()
    else:
        with zipfile.ZipFile(target) as z: assert z.read("Clash Verge/中文 & notes.txt") == (folder / "中文 & notes.txt").read_bytes()

def custom_extract(cancel=False, multiple=False):
    archives = [sample]
    if multiple:
        second = AREA / "second.zip"; second.write_bytes(sample.read_bytes()); archives.append(second)
    p = start(["--extract-to", *archives]); dialog(p, "解压归档 · Hzip")
    if not cancel and not multiple: ui(p, "screenshot", OutputPath=AREA / "extract-settings-only.png")
    dest = AREA / ("cancelled-extract" if cancel else "multi-extract" if multiple else "custom-extract")
    ui(p, "set", Id="ExtractDestination", Value=dest)
    ui(p, "invoke", Name="取消") if cancel else ui(p, "invoke", Id="StartExtractButton")
    finish(p, "cancelled" if cancel else "completed", 1 if cancel else 0)
    allowed(p, {"解压归档 · Hzip"})
    if cancel: assert not dest.exists()
    else:
        for archive in archives: assert (dest / archive.stem / "sample.txt").read_text() == "sample data"

def encrypted(cancel=False, retry=False):
    stem = "encrypted-cancel" if cancel else "encrypted-retry" if retry else "encrypted"
    path = AREA / (stem + ".7z")
    cli(["create", path, folder, "--format", "7z", "--password", "Shell-secret-2026"])
    p = start(["--extract-subfolder", path]); dialog(p, "输入归档密码 · Hzip")
    if cancel: ui(p, "invoke", Name="取消")
    else:
        if retry:
            password_choice(p, "wrong")
            until(lambda: len([e for e in events if e["pid"] == p.pid and e["title"] == "输入归档密码 · Hzip"]) >= 2)
            controls = dialog(p, "输入归档密码 · Hzip")
            assert any("密码不正确" in x["Name"] for x in controls)
        password_choice(p, "correct")
    finish(p, "cancelled" if cancel else "completed", 1 if cancel else 0)
    allowed(p, {"输入归档密码 · Hzip", ""})  # WPF ComboBox popup has an empty top-level title.
    if cancel: assert not (AREA / stem).exists()
    else: assert sha(AREA / stem / folder.name / "中文 & notes.txt") == source_hash

def error_case():
    path = AREA / "damaged.zip"; path.write_bytes(b"not a zip")
    before = sha(path); p = start(["--extract-direct", path]); dialog(p, "右键任务未完成 · Hzip")
    ui(p, "invoke", Id="DismissShellError"); finish(p, "failed", 2)
    allowed(p, {"右键任务未完成 · Hzip"}); assert sha(path) == before

try:
    seed_passwords()
    check("quick ZIP: correct content, no windows, process exits", lambda: quick("zip"))
    check("quick 7Z: correct content, no windows, process exits", lambda: quick("7z"))
    check("quick collision keeps existing archive", collision)
    for flag, stem, layout in [("--extract-direct", "direct", "here"), ("--extract-many-here", "smart", "smart"), ("--extract-subfolder", "subfolder", "subfolder"), ("--extract-here", "legacy", "smart")]:
        check(flag + ": correct content, no windows", lambda f=flag, s=stem, l=layout: extract(f, s, l))
    check("custom creation shows settings only and closes after success", custom_create)
    check("cancel creation exits without output or main window", lambda: custom_create(cancel=True))
    check("custom extraction shows settings only and closes after success", custom_extract)
    check("multi archive extraction shares settings, preserves separate folders", lambda: custom_extract(multiple=True))
    check("cancel extraction exits without output or main window", lambda: custom_extract(cancel=True))
    check("encrypted archive shows password only and extracts correctly", encrypted)
    check("wrong password retries without main window", lambda: encrypted(retry=True))
    check("cancel password preserves archive and writes no files", lambda: encrypted(cancel=True))
    check("damaged archive reports failure without main window", error_case)
    assert sha(sample) == archive_hash and sha(folder / "中文 & notes.txt") == source_hash
except BaseException as error:
    results.append({"passed": False, "error": repr(error)})
    raise
finally:
    pump()
    (AREA / "results.json").write_text(json.dumps({"Failures": sum(not x["passed"] for x in results), "Results": results, "WindowShowEvents": events, "Application": str(APP)}, ensure_ascii=False, indent=2), encoding="utf-8")
    (ROOT / "verification/shell-ui-latest.txt").write_text(str(AREA), encoding="utf-8")
    for p in processes:
        if p.poll() is None:
            for hwnd, _ in windows(p): USER.PostMessageW(hwnd, 0x10, 0, 0)
            try: p.wait(timeout=3)
            except subprocess.TimeoutExpired: p.kill()
    USER.UnhookWinEvent.argtypes = [w.HANDLE]
    USER.UnhookWinEvent(hook)
print(json.dumps({"passed": len(results), "area": str(AREA)}, ensure_ascii=False))

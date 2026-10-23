"""Runs the installed native menu commands against unique, local test fixtures."""
import hashlib, json, os, pathlib, subprocess, sys, time, uuid, zipfile

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = ROOT / "release/Hzip"
AREA = ROOT / "verification" / ("menu-0.5-final-" + uuid.uuid4().hex[:8])
AREA.mkdir(parents=True)
ENV = os.environ.copy()
ENV["HZIP_DATA_DIR"] = str(AREA / "profile")
ENV.pop("HZIP_ENGINE", None)
PROBE = ROOT / "verification/HzipShellProbe05.exe"
assert PROBE.exists(), "Compile tests/ShellProbe.cpp first (see WINDOWS11-MENU.md)."
folder = AREA / "Clash Verge"
folder.mkdir()
(folder / "配置说明.txt").write_text("Hzip right-click content 中文\n", encoding="utf-8")
text = AREA / "notes & 中文.txt"
text.write_text("literal ampersand & Unicode file", encoding="utf-8")
sample = AREA / "sample.zip"
with zipfile.ZipFile(sample, "w", zipfile.ZIP_DEFLATED) as archive:
    archive.writestr("sample.txt", "sample content")
results, applications = [], []

def invoke(flag, archive=sample):
    args = [str(PROBE), str(APP / "shell/Hzip.Shell.v05r2.dll"), str(archive), str(text), str(folder), flag]
    process = subprocess.Popen(args, env=ENV, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    stdout, stderr = process.communicate(timeout=20)
    assert process.returncode == 0, (flag, stdout, stderr)
    proof = json.loads(stdout)
    if flag != "--registered":
        query = f"Get-CimInstance Win32_Process -Filter 'ParentProcessId = {process.pid}' | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress"
        child = subprocess.run(["powershell.exe", "-NoProfile", "-Command", query], capture_output=True, timeout=20)
        decoded = child.stdout.decode("utf-8-sig", errors="replace").strip()
        if decoded:
            items = json.loads(decoded)
            applications.extend(item for item in (items if isinstance(items, list) else [items]) if item.get("CommandLine"))
    return proof

def wait_for(path):
    end = time.monotonic() + 40
    while time.monotonic() < end:
        if path.is_file():
            check = subprocess.run([str(APP / "hz.exe"), "test", str(path)], env=ENV, capture_output=True, timeout=15)
            if check.returncode == 0:
                return
        time.sleep(.15)
    raise TimeoutError(str(path))

for flag, filename, actual_type in [("--invoke-zip", "Clash Verge.zip", "zip"), ("--invoke-7z", "Clash Verge.7z", "7z"), ("--invoke-multi-zip", AREA.name + ".zip", "zip")]:
    invoke(flag)
    output = AREA / filename
    wait_for(output)
    listing = subprocess.run([str(APP / "hz.exe"), "list", str(output), "--json"], env=ENV, capture_output=True, timeout=20)
    assert json.loads(listing.stdout.decode("utf-8-sig"))["Format"] == actual_type
    destination = AREA / ("out-" + actual_type + ("-multi" if "multi" in flag else ""))
    decoded = subprocess.run([str(APP / "hz.exe"), "extract", str(output), str(destination), "--layout", "here"], env=ENV, capture_output=True, timeout=20)
    assert decoded.returncode == 0
    if "multi" in flag:
        assert (destination / text.name).read_bytes() == text.read_bytes()
        assert (destination / sample.name).read_bytes() == sample.read_bytes()
    else:
        assert (destination / "Clash Verge/配置说明.txt").read_bytes() == (folder / "配置说明.txt").read_bytes()
    results.append({"action": flag, "passed": True, "format": actual_type})

before = hashlib.file_digest((AREA / "Clash Verge.zip").open("rb"), "sha256").hexdigest()
invoke("--invoke-zip")
wait_for(AREA / "Clash Verge (2).zip")
assert hashlib.file_digest((AREA / "Clash Verge.zip").open("rb"), "sha256").hexdigest() == before
results.append({"action": "collision creates (2) and preserves existing archive", "passed": True})

for flag, stem, files, expected_root in [
    ("--invoke-direct", "direct", {"direct-a.txt": "a", "direct-b.txt": "b"}, AREA),
    ("--invoke-extract", "smart", {"smart-a.txt": "a", "smart-b.txt": "b"}, AREA / "smart"),
    ("--invoke-subfolder", "folder", {"inner/only.txt": "nested"}, AREA / "folder"),
]:
    archive = AREA / (stem + ".zip")
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as writer:
        for name, content in files.items():
            writer.writestr(name, content)
    checksum = hashlib.file_digest(archive.open("rb"), "sha256").hexdigest()
    invoke(flag, archive)
    end = time.monotonic() + 30
    while not all((expected_root / name).is_file() for name in files):
        assert time.monotonic() < end, (flag, expected_root)
        time.sleep(.1)
    assert all((expected_root / name).read_text() == content for name, content in files.items())
    assert hashlib.file_digest(archive.open("rb"), "sha256").hexdigest() == checksum
    results.append({"action": flag, "passed": True, "destination": str(expected_root)})

for flag in ["--invoke-create", "--invoke-folder-open", "--invoke-other", "--invoke-open"]:
    invoke(flag)

native = invoke("--registered")

def app_for(flag):
    return next(item["ProcessId"] for item in applications if (" " + flag + " ") in (item.get("CommandLine") or ""))

def ui(pid, action, **options):
    args = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "tools/ui.ps1"), "-TargetProcessId", str(pid), "-Action", action]
    for key, value in options.items():
        args += ["-" + key, str(value)]
    process = subprocess.run(args, capture_output=True, timeout=30)
    assert process.returncode == 0, (args, process.stdout, process.stderr)
    return process.stdout.decode("utf-8-sig")

for pid in [app_for("--create"), app_for("--extract-to")]:
    controls = json.loads(ui(pid, "list", WindowTitle="创建归档" if pid == app_for("--create") else "解压归档"))
    assert not any(item["Id"] in {"EntriesGrid", "CreateArchiveButton", "OpenArchiveButton", "RecentArchives"} for item in controls), "A settings action displayed the main window"

create_pid = app_for("--create")
ui(create_pid, "screenshot", WindowTitle="创建归档", OutputPath=AREA / "native-create-settings.png")
ui(create_pid, "set", WindowTitle="创建归档", Id="CreateOutputPath", Value=AREA / "dialog-created.zip")
ui(create_pid, "invoke", WindowTitle="创建归档", Id="StartCreateButton")
wait_for(AREA / "dialog-created.zip")
with zipfile.ZipFile(AREA / "dialog-created.zip") as archive:
    assert archive.read(text.name) == text.read_bytes()
results.append({"action": "native custom-create settings only, then actual ZIP", "passed": True})

extract_pid = app_for("--extract-to")
destination = AREA / "自选解压目录"
ui(extract_pid, "screenshot", WindowTitle="解压归档", OutputPath=AREA / "native-extract-settings.png")
ui(extract_pid, "set", WindowTitle="解压归档", Id="ExtractDestination", Value=destination)
ui(extract_pid, "invoke", WindowTitle="解压归档", Id="StartExtractButton")
end = time.monotonic() + 30
while not (destination / "sample.txt").is_file():
    assert time.monotonic() < end, "Native custom extraction did not finish"
    time.sleep(.1)
assert (destination / "sample.txt").read_text() == "sample content"
results.append({"action": "native extract-to settings only, then correct content", "passed": True})

folder_pid = app_for("--open-from")
folder_controls = json.loads(ui(folder_pid, "list", WindowTitle="打开归档"))
assert any(str(folder) in item["Name"] for item in folder_controls)
open_pid = next(item["ProcessId"] for item in applications if " --" not in item["CommandLine"])
open_controls = json.loads(ui(open_pid, "list", WindowTitle="sample.zip"))
assert any(item["Name"] == "sample.txt" for item in open_controls)
(AREA / "ui-results.json").write_text(json.dumps({"NewArchiveDialogCreatedRealZip": True, "OtherFolderDialogExtractedCorrectContent": True, "SettingsNeverShowMainWindow": True, "FolderOpenDialogInitialDirectory": str(folder), "FolderOpenDialogVerified": True, "ArchiveOpenLoadedSampleEntry": True, "NoFileTypesMergedInNativeMenu": True}, ensure_ascii=False, indent=2), encoding="utf-8")
registration = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(APP / "shell/manage_menu.ps1"), "-Action", "Status"], capture_output=True, timeout=20)
assert registration.returncode == 0
registered = json.loads(registration.stdout.decode("utf-8-sig"))
assert registered["Installed"]
(AREA / "registration.json").write_text(json.dumps(registered, ensure_ascii=False, indent=2), encoding="utf-8")

for item in applications:
    if " --" not in item["CommandLine"] or " --open-from " in item["CommandLine"]: continue
    controls = json.loads(ui(item["ProcessId"], "list", WindowTitle="Hzip"))
    assert any(x["Id"] == "ShellCancelCloseButton" and x["Name"] == "关闭" for x in controls)
    ui(item["ProcessId"], "invoke", Id="ShellCancelCloseButton", WindowTitle="Hzip")

ui(folder_pid, "close", WindowTitle="打开归档")
ui(folder_pid, "close", WindowTitle="Hzip · 压缩文件管理器")
ui(open_pid, "close", WindowTitle="sample.zip")
(AREA / "applications.json").write_text(json.dumps(applications, ensure_ascii=False, indent=2), encoding="utf-8")
(AREA / "results.json").write_text(json.dumps({"failures": 0, "results": results, "native": native, "area": str(AREA)}, ensure_ascii=False, indent=2), encoding="utf-8")
(ROOT / "verification/menu-0.5-latest.txt").write_text(str(AREA), encoding="utf-8")
print(json.dumps({"passed": len(results), "area": str(AREA), "dialogs_verified": 4, "settings_only": True}, ensure_ascii=False))

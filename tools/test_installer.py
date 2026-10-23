"""Install, run, upgrade and uninstall the produced installer in an owned directory."""
import base64, hashlib, json, os, pathlib, subprocess, sys, time, uuid, winreg, zipfile
sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(__file__).resolve().parents[1]
WORK = pathlib.Path((ROOT / "verification/installer-latest.txt").read_text(encoding="utf-8-sig").strip())
AREA = WORK / ("run-" + uuid.uuid4().hex[:8]); AREA.mkdir()
(WORK / "test-run-latest.txt").write_text(str(AREA), encoding="utf-8")
SETUP = ROOT / "release/installer/Hzip-Setup-0.5.0-x64.exe"
APP = AREA / "安装测试 Hzip"
assert not APP.exists(), "Use a new test directory"
ENV = os.environ.copy(); ENV["HZIP_DATA_DIR"] = str(AREA / "runtime-profile"); ENV.pop("HZIP_ENGINE", None)
ENV["DOTNET_ROOT"] = str(AREA / "no-global-runtime"); ENV["DOTNET_ROOT_X64"] = ENV["DOTNET_ROOT"]; ENV["DOTNET_MULTILEVEL_LOOKUP"] = "0"
GROUP = "Hzip 安装验证 " + AREA.name
desktop = pathlib.Path(os.environ["USERPROFILE"]) / "Desktop"
shortcut = desktop / "Hzip.lnk"; shortcut_before = shortcut.read_bytes() if shortcut.exists() else None
if shortcut_before: (AREA / "desktop-shortcut-before.lnk").write_bytes(shortcut_before)
extensions = [".zip", ".zipx", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".txz", ".bz2", ".tbz2", ".iso", ".cab", ".lzh", ".lha", ".wim", ".arj", ".dmg", ".001", ".zst", ".lzma"]
paths = ["Software\\Classes\\" + kind + "\\shell\\" + verb for kind in ["*", "Directory"] for verb in ["Hzip.Compress", "Hzip.QuickZip", "Hzip.Quick7z"]]
paths += [r"Software\Classes\Directory\shell\Hzip.Open", r"Software\Classes\Hzip.Archive", r"Software\Hzip\Capabilities"]
paths += ["Software\\Classes\\SystemFileAssociations\\" + ext + "\\shell\\" + verb for ext in extensions for verb in ["Hzip.Open", "Hzip.Extract", "Hzip.ExtractDirect", "Hzip.ExtractSubfolder", "Hzip.ExtractTo"]]
def read_tree(path):
    try: key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, path)
    except FileNotFoundError: return None
    with key:
        values, children = [], {}
        i = 0
        while True:
            try: values.append(winreg.EnumValue(key, i)); i += 1
            except OSError: break
        i = 0
        while True:
            try: name = winreg.EnumKey(key, i); children[name] = read_tree(path + "\\" + name); i += 1
            except OSError: break
    return {"values": values, "children": children}
def value(path, name=""):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key: return winreg.QueryValueEx(key, name)
    except FileNotFoundError: return None
def write_value(path, name, previous):
    if previous is not None:
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, path) as key: winreg.SetValueEx(key, name, 0, previous[1], previous[0])
    else:
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_SET_VALUE) as key: winreg.DeleteValue(key, name)
        except FileNotFoundError: pass
def restore_tree(path, tree):
    if tree is None: return
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, path) as key:
        for name, data, kind in tree["values"]: winreg.SetValueEx(key, name, 0, kind, data)
    for name, child in tree["children"].items(): restore_tree(path + "\\" + name, child)
before = {path: read_tree(path) for path in paths}
open_with = {"Software\\Classes\\" + ext + "\\OpenWithProgids": value("Software\\Classes\\" + ext + "\\OpenWithProgids", "Hzip.Archive") for ext in extensions}
registered = value(r"Software\RegisteredApplications", "Hzip")
defaults = {ext: (value("Software\\Classes\\" + ext), read_tree("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\" + ext + "\\UserChoice")) for ext in [".zip", ".7z", ".rar"]}
receipt_path = pathlib.Path(os.environ["LOCALAPPDATA"]) / "Hzip/modern-menu.json"
modern_before = receipt_path.read_bytes() if receipt_path.exists() else None
(AREA / "registry-before.json").write_text(json.dumps(before, ensure_ascii=False, indent=2, default=lambda x: {"bytes": base64.b64encode(x).decode()}), encoding="utf-8")
results = []
def run(args, expected=0, env=ENV):
    p = subprocess.run(args if isinstance(args, str) else list(map(str, args)), env=env, capture_output=True, timeout=90)
    assert p.returncode == expected, (args, p.returncode, p.stdout, p.stderr)
    return p.stdout.decode("utf-8-sig", errors="replace").strip()
def ui(pid, action, **options):
    args = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ROOT / "tools/ui.ps1", "-TargetProcessId", pid, "-Action", action, "-WindowTitle", options.pop("WindowTitle", "Hzip")]
    for key, val in options.items(): args.extend(["-" + key, val])
    return run(args)
def wait(predicate, timeout=25):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        result = predicate()
        if result: return result
        time.sleep(.1)
    raise TimeoutError("Installer verification timed out")
def sha(path):
    with path.open("rb") as f: return hashlib.file_digest(f, "sha256").hexdigest().upper()
def passed(name):
    results.append({"Name": name, "Passed": True}); print("PASS", name, flush=True)
    (AREA / "test-results.json").write_text(json.dumps({"Failures": 0, "Results": results}, ensure_ascii=False, indent=2), encoding="utf-8")
def install(log):
    command = '"' + str(SETUP) + '" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=chinesesimplified /TASKS=desktopicon,classicmenu /DIR="' + str(APP) + '" /LOG="' + str(AREA / log) + '"'
    (AREA / (log + ".command.txt")).write_text(command, encoding="utf-8")
    run(command)
try:
    install("install.log")
    manifest = json.loads((WORK / "payload-manifest.json").read_text(encoding="utf-8-sig"))
    for item in manifest:
        path = APP / item["Path"]; assert path.stat().st_size == item["Bytes"] and sha(path) == item["SHA256"], path
    passed("Installed payload matches every manifest hash, including the beautified GUI")
    assert shortcut.exists()
    link = run(["powershell.exe", "-NoProfile", "-Command", "$w=New-Object -ComObject WScript.Shell; $w.CreateShortcut([Environment]::GetFolderPath('DesktopDirectory')+'\\Hzip.lnk').TargetPath"])
    assert link == str(APP / "Hzip.exe")
    start_menu = pathlib.Path(os.environ["APPDATA"]) / "Microsoft/Windows/Start Menu/Programs/Hzip"
    assert (start_menu / "Hzip.lnk").exists() and (start_menu / "卸载 Hzip.lnk").exists()
    assert str(APP / "Hzip.exe") in value(r"Software\Classes\Hzip.Archive\shell\open\command")[0]
    passed("Desktop and Start Menu shortcuts plus optional classic menu point to this installation")
    uninstall_key = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{51338024-AC9E-4C7D-80F4-094B07238940}_is1"
    assert value(uninstall_key, "DisplayName") and value(uninstall_key, "UninstallString")
    passed("Windows installed-app list has a working uninstall registration")
    update = json.loads((APP / "update.json").read_text(encoding="utf-8-sig"))
    assert update["updateUrl"] == "https://github.com/RUI-JACK/Hzip/releases/latest"
    assert value(uninstall_key, "URLUpdateInfo")[0] == update["updateUrl"]
    passed("Program update configuration and Windows update information point to the GitHub release page")
    p = subprocess.Popen([str(APP / "Hzip.exe")], env=ENV)
    def loaded():
        try: return any(x["Id"] == "HomeButton" for x in json.loads(ui(p.pid, "list")))
        except AssertionError: return False
    wait(loaded); ui(p.pid, "screenshot", OutputPath=AREA / "installed-home.png"); ui(p.pid, "close"); assert p.wait(timeout=10) == 0
    passed("Installed beautified GUI starts without any global .NET runtime")
    source = AREA / "说明.txt"; source.write_text("Hzip 安装包真实压缩解压验证", encoding="utf-8")
    archive = AREA / "created.zip"
    run([APP / "hz.exe", "create", archive, source, "--password", "安装验证密码"])
    run([APP / "hz.exe", "test", archive, "--password", "安装验证密码"])
    out = AREA / "decoded"; run([APP / "hz.exe", "extract", archive, out, "--password", "安装验证密码", "--layout", "here"])
    assert (out / source.name).read_bytes() == source.read_bytes()
    passed("Installed CLI creates tests and extracts a genuinely encrypted ZIP")
    case = AREA / "shell-case"; case.mkdir(); archive = case / "sample.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z: z.writestr("同名.txt", "new content")
    (case / "同名.txt").write_text("old content")
    p = subprocess.Popen([str(APP / "Hzip.exe"), "--extract-direct", str(archive)], env=ENV)
    def conflict():
        try: return any(x["Id"] == "ConflictRename" for x in json.loads(ui(p.pid, "list")))
        except AssertionError: return False
    wait(conflict); ui(p.pid, "screenshot", OutputPath=AREA / "installed-conflict.png"); ui(p.pid, "invoke", Id="ConflictRename")
    def complete():
        for path in (AREA / "runtime-profile/ShellJobs").glob("*.json"):
            try: data = json.loads(path.read_text(encoding="utf-8-sig"))
            except (OSError, json.JSONDecodeError): continue
            if data["ProcessId"] == p.pid and data["Status"] != "running": return data
    receipt = wait(complete); assert receipt["Status"] == "completed"
    assert (case / "同名.txt").read_text() == "old content" and (case / "同名 (2).txt").read_text() == "new content"
    ui(p.pid, "invoke", Id="ShellCancelCloseButton"); assert p.wait(timeout=10) == 0
    passed("Installed shell task shows conflict dialog and preserves both files after renaming")
    # Simulate a portable copy taking ownership of one verb. Cleanup must leave it.
    foreign = str(ROOT / "release/Hzip/Hzip.exe")
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Classes\*\shell\Hzip.QuickZip", 0, winreg.KEY_SET_VALUE) as key: winreg.SetValueEx(key, "Icon", 0, winreg.REG_SZ, foreign)
    run([APP / "Hzip.SetupHelper.exe", "cleanup"])
    assert value(r"Software\Classes\*\shell\Hzip.QuickZip", "Icon")[0] == foreign
    assert value(r"Software\Classes\Hzip.Archive\shell\open\command") is None
    passed("Integration cleanup preserves registrations owned by another Hzip copy")
    install("reinstall.log")
    assert value(r"Software\Classes\Hzip.Archive\DefaultIcon")[0] == str(APP / "Hzip.exe")
    passed("Reinstalling to the same directory succeeds and restores selected integration")
    sentinel = APP / "user-created-note.txt"; sentinel.write_text("user data must remain")
    profile_note = AREA / "runtime-profile/keep-user-data.txt"; profile_note.write_text("keep profile data")
    run([APP / "unins000.exe", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + str(AREA / "uninstall.log")])
    assert not (APP / "Hzip.exe").exists() and not (APP / "Hzip.Core.dll").exists()
    assert value(uninstall_key, "DisplayName") is None and value(r"Software\Classes\Hzip.Archive\shell\open\command") is None
    assert not shortcut.exists() and not (start_menu / "Hzip.lnk").exists()
    assert sentinel.read_text() == "user data must remain" and profile_note.read_text() == "keep profile data"
    passed("Uninstall removes application shortcuts owned integration and installed-app entry while preserving user files")
finally:
    # Restore the prior portable registrations/shortcut, never the user's defaults.
    for path, tree in before.items(): restore_tree(path, tree)
    for path, previous in open_with.items(): write_value(path, "Hzip.Archive", previous)
    write_value(r"Software\RegisteredApplications", "Hzip", registered)
    if shortcut_before is not None: shortcut.write_bytes(shortcut_before)
assert defaults == {ext: (value("Software\\Classes\\" + ext), read_tree("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\" + ext + "\\UserChoice")) for ext in [".zip", ".7z", ".rar"]}
assert (receipt_path.read_bytes() if receipt_path.exists() else None) == modern_before
for path, tree in before.items(): assert read_tree(path) == tree, path
passed("Original default apps portable registrations and native-menu receipt remain unchanged")
proof = {"Failures": 0, "Results": results, "InstalledManifestHashesMatched": len(manifest), "ApplicationUIChanged": False, "NativeMenuInstallationSelected": False, "InstallerSHA256": sha(SETUP), "InstallerBytes": SETUP.stat().st_size, "VerifiedSourceGUIDLLSHA256": sha(ROOT / "release/Hzip/Hzip.dll")}
(AREA / "results.json").write_text(json.dumps(proof, ensure_ascii=False, indent=2), encoding="utf-8")
(WORK / "results.json").write_text(json.dumps(proof | {"TestDirectory": str(AREA)}, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"passed": len(results), "area": str(AREA)}, ensure_ascii=False))

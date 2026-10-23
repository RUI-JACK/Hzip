"""Check GZIP tail handling through the installed Explorer COM command.

--original extracts beside the supplied archive; the default uses a test copy.
No archive or existing output is overwritten.
"""
import hashlib, json, os, pathlib, subprocess, sys, time, zlib

sys.stdout.reconfigure(encoding="utf-8")
ROOT = pathlib.Path(__file__).resolve().parents[1]
AREA = pathlib.Path((ROOT / "verification/gzip-tail-latest.txt").read_text(encoding="utf-8-sig").strip())
APP = ROOT / "release/Hzip"
analysis = json.loads((AREA / "analysis.json").read_text(encoding="utf-8-sig"))
original = pathlib.Path(analysis["Source"])
source = original if "--original" in sys.argv else AREA / original.name
if source != original:
    assert not source.exists()
    source.write_bytes(original.read_bytes())
env = os.environ.copy()
env.pop("HZIP_DATA_DIR", None)
env.pop("HZIP_ENGINE", None)

def sha(path):
    with path.open("rb") as f: return hashlib.file_digest(f, "sha256").hexdigest()

def run(args):
    p = subprocess.run(list(map(str, args)), env=env, capture_output=True, timeout=45)
    assert p.returncode == 0, (args, p.stdout, p.stderr)
    return p.stdout.decode("utf-8-sig").strip()

source_hash = sha(source)
assert source_hash == analysis["SourceSHA256"]
decoder = zlib.decompressobj(31); decoded_hash = hashlib.sha256(); decoded_bytes = 0
with source.open("rb") as f:
    while not decoder.eof:
        chunk = f.read(65536)
        assert chunk, "GZIP ended before its trailer"
        while chunk:
            output = decoder.decompress(chunk, 65536)
            decoded_hash.update(output); decoded_bytes += len(output)
            chunk = decoder.unconsumed_tail
            if decoder.eof:
                tail = decoder.unused_data + f.read()
                break
assert decoded_bytes == analysis["DecodedBytes"] and decoded_hash.hexdigest() == analysis["DecodedSHA256"]
assert len(tail) == analysis["TrailingBytes"]
cli = json.loads(run([APP / "hz.exe", "test", source, "--json"]))
assert cli["Valid"] and len(cli["Warnings"]) == 1 and str(len(tail)) in cli["Warnings"][0]
text = AREA / "ordinary.txt"; text.write_text("GZIP menu test", encoding="utf-8")
folder = AREA / "ordinary-folder"; folder.mkdir(exist_ok=True)
efs = pathlib.Path((ROOT / "verification/efs-fix-latest.txt").read_text(encoding="utf-8-sig").strip())
profile = pathlib.Path(os.environ["LOCALAPPDATA"]) / "Packages/Hzip.ContextMenu_fss7vx0rtmayp/LocalCache/Local/Hzip"
jobs = profile / "ShellJobs"
before_jobs = set(jobs.glob("*.json"))
before_files = {p.name: (p.stat().st_size, p.stat().st_mtime_ns) for p in source.parent.iterdir() if p.is_file()}
probe = json.loads(run([efs / "HzipShellProbeEfs.exe", APP / "shell/Hzip.Shell.v05r2.dll", source, text, folder, "--registered-invoke-direct"]))
assert probe["registered_activation"] and probe["invoked"]
report = None
last_prompt_check = 0
end = time.monotonic() + 90
while time.monotonic() < end:
    for path in set(jobs.glob("*.json")) - before_jobs:
        try: candidate = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError): continue
        if candidate["Status"] == "running":
            if time.monotonic() - last_prompt_check > 1:
                last_prompt_check = time.monotonic()
                subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ROOT / "tools/ui.ps1"), "-TargetProcessId", str(candidate["ProcessId"]), "-Action", "invoke", "-WindowTitle", "同名文件处理", "-Id", "ConflictRenameAll"], capture_output=True, timeout=15)
            continue
        if any(item.get("Input") == str(source) for item in candidate["Completed"]) or candidate["Status"] == "failed":
            report = candidate; break
    if report: break
    time.sleep(.05)
assert report and report["Status"] == "completed", report
assert report["Completed"][0]["Files"] == 1
assert str(len(tail)) in report["Completed"][0]["Warnings"][0]
destination = pathlib.Path(report["Completed"][0]["Destination"])
new_files = [p for p in destination.iterdir() if p.is_file() and p.name not in before_files]
assert len(new_files) == 1, new_files
output = new_files[0]
assert output.stat().st_size == decoded_bytes and sha(output) == decoded_hash.hexdigest()
assert not (output.stat().st_file_attributes & 0x4000), "Output inherited profile EFS"
assert sha(source) == source_hash and sha(original) == source_hash
for name, metadata in before_files.items():
    path = source.parent / name
    assert path.is_file() and (path.stat().st_size, path.stat().st_mtime_ns) == metadata

def ui(action, **options):
    args = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ROOT / "tools/ui.ps1", "-TargetProcessId", report["ProcessId"], "-Action", action, "-WindowTitle", "Hzip"]
    for key, value in options.items(): args.extend(["-" + key, value])
    return run(args)

controls = json.loads(ui("list"))
assert not any(x["Id"] in {"ArchiveEntries", "CreateArchiveButton", "OpenArchiveButton"} for x in controls)
assert all(any(x["Id"] == id and x["RangeValue"] == 100 for x in controls) for id in ["ShellCurrentProgress", "ShellTotalProgress"])
details = ui("get", Id="ShellProgressResult")
assert str(len(tail)) in details and "未写入解压文件" in details
ui("screenshot", OutputPath=AREA / "original-extraction-completed.png")
(AREA / "packaged-controls.json").write_text(json.dumps(controls, ensure_ascii=False, indent=2), encoding="utf-8")
ui("invoke", Id="ShellCancelCloseButton")
result = {
    "Passed": True, "RegisteredCOMActivation": probe, "Receipt": report,
    "CLIIntegrityResult": cli, "SourceUnchanged": True, "ExistingFilesPreserved": True,
    "IndependentZlibCRCAndSizeVerified": True, "Output": str(output), "OutputBytes": decoded_bytes,
    "OutputSHA256": decoded_hash.hexdigest(), "SourceSHA256": source_hash, "TrailingBytes": len(tail),
    "ExternalMetadataNotWrittenToOutput": True, "OnlyStandaloneProgressWindow": True,
    "ProgressCompleted100": True, "WarningAvailableInResult": True, "OutputUnencrypted": True,
    "ProfileTempEncrypted": bool((profile / "Temp").stat().st_file_attributes & 0x4000)
}
(AREA / "packaged-results.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"passed": True, "output": str(output), "bytes": decoded_bytes, "sha256": result["OutputSHA256"]}, ensure_ascii=False))

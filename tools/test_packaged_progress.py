"""Pause and finish a task started by the actual registered shell COM server."""
import hashlib, json, os, pathlib, subprocess, sys, time
sys.stdout.reconfigure(encoding="utf-8")
R=pathlib.Path(__file__).resolve().parents[1]
W=pathlib.Path((R/'verification/shell-progress-latest.txt').read_text(encoding='utf-8-sig').strip())
E=pathlib.Path((R/'verification/efs-fix-latest.txt').read_text(encoding='utf-8-sig').strip())
A=W/'packaged-progress';A.mkdir()
F=A/'large-folder';F.mkdir();source=F/'payload.bin'
with source.open('wb') as out:
    for i in range(256):out.write(os.urandom(1024*1024))
archive=A/'selection.zip'
import zipfile
with zipfile.ZipFile(archive,'w') as z:z.writestr('sample.txt','fixture')
text=A/'selection.txt';text.write_text('fixture')
def run(args):
    p=subprocess.run(list(map(str,args)),capture_output=True,timeout=45)
    assert p.returncode==0,(args,p.stdout,p.stderr)
    return p.stdout.decode('utf-8-sig').strip()
proof=json.loads(run([E/'HzipShellProbeEfs.exe',R/'release/Hzip/shell/Hzip.Shell.v05r2.dll',archive,text,F,'--registered-invoke-7z']))
assert proof['registered_activation'] and proof['invoked']
items=json.loads(run(['powershell.exe','-NoProfile','-Command',"Get-CimInstance Win32_Process -Filter \"Name='Hzip.exe'\" | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress"]))
if isinstance(items,dict):items=[items]
pid=next(x['ProcessId'] for x in items if str(F) in (x['CommandLine'] or ''))
def ui(action,**kw):
    args=['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',R/'tools/ui.ps1','-TargetProcessId',pid,'-Action',action,'-WindowTitle','Hzip']
    for k,v in kw.items():args += ['-'+k,v]
    return run(args)
ui('invoke',Id='ShellPauseButton')
end=time.monotonic()+20
while True:
    controls=json.loads(ui('list'))
    if any(x['Id']=='ShellProgressState' and x['Name']=='已暂停' for x in controls):break
    assert time.monotonic()<end,controls
cpu=json.loads(run(['powershell.exe','-NoProfile','-Command',f"$w=Get-CimInstance Win32_Process -Filter 'ParentProcessId = {pid}' | Where-Object Name -eq '7z.exe' | Select-Object -First 1; if(-not $w){{throw 'No engine'}}; $a=(Get-Process -Id $w.ProcessId).TotalProcessorTime.TotalSeconds; Start-Sleep -Milliseconds 600; $b=(Get-Process -Id $w.ProcessId).TotalProcessorTime.TotalSeconds; [pscustomobject]@{{Worker=$w.ProcessId;CpuDelta=$b-$a}} | ConvertTo-Json -Compress"]))
assert cpu['CpuDelta']<=.02,cpu
ui('screenshot',OutputPath=A/'packaged-paused.png')
ui('invoke',Id='ShellPauseButton')
jobs=pathlib.Path(os.environ['LOCALAPPDATA'])/'Packages/Hzip.ContextMenu_fss7vx0rtmayp/LocalCache/Local/Hzip/ShellJobs'
end=time.monotonic()+180;report=None
while time.monotonic()<end:
    for f in jobs.glob('*.json'):
        try:v=json.loads(f.read_text(encoding='utf-8-sig'))
        except (OSError,json.JSONDecodeError):continue
        if v['ProcessId']==pid and v['Status']!='running':report=v;break
    if report:break
    time.sleep(.2)
assert report and report['Status']=='completed',report
controls=json.loads(ui('list'))
assert any(x['Id']=='ShellTotalProgress' and x['RangeValue']==100 for x in controls)
assert not any(x['Id'] in {'EntriesGrid','CreateArchiveButton','RecentArchives'} for x in controls)
ui('screenshot',OutputPath=A/'packaged-completed.png');ui('invoke',Id='ShellCancelCloseButton')
output=pathlib.Path(report['Completed'][0]['Outputs'][0])
run([R/'release/Hzip/hz.exe','extract',output,A/'decoded','--layout','here'])
with source.open('rb') as x,(A/'decoded'/F.name/source.name).open('rb') as y:assert hashlib.file_digest(x,'sha256').digest()==hashlib.file_digest(y,'sha256').digest()
result={'Failures':0,'ActualRegisteredCOM':True,'PausedCpuDeltaSeconds':cpu['CpuDelta'],'PausedMilliseconds':600,'ResumeContentHashMatches':True,'CompletionProgress':100,'MainWindowAbsent':True,'Receipt':report}
(A/'results.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'passed':True,'area':str(A)},ensure_ascii=False))

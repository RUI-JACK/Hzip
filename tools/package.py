import pathlib,zipfile,json,hashlib,shutil,sys
import xml.etree.ElementTree as ET
sys.stdout.reconfigure(encoding='utf-8')
ROOT=pathlib.Path(__file__).resolve().parents[1];APP=ROOT/'release'/'Hzip'
assert (APP/'Hzip.exe').exists() and (APP/'hz.exe').exists()
active_shell=pathlib.PureWindowsPath(ET.parse(ROOT/'src/Hzip.Shell/AppxManifest.xml').find('.//{http://schemas.microsoft.com/appx/manifest/com/windows10}Class').attrib['Path']).name
def runtime_file(p):
    return p.is_file() and not (p.parent==APP/'shell' and p.name.startswith('Hzip.Shell') and p.suffix=='.dll' and p.name!=active_shell)
licenses=APP/'licenses';licenses.mkdir(exist_ok=True)
codec=licenses/'Hzip.ZipCodec-source.zip'
with zipfile.ZipFile(codec,'w',zipfile.ZIP_DEFLATED,compresslevel=8) as z:
    for p in (ROOT/'vendor'/'sharpziplib-source'/'SharpZipLib-1.4.2'/'src'/'ICSharpCode.SharpZipLib').rglob('*.cs'):
        if 'obj' not in p.parts and 'bin' not in p.parts:z.write(p,str(p.relative_to(ROOT)))
    for p in (ROOT/'src'/'Hzip.ZipCodec').glob('*'):
        if p.is_file():z.write(p,str(p.relative_to(ROOT)))
    for name in ['Directory.Build.props','global.json','THIRD-PARTY-NOTICES.md']:z.write(ROOT/name,name)
    z.write(ROOT/'vendor'/'licenses'/'SharpZipLib-MIT.txt','LICENSE.txt')
for name in ['LICENSE.txt','ThirdPartyNotices.txt']:
    p=ROOT/'tools'/'dotnet'/name
    if p.exists():shutil.copy2(p,licenses/('dotnet-'+name))
for name in ['README.md','THIRD-PARTY-NOTICES.md']:shutil.copy2(ROOT/name,APP/name)
shutil.copy2(ROOT/'docs'/'FEATURES.md',APP/'功能对照表.md')
shutil.copy2(ROOT/'README.md',APP/'使用说明.txt')
example=APP/'examples';example.mkdir(exist_ok=True)
with zipfile.ZipFile(example/'Hzip示例.zip','w',zipfile.ZIP_DEFLATED) as z:
    z.writestr('文档/欢迎使用 Hzip.txt','Hzip 示例归档\n\n可以浏览文件夹、预览和编辑这段文字，也可以将归档解压到其他目录。\n这是程序附带的演示资料。\n')
    z.writestr('清单.csv','name,type\n欢迎使用 Hzip.txt,text\nHzip.png,image\n')
    z.write(ROOT/'src'/'Hzip.App'/'Assets'/'Hzip.png','图像/Hzip.png')
    z.writestr('空文件夹/','')
verification=ROOT/'verification'
reports={}
for label,path in [('core',verification/'core-final'/'results.json'),('integration',verification/'integration-final'/'results.json'),('zip64',verification/'core-large'/'zip64-proof.json'),('cli',verification/'cli-final'/'results.json'),('ui',verification/'ui-workflow-results.json'),('ui_release',verification/'ui-release-results.json'),('icon',verification/'icon-update-results.json'),('amsi',verification/'core-final'/'amsi-report.json')]:
    if path.exists():reports[label]=json.loads(path.read_text(encoding='utf-8-sig'))
for label,name in [('core_0_2','priority-delivery/results.json'),('modern_menu_com','shell-probe-results.json'),('modern_menu_registration','modern-menu-registration.json'),('modern_menu_workflow','modern-menu-workflow.json')]:
    path=verification/name
    if path.exists():reports[label]=json.loads(path.read_text(encoding='utf-8-sig'))
for label,name in [('core_0_3','release-0.3-final/results.json'),('native_pause_0_3','release-0.3-final/native-pause-proof.json'),('ui_0_3','ui-0.3-results.json'),('ui_0_3_1','ui-0.3.1-results.json')]:
    path=verification/name
    if path.exists():reports[label]=json.loads(path.read_text(encoding='utf-8-sig'))
for label,name in [('core_0_4','release-0.4-final/results.json'),('native_pause_0_4','release-0.4-final/native-pause-proof.json'),('ui_0_4','ui-0.4-release-final/results.json'),('published_ui_0_4','ui-0.4-release-final/published-launch.json')]:
    path=verification/name
    if path.exists():reports[label]=json.loads(path.read_text(encoding='utf-8-sig'))
menu_area=None
latest=verification/'menu-0.5-latest.txt'
if latest.exists():
    menu_area=verification/pathlib.PureWindowsPath(latest.read_text(encoding='utf-8-sig').strip()).name
    if not menu_area.exists():menu_area=None
new_reports=[('core_0_5',verification/'release-0.5-final/results.json')]
if menu_area:new_reports += [('context_menu_0_5',menu_area/'results.json'),('context_menu_ui_0_5',menu_area/'ui-results.json'),('menu_registration_0_5',menu_area/'registration.json')]
for label,path in new_reports:
    if path.exists():reports[label]=json.loads(path.read_text(encoding='utf-8-sig'))
audit_area=None
audit_latest=verification/'audit-latest.txt'
if audit_latest.exists():
    candidate=verification/pathlib.PureWindowsPath(audit_latest.read_text(encoding='utf-8-sig').strip()).name
    if (candidate/'results.json').exists():
        audit_area=candidate
        reports['overall_audit']=json.loads((candidate/'results.json').read_text(encoding='utf-8-sig'))
if (ROOT/'docs/CURRENT-AUDIT.md').exists():shutil.copy2(ROOT/'docs/CURRENT-AUDIT.md',APP/'CURRENT-AUDIT.md')
background_area=None
shell_ui_area=None
for pointer in ['shell-background-latest.txt','shell-ui-latest.txt']:
    path=verification/pointer
    if path.exists():
        candidate=verification/pathlib.PureWindowsPath(path.read_text(encoding='utf-8-sig').strip()).name
        if (candidate/'results.json').exists():
            if pointer=='shell-background-latest.txt':background_area=candidate
            else:shell_ui_area=candidate
if background_area:reports['shell_background_update']=json.loads((background_area/'results.json').read_text(encoding='utf-8-sig'))
if (ROOT/'docs/SHELL-BACKGROUND.md').exists():shutil.copy2(ROOT/'docs/SHELL-BACKGROUND.md',APP/'SHELL-BACKGROUND.md')
efs_area=None
packaged_efs_area=None
for pointer in ['efs-fix-latest.txt','packaged-efs-latest.txt']:
    path=verification/pointer
    if path.exists():
        relative=pathlib.PureWindowsPath(path.read_text(encoding='utf-8-sig').strip())
        candidate=verification/relative.name if pointer=='efs-fix-latest.txt' else verification/relative.parent.name/relative.name
        if (candidate/'results.json').exists():
            if pointer=='efs-fix-latest.txt':efs_area=candidate
            else:packaged_efs_area=candidate
if efs_area:reports['efs_fix']=json.loads((efs_area/'results.json').read_text(encoding='utf-8-sig'))
if (ROOT/'docs/EFS-FIX.md').exists():shutil.copy2(ROOT/'docs/EFS-FIX.md',APP/'EFS-FIX.md')
progress_area=None
progress_ui_area=None
for pointer in ['shell-progress-latest.txt','shell-progress-ui-latest.txt']:
    path=verification/pointer
    if path.exists():
        relative=pathlib.PureWindowsPath(path.read_text(encoding='utf-8-sig').strip())
        candidate=verification/relative.name if pointer=='shell-progress-latest.txt' else verification/relative.parent.name/relative.name
        if (candidate/'results.json').exists():
            if pointer=='shell-progress-latest.txt':progress_area=candidate
            else:progress_ui_area=candidate
if progress_area:reports['standalone_progress']=json.loads((progress_area/'results.json').read_text(encoding='utf-8-sig'))
if (ROOT/'docs/SHELL-PROGRESS.md').exists():shutil.copy2(ROOT/'docs/SHELL-PROGRESS.md',APP/'SHELL-PROGRESS.md')
gzip_area=None
gzip_latest=verification/'gzip-tail-latest.txt'
if gzip_latest.exists():
    candidate=verification/pathlib.PureWindowsPath(gzip_latest.read_text(encoding='utf-8-sig').strip()).name
    if (candidate/'results.json').exists():
        gzip_area=candidate
        reports['gzip_tail_fix']=json.loads((candidate/'results.json').read_text(encoding='utf-8-sig'))
if (ROOT/'docs/GZIP-TAIL.md').exists():shutil.copy2(ROOT/'docs/GZIP-TAIL.md',APP/'GZIP-TAIL.md')
conflict_area=None
conflict_ui_area=None
for pointer in ['conflict-latest.txt','conflict-ui-latest.txt']:
    path=verification/pointer
    if path.exists():
        relative=pathlib.PureWindowsPath(path.read_text(encoding='utf-8-sig').strip())
        candidate=verification/relative.name if pointer=='conflict-latest.txt' else verification/relative.parent.name/relative.name
        if (candidate/'results.json').exists():
            if pointer=='conflict-latest.txt':conflict_area=candidate
            else:conflict_ui_area=candidate
if conflict_area:reports['extraction_conflicts']=json.loads((conflict_area/'results.json').read_text(encoding='utf-8-sig'))
if (ROOT/'docs/EXTRACTION-CONFLICTS.md').exists():shutil.copy2(ROOT/'docs/EXTRACTION-CONFLICTS.md',APP/'EXTRACTION-CONFLICTS.md')
for name in ['WINDOWS11-MENU.md','VERIFICATION.md','PAUSE-AND-REPAIR.md','DAILY-WORKFLOWS.md','UPDATES.md']:
    shutil.copy2(ROOT/'docs'/name,APP/name)
manifest=[]
for p in sorted(APP.rglob('*')):
    if runtime_file(p):manifest.append({'path':str(p.relative_to(APP)).replace('\\','/'),'bytes':p.stat().st_size,'sha256':hashlib.file_digest(p.open('rb'),'sha256').hexdigest()})
(verification/'release-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
(verification/'verification-summary.json').write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf-8')
if '--prepare' in sys.argv:
    print('Runtime files, licenses and sample archive prepared',flush=True)
    raise SystemExit(0)
appzip=ROOT.parent/'Hzip-Windows-x64.zip'
print('Packaging standalone application',flush=True)
with zipfile.ZipFile(appzip,'w',zipfile.ZIP_DEFLATED,compresslevel=8) as z:
    for p in sorted(APP.rglob('*')):
        if runtime_file(p):z.write(p,'Hzip/'+str(p.relative_to(APP)))
sourcezip=ROOT.parent/'Hzip-source.zip'
print('Packaging source',flush=True)
with zipfile.ZipFile(sourcezip,'w',zipfile.ZIP_DEFLATED,compresslevel=8) as z:
    for name in ['Directory.Build.props','global.json','NuGet.Config','Hzip.sln','build.ps1','README.md','LICENSE.txt','THIRD-PARTY-NOTICES.md','.gitignore']:z.write(ROOT/name,'Hzip/'+name)
    for folder in ['src','tests','docs','vendor','installer']:
        for p in sorted((ROOT/folder).rglob('*')):
            if p.is_file() and not any(part in ['bin','obj','.git','.vs'] for part in p.relative_to(ROOT).parts):z.write(p,'Hzip/'+str(p.relative_to(ROOT)))
    for p in (ROOT/'tools').glob('*'):
        if p.is_file() and p.suffix in ['.py','.ps1','.json']:z.write(p,'Hzip/'+str(p.relative_to(ROOT)))
    for name in ['verification-summary.json','release-manifest.json','ui-workflow-results.json','ui-release-results.json','icon-update-results.json','cli-final/results.json','core-final/results.json','core-large/zip64-proof.json','integration-final/results.json','core-final/amsi-report.json']:
        p=verification/name
        if p.exists():z.write(p,'Hzip/verification/'+name)
    for name in ['priority-delivery/results.json','shell-probe-results.json','modern-menu-registration.json','modern-menu-workflow.json','release-0.3-final/results.json','release-0.3-final/native-pause-proof.json','ui-0.3-results.json','ui-0.3.1-results.json','release-0.4-final/results.json','release-0.4-final/native-pause-proof.json','ui-0.4-release-final/results.json','ui-0.4-release-final/published-launch.json','ui-0.4-release-final/create-vault.png','ui-0.4-release-final/legacy-preview.png','ui-0.4-release-final/external-pending.png','ui-0.4-release-final/external-saved.png','release-0.3-final/repair-plus/partial.recovered.zip.repair.json','release-0.3-final/repair-plus/aes-v2.recovered.zip.repair.json']:
        p=verification/name
        if p.exists():z.write(p,'Hzip/verification/'+name)
    for p in [verification/'release-0.5-final/results.json',verification/'release-0.5-final/native-pause-proof.json',verification/'menu-0.5-latest.txt']+([menu_area/'results.json',menu_area/'ui-results.json',menu_area/'registration.json'] if menu_area else []):
        if p.exists():z.write(p,'Hzip/verification/'+str(p.relative_to(verification)).replace('\\','/'))
    if audit_area:
        for p in [audit_latest]+[audit_area/name for name in ['results.json','core-final/results.json','core-final/native-pause-proof.json','core-final/zip64-proof.json','core-final/amsi-report.json','cli-results.json','split-results.json','ui-published/results.json','runtime-evidence.json','shortcut.json','published-home.png','published-archive.png']]:
            if p.exists():z.write(p,'Hzip/verification/'+str(p.relative_to(verification)).replace('\\','/'))
    background_files=[]
    if background_area:background_files += [verification/'shell-background-latest.txt',background_area/'results.json',background_area/'ui-regression/results.json',background_area/'deployed-files.json']
    if shell_ui_area:background_files += [verification/'shell-ui-latest.txt',shell_ui_area/'results.json',shell_ui_area/'create-settings-only.png',shell_ui_area/'extract-settings-only.png']
    for p in background_files:
        if p.exists():z.write(p,'Hzip/verification/'+str(p.relative_to(verification)).replace('\\','/'))
    efs_files=[]
    if efs_area:efs_files += [verification/'efs-fix-latest.txt']+[efs_area/name for name in ['results.json','core-final/results.json','large/results.json','large/zip64-proof.json','ui-regression/results.json','original-location-results.json','deployed-files.json']]
    if packaged_efs_area:efs_files += [verification/'packaged-efs-latest.txt',packaged_efs_area/'results.json']
    for p in efs_files:
        if p.exists():z.write(p,'Hzip/verification/'+str(p.relative_to(verification)).replace('\\','/'))
    progress_files=[]
    if progress_area:progress_files += [verification/'shell-progress-latest.txt']+[progress_area/name for name in ['results.json','ui-regression/results.json','packaged-progress/results.json','packaged-progress/packaged-paused.png','packaged-progress/packaged-completed.png']]
    if progress_ui_area:progress_files += [verification/'shell-progress-ui-latest.txt']+[progress_ui_area/name for name in ['results.json','resume-pause.json','cancel-pause.json','extraction-completed.png','compression-completed.png','paused.png','cancelled-result.png','failure-result.png']]
    for p in progress_files:
        if p.exists():z.write(p,'Hzip/verification/'+str(p.relative_to(verification)).replace('\\','/'))
    if gzip_area:
        for p in [gzip_latest]+[gzip_area/name for name in ['results.json','analysis.json','core-final-current/results.json','core-final-current/native-pause-proof.json','ui-regression/results.json','gui-test-warning.json','packaged-results.json','packaged-controls.json','original-extraction-completed.png','gui-test-warning.png','deployed-files.json']]:
            if p.exists():z.write(p,'Hzip/verification/'+str(p.relative_to(verification)).replace('\\','/'))
    conflict_files=[]
    if conflict_area:conflict_files += [verification/'conflict-latest.txt']+[conflict_area/name for name in ['results.json','core-current/results.json','core-current/native-pause-proof.json','ui-regression/results.json','deployed-files.json','cli-results.json']]
    if conflict_ui_area:conflict_files += [verification/'conflict-ui-latest.txt']+[conflict_ui_area/name for name in ['results.json','registered-results.json','file-comparison.png','folder-comparison.png','equal-metadata.png','main-conflict.png','registered-conflict.png']]
    for p in conflict_files:
        if p.exists():z.write(p,'Hzip/verification/'+str(p.relative_to(verification)).replace('\\','/'))
    for name in ['home.png','archive-root.png','search.png','text-preview.png','extract.png','final-home.png','final-archive.png','final-compact.png','image-preview.png','unsaved-prompt.png','icon-home.png','icon-settings.png','icon-executable.png','priority-settings.png','priority-home.png','pause-0.3.png','pause-jobs-0.3.png','repair-dialog-0.3.png','repair-result-0.3.png','toolbox-0.3.1.png','home-0.4.png']:
        p=verification/'screenshots'/name
        if p.exists():z.write(p,'Hzip/verification/screenshots/'+name)
for p in [appzip,sourcezip,codec]:
    with zipfile.ZipFile(p) as z:assert z.testzip() is None
print(json.dumps({'application':{'path':str(appzip),'bytes':appzip.stat().st_size},'source':{'path':str(sourcezip),'bytes':sourcezip.stat().st_size},'application_files':len(manifest)},ensure_ascii=True))

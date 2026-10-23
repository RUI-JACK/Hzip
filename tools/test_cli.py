import pathlib,subprocess,os,json,hashlib,shutil,sys,time,zipfile
sys.stdout.reconfigure(encoding='utf-8')
ROOT=pathlib.Path(__file__).resolve().parents[1]
OUT=ROOT/'verification'/'cli-final';OUT.mkdir(exist_ok=True)
APP=ROOT/'release'/'Hzip'
env=os.environ.copy();env.pop('HZIP_ENGINE',None)
env['HZIP_DATA_DIR']=str(OUT/'profile');env['DOTNET_ROOT']=str(OUT/'missing-dotnet');env['DOTNET_ROOT_X64']=env['DOTNET_ROOT'];env['DOTNET_MULTILEVEL_LOOKUP']='0'
exe=APP/'hz.exe';results=[]
def run(args,expected=0):
    proc=subprocess.run([str(exe)]+list(map(str,args)),capture_output=True,timeout=45,env=env,cwd=OUT)
    assert proc.returncode==expected,(args,proc.returncode,proc.stdout.decode('utf-8',errors='replace'),proc.stderr.decode('utf-8',errors='replace'))
    return proc.stdout.decode('utf-8-sig')
def check(name,fn):
    try:fn();results.append({'name':name,'passed':True});print('PASS',name,flush=True)
    except BaseException as err:results.append({'name':name,'passed':False,'error':str(err)});print('FAIL',name,str(err),flush=True)
def sha(p):return hashlib.file_digest(pathlib.Path(p).open('rb'),'sha256').hexdigest()
source=OUT/'资料';source.mkdir(exist_ok=True);text=source/'hello.txt';text.write_text('Hzip portable CLI verification. 中文文件。\n',encoding='utf-8')
zip_path=OUT/'cli.zip'
check('standalone runtime + create',lambda:run(['create',zip_path,source,'--format','zip','--replace']))
def listing():
    data=json.loads(run(['list',zip_path,'--json']));assert data['Format']=='zip' and any(e['Path'].endswith('hello.txt') for e in data['Entries'])
check('JSON listing',listing)
def extraction():
    run(['extract',zip_path,OUT/'extracted','--layout','here','--overwrite','replace']);assert sha(text)==sha(OUT/'extracted'/'资料'/'hello.txt')
check('extract and hash',extraction)
check('integrity test',lambda:run(['test',zip_path]))
def invalid_option():
    before=sha(zip_path);run(['delete',zip_path,'资料/hello.txt','--dry-run=true'],2);assert sha(zip_path)==before
check('unknown option cannot mutate archive',invalid_option)
def boolean_false():
    before=sha(zip_path);run(['create',zip_path,text,'--replace=false'],2);assert sha(zip_path)==before
check('replace=false preserves archive',boolean_false)
def encryption():
    target=OUT/'protected.zip';run(['create',target,text,'--replace','--password','验收密码','--password-encoding','936']);run(['test',target,'--password','验收密码']);run(['test',target,'--password','wrong'],2)
check('Unicode encrypted ZIP CLI',encryption)
check('missing mutation inputs rejected',lambda:run(['delete',zip_path],2))
repair_root=OUT/('repair-'+str(time.time_ns()));repair_root.mkdir()
repair_source=repair_root/'damaged-aes.zip'
original_fixture=ROOT/'tests'/'fixtures'/'compatibility'/'pyzipper-aes-lzma.zip'
with zipfile.ZipFile(original_fixture) as original_archive:central_offset=original_archive.start_dir
repair_source.write_bytes(original_fixture.read_bytes()[:central_offset])
def encrypted_repair():
    target=repair_root/'repaired.zip';before=sha(repair_source)
    data=json.loads(run(['repair',repair_source,target,'--password','中文 AES 兼容密码','--json']))
    assert data['Recovered']==1 and pathlib.Path(data['ReportPath']).exists() and sha(repair_source)==before
    with zipfile.ZipFile(target) as archive:assert 'Hzip independent ZIP compatibility'.encode() in archive.read('独立样本.txt')
check('encrypted ZIP repair plus JSON recovery receipt',encrypted_repair)
def bad_repair_password():
    target=repair_root/'wrong.zip';run(['repair',repair_source,target,'--password','wrong'],2);assert not target.exists()
check('ZIP repair rejects incorrect password without final output',bad_repair_password)
def repair_output_protection():
    target=repair_root/'protected.zip';target.write_text('keep',encoding='utf-8')
    run(['repair',repair_source,target,'--password','中文 AES 兼容密码'],2);assert target.read_text(encoding='utf-8')=='keep'
check('ZIP repair preserves an existing output',repair_output_protection)
config=json.loads((APP/'hz.runtimeconfig.json').read_text());assert config['runtimeOptions'].get('includedFrameworks') and not config['runtimeOptions'].get('framework')
report={'results':results,'self_contained_runtime':config['runtimeOptions']['includedFrameworks'],'dotnet_root_deliberately_missing':True,'engine_copied':(APP/'engine'/'7z.exe').exists()}
(OUT/'results.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
assert all(r['passed'] for r in results)

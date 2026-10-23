import pathlib, urllib.request, json, hashlib, zipfile, concurrent.futures, subprocess, sys
sys.stdout.reconfigure(encoding='utf-8')
ROOT=pathlib.Path(__file__).resolve().parents[1]
def fetch(url,path,hash_value=None,algorithm='sha256'):
    path=pathlib.Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    if not path.exists():
        print('Download',url,flush=True)
        urllib.request.urlretrieve(url,path)
    actual=hashlib.file_digest(path.open('rb'),algorithm).hexdigest()
    if hash_value and actual.lower()!=hash_value.lower():raise RuntimeError('Digest mismatch '+str(path))
    return {'url':url,'filename':path.name,'bytes':path.stat().st_size,algorithm:actual}
def sdk():
    meta=json.load(urllib.request.urlopen('https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json'))
    release=next(r for r in meta['releases'] if r['sdk']['version']=='8.0.426')
    file=next(f for f in release['sdk']['files'] if f['rid']=='win-x64' and f['name'].endswith('.zip'))
    path=ROOT/'tools'/'dotnet-sdk.zip'; evidence=fetch(file['url'],path,file['hash'],'sha512')
    dest=ROOT/'tools'/'dotnet'
    if not (dest/'dotnet.exe').exists():
        with zipfile.ZipFile(path) as z:z.extractall(dest)
    (ROOT/'tools'/'dotnet-provenance.json').write_text(json.dumps(evidence,indent=2),encoding='utf-8')
    print('SDK ready',flush=True)
def engine():
    url='https://api.github.com/repos/ip7z/7zip/releases/tags/26.04'
    release=json.load(urllib.request.urlopen(url));assets={a['name']:a for a in release['assets']}
    evidence=[]
    for name in ['7z2604-x64.exe','7zr.exe','7z2604-src.tar.xz']:
        asset=assets[name];digest=asset.get('digest','').removeprefix('sha256:') or None
        evidence.append(fetch(asset['browser_download_url'],ROOT/'tools'/name,digest))
    out=ROOT/'vendor'/'7zip';out.mkdir(parents=True,exist_ok=True)
    result=subprocess.run([str(ROOT/'tools'/'7zr.exe'),'x',str(ROOT/'tools'/'7z2604-x64.exe'),'-o'+str(out),'-y'],capture_output=True)
    print('Engine extraction',result.returncode,result.stdout.decode(errors='replace')[-300:],flush=True)
    if not (out/'7z.exe').exists():raise RuntimeError('Full 7-Zip engine not extracted')
    for name in ['License.txt','readme.txt','7-zip.chm']:
        if (out/name).exists():print('Included',name,flush=True)
    licenses=ROOT/'vendor'/'licenses';licenses.mkdir(exist_ok=True)
    fetch('https://www.7-zip.org/license.txt',licenses/'7zip-license.txt')
    import shutil
    shutil.copy2(ROOT/'tools'/'7z2604-src.tar.xz',licenses/'7z2604-src.tar.xz')
    (ROOT/'vendor'/'7zip-provenance.json').write_text(json.dumps({'release':release['html_url'],'downloads':evidence},indent=2),encoding='utf-8')
    print('Engine ready',flush=True)
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    tasks=[pool.submit(sdk),pool.submit(engine)]
    for t in tasks:t.result()

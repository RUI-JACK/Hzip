"""Fetch pinned official Windows SDK packaging tools (build-time dependency only)."""
from pathlib import Path
import hashlib, urllib.request, zipfile

root = Path(__file__).resolve().parent
url = 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/10.0.26100.7705/microsoft.windows.sdk.buildtools.10.0.26100.7705.nupkg'
expected = '48a81375752f9f1ff56a34062084b426bfe412a5a8072e1c99b6a4be0e774841'
package = root / 'windows-sdk-buildtools.nupkg'
if not package.exists():
    urllib.request.urlretrieve(url, package)
assert hashlib.file_digest(package.open('rb'), 'sha256').hexdigest() == expected, 'SDK package hash mismatch'
with zipfile.ZipFile(package) as archive:
    for name in archive.namelist():
        if name.startswith('bin/10.0.26100.0/x64/') and '..' not in Path(name).parts:
            archive.extract(name, root / 'windows-sdk-buildtools')
print('Official Windows SDK build tools ready; SHA-256 verified')

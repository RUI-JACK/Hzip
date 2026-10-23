"""Own ZIP fixtures from Python and PyZipper; AES samples use random salts.

To regenerate AES samples: pip install --target tools/python-deps pyzipper==0.3.6
"""
from pathlib import Path
import zipfile, json, hashlib, struct, zlib, sys

root = Path(__file__).resolve().parents[1] / 'tests' / 'fixtures' / 'compatibility'
root.mkdir(exist_ok=True)
payload = ('Hzip independent ZIP compatibility / 中文 / 日本語 / 😀\n' * 40).encode()
proof = []
for label, method in [('store', zipfile.ZIP_STORED), ('deflate', zipfile.ZIP_DEFLATED), ('bzip2', zipfile.ZIP_BZIP2), ('lzma', zipfile.ZIP_LZMA)]:
    path = root / f'python-{label}.zip'
    with zipfile.ZipFile(path, 'w', compression=method) as archive:
        entry = zipfile.ZipInfo('中文目录/日本語 😀.txt', date_time=(2026, 1, 1, 0, 0, 0))
        entry.compress_type = method
        archive.writestr(entry, payload)
    proof.append({'archive': path.name, 'entry': entry.filename, 'sha256': hashlib.sha256(payload).hexdigest(), 'archive_sha256': hashlib.sha256(path.read_bytes()).hexdigest()})
# An old ZIP filename without the UTF-8 flag, in the original ZIP OEM encoding.
name = 'café.txt'.encode('cp437'); crc = zlib.crc32(payload)
local = struct.pack('<I5H3I2H', 0x04034B50, 20, 0, 0, 0, 0, crc, len(payload), len(payload), len(name), 0) + name + payload
central = struct.pack('<I6H3I5H2I', 0x02014B50, 20, 20, 0, 0, 0, 0, crc, len(payload), len(payload), len(name), 0, 0, 0, 0, 0, 0) + name
path = root / 'oem437.zip'; path.write_bytes(local + central + struct.pack('<I4H2IH', 0x06054B50, 0, 0, 1, 1, len(central), len(local), 0))
proof.append({'archive': path.name, 'entry': 'café.txt', 'code_page': 437, 'sha256': hashlib.sha256(payload).hexdigest(), 'archive_sha256': hashlib.sha256(path.read_bytes()).hexdigest()})
(root / 'expected.json').write_text(json.dumps(proof, ensure_ascii=False, indent=2), encoding='utf-8')
sys.path.insert(0, str(Path(__file__).resolve().parent / 'python-deps'))
import pyzipper
for label, method in [('bzip2', zipfile.ZIP_BZIP2), ('lzma', zipfile.ZIP_LZMA)]:
    with pyzipper.AESZipFile(str(root / f'pyzipper-aes-{label}.zip'), 'w', compression=method, encryption=pyzipper.WZ_AES) as archive:
        archive.setpassword('中文 AES 兼容密码'.encode('utf-8'))
        archive.writestr('独立样本.txt', payload)
print(f'Generated {len(proof)} independent compatibility fixtures')

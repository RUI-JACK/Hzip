"""Render the supplied SVG as-is, then create transparent multi-size Windows icons."""
from pathlib import Path
import subprocess
from PIL import Image

root = Path(__file__).resolve().parents[1]
assets = root / 'src' / 'Hzip.App' / 'Assets'
subprocess.run([
    'powershell.exe', '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass',
    '-File', str(root / 'tools' / 'render_icon.ps1'),
    '-SvgPath', str(assets / 'Hzip.svg'), '-PngPath', str(assets / 'Hzip.png'),
], check=True, creationflags=subprocess.CREATE_NO_WINDOW)
with Image.open(assets / 'Hzip.png') as rendered:
    rendered.convert('RGBA').save(
        assets / 'Hzip.ico',
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40),
               (48, 48), (64, 64), (96, 96), (128, 128), (256, 256)],
    )
print(assets / 'Hzip.ico')

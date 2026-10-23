param([string]$OutputDirectory=(Join-Path (Split-Path $PSScriptRoot) 'release\Hzip'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$compiler=if($env:HZIP_CXX){$env:HZIP_CXX}else{'C:\Program Files\Windhawk\Compiler\bin\clang++.exe'}
if(-not(Test-Path -LiteralPath $compiler)){throw '需要支持 MinGW-w64 的 clang++；请设置 HZIP_CXX。'}
$sdk=Join-Path $PSScriptRoot 'windows-sdk-buildtools\bin\10.0.26100.0\x64'
$shell=Join-Path $OutputDirectory 'shell'
New-Item -ItemType Directory -Force -Path $shell | Out-Null
$dll=Join-Path $shell 'Hzip.Shell.v05r2.dll'
$latestSource=@((Join-Path $root 'src\Hzip.Shell\Hzip.Shell.cpp'),(Join-Path $root 'src\Hzip.Shell\Hzip.Shell.def')) | Get-Item | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if(-not(Test-Path -LiteralPath $dll) -or (Get-Item -LiteralPath $dll).LastWriteTimeUtc -lt $latestSource.LastWriteTimeUtc){
 & $compiler -std=c++17 -shared -O2 -static -static-libgcc -static-libstdc++ '-Wl,--no-insert-timestamp' (Join-Path $root 'src\Hzip.Shell\Hzip.Shell.cpp') (Join-Path $root 'src\Hzip.Shell\Hzip.Shell.def') -o $dll -lole32 -lshell32 -lshlwapi -luuid
 if($LASTEXITCODE -ne 0){throw 'Shell extension build failed'}
}
$packageRoot=Join-Path $root 'verification\shell-package-staging'
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot 'Assets') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src\Hzip.Shell\AppxManifest.xml') -Destination $packageRoot -Force
python -c "from PIL import Image;from pathlib import Path;p=Path(r'$packageRoot')/'Assets';im=Image.open(r'$root\src\Hzip.App\Assets\Hzip.png');[(im.resize((s,s),Image.Resampling.LANCZOS).save(p/n)) for s,n in [(50,'StoreLogo.png'),(150,'Logo150.png'),(44,'Logo44.png')]]"
$msix=Join-Path $shell 'Hzip.ContextMenu.msix'
& (Join-Path $sdk 'makeappx.exe') pack /o /d $packageRoot /nv /p $msix | Out-Null
if($LASTEXITCODE -ne 0){throw 'Context menu package build failed'}
$subject='CN=Hzip Local Development'
$certificate=Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object {$_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(30)} | Sort-Object NotAfter -Descending | Select-Object -First 1
if(-not $certificate){$certificate=New-SelfSignedCertificate -Subject $subject -Type CodeSigningCert -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm SHA256 -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2)}
& (Join-Path $sdk 'signtool.exe') sign /fd SHA256 /s My /sha1 $certificate.Thumbprint $msix | Out-Null
if($LASTEXITCODE -ne 0){throw 'Context menu package signing failed'}
Export-Certificate -Cert $certificate -FilePath (Join-Path $shell 'Hzip.ContextMenu.cer') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src\Hzip.Shell\manage_menu.ps1') -Destination $shell -Force
Write-Output "Native shell extension ready: $shell"

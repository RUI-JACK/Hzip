param([string]$RuntimeDirectory=(Join-Path (Split-Path $PSScriptRoot) 'release\Hzip'),[string]$OutputDirectory=(Join-Path (Split-Path $PSScriptRoot) 'release\installer'),[string]$VerificationDirectory='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$area=if ($VerificationDirectory) { [System.IO.Path]::GetFullPath($VerificationDirectory) } else { Join-Path $root ('verification\installer-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Path $area -ErrorAction Stop | Out-Null
Set-Content -LiteralPath (Join-Path $root 'verification\installer-latest.txt') -Value $area -Encoding utf8
$snapshot=Get-ChildItem -LiteralPath (Join-Path $root 'src\Hzip.App') -File | ForEach-Object { [pscustomobject]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash} }
$snapshot | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $area 'beautified-input.json') -Encoding utf8
$compiler=Join-Path $root 'tools\inno-7.1.0\ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) { & (Join-Path $PSScriptRoot 'bootstrap_installer.ps1'); if ($LASTEXITCODE -ne 0) { throw '无法准备安装编译器。' } }
$signature=Get-AuthenticodeSignature -LiteralPath $compiler
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') { throw '安装编译器签名校验失败。' }
$payload=Join-Path $area 'payload'
if (Test-Path -LiteralPath $payload) { throw '安装载荷目录已存在；请使用新的验证目录，避免混入旧文件。' }
Copy-Item -LiteralPath $RuntimeDirectory -Destination $payload -Recurse
# Only ship the current native extension; older DLLs may remain mapped in Explorer.
$activeDll='Hzip.Shell.v05r2.dll'
Get-ChildItem -LiteralPath (Join-Path $payload 'shell') -File -Filter 'Hzip.Shell*.dll' | Where-Object { $_.Name -ne $activeDll } | ForEach-Object { [System.IO.File]::Delete($_.FullName) }
$helperOut=Join-Path $area 'helper-build'
& (Join-Path $root 'tools\dotnet\dotnet.exe') publish (Join-Path $root 'installer\Hzip.SetupHelper\Hzip.SetupHelper.csproj') -c Release -r win-x64 --self-contained true -o $helperOut -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw '安装辅助组件编译失败。' }
foreach ($name in @('Hzip.SetupHelper.exe','Hzip.SetupHelper.dll','Hzip.SetupHelper.deps.json','Hzip.SetupHelper.runtimeconfig.json')) { Copy-Item -LiteralPath (Join-Path $helperOut $name) -Destination (Join-Path $payload $name) }
$manifest=Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object { [pscustomobject]@{Path=[System.IO.Path]::GetRelativePath($payload,$_.FullName);Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash} }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $area 'payload-manifest.json') -Encoding utf8
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& $compiler '/Qp' "/DPayloadDir=$payload" "/DOutputPath=$OutputDirectory" (Join-Path $root 'installer\Hzip.iss')
if ($LASTEXITCODE -ne 0) { throw '安装包编译失败。' }
Get-ChildItem -LiteralPath $OutputDirectory -File -Filter 'Hzip-Setup-*-x64.exe' | Select-Object FullName,Length

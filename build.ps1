param([switch]$FrameworkDependent,[switch]$SkipTests)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$sdk=Join-Path $root 'tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk=(Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'
$out=Join-Path $root 'release\Hzip'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$selfContained=if ($FrameworkDependent) { 'false' } else { 'true' }
foreach ($project in @('Hzip.App','Hzip.Cli')) {
 & $sdk publish (Join-Path $root "src\$project\$project.csproj") -c Release -r win-x64 --self-contained $selfContained -o $out -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false --nologo -v:minimal
 if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
foreach ($icon in @('Hzip.ico','Hzip.svg','Hzip.png')) { Copy-Item -LiteralPath (Join-Path $root "src\Hzip.App\Assets\$icon") -Destination $out -Force }
$engine=Join-Path $out 'engine'
New-Item -ItemType Directory -Force -Path $engine | Out-Null
foreach ($file in @('7z.exe','7z.dll','7z.sfx','7zCon.sfx','License.txt','History.txt','readme.txt')) { Copy-Item -LiteralPath (Join-Path $root "vendor\7zip\$file") -Destination $engine -Force }
$licenses=Join-Path $out 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -Path (Join-Path $root 'vendor\licenses\*') -Destination $licenses -Force
Copy-Item -LiteralPath (Join-Path $root 'vendor\7zip-provenance.json'),(Join-Path $root 'vendor\sharpziplib-provenance.json') -Destination $licenses -Force
foreach ($name in @('README.md','LICENSE.txt','THIRD-PARTY-NOTICES.md')) { $file=Join-Path $root $name;if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $out $(if ($name -eq 'LICENSE.txt') { 'Hzip-LICENSE.txt' } else { $name })) -Force } }
if (Test-Path -LiteralPath (Join-Path $root 'docs\FEATURES.md')) { Copy-Item -LiteralPath (Join-Path $root 'docs\FEATURES.md') -Destination (Join-Path $out '功能对照表.md') -Force }
if (-not (Test-Path -LiteralPath (Join-Path $root 'tools\windows-sdk-buildtools\bin\10.0.26100.0\x64\makeappx.exe'))) {
 & python (Join-Path $root 'tools\bootstrap_shell.py')
 if ($LASTEXITCODE -ne 0) { throw 'Windows SDK bootstrap failed' }
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\build_shell.ps1') -OutputDirectory $out
if ($LASTEXITCODE -ne 0) { throw 'Native context menu build failed' }
if (-not $SkipTests) {
 & $sdk run --project (Join-Path $root 'tests\Hzip.Tests.csproj') -c Release -- (Join-Path $root ('verification\build-'+(Get-Date -Format 'yyyyMMdd-HHmmss')))
 if ($LASTEXITCODE -ne 0) { throw 'Core verification failed' }
}
Write-Output "Ready: $out\Hzip.exe"

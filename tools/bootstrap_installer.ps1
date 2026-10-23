$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$compiler=Join-Path $PSScriptRoot 'inno-7.1.0\ISCC.exe'
if (Test-Path -LiteralPath $compiler) {
 $signature=Get-AuthenticodeSignature -LiteralPath $compiler
 if ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Subject -like '*Pyrsys B.V.*') { Write-Output $compiler; exit 0 }
 throw '已有安装编译器的签名无效，请检查该目录。'
}
$cache=Join-Path $PSScriptRoot 'installer-cache'; New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive=Join-Path $cache 'tools.innosetup.7.1.0.nupkg'
$url='https://api.nuget.org/v3-flatcontainer/tools.innosetup/7.1.0/tools.innosetup.7.1.0.nupkg'
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $url -OutFile $archive }
$expected='AAD15C662593C6F5656457A5C001F4591BABD72820CACF63F9706D4AF5D93B75'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw '编译器归档 SHA-256 与固定版本不符。' }
& (Join-Path $root 'tools\dotnet\dotnet.exe') nuget verify $archive --all --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw '编译器归档的 NuGet 仓库签名校验失败。' }
$destination=Join-Path $PSScriptRoot 'inno-7.1.0'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[System.IO.Compression.ZipFile]::OpenRead($archive)
try {
 foreach ($entry in $zip.Entries) {
  if (-not $entry.FullName.StartsWith('tools/') -or $entry.FullName.EndsWith('/')) { continue }
  $target=[System.IO.Path]::GetFullPath((Join-Path $destination $entry.FullName.Substring(6)))
  if (-not $target.StartsWith([System.IO.Path]::GetFullPath($destination)+'\',[System.StringComparison]::OrdinalIgnoreCase)) { throw '编译器归档包含越界路径。' }
  [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
  [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$false)
 }
} finally { $zip.Dispose() }
$signature=Get-AuthenticodeSignature -LiteralPath $compiler
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') { throw '编译器发布者签名校验失败。' }
[pscustomobject]@{URL=$url;PackageSHA256=$expected;Publisher=$signature.SignerCertificate.Subject;CompilerVersion='7.1.0';Method='Archive extraction; no compiler installer executed'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'inno-7.1.0-provenance.json') -Encoding utf8
Write-Output $compiler

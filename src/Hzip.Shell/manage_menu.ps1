param([ValidateSet('Install','Remove','Status')][string]$Action='Status')
$ErrorActionPreference='Stop'
$packageName='Hzip.ContextMenu'
$root=Split-Path $PSScriptRoot
$stateDirectory=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Hzip'
$receiptPath=Join-Path $stateDirectory 'modern-menu.json'
$packages=@(Get-AppxPackage -Name $packageName)
if($Action -eq 'Status') {
 [pscustomobject]@{Installed=($packages.Count -gt 0);Package=@($packages.PackageFullName);Receipt=if(Test-Path -LiteralPath $receiptPath){Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json}else{$null}} | ConvertTo-Json -Depth 5
 exit
}
if($Action -eq 'Remove') {
 foreach($package in $packages){Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop}
 if(Test-Path -LiteralPath $receiptPath){
  $receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
  if($receipt.ImportedTrust -eq $true -and $receipt.Thumbprint -match '^[0-9A-F]{40}$') {
   $certificate=Get-Item -LiteralPath (Join-Path 'Cert:\LocalMachine\TrustedPeople' $receipt.Thumbprint) -ErrorAction SilentlyContinue
   if($certificate -and $certificate.Subject -eq 'CN=Hzip Local Development'){Remove-Item -LiteralPath $certificate.PSPath}
  }
  Remove-Item -LiteralPath $receiptPath
 }
 Write-Output 'Windows 11 新版菜单已移除。'
 exit
}
if([Environment]::OSVersion.Version.Build -lt 22000){throw 'Windows 11 新版菜单需要 Windows 11。'}
$msix=Join-Path $PSScriptRoot 'Hzip.ContextMenu.msix'
$cer=Join-Path $PSScriptRoot 'Hzip.ContextMenu.cer'
foreach($file in @($msix,$cer,(Join-Path $PSScriptRoot 'Hzip.Shell.v05r2.dll'),(Join-Path $root 'Hzip.exe'))){if(-not(Test-Path -LiteralPath $file)){throw "缺少菜单组件：$file"}}
$publicCertificate=[System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)
if($publicCertificate.Subject -ne 'CN=Hzip Local Development'){throw '菜单证书名称不匹配。'}
$signature=Get-AuthenticodeSignature -LiteralPath $msix
if(-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $publicCertificate.Thumbprint){throw '菜单包的签名与随附证书不匹配。'}
$imported=$false
$previousReceipt=if(Test-Path -LiteralPath $receiptPath){Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json}else{$null}
try {
 if(-not(Test-Path -LiteralPath (Join-Path 'Cert:\LocalMachine\TrustedPeople' $publicCertificate.Thumbprint))){
  Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
  $imported=$true
 }
 # Let Windows upgrade atomically; a failed update must not remove the working old menu.
 Add-AppxPackage -Path $msix -ExternalLocation $root -ErrorAction Stop
 if(-not(Get-AppxPackage -Name $packageName)){throw 'Windows 没有返回已注册的菜单包。'}
 New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
 [ordered]@{Root=$root;Thumbprint=$publicCertificate.Thumbprint;ImportedTrust=($imported -or ($previousReceipt -and $previousReceipt.ImportedTrust -and $previousReceipt.Thumbprint -eq $publicCertificate.Thumbprint));Package=(Get-AppxPackage -Name $packageName).PackageFullName;RegisteredAt=(Get-Date).ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding UTF8
 Write-Output 'Windows 11 新版 Hzip 菜单已启用。若已有文件夹窗口尚未刷新，请关闭该窗口后重新打开。'
} catch {
 if($imported){Remove-Item -LiteralPath (Join-Path 'Cert:\LocalMachine\TrustedPeople' $publicCertificate.Thumbprint) -ErrorAction SilentlyContinue}
 throw
}

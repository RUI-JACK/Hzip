param([string]$ApplicationRoot=(Join-Path (Split-Path $PSScriptRoot) 'release\Hzip'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$resultPath=Join-Path $root 'verification\modern-menu-registration.json'
$manage=Join-Path $ApplicationRoot 'shell\manage_menu.ps1'
function Read-Defaults {
 @('.zip','.7z','.rar') | ForEach-Object {
  $key=Get-ItemProperty -LiteralPath ("HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\"+$_+'\UserChoice') -ErrorAction SilentlyContinue
  [pscustomobject]@{Extension=$_;ProgId=$key.ProgId;Hash=$key.Hash}
 } | ConvertTo-Json -Compress
}
$before=Read-Defaults
$passed=$false;$problem=$null;$removed=$false;$trustRemoved=$false
try {
 & $manage -Action Remove
 if(Get-AppxPackage -Name Hzip.ContextMenu){throw 'Package still present after removal'}
 $removed=$true
 $cert=[System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $ApplicationRoot 'shell\Hzip.ContextMenu.cer'))
 $trustRemoved=-not(Test-Path -LiteralPath (Join-Path 'Cert:\LocalMachine\TrustedPeople' $cert.Thumbprint))
 if(-not $trustRemoved){throw 'Installer-owned certificate trust was not removed'}
} catch { $problem=$_.Exception.Message }
finally {
 try { & $manage -Action Install; if(-not(Get-AppxPackage -Name Hzip.ContextMenu)){throw 'Reinstallation did not register package'} }
 catch { $problem=($problem+' / '+$_.Exception.Message).Trim(' /') }
}
$defaultsUnchanged=$before -eq (Read-Defaults)
$passed=$removed -and $trustRemoved -and $defaultsUnchanged -and -not $problem
[ordered]@{Passed=$passed;PackageRemoved=$removed;OwnedCertificateTrustRemoved=$trustRemoved;Reinstalled=[bool](Get-AppxPackage -Name Hzip.ContextMenu);DefaultFileAssociationsUnchanged=$defaultsUnchanged;Error=$problem} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
if(-not $passed){exit 1}

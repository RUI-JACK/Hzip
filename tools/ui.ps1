param([Parameter(Mandatory=$true)][int]$TargetProcessId,[string]$Action='list',[string]$WindowTitle='',[string]$Id='',[string]$Name='',[string]$Value='',[string]$OutputPath='',[double]$Width=0,[double]$Height=0,[int]$VirtualKey=32)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HzipUiMessages {
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
}
'@
$processCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$TargetProcessId)
$topWindows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$processCondition)
$windowCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window)
$windows=@($topWindows | ForEach-Object { $_; $_.FindAll([System.Windows.Automation.TreeScope]::Descendants,$windowCondition) })
$window=@($windows | Where-Object { -not $_.Current.IsOffscreen -and ($WindowTitle -eq '' -or $_.Current.Name.Contains($WindowTitle)) }) | Select-Object -Last 1
if (-not $window) { throw "No matching Hzip window in process $TargetProcessId" }
if ($Action -eq 'windows') { $windows | ForEach-Object { [pscustomobject]@{Title=$_.Current.Name;Handle=$_.Current.NativeWindowHandle;Offscreen=$_.Current.IsOffscreen} } | ConvertTo-Json; exit }
$all=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
if ($Action -eq 'list') { $all | ForEach-Object { $range=$null; $rangeValue=$null; if($_.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern,[ref]$range)){$rangeValue=([System.Windows.Automation.RangeValuePattern]$range).Current.Value}; [pscustomobject]@{Name=$_.Current.Name;Id=$_.Current.AutomationId;Type=$_.Current.ControlType.ProgrammaticName;Enabled=$_.Current.IsEnabled;Offscreen=$_.Current.IsOffscreen;RangeValue=$rangeValue} } | ConvertTo-Json -Depth 4; exit }
if ($Action -eq 'close') { [HzipUiMessages]::PostMessage([IntPtr]$window.Current.NativeWindowHandle,0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null; 'Close requested'; exit }
if ($Action -eq 'resize') { $transform=[System.Windows.Automation.TransformPattern]$window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern);$transform.Resize($Width,$Height); 'Resized'; exit }
if ($Action -eq 'key') { [HzipUiMessages]::PostMessage([IntPtr]$window.Current.NativeWindowHandle,0x100,[IntPtr]$VirtualKey,[IntPtr]::Zero) | Out-Null;[HzipUiMessages]::PostMessage([IntPtr]$window.Current.NativeWindowHandle,0x101,[IntPtr]$VirtualKey,[IntPtr]::Zero) | Out-Null; 'Key posted'; exit }
if ($Action -eq 'screenshot') { & python (Join-Path $PSScriptRoot 'capture_window.py') $window.Current.NativeWindowHandle $OutputPath; exit }
$matches=@($all | Where-Object { ($Id -eq '' -or $_.Current.AutomationId -eq $Id) -and ($Name -eq '' -or $_.Current.Name -eq $Name) })
if ($Action -eq 'select') {
 foreach ($candidate in $matches) { $element=$candidate;for ($n=0;$n -lt 6 -and $element;$n++) { $pattern=$null;if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) { ([System.Windows.Automation.SelectionItemPattern]$pattern).Select();'Selected';exit };$element=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($element) } }
 throw 'Selectable item not found'
}
foreach ($element in $matches) {
 $pattern=$null
 if ($Action -eq 'get' -and $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$pattern)) { ([System.Windows.Automation.ValuePattern]$pattern).Current.Value;exit }
 if ($Action -eq 'range' -and $element.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern,[ref]$pattern)) { ([System.Windows.Automation.RangeValuePattern]$pattern).Current.Value;exit }
 if ($Action -eq 'invoke' -and $element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)) { ([System.Windows.Automation.InvokePattern]$pattern).Invoke(); 'Invoked';exit }
 if ($Action -eq 'set' -and $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$pattern)) { ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value);'Value set';exit }
 if ($Action -eq 'toggle' -and $element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern,[ref]$pattern)) { ([System.Windows.Automation.TogglePattern]$pattern).Toggle();'Toggled';exit }
 if ($Action -eq 'expand' -and $element.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern,[ref]$pattern)) { ([System.Windows.Automation.ExpandCollapsePattern]$pattern).Expand();'Expanded';exit }
}
throw "No matching control for action $Action"

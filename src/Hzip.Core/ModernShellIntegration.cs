using System.Diagnostics;
using System.Text;

namespace Hzip.Core;

public static class ModernShellIntegration
{
    public static async Task ManageAsync(string executable, bool install, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            throw new PlatformNotSupportedException("新版右键菜单需要 Windows 11，其他系统可启用经典菜单。");
        string root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(executable))!;
        string script = System.IO.Path.Combine(root, "shell", "manage_menu.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("缺少新版菜单组件，请重新解压完整的 Hzip 程序包。", script);
        // Only the menu package's own certificate is trusted; installation is user-triggered.
        string command = $"$ErrorActionPreference='Stop';try {{ & '{script.Replace("'", "''")}' -Action {(install ? "Install" : "Remove")}; exit 0 }} catch {{ Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show($_.Exception.Message,'Hzip 菜单设置失败') | Out-Null; exit 1 }}";
        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command))
        };
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Windows 菜单安装程序。");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new ArchiveOperationException("新版菜单设置未完成，请检查 Windows 提示。经典右键菜单仍可单独设置。");
    }
}

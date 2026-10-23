using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Hzip.Core;

public sealed record RecoveryProgress(long Attempts, long Total, double PerSecond, string Status);
public sealed record RecoveryResult(bool Found, string? Password, long Attempts, TimeSpan Elapsed);
public static class PasswordRecovery
{
    public static IEnumerable<string> Numeric(int digits, long start = 0, long? end = null)
    {
        if (digits is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(digits), "数字密码长度为 1–12 位。");
        long limit = (long)Math.Pow(10, digits); long last = Math.Min(end ?? limit - 1, limit - 1);
        if (start < 0 || last < start) throw new ArgumentOutOfRangeException(nameof(start));
        for (long i = start; i <= last; i++) yield return i.ToString("D" + digits);
    }
    public static async Task<RecoveryResult> SearchAsync(ArchiveEngine engine, string archive, IEnumerable<string> candidates, long total, int parallelism = 2, CancellationToken ct = default, IProgress<RecoveryProgress>? progress = null)
    {
        if (!File.Exists(archive)) throw new FileNotFoundException("归档不存在。", archive);
        try { var info = await engine.ListAsync(archive, cancellation: ct); if (!info.HasEncryption) throw new ArchiveOperationException("此归档没有加密条目，无需恢复密码。"); }
        catch (ArchiveOperationException error) when (error.NeedsPassword) { }
        var clock = Stopwatch.StartNew(); long attempts = 0; string? found = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(parallelism, 1, 16), CancellationToken = linked.Token }, async (candidate, token) =>
            {
                if (string.IsNullOrEmpty(candidate)) return;
                try
                {
                    await engine.TestAsync(archive, candidate, token);
                    if (Interlocked.CompareExchange(ref found, candidate, null) == null) linked.Cancel();
                }
                catch (ArchiveOperationException error)
                {
                    if (!error.NeedsPassword && !error.Details.Contains("Data Error", StringComparison.OrdinalIgnoreCase)) throw;
                }
                finally
                {
                    long count = Interlocked.Increment(ref attempts);
                    progress?.Report(new RecoveryProgress(count, total, count / Math.Max(.001, clock.Elapsed.TotalSeconds), "正在尝试候选密码"));
                }
            });
        }
        catch (OperationCanceledException) when (found != null && !ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested(); return new RecoveryResult(found != null, found, attempts, clock.Elapsed);
    }
}
public sealed record ScanFinding(string Path, string Status, int Result);
public sealed record ScanReport(int Scanned, int Detected, IReadOnlyList<ScanFinding> Findings, IReadOnlyList<string> Skipped);
public static class ArchiveScanner
{
    [DllImport("amsi.dll", CharSet = CharSet.Unicode)] private static extern int AmsiInitialize(string appName, out IntPtr context);
    [DllImport("amsi.dll")] private static extern void AmsiUninitialize(IntPtr context);
    [DllImport("amsi.dll", CharSet = CharSet.Unicode)] private static extern int AmsiScanBuffer(IntPtr context, byte[] buffer, uint length, string contentName, IntPtr session, out int result);
    public static async Task<ScanReport> ScanAsync(ArchiveService service, string archive, string? password = null, CancellationToken ct = default, IProgress<JobProgress>? progress = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        int hr = AmsiInitialize("Hzip", out IntPtr context);
        if (hr < 0 || context == IntPtr.Zero) throw new ArchiveOperationException("本机没有可用的 AMSI 安全扫描提供程序，扫描没有执行。请在 Windows 安全中心检查杀毒软件状态。", -8, $"AmsiInitialize: 0x{hr:X8}");
        using var workspace = new Workspace(); var findings = new List<ScanFinding>(); var skipped = new List<string>(); int scanned = 0, detected = 0;
        try
        {
            await service.ExtractAsync(archive, workspace.Path, new ExtractOptions { Layout = ExtractLayout.Here, Password = password }, ct, progress);
            var files = SafePaths.WalkRegularFiles(workspace.Path);
            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested(); string name = System.IO.Path.GetRelativePath(workspace.Path, file);
                if (new FileInfo(file).Length > 32 * 1024 * 1024) { skipped.Add(name + "：超过 32 MB 扫描上限"); continue; }
                byte[] bytes = await File.ReadAllBytesAsync(file, ct);
                hr = AmsiScanBuffer(context, bytes, (uint)bytes.Length, name, IntPtr.Zero, out int result);
                if (hr < 0) { skipped.Add(name + $"：扫描提供程序不可用 (0x{hr:X8})"); continue; }
                scanned++; if (result >= 0x8000 || result is >= 0x4000 and <= 0x4fff) { detected++; findings.Add(new ScanFinding(name, result >= 0x8000 ? "发现威胁" : "被安全策略阻止", result)); }
                progress?.Report(new JobProgress(100.0 * scanned / Math.Max(1, files.Count), name, "安全扫描"));
            }
            return new ScanReport(scanned, detected, findings, skipped);
        }
        finally { AmsiUninitialize(context); }
    }
}
public static class ShellIntegration
{
    public static readonly string[] Extensions = [".zip", ".zipx", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".txz", ".bz2", ".tbz2", ".iso", ".cab", ".lzh", ".lha", ".wim", ".arj", ".dmg", ".001", ".zst", ".lzma"];
    private const string Marker = "Hzip.Desktop.v1";
    public static void Install(string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        executable = System.IO.Path.GetFullPath(executable); if (!File.Exists(executable)) throw new FileNotFoundException(executable);
        void Verb(string path, string title, string args)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + path);
            key.SetValue("", title); key.SetValue("Icon", executable); key.SetValue("HzipOwner", Marker); key.SetValue("MultiSelectModel", "Single");
            using var command = key.CreateSubKey("command"); command.SetValue("", $"\"{executable}\" {args} \"%1\"");
        }
        Verb(@"*\shell\Hzip.Compress", "新建压缩包 (Hzip)…", "--create"); Verb(@"Directory\shell\Hzip.Compress", "新建压缩包 (Hzip)…", "--create");
        foreach (string kind in new[] { "*", "Directory" })
        {
            Verb(kind + @"\shell\Hzip.QuickZip", "压缩为 ZIP (Hzip)", "--quick-zip");
            Verb(kind + @"\shell\Hzip.Quick7z", "压缩为 7Z (Hzip)", "--quick-7z");
        }
        Verb(@"Directory\shell\Hzip.Open", "用 Hzip 打开…", "--open-from");
        foreach (string extension in Extensions)
        {
            Verb(@"SystemFileAssociations\" + extension + @"\shell\Hzip.Open", "用 Hzip 打开…", "");
            Verb(@"SystemFileAssociations\" + extension + @"\shell\Hzip.Extract", "Hzip 智能解压到此处", "--extract-here");
            Verb(@"SystemFileAssociations\" + extension + @"\shell\Hzip.ExtractDirect", "Hzip 解压到此处", "--extract-direct");
            Verb(@"SystemFileAssociations\" + extension + @"\shell\Hzip.ExtractSubfolder", "Hzip 解压到同名文件夹", "--extract-subfolder");
            Verb(@"SystemFileAssociations\" + extension + @"\shell\Hzip.ExtractTo", "Hzip 解压到其他文件夹…", "--extract-to");
            using var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids"); openWith.SetValue("Hzip.Archive", "");
        }
        using (var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Hzip.Archive"))
        {
            progId.SetValue("", "Hzip 归档文件"); progId.SetValue("HzipOwner", Marker);
            using var icon = progId.CreateSubKey("DefaultIcon"); icon.SetValue("", executable);
            using var command = progId.CreateSubKey(@"shell\open\command"); command.SetValue("", $"\"{executable}\" \"%1\"");
        }
        using (var capabilities = Registry.CurrentUser.CreateSubKey(@"Software\Hzip\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", "Hzip"); capabilities.SetValue("ApplicationDescription", "Hzip 压缩文件管理器"); capabilities.SetValue("ApplicationIcon", executable);
            using var associations = capabilities.CreateSubKey("FileAssociations"); foreach (var extension in Extensions) associations.SetValue(extension, "Hzip.Archive");
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) registered.SetValue("Hzip", @"Software\Hzip\Capabilities");
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
    public static void Remove()
    {
        if (!OperatingSystem.IsWindows()) return;
        void OwnedDelete(string path)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + path);
            if ((string?)key?.GetValue("HzipOwner") == Marker) Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + path, false);
        }
        OwnedDelete(@"*\shell\Hzip.Compress"); OwnedDelete(@"Directory\shell\Hzip.Compress");
        foreach (string kind in new[] { "*", "Directory" }) { OwnedDelete(kind + @"\shell\Hzip.QuickZip"); OwnedDelete(kind + @"\shell\Hzip.Quick7z"); }
        OwnedDelete(@"Directory\shell\Hzip.Open");
        foreach (var extension in Extensions)
        {
            OwnedDelete(@"SystemFileAssociations\" + extension + @"\shell\Hzip.Open"); OwnedDelete(@"SystemFileAssociations\" + extension + @"\shell\Hzip.Extract");
            foreach (string verb in new[] { "Hzip.ExtractDirect", "Hzip.ExtractSubfolder", "Hzip.ExtractTo" }) OwnedDelete(@"SystemFileAssociations\" + extension + @"\shell\" + verb);
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", true); key?.DeleteValue("Hzip.Archive", false);
        }
        OwnedDelete("Hzip.Archive"); using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true); registered?.DeleteValue("Hzip", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Hzip\Capabilities", false); SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}

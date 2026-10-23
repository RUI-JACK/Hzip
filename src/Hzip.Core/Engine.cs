using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hzip.Core;

public sealed partial class ArchiveEngine
{
    public string Executable { get; }
    public ArchiveEngine(string? executable = null) => Executable = executable ?? AppPaths.FindEngine();
    public async Task<EngineResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default,
        IProgress<JobProgress>? progress = null, string? workingDirectory = null, bool allowWarning = false)
    {
        var info = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        cancellation.ThrowIfCancellationRequested();
        var control = JobControl.Current; NativeWorkerPause? ownedWorker = null;
        if (control != null)
        {
            do { await JobControl.CheckpointAsync(cancellation); } while (!control.TryStart(process, out ownedWorker));
        }
        else if (!process.Start()) throw new ArchiveOperationException("无法启动压缩任务。");
        using var worker = ownedWorker;
        process.StandardInput.Close();
        using var registration = cancellation.Register(() => { control?.Resume(); try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        async Task<string> Drain(StreamReader reader, bool report)
        {
            var output = new StringBuilder(); char[] buffer = new char[4096]; long last = 0;
            while (true)
            {
                int length = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None);
                if (length == 0) break;
                string text = new(buffer, 0, length);
                if (output.Length + length > 64 * 1024 * 1024) { try { process.Kill(true); } catch { } throw new ArchiveOperationException("归档文件目录过大，超过本版本的目录读取容量。"); }
                output.Append(text);
                if (report && progress != null && Environment.TickCount64 - last > 80)
                {
                    var matches = PercentPattern().Matches(text);
                    if (matches.Count > 0) progress.Report(new JobProgress(Math.Min(100, int.Parse(matches[^1].Groups[1].Value)), "正在处理文件…"));
                    last = Environment.TickCount64;
                }
            }
            return output.ToString();
        }
        var stdout = Drain(process.StandardOutput, true); var stderr = Drain(process.StandardError, false);
        try { await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(stdout, stderr); }
        catch { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
        cancellation.ThrowIfCancellationRequested();
        var result = new EngineResult(process.ExitCode, await stdout, await stderr);
        if (result.ExitCode != 0 && !(allowWarning && result.ExitCode == 1))
        {
            string details = result.Error + "\n" + result.Output;
            string message = details.Contains("password", StringComparison.OrdinalIgnoreCase) ? "密码不正确，或该归档需要密码。" :
                details.Contains("not supported", StringComparison.OrdinalIgnoreCase) || details.Contains("Unsupported", StringComparison.OrdinalIgnoreCase) ? "当前格式不支持此操作。" :
                details.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ? "无法访问文件，请检查权限或文件是否被占用。" :
                details.Contains("Can not open", StringComparison.OrdinalIgnoreCase) || details.Contains("Cannot open", StringComparison.OrdinalIgnoreCase) ? "无法打开归档，文件可能已损坏或格式不受支持。" : "归档操作未完成，请查看详细信息。";
            throw new ArchiveOperationException(message, result.ExitCode, details.Trim());
        }
        return result;
    }
    public async Task<ArchiveInfo> ListAsync(string path, string? password = null, CancellationToken cancellation = default, int codePage = 0)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("归档文件不存在。", path);
        var args = new List<string> { "l", "-slt", "-sccUTF-8", "-bd", "-y" };
        if (!string.IsNullOrEmpty(password)) args.Add("-p" + password);
        if (codePage != 0) args.Add("-mcp=" + codePage);
        args.Add("--"); args.Add(path);
        var result = await RunAsync(args, cancellation);
        return ParseListing(path, result.Output);
    }
    public static ArchiveInfo ParseListing(string path, string output)
    {
        var entries = new List<ArchiveEntry>(); string format = "Archive";
        var fields = new Dictionary<string, string>(StringComparer.Ordinal); bool members = false;
        void Flush()
        {
            if (members && !fields.ContainsKey("Path") && fields.ContainsKey("Size") && format is "xz" or "bzip2" or "gzip") fields["Path"] = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!fields.TryGetValue("Path", out string? name)) { fields.Clear(); return; }
            if (!members) { if (fields.TryGetValue("Type", out var type)) format = type; fields.Clear(); return; }
            _ = long.TryParse(fields.GetValueOrDefault("Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size);
            _ = long.TryParse(fields.GetValueOrDefault("Packed Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long packed);
            DateTime? modified = DateTime.TryParse(fields.GetValueOrDefault("Modified"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
            string attributes = fields.GetValueOrDefault("Attributes", "");
            bool directory = fields.GetValueOrDefault("Folder") == "+" || attributes.StartsWith('D') || name.EndsWith('/') || name.EndsWith('\\');
            if (directory && name.Replace('\\', '/').Split('/').All(p => p is "" or ".")) { fields.Clear(); return; }
            bool link = !string.IsNullOrEmpty(fields.GetValueOrDefault("Symbolic Link")) || !string.IsNullOrEmpty(fields.GetValueOrDefault("Hard Link")) || attributes.Contains("lrwx", StringComparison.Ordinal) || fields.GetValueOrDefault("Mode", "").StartsWith('l');
            entries.Add(new ArchiveEntry(name, size, packed, directory, modified, fields.GetValueOrDefault("Encrypted") == "+", fields.GetValueOrDefault("Method", ""), fields.GetValueOrDefault("CRC", ""), attributes, link));
            fields.Clear();
        }
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("----------", StringComparison.Ordinal)) { Flush(); members = true; continue; }
            if (string.IsNullOrWhiteSpace(raw)) { Flush(); continue; }
            int index = raw.IndexOf(" = ", StringComparison.Ordinal);
            if (index > 0)
            {
                string key = raw[..index];
                if (key == "Path" && fields.ContainsKey("Path")) Flush();
                fields[key] = raw[(index + 3)..];
            }
            else if (members && fields.Count > 0) throw new ArchiveOperationException("归档包含无法安全解析的条目名称或多行字段，请使用其他工具检查文件。");
        }
        Flush();
        return new ArchiveInfo(path, format, entries, File.Exists(path) ? new FileInfo(path).Length : 0);
    }
    public async Task<EngineResult> TestAsync(string path, string? password = null, CancellationToken cancellation = default, IProgress<JobProgress>? progress = null)
    {
        if (GzipReader.HasSignature(path))
        {
            long trailing = await GzipReader.ReadAsync(path, Stream.Null, cancellation, progress);
            return new EngineResult(trailing > 0 ? 1 : 0, "GZIP 主体完整性校验通过。", trailing > 0 ? GzipReader.TailWarning(trailing) : "");
        }
        var args = new List<string> { "t", "-sccUTF-8", "-bsp1", "-y" };
        if (!string.IsNullOrEmpty(password)) args.Add("-p" + password);
        args.Add("--"); args.Add(System.IO.Path.GetFullPath(path));
        try { return await RunAsync(args, cancellation, progress); }
        catch (ArchiveOperationException error) when (error.NeedsPassword && ManagedZip.Needed(password) && ManagedZip.IsZip(path))
        { return await ManagedZip.TestAsync(path, password!, cancellation, progress); }
    }
    [GeneratedRegex(@"(\d{1,3})%", RegexOptions.CultureInvariant)] private static partial Regex PercentPattern();
}

using System.Globalization;
using System.Text;

namespace Hzip.Core;

public sealed record ArchiveEntry(string Path, long Size, long PackedSize, bool IsDirectory,
    DateTime? Modified, bool Encrypted, string Method, string Crc, string Attributes, bool IsLink = false)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));
    public string DisplaySize => IsDirectory ? "—" : TextFormat.Bytes(Size);
    public string DisplayPackedSize => IsDirectory ? "—" : TextFormat.Bytes(PackedSize);
    public string DisplayDate => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string Kind => IsDirectory ? "文件夹" : System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext + " 文件" : "文件";
    public string Glyph => IsDirectory ? "\uE8B7" : PreviewKind.IsImage(Path) ? "\uEB9F" : "\uE8A5";
    public string NormalizedPath => string.Join('/', Path.Replace('\\', '/').Split('/').Where(p => p.Length > 0 && p != "."));
}
public sealed record ArchiveInfo(string FilePath, string Format, IReadOnlyList<ArchiveEntry> Entries, long ArchiveBytes)
{
    public long UnpackedBytes => Entries.Sum(e => e.Size);
    public bool HasEncryption => Entries.Any(e => e.Encrypted);
}
public sealed record JobProgress(double? Percent, string Message, string Stage = "处理", long Completed = 0, long Total = 0);
public sealed record EngineResult(int ExitCode, string Output, string Error)
{
    public bool HasWarning => ExitCode == 1;
    public bool ManagedZipCompatibility { get; init; }
}
public sealed class ArchiveOperationException(string message, int code = -1, string details = "") : Exception(message)
{
    public int ExitCode { get; } = code;
    public string Details { get; } = details;
    public bool NeedsPassword => ExitCode is 2 or 255 && (Details.Contains("password", StringComparison.OrdinalIgnoreCase) || Details.Contains("encrypted", StringComparison.OrdinalIgnoreCase));
}
public enum OverwritePolicy { Rename, Skip, Overwrite, Fail, Ask }
public enum ConflictChoice { Replace, Skip, Rename, Cancel }
public sealed record ConflictResolution(ConflictChoice Choice, bool ApplyToAll = false);
public sealed record ExtractionConflict(string ArchivePath, string RelativePath, string DestinationPath,
    long? IncomingSize, DateTime IncomingModified, long? ExistingSize, DateTime ExistingModified,
    bool IncomingIsDirectory, bool ExistingIsDirectory)
{
    public bool CanReplace => !IncomingIsDirectory && !ExistingIsDirectory;
}
public delegate Task<ConflictResolution> ConflictResolver(ExtractionConflict conflict, CancellationToken cancellation);
public enum ExtractLayout { Smart, Here, Subfolder }
public sealed record CreateOptions
{
    public string Format { get; init; } = "zip";
    public int Level { get; init; } = 5;
    public string? Password { get; init; }
    public bool EncryptHeaders { get; init; } = true;
    public bool Solid { get; init; } = true;
    public string? VolumeSize { get; init; }
    public int Threads { get; init; }
    public bool Overwrite { get; init; }
    public string ZipEncryption { get; init; } = "AES256";
    public int ZipPasswordCodePage { get; init; } = 65001;
}
public sealed record ExtractOptions
{
    public string? Password { get; init; }
    public OverwritePolicy Overwrite { get; init; } = OverwritePolicy.Rename;
    public ExtractLayout Layout { get; init; } = ExtractLayout.Smart;
    public IReadOnlyList<string>? SelectedPaths { get; init; }
    public int CodePage { get; init; }
}
public sealed record ExtractResult(string Destination, int Files, IReadOnlyList<string> Warnings);
public static class TextFormat
{
    public static string Bytes(long value)
    {
        double n = Math.Max(0, value); string[] units = ["B", "KB", "MB", "GB", "TB"];
        int index = 0; while (n >= 1024 && index < units.Length - 1) { n /= 1024; index++; }
        return index == 0 ? $"{n:0} B" : $"{n:0.##} {units[index]}";
    }
    public static string ArchiveStem(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        foreach (var suffix in new[] { ".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst", ".7z.001", ".zip.001" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return System.IO.Path.GetFileNameWithoutExtension(name);
    }
}
public static class PreviewKind
{
    public static bool IsImage(string path) => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
    public static bool IsArchive(string path) => new[] { ".zip", ".zipx", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2", ".tbz2", ".txz", ".iso", ".cab", ".lzh", ".lha", ".wim", ".zst", ".001", ".arj", ".lzma", ".dmg", ".vhd", ".vhdx" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
}
public static class AppPaths
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetACP();
    public static int SystemCodePage => OperatingSystem.IsWindows() ? (int)GetACP() : 65001;
    public static string DataRoot { get; } = Environment.GetEnvironmentVariable("HZIP_DATA_DIR") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hzip");
    public static string TempRoot => System.IO.Path.Combine(DataRoot, "Temp");
    public static string FindEngine()
    {
        var configured = Environment.GetEnvironmentVariable("HZIP_ENGINE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return System.IO.Path.GetFullPath(configured);
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (int n = 0; n < 8 && cursor != null; n++, cursor = cursor.Parent)
            foreach (var relative in new[] { "engine/7z.exe", "vendor/7zip/7z.exe" })
            {
                var candidate = System.IO.Path.Combine(cursor.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException("缺少压缩引擎，请重新解压完整的 Hzip 程序包。");
    }
}
public sealed class Workspace : IDisposable
{
    public string Path { get; }
    private bool retained;
    public Workspace(string? parent = null)
    {
        parent ??= AppPaths.TempRoot; Directory.CreateDirectory(parent);
        Path = System.IO.Path.Combine(parent, ".hzip-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        if (retained) return;
        try
        {
            if (!Directory.Exists(Path)) return;
            RemoveTree(Path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    internal void KeepInPlace() => retained = true;
    internal string PreserveForRecovery()
    {
        retained = true; // Never delete the only backup, even if relocation fails.
        try
        {
            string parent = System.IO.Path.Combine(AppPaths.DataRoot, "Recovery"); Directory.CreateDirectory(parent);
            string saved = System.IO.Path.Combine(parent, System.IO.Path.GetFileName(Path)); Directory.Move(Path, saved); return saved;
        }
        catch (IOException) { return Path; }
        catch (UnauthorizedAccessException) { return Path; }
    }
    private static void RemoveTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry); else File.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0) RemoveTree(entry);
            else { File.SetAttributes(entry, FileAttributes.Normal); File.Delete(entry); }
        }
        Directory.Delete(directory);
    }
}

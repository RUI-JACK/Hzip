namespace Hzip.Core;

public static class SafePaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" };
    public static string Relative(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || System.IO.Path.IsPathRooted(name) || name.Contains(':'))
            throw new ArchiveOperationException("归档含不安全的路径，已停止解压。", details: name);
        string normalized = name.Replace('\\', '/').TrimEnd('/');
        var parts = normalized.Split('/').Where(p => p.Length > 0 && p != ".").ToArray();
        if (parts.Length == 0) throw new ArchiveOperationException("归档条目缺少有效的文件名。", details: name);
        if (parts.Length > 256) throw new ArchiveOperationException("归档目录层级过深。");
        foreach (string part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c < 32 || "<>\"|?*".Contains(c)) || Reserved.Contains(part.Split('.')[0].TrimEnd(' ')))
                throw new ArchiveOperationException("归档含不适用于 Windows 的路径，已停止解压。", details: name);
        }
        return string.Join('/', parts);
    }
    public static string Within(string root, string relative)
    {
        string clean = Relative(relative);
        string fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, clean.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new ArchiveOperationException("解压路径越出了目标目录。");
        return full;
    }
    public static void ValidateEntries(IReadOnlyList<ArchiveEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.IsLink) throw new ArchiveOperationException("归档包含符号链接或硬链接，本次解压已停止。", details: entry.Path);
            string normalized = Relative(entry.Path);
            if (!seen.Add(normalized)) throw new ArchiveOperationException("归档包含重名或大小写冲突的条目，请先修正归档。", details: entry.Path);
        }
        var files = entries.Where(e => !e.IsDirectory).Select(e => Relative(e.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in seen)
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (files.Contains(path[..slash])) throw new ArchiveOperationException("归档中同一路径同时被用作文件和文件夹，操作已停止。", details: path);
    }
    public static void NoReparseAncestors(string path)
    {
        string? current = System.IO.Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArchiveOperationException("目标路径含目录链接，请选择一个普通文件夹。", details: current);
            current = System.IO.Path.GetDirectoryName(current);
        }
    }
    public static IReadOnlyList<string> WalkRegularFiles(string root)
    {
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArchiveOperationException("输出中检测到文件系统链接，操作已停止。");
                _ = Within(root, System.IO.Path.GetRelativePath(root, entry));
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry); else files.Add(entry);
            }
        return files;
    }
}

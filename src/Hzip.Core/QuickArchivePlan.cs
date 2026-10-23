namespace Hzip.Core;

public sealed record QuickArchivePlan(IReadOnlyList<string> Inputs, string OutputPath, string Format)
{
    public static QuickArchivePlan Build(IEnumerable<string> paths, string format)
    {
        if (format is not ("zip" or "7z")) throw new ArgumentException("快捷压缩仅支持 ZIP 或 7Z。");
        var inputs = paths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (inputs.Length == 0) throw new ArgumentException("请先选择要压缩的文件或文件夹。");
        foreach (string path in inputs) if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("所选文件或文件夹不存在。", path);
        string? parent = Path.GetDirectoryName(inputs[0]);
        if (string.IsNullOrEmpty(parent)) throw new ArchiveOperationException("请在驱动器内选择文件或文件夹，再使用快捷压缩。");
        string stem = inputs.Length == 1 ? Path.GetFileName(inputs[0]) : Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
        if (inputs.Length == 1 && !Directory.Exists(inputs[0])) stem = Path.GetFileNameWithoutExtension(stem) is { Length: > 0 } shorter ? shorter : stem;
        if (string.IsNullOrWhiteSpace(stem) || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) stem = "归档";
        if (stem.Length > 240) { stem = stem[..240]; if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; }
        for (int n = 1; n <= 10000; n++)
        {
            string output = Path.Combine(parent, stem + (n == 1 ? "" : $" ({n})") + "." + format);
            if (!File.Exists(output) && !Directory.Exists(output)) return new(inputs, output, format);
        }
        throw new IOException("同名归档过多，请使用“新建压缩包”选择其他名称。");
    }
}

namespace Hzip.Core;

/// <summary>Materializes a copy-only Shell file-drop payload, including selected folders.</summary>
public sealed class ArchiveExportLease : IDisposable
{
    private readonly Workspace workspace;
    public IReadOnlyList<string> Paths { get; }
    private ArchiveExportLease(Workspace work, IReadOnlyList<string> paths) { workspace = work; Paths = paths; }
    public static async Task<ArchiveExportLease> CreateAsync(ArchiveService service, string archive, IReadOnlyList<string> selected,
        string? password = null, int codePage = 0, CancellationToken ct = default, IProgress<JobProgress>? progress = null)
    {
        if (selected.Count == 0) throw new ArgumentException("请选择要拖出的文件或文件夹。");
        var roots = selected.Select(SafePaths.Relative).Distinct(StringComparer.Ordinal).OrderBy(x => x.Length)
            .Aggregate(new List<string>(), (list, path) => { if (!list.Any(p => path.StartsWith(p + "/", StringComparison.Ordinal))) list.Add(path); return list; });
        var work = new Workspace(System.IO.Path.Combine(AppPaths.DataRoot, "DragExports"));
        try
        {
            string payload = System.IO.Path.Combine(work.Path, "payload");
            await service.ExtractAsync(archive, payload, new ExtractOptions { Password = password, CodePage = codePage,
                Layout = ExtractLayout.Here, SelectedPaths = roots, Overwrite = OverwritePolicy.Fail }, ct, progress);
            var paths = roots.Select(p => SafePaths.Within(payload, p)).ToArray();
            if (paths.Any(p => !File.Exists(p) && !Directory.Exists(p))) throw new IOException("所选条目无法完整准备，请重新打开归档。");
            return new ArchiveExportLease(work, paths);
        }
        catch { work.Dispose(); throw; }
    }
    // Some Shell destinations read the physical files after the OLE call returns.
    public void RetainForShellCopy()
    {
        File.WriteAllText(System.IO.Path.Combine(workspace.Path, ".hzip-drag-cache"), DateTimeOffset.UtcNow.ToString("O"));
        workspace.KeepInPlace();
    }
    public void Dispose() => workspace.Dispose();
    public static void CleanupExpired()
    {
        string root = System.IO.Path.Combine(AppPaths.DataRoot, "DragExports"); if (!Directory.Exists(root)) return;
        foreach (string directory in Directory.EnumerateDirectories(root, ".hzip-*"))
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                string marker = System.IO.Path.Combine(directory, ".hzip-drag-cache");
                if (!File.Exists(marker) || File.GetLastWriteTimeUtc(marker) > DateTime.UtcNow.AddDays(-7)) continue;
                SafePaths.NoReparseAncestors(marker); _ = SafePaths.WalkRegularFiles(directory);
                Directory.Delete(directory, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArchiveOperationException) { }
        }
    }
}

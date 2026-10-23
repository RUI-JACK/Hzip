using System.Text.Json;
using System.Security.Cryptography;

namespace Hzip.Core;

/// <summary>Durable editing copy. Never deletes the user's working copy on close or failed save.</summary>
public sealed class ExternalEditSession
{
    public string FilePath { get; }
    public string DirectoryPath { get; }
    private string savedHash;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ExternalEditSession(string directory, string file, string hash) { DirectoryPath = directory; FilePath = file; savedHash = hash; }
    public static async Task<ExternalEditSession> CreateAsync(string sourceFile, string archive, string entry, int codePage = 0, CancellationToken ct = default)
    {
        string name = System.IO.Path.GetFileName(SafePaths.Relative(entry));
        string directory = System.IO.Path.Combine(AppPaths.DataRoot, "ExternalEdits", Guid.NewGuid().ToString("N"));
        SafePaths.NoReparseAncestors(directory); Directory.CreateDirectory(System.IO.Path.Combine(directory, "working"));
        string path = System.IO.Path.Combine(directory, "working", name);
        await using (var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await JobControl.CopyAsync(input, output, ct);
        if (OperatingSystem.IsWindows() && File.Exists(sourceFile + ":Zone.Identifier"))
            await File.WriteAllTextAsync(path + ":Zone.Identifier", await File.ReadAllTextAsync(sourceFile + ":Zone.Identifier", ct), ct);
        string hash = await ArchiveService.HashAsync(path, ct);
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "session.json"), JsonSerializer.Serialize(new {
            Archive = System.IO.Path.GetFullPath(archive), Entry = entry, CodePage = codePage, WorkingFile = path,
            OriginalSHA256 = hash, Created = DateTimeOffset.Now }, new JsonSerializerOptions { WriteIndented = true }), ct);
        return new ExternalEditSession(directory, path, hash);
    }
    public async Task<bool> HasChangesAsync(CancellationToken ct = default)
    {
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant() != savedHash;
    }
    public async Task SaveAsync(Func<string, Task> saveBack, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        string snapshotDirectory = System.IO.Path.Combine(DirectoryPath, "snapshots", Guid.NewGuid().ToString("N"));
        try
        {
            SafePaths.NoReparseAncestors(FilePath); Directory.CreateDirectory(snapshotDirectory);
            string snapshot = System.IO.Path.Combine(snapshotDirectory, System.IO.Path.GetFileName(FilePath));
            // Deny writers and replace/rename while taking a consistent snapshot. Office-style locks fail safely.
            await using (var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await JobControl.CopyAsync(input, output, ct);
            string hash = await ArchiveService.HashAsync(snapshot, ct); ct.ThrowIfCancellationRequested();
            await saveBack(snapshot); savedHash = hash;
            // If the external editor saved another version during write-back, HasChangesAsync remains true.
        }
        finally
        {
            gate.Release();
            try { if (Directory.Exists(snapshotDirectory)) Directory.Delete(snapshotDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

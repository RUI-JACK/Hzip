using Hzip.Core;
using System.IO.Compression;
using System.Text.Json;

static class ReliabilityTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Fails(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is IOException or ArchiveOperationException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected an explicit failure");
    }
    public static async Task RunAsync(string root, ArchiveService service, Func<string, Func<Task>, Task> test)
    {
        string folder = Path.Combine(root, "reliability"); Directory.CreateDirectory(folder);
        await test("Create refuses source overwrite and split-volume source collision", async () =>
        {
            string source = Path.Combine(folder, "own-source.zip"); await File.WriteAllTextAsync(source, "irreplaceable source"); string hash = await ArchiveService.HashAsync(source);
            await Fails(() => service.CreateAsync([source], source, new CreateOptions { Overwrite = true }));
            Check(await ArchiveService.HashAsync(source) == hash, "Source replaced by output");
            string firstVolume = Path.Combine(folder, "collision.7z.001"); File.Copy(source, firstVolume); hash = await ArchiveService.HashAsync(firstVolume);
            await Fails(() => service.CreateAsync([firstVolume], Path.Combine(folder, "collision.7z"), new CreateOptions { Format = "7z", VolumeSize = "1k", Overwrite = true }));
            Check(await ArchiveService.HashAsync(firstVolume) == hash, "Split volume replaced source");
        });
        await test("Atomic non-overwrite preserves a destination that already exists", async () =>
        {
            string input = Path.Combine(folder, "incoming"), target = Path.Combine(folder, "keep"); File.WriteAllText(input, "new"); File.WriteAllText(target, "old");
            await Fails(() => { ArchiveService.AtomicCommit(input, target, overwrite: false); return Task.CompletedTask; });
            Check(File.ReadAllText(target) == "old", "Non-overwrite changed destination");
        });
        await test("Atomic commit does not inherit EFS from an encrypted source", async () =>
        {
            // This machine's real profile is encrypted, unlike the ordinary D: test root.
            // Exercise an actual encrypted file across the two volumes, not a mock flag.
            string efsParent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hzip.Efs.Verification");
            using var encrypted = new Workspace(efsParent);
            string source = Path.Combine(encrypted.Path, "encrypted-source.bin"), target = Path.Combine(folder, "plain-efs-output.bin");
            byte[] content = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
            await File.WriteAllBytesAsync(source, content);
            if ((File.GetAttributes(source) & FileAttributes.Encrypted) == 0) File.Encrypt(source);
            Check((File.GetAttributes(source) & FileAttributes.Encrypted) != 0, "Source fixture is not genuinely encrypted");
            Check((File.GetAttributes(folder) & FileAttributes.Encrypted) == 0, "Destination fixture must be unencrypted");
            ArchiveService.AtomicCommit(source, target, overwrite: false);
            Check((File.GetAttributes(target) & FileAttributes.Encrypted) == 0, "Temporary EFS encryption leaked to output");
            Check((await File.ReadAllBytesAsync(target)).SequenceEqual(content), "Encrypted source content changed");
            await File.WriteAllTextAsync(target, "existing destination");
            ArchiveService.AtomicCommit(source, target, overwrite: true);
            Check((await File.ReadAllBytesAsync(target)).SequenceEqual(content), "Replacing existing output lost bytes");
            Check((File.GetAttributes(target) & FileAttributes.Encrypted) == 0, "Replacement unexpectedly encrypted output");
            Check((await File.ReadAllBytesAsync(source)).SequenceEqual(content), "Source was modified");
            Check(!Directory.EnumerateFiles(folder, ".hzip-incoming-*").Any(), "Incoming file was left behind");
        });
        await test("Replacing an existing single archive handles Windows wildcard matching", async () =>
        {
            string input = Path.Combine(folder, "replace-source.txt"), path = Path.Combine(folder, "existing.zip"); File.WriteAllText(input, "first");
            await service.CreateAsync([input], path, new CreateOptions()); File.WriteAllText(input, "second");
            await service.CreateAsync([input], path, new CreateOptions { Overwrite = true });
            string dest = Path.Combine(folder, "replace-existing-out"); await service.ExtractAsync(path, dest, new ExtractOptions { Layout = ExtractLayout.Here });
            Check(File.ReadAllText(Path.Combine(dest, Path.GetFileName(input))) == "second", "Single archive replacement failed");
        });
        await test("Extract never overwrites its own input archive", async () =>
        {
            string path = Path.Combine(folder, "self.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { await using var writer = new StreamWriter(zip.CreateEntry("self.zip").Open()); await writer.WriteAsync("replacement"); }
            string hash = await ArchiveService.HashAsync(path);
            await Fails(() => service.ExtractAsync(path, folder, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite }));
            Check(hash == await ArchiveService.HashAsync(path), "Extraction destroyed its input");
        });
        await test("Rename collisions and stale edits preserve the original archive", async () =>
        {
            string path = Path.Combine(folder, "collision.zip"), edit = Path.Combine(folder, "edit.txt"); File.WriteAllText(edit, "edited");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) foreach (string name in new[] { "first.txt", "second.txt", "folder/item.txt" }) { await using var writer = new StreamWriter(zip.CreateEntry(name).Open()); await writer.WriteAsync(name); }
            string hash = await ArchiveService.HashAsync(path);
            await Fails(() => service.RenameAsync(path, "first.txt", "second.txt"));
            await Fails(() => service.RenameAsync(path, "folder", "folder/inside"));
            await Fails(() => service.UpdateMemberAsync(path, "missing.txt", edit));
            Check(hash == await ArchiveService.HashAsync(path), "Failed edit modified original");
        });
        await test("Corrupted ZIP refuses mutation without replacing the original", async () =>
        {
            string path = Path.Combine(folder, "damaged.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { await using var writer = new StreamWriter(zip.CreateEntry("bad.txt", CompressionLevel.NoCompression).Open()); await writer.WriteAsync("known undamaged payload"); }
            byte[] bytes = File.ReadAllBytes(path); int offset = 30 + BitConverter.ToUInt16(bytes, 26) + BitConverter.ToUInt16(bytes, 28); bytes[offset] ^= 0x40; File.WriteAllBytes(path, bytes);
            string hash = await ArchiveService.HashAsync(path); await Fails(() => service.DeleteAsync(path, ["bad.txt"]));
            Check(hash == await ArchiveService.HashAsync(path), "Damaged original replaced");
        });
        await test("Mid-commit file lock rolls back earlier overwritten files", async () =>
        {
            string path = Path.Combine(folder, "rollback.zip"), dest = Path.Combine(folder, "rollback-out"); Directory.CreateDirectory(dest);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) foreach (string name in new[] { "a.txt", "b.txt" }) { await using var writer = new StreamWriter(zip.CreateEntry(name).Open()); await writer.WriteAsync("new-" + name); }
            File.WriteAllText(Path.Combine(dest, "a.txt"), "original-a"); File.WriteAllText(Path.Combine(dest, "b.txt"), "original-b");
            using (var blocked = new FileStream(Path.Combine(dest, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
                await Fails(() => service.ExtractAsync(path, dest, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite }));
            Check(File.ReadAllText(Path.Combine(dest, "a.txt")) == "original-a", "Earlier file not rolled back");
            Check(File.ReadAllText(Path.Combine(dest, "b.txt")) == "original-b", "Locked file changed");
        });
        await test("Blocked rollback retains original backup and recovery receipt", async () =>
        {
            string path = Path.Combine(folder, "retain.zip"), dest = Path.Combine(folder, "retain-out"); Directory.CreateDirectory(dest);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { await using var writer = new StreamWriter(zip.CreateEntry("a.txt").Open()); await writer.WriteAsync("new"); }
            string target = Path.Combine(dest, "a.txt"); File.WriteAllText(target, "only original copy");
            FileStream? blocked = null; using var cancellation = new CancellationTokenSource();
            var reporter = new ImmediateProgress(p => { if (p.Stage == "写入") { blocked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read); cancellation.Cancel(); } });
            try { await Fails(() => service.ExtractAsync(path, dest, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite }, cancellation.Token, reporter)); }
            finally { blocked?.Dispose(); }
            string receipt = Directory.GetFiles(Path.Combine(AppPaths.DataRoot, "Recovery"), "recovery.json", SearchOption.AllDirectories).Single();
            using var document = JsonDocument.Parse(File.ReadAllText(receipt));
            string savedName = document.RootElement.GetProperty("Files")[0].GetProperty("Backup").GetString()!;
            Check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(receipt)!, savedName)) == "only original copy", "Original backup deleted after failed rollback");
        });
        await test("Conflicting file-directory entries are rejected before extraction", async () =>
        {
            string path = Path.Combine(folder, "conflicting-paths.zip"), dest = Path.Combine(folder, "conflicting-out");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) foreach (string name in new[] { "node", "node/child.txt" }) { await using var writer = new StreamWriter(zip.CreateEntry(name).Open()); await writer.WriteAsync("data"); }
            await Fails(() => service.ExtractAsync(path, dest, new ExtractOptions { Layout = ExtractLayout.Here }));
            Check(!Directory.Exists(dest), "Unsafe archive reached destination");
        });
        string fixtures = Path.Combine(TestPaths.Repository, "tests", "fixtures", "compatibility");
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "expected.json")));
        foreach (var row in expected.RootElement.EnumerateArray())
        {
            string fixtureName = row.GetProperty("archive").GetString()!, member = row.GetProperty("entry").GetString()!, hash = row.GetProperty("sha256").GetString()!;
            int codePage = row.TryGetProperty("code_page", out var page) ? page.GetInt32() : 0;
            await test("Independent ZIP compatibility: " + fixtureName, async () =>
            {
                string path = Path.Combine(fixtures, fixtureName), dest = Path.Combine(folder, fixtureName + "-out");
                await service.ExtractAsync(path, dest, new ExtractOptions { Layout = ExtractLayout.Here, CodePage = codePage });
                Check(await ArchiveService.HashAsync(SafePaths.Within(dest, member)) == hash, "Independent ZIP payload hash");
                await service.Engine.TestAsync(path, "多余的中文密码");
                string withPassword = dest + "-provided-password";
                await service.ExtractAsync(path, withPassword, new ExtractOptions { Layout = ExtractLayout.Here, CodePage = codePage, Password = "多余的中文密码" });
                Check(await ArchiveService.HashAsync(SafePaths.Within(withPassword, member)) == hash, "Providing a password broke an unencrypted ZIP method");
            });
        }
        foreach (string codec in new[] { "bzip2", "lzma" }) await test("Independent Unicode AES ZIP keeps native codec compatibility: " + codec, async () =>
        {
            string path = Path.Combine(folder, "independent-aes-" + codec + ".zip"), dest = Path.Combine(folder, "independent-aes-out-" + codec);
            File.Copy(Path.Combine(fixtures, "pyzipper-aes-" + codec + ".zip"), path);
            const string password = "中文 AES 兼容密码";
            await service.Engine.TestAsync(path, password);
            await Fails(() => service.Engine.TestAsync(path, "错误密码"));
            string beforeRename = dest + "-original";
            await service.ExtractAsync(path, beforeRename, new ExtractOptions { Layout = ExtractLayout.Here, Password = password });
            Check(hashForFixture(fixtures) == await ArchiveService.HashAsync(Path.Combine(beforeRename, "独立样本.txt")), "Original encrypted codec extraction mismatch");
            await service.RenameAsync(path, "独立样本.txt", "renamed.txt", password);
            await service.ExtractAsync(path, dest, new ExtractOptions { Layout = ExtractLayout.Here, Password = password });
            Check(hashForFixture(fixtures) == await ArchiveService.HashAsync(Path.Combine(dest, "renamed.txt")), "Independent encrypted payload mismatch");
        });
        foreach (string format in new[] { "zip", "7z", "tar", "wim" }) await test("Mutation preserves untouched payloads: " + format, async () =>
        {
            string source = Path.Combine(folder, "mutation-source-" + format); Directory.CreateDirectory(source);
            string keep = Path.Combine(source, "保留 😀.bin"), change = Path.Combine(source, "change.txt"), archive = Path.Combine(folder, "preserve." + format);
            File.WriteAllBytes(keep, Enumerable.Range(0, 90000).Select(i => (byte)(i * 31)).ToArray()); File.WriteAllText(change, "change"); string hash = await ArchiveService.HashAsync(keep);
            await service.CreateAsync([source], archive, new CreateOptions { Format = format });
            await service.RenameAsync(archive, Path.GetFileName(source) + "/change.txt", Path.GetFileName(source) + "/renamed.txt");
            await service.DeleteAsync(archive, [Path.GetFileName(source) + "/renamed.txt"]);
            string dest = Path.Combine(folder, "preserve-out-" + format); await service.ExtractAsync(archive, dest, new ExtractOptions { Layout = ExtractLayout.Here });
            Check(await ArchiveService.HashAsync(Path.Combine(dest, Path.GetFileName(source), Path.GetFileName(keep))) == hash, "Untouched member lost or changed");
        });
    }
    static string hashForFixture(string fixtures) { using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "expected.json"))); return doc.RootElement[0].GetProperty("sha256").GetString()!; }
    sealed class ImmediateProgress(Action<JobProgress> action) : IProgress<JobProgress> { public void Report(JobProgress value) => action(value); }
}

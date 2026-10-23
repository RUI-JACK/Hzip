using Hzip.Core;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;
string root = System.IO.Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? System.IO.Path.Combine(AppContext.BaseDirectory, "verification"));
Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("HZIP_DATA_DIR", System.IO.Path.Combine(root, "appdata"));
string marker = System.IO.Path.Combine(root, ".hzip-verification");
if (Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(marker)) throw new IOException("测试输出必须是空目录，或由 Hzip 验证器创建的目录。");
File.WriteAllText(marker, "Hzip owns the generated fixtures in this directory.");
var service = new ArchiveService(); var results = new List<object>(); int failures = 0;
string source = System.IO.Path.Combine(root, "素材 folder"); Directory.CreateDirectory(System.IO.Path.Combine(source, "子目录")); Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
await File.WriteAllTextAsync(System.IO.Path.Combine(source, "说明 [readme].txt"), string.Concat(Enumerable.Repeat("Hzip 中文路径与 ZIP interoperability.\n", 80)), new UTF8Encoding(false));
await File.WriteAllTextAsync(System.IO.Path.Combine(source, "子目录", "hello.txt"), "nested content\n");
await File.WriteAllBytesAsync(System.IO.Path.Combine(source, "binary.bin"), Enumerable.Range(0, 4096).Select(i => (byte)(i * 17 % 251)).ToArray());
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task ExpectError(Func<Task> action) { bool failed = false; try { await action(); } catch (Exception e) when (e is ArchiveOperationException or IOException or OperationCanceledException) { failed = true; } Check(failed, "Expected the operation to fail"); }
async Task Test(string name, Func<Task> action)
{
    if (args.Contains("--pause-only") && !name.StartsWith("Pause ")) return;
    if (args.Contains("--repair-only") && !name.StartsWith("Repair+")) return;
    if (args.Contains("--large-only") && !name.StartsWith("ZIP64 >4GB")) return;
    int caseIndex = Array.IndexOf(args, "--case"); if (caseIndex >= 0 && caseIndex + 1 < args.Length && !name.Contains(args[caseIndex + 1], StringComparison.OrdinalIgnoreCase)) return;
    if ((name.StartsWith("Bandizip ") || name.Contains("interoperability with Bandizip")) && !File.Exists(@"C:\Program Files\Bandizip\bz.exe")) { results.Add(new { Name = name, Passed = (bool?)null, Skipped = true, Reason = "Optional Bandizip interoperability oracle is not installed" }); return; }
    var clock = Stopwatch.StartNew(); Console.WriteLine("RUN " + name);
    try { await action(); results.Add(new { Name = name, Passed = true, Seconds = clock.Elapsed.TotalSeconds }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; string details = e is ArchiveOperationException archiveError ? archiveError.Details : ""; results.Add(new { Name = name, Passed = false, Seconds = clock.Elapsed.TotalSeconds, Error = e.ToString(), Details = details }); Console.WriteLine("FAIL " + name + ": " + e + "\n" + details); }
    await File.WriteAllTextAsync(System.IO.Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Failures = failures, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
}
async Task VerifyTree(string extracted)
{
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string target = System.IO.Path.Combine(extracted, System.IO.Path.GetRelativePath(source, file));
        Check(File.Exists(target), "Missing file: " + target); Check(await ArchiveService.HashAsync(file) == await ArchiveService.HashAsync(target), "Hash mismatch: " + target);
    }
    Check(Directory.Exists(System.IO.Path.Combine(extracted, "empty")), "Empty folder missing");
}
await Test("ZIP roundtrip: Unicode, spaces, brackets, empty folder", async () =>
{
    string zip = System.IO.Path.Combine(root, "sample.zip"); await service.CreateAsync([source], zip, new CreateOptions { Overwrite = true });
    var info = await service.ListAsync(zip); Check(info.Format == "zip", "Format"); Check(info.Entries.Count >= 5, "Entry count");
    using var independent = ZipFile.OpenRead(zip); Check(independent.Entries.Any(e => e.FullName.Contains("说明")), "Independent reader Unicode");
    await service.ExtractAsync(zip, System.IO.Path.Combine(root, "zip-out"), new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite });
    await VerifyTree(System.IO.Path.Combine(root, "zip-out", System.IO.Path.GetFileName(source)));
});
await Test("ZIP AES-256 encryption and wrong-password rejection", async () =>
{
    string zip = System.IO.Path.Combine(root, "aes.zip"); await service.CreateAsync([source], zip, new CreateOptions { Password = "Hzip@Test2026", Overwrite = true });
    Check((await service.ListAsync(zip)).HasEncryption, "Missing encrypted flag"); await service.Engine.TestAsync(zip, "Hzip@Test2026");
    await ExpectError(() => service.Engine.TestAsync(zip, "incorrect"));
});
await Test("7Z header encryption", async () =>
{
    string zip = System.IO.Path.Combine(root, "encrypted.7z"); await service.CreateAsync([source], zip, new CreateOptions { Format = "7z", Password = "07", Overwrite = true });
    await ExpectError(() => service.ListAsync(zip)); var info = await service.ListAsync(zip, "07"); Check(info.Entries.Count >= 5, "Encrypted list");
    await service.ExtractAsync(zip, System.IO.Path.Combine(root, "7z-out"), new ExtractOptions { Password = "07", Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite });
    await VerifyTree(System.IO.Path.Combine(root, "7z-out", System.IO.Path.GetFileName(source)));
});
await Test("Unicode AES ZIP password: create/test/extract/mutate/split", async () =>
{
    const string secret = "Hzip@中文密码2026";
    string archive = System.IO.Path.Combine(root, "unicode-aes.zip"); await service.CreateAsync([source], archive, new CreateOptions { Password = secret, Overwrite = true });
    await service.Engine.TestAsync(archive, secret); await ExpectError(() => service.Engine.TestAsync(archive, "错误密码"));
    await service.ExtractAsync(archive, System.IO.Path.Combine(root, "unicode-aes-out"), new ExtractOptions { Password = secret, Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite });
    await VerifyTree(System.IO.Path.Combine(root, "unicode-aes-out", System.IO.Path.GetFileName(source)));
    string extra = System.IO.Path.Combine(root, "中文追加.txt"); await File.WriteAllTextAsync(extra, "managed add"); await service.AddAsync(archive, [extra], secret);
    await service.RenameAsync(archive, "中文追加.txt", "中文改名.txt", secret); await service.UpdateMemberAsync(archive, "中文改名.txt", extra, secret); await service.DeleteAsync(archive, ["中文改名.txt"], secret); await service.Engine.TestAsync(archive, secret);
    string split = System.IO.Path.Combine(root, "unicode-split.zip"); await service.CreateAsync([source], split, new CreateOptions { Password = secret, VolumeSize = "1k", Overwrite = true }); await service.Engine.TestAsync(split + ".001", secret);
    await service.ExtractAsync(split + ".001", System.IO.Path.Combine(root, "unicode-split-out"), new ExtractOptions { Password = secret, Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite }); await VerifyTree(System.IO.Path.Combine(root, "unicode-split-out", System.IO.Path.GetFileName(source)));
});
await Test("Unicode AES ZIP interoperability with Bandizip", async () =>
{
    string bz = @"C:\Program Files\Bandizip\bz.exe"; const string secret = "Hzip@中文密码2026";
    async Task RunBz(params string[] arguments)
    {
        var start = new ProcessStartInfo(bz) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string arg in arguments) start.ArgumentList.Add(arg); using var process = Process.Start(start)!; process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(); var stderrTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); } catch (TimeoutException) { process.Kill(true); throw new Exception("Bandizip oracle timed out for " + arguments[0]); }
        string stdout = await stdoutTask, stderr = await stderrTask; Check(process.ExitCode == 0, stdout + stderr);
    }
    string compatible = System.IO.Path.Combine(root, "unicode-aes-gbk.zip");
    await service.CreateAsync([source], compatible, new CreateOptions { Password = secret, ZipPasswordCodePage = 936, Overwrite = true });
    await RunBz("t", "-p:" + secret, compatible);
    string oracle = System.IO.Path.Combine(root, "bandizip-unicode.zip");
    await RunBz("c", "-y", "-p:" + secret, oracle, System.IO.Path.Combine(source, "binary.bin")); await service.Engine.TestAsync(oracle, secret);
    await service.ExtractAsync(oracle, System.IO.Path.Combine(root, "oracle-unicode-out"), new ExtractOptions { Password = secret, Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite });
    Check(await ArchiveService.HashAsync(System.IO.Path.Combine(source, "binary.bin")) == await ArchiveService.HashAsync(System.IO.Path.Combine(root, "oracle-unicode-out", "binary.bin")), "Oracle Unicode password extraction");
});
foreach (var (format, extension) in new[] { ("tar", "tar"), ("tgz", "tar.gz"), ("txz", "tar.xz"), ("tbz2", "tar.bz2"), ("zipx", "zipx"), ("lzh", "lzh"), ("iso", "iso"), ("wim", "wim"), ("exe", "exe") })
    await Test("Format " + format + " create/test/extract", async () =>
    {
        string archive = System.IO.Path.Combine(root, "format-" + format + "." + extension);
        await service.CreateAsync([source], archive, new CreateOptions { Format = format, Overwrite = true }); await service.Engine.TestAsync(archive);
        var info = await service.ListAsync(archive, codePage: format == "lzh" ? 936 : 0); Check(info.Entries.Count > 0, "List empty");
        string dest = System.IO.Path.Combine(root, "format-out-" + format);
        await service.ExtractAsync(archive, dest, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite, CodePage = format == "lzh" ? 936 : 0 });
        await VerifyTree(System.IO.Path.Combine(dest, System.IO.Path.GetFileName(source)));
    });
foreach (string format in new[] { "gz", "xz", "bz2" })
    await Test("Single stream format " + format, async () =>
    {
        string file = System.IO.Path.Combine(source, "binary.bin"), archive = System.IO.Path.Combine(root, "single." + format), dest = System.IO.Path.Combine(root, "single-out-" + format);
        await service.CreateAsync([file], archive, new CreateOptions { Format = format, Overwrite = true }); await service.Engine.TestAsync(archive);
        await service.ExtractAsync(archive, dest, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite });
        string output = Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Single(); Check(await ArchiveService.HashAsync(file) == await ArchiveService.HashAsync(output), "Stream hash");
    });
await Test("Archive mutation: add / rename / edit / delete", async () =>
{
    string archive = System.IO.Path.Combine(root, "mutate.zip"), extra = System.IO.Path.Combine(root, "extra.txt");
    File.Copy(System.IO.Path.Combine(root, "sample.zip"), archive, true); await File.WriteAllTextAsync(extra, "added data");
    await service.AddAsync(archive, [extra]); Check((await service.ListAsync(archive)).Entries.Any(x => x.Path == "extra.txt"), "Add");
    await service.RenameAsync(archive, "extra.txt", "renamed.txt"); await File.WriteAllTextAsync(extra, "edited data"); await service.UpdateMemberAsync(archive, "renamed.txt", extra);
    using (var preview = await service.PreviewAsync(archive, (await service.ListAsync(archive)).Entries.Single(x => x.Path == "renamed.txt"))) Check(await File.ReadAllTextAsync(preview.FilePath) == "edited data", "Edit");
    await service.DeleteAsync(archive, ["renamed.txt"]); Check(!(await service.ListAsync(archive)).Entries.Any(x => x.Path == "renamed.txt"), "Delete");
    string hash = await ArchiveService.HashAsync(archive); await ExpectError(() => service.RenameAsync(archive, "missing", "../unsafe")); Check(hash == await ArchiveService.HashAsync(archive), "Original changed on failure");
});
await Test("Selected extraction includes selected directory descendants", async () =>
{
    string zip = System.IO.Path.Combine(root, "sample.zip"), selected = System.IO.Path.GetFileName(source) + "/子目录/";
    string dest = System.IO.Path.Combine(root, "selected-out"); await service.ExtractAsync(zip, dest, new ExtractOptions { Layout = ExtractLayout.Here, SelectedPaths = [selected], Overwrite = OverwritePolicy.Overwrite });
    Check(Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length == 1, "Selected extraction leaked other files");
});
await Test("Split 7Z volume and reconstruction", async () =>
{
    string zip = System.IO.Path.Combine(root, "volumes.7z"); var files = await service.CreateAsync([source], zip, new CreateOptions { Format = "7z", Level = 0, VolumeSize = "1k", Overwrite = true });
    Check(files.Count > 1 && File.Exists(zip + ".001"), "Split missing"); await service.Engine.TestAsync(zip + ".001");
});
await Test("Volume overwrite removes stale parts transactionally", async () =>
{
    string path = System.IO.Path.Combine(root, "replace-volumes.7z"); var first = await service.CreateAsync([source], path, new CreateOptions { Format = "7z", Level = 0, VolumeSize = "1k" }); Check(first.Count > 1, "Initial split");
    string small = System.IO.Path.Combine(root, "tiny-volume.txt"); File.WriteAllText(small, "tiny");
    var second = await service.CreateAsync([small], path, new CreateOptions { Format = "7z", VolumeSize = "1k", Overwrite = true }); Check(second.Count == 1 && !File.Exists(path + ".002"), "Stale volume retained"); await service.Engine.TestAsync(path + ".001");
    await service.CreateAsync([small], path, new CreateOptions { Format = "7z", Overwrite = true }); Check(File.Exists(path) && !File.Exists(path + ".001"), "Split-to-single leaves obsolete volume"); await service.Engine.TestAsync(path);
});
await Test("Traversal and alternate-stream paths blocked", async () =>
{
    foreach (string name in new[] { "../escape.txt", "C:/escape.txt", "hello.txt:stream", "CON.txt", "folder/../escape.txt" })
    {
        string zip = System.IO.Path.Combine(root, "bad-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { await using var writer = new StreamWriter(archive.CreateEntry(name).Open()); await writer.WriteAsync("unsafe"); }
        await ExpectError(() => service.ExtractAsync(zip, System.IO.Path.Combine(root, "unsafe-out"), new ExtractOptions { Layout = ExtractLayout.Here }));
    }
    Check(!File.Exists(System.IO.Path.Combine(root, "escape.txt")), "Escaped destination");
});
await Test("Symlink archive member blocked", async () =>
{
    string zip = System.IO.Path.Combine(root, "symlink.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { var entry = archive.CreateEntry("link"); entry.ExternalAttributes = unchecked((int)0xa1ff0000); await using var writer = new StreamWriter(entry.Open()); await writer.WriteAsync("../target"); }
    byte[] raw = await File.ReadAllBytesAsync(zip);
    for (int offset = 0; offset < raw.Length - 46; offset++) if (BitConverter.ToUInt32(raw, offset) == 0x02014b50) { raw[offset + 5] = 3; BitConverter.GetBytes(unchecked((int)0xa1ff0000)).CopyTo(raw, offset + 38); break; }
    await File.WriteAllBytesAsync(zip, raw);
    var info = await service.ListAsync(zip); Check(info.Entries.Any(e => e.IsLink), "Symlink not recognized"); await ExpectError(() => service.ExtractAsync(zip, System.IO.Path.Combine(root, "symlink-out"), new ExtractOptions()));
});
await Test("Extraction cancel rolls back overwritten files", async () =>
{
    string dest = System.IO.Path.Combine(root, "cancel-out"), folder = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(source)); Directory.CreateDirectory(folder);
    string original = System.IO.Path.Combine(folder, "binary.bin"); await File.WriteAllTextAsync(original, "preserve original");
    using var cts = new CancellationTokenSource();
    var progress = new InlineProgress<JobProgress>(p => { if (p.Stage == "写入" && p.Completed >= 1) cts.Cancel(); });
    await ExpectError(() => service.ExtractAsync(System.IO.Path.Combine(root, "sample.zip"), dest, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite }, cts.Token, progress));
    Check(await File.ReadAllTextAsync(original) == "preserve original", "Rollback lost existing content");
});
await Test("Create cancellation removes partial archive", async () =>
{
    string big = System.IO.Path.Combine(root, "cancel-input.bin"); using (var f = File.Create(big)) f.SetLength(256 * 1024 * 1024);
    string archive = System.IO.Path.Combine(root, "cancel-create.7z"); using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    await ExpectError(() => service.CreateAsync([big], archive, new CreateOptions { Format = "7z", Level = 9 }, cts.Token));
    Check(!File.Exists(archive), "Partial output committed"); File.Delete(big);
});
await Test("ZIP local-header recovery without central directory", async () =>
{
    byte[] bytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(root, "sample.zip")); int central = -1;
    for (int n = 0; n < bytes.Length - 4; n++) if (BitConverter.ToUInt32(bytes, n) == 0x02014b50) { central = n; break; }
    Check(central > 0, "No central directory fixture"); string damaged = System.IO.Path.Combine(root, "damaged.zip"), repaired = System.IO.Path.Combine(root, "repaired.zip");
    if (File.Exists(repaired)) File.Delete(repaired); await File.WriteAllBytesAsync(damaged, bytes[..central]);
    var report = await ZipRepair.RepairAsync(damaged, repaired); Check(report.Recovered >= 3, "No recovered files");
    await service.Engine.TestAsync(repaired); await service.ExtractAsync(repaired, System.IO.Path.Combine(root, "repaired-out"), new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite });
    await VerifyTree(System.IO.Path.Combine(root, "repaired-out", System.IO.Path.GetFileName(source)));
});
await Test("Password recovery tests actual encrypted content", async () =>
{
    var recovery = await PasswordRecovery.SearchAsync(service.Engine, System.IO.Path.Combine(root, "encrypted.7z"), PasswordRecovery.Numeric(2, 0, 15), 16, 2);
    Check(recovery.Found && recovery.Password == "07", "Password not recovered");
});
await Test("DPAPI vault roundtrip and no cleartext storage", () =>
{
    var vault = new PasswordVault(System.IO.Path.Combine(root, "vault")); string secret = "secret-" + Guid.NewGuid(); var entry = vault.Save("test", secret);
    Check(vault.Reveal(entry) == secret, "Vault decrypt"); Check(!File.ReadAllText(System.IO.Path.Combine(root, "vault", "passwords.json")).Contains(secret), "Cleartext password");
    vault.Delete(entry.Id); Check(vault.List().All(x => x.Id != entry.Id), "Vault delete"); return Task.CompletedTask;
});
await Test("Bandizip interoperability for ZIPX / LZH / ISO", async () =>
{
    string bz = @"C:\Program Files\Bandizip\bz.exe";
    if (!File.Exists(bz)) { Console.WriteLine("External oracle not installed; this check cannot run."); throw new FileNotFoundException("Bandizip oracle unavailable"); }
    foreach (string extension in new[] { "zipx", "lzh", "iso" })
    {
        var info = new ProcessStartInfo(bz) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("t"); info.ArgumentList.Add(System.IO.Path.Combine(root, "format-" + extension + "." + extension));
        using var process = Process.Start(info)!; string stdout = await process.StandardOutput.ReadToEndAsync(), stderr = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        await File.WriteAllTextAsync(System.IO.Path.Combine(root, "bandizip-" + extension + ".txt"), stdout + "\n" + stderr);
        Check(process.ExitCode == 0, extension + " rejected by Bandizip: " + stdout + stderr);
    }
});
await Test("RAR4 safe selected extraction and link rejection", async () =>
{
    string fixture = System.IO.Path.Combine(TestPaths.Repository, "tests", "fixtures", "test_read_format_rar.rar");
    var info = await service.ListAsync(fixture); Check(info.Format.Equals("Rar", StringComparison.OrdinalIgnoreCase), "RAR format");
    await service.Engine.TestAsync(fixture);
    string dest = System.IO.Path.Combine(root, "rar4-out"); await service.ExtractAsync(fixture, dest, new ExtractOptions { Layout = ExtractLayout.Here, SelectedPaths = ["test.txt"], Overwrite = OverwritePolicy.Overwrite });
    Check((await Checksums.Crc32Async(System.IO.Path.Combine(dest, "test.txt"))).ToString("X8") == "BEC8A242", "RAR CRC");
    await ExpectError(() => service.ExtractAsync(fixture, System.IO.Path.Combine(root, "rar4-all"), new ExtractOptions { Layout = ExtractLayout.Here }));
});
await Test("RAR5 compressed member extraction", async () =>
{
    string fixture = System.IO.Path.Combine(TestPaths.Repository, "tests", "fixtures", "test_read_format_rar5_compressed.rar");
    var info = await service.ListAsync(fixture); Check(info.Entries.Count == 1 && info.Entries[0].Size == 1200, "RAR5 listing");
    string dest = System.IO.Path.Combine(root, "rar5-out"); await service.ExtractAsync(fixture, dest, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite });
    Check((await Checksums.Crc32Async(System.IO.Path.Combine(dest, "test.bin"))).ToString("X8") == "7CCA70CD", "RAR5 CRC");
});
await Test("Canonical ./ entry paths remain extractable", async () =>
{
    string zip = System.IO.Path.Combine(root, "dot-prefix.zip"); using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
    {
        archive.CreateEntry("./"); archive.CreateEntry("./docs/"); await using var writer = new StreamWriter(archive.CreateEntry("./docs/file.txt").Open()); await writer.WriteAsync("dot-prefix");
    }
    string dest = System.IO.Path.Combine(root, "dot-prefix-out"); await service.ExtractAsync(zip, dest, new ExtractOptions { Layout = ExtractLayout.Here }); Check(File.ReadAllText(System.IO.Path.Combine(dest, "docs", "file.txt")) == "dot-prefix", "Dot prefix lost");
});
await Test("Overwrite policies rename, skip and fail preserve content", async () =>
{
    string zip = System.IO.Path.Combine(root, "sample.zip"), dest = System.IO.Path.Combine(root, "overwrite-out");
    await service.ExtractAsync(zip, dest, new ExtractOptions { Layout = ExtractLayout.Here }); string existing = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(source), "binary.bin"); await File.WriteAllTextAsync(existing, "keep");
    await service.ExtractAsync(zip, dest, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Skip }); Check(File.ReadAllText(existing) == "keep", "Skip overwrote file");
    await service.ExtractAsync(zip, dest, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Rename }); Check(File.ReadAllText(existing) == "keep" && File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(existing)!, "binary (2).bin")), "Rename policy");
    await ExpectError(() => service.ExtractAsync(zip, dest, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Fail })); Check(File.ReadAllText(existing) == "keep", "Fail policy overwrote");
});
await Test("Folder rename and removal update descendants", async () =>
{
    string zip = System.IO.Path.Combine(root, "folder-edit.zip"); File.Copy(System.IO.Path.Combine(root, "sample.zip"), zip);
    string old = System.IO.Path.GetFileName(source); await service.RenameAsync(zip, old, "changed"); var changed = await service.ListAsync(zip); Check(changed.Entries.All(e => e.NormalizedPath == "changed" || e.NormalizedPath.StartsWith("changed/")), "Descendant rename");
    await service.DeleteAsync(zip, ["changed/"]); Check((await service.ListAsync(zip)).Entries.Count == 0, "Descendants left after deletion");
});
await Test("ZIP data-descriptor local-header recovery", async () =>
{
    string sourceZip = System.IO.Path.Combine(root, "descriptors.zip");
    using (var file = File.Create(sourceZip)) using (var nonseek = new NonSeekOutput(file)) using (var zip = new ZipArchive(nonseek, ZipArchiveMode.Create, true)) { await using var writer = new StreamWriter(zip.CreateEntry("descriptor.txt").Open()); await writer.WriteAsync("descriptor payload"); }
    byte[] raw = await File.ReadAllBytesAsync(sourceZip); int central = Enumerable.Range(0, raw.Length - 4).First(n => BitConverter.ToUInt32(raw, n) == 0x02014b50); string damaged = System.IO.Path.Combine(root, "descriptors-damaged.zip"); await File.WriteAllBytesAsync(damaged, raw[..central]);
    string output = System.IO.Path.Combine(root, "descriptors-repaired.zip"); var result = await ZipRepair.RepairAsync(damaged, output); Check(result.Recovered == 1, "Descriptor not recovered"); using var repaired = ZipFile.OpenRead(output); using var reader = new StreamReader(repaired.Entries[0].Open()); Check(await reader.ReadToEndAsync() == "descriptor payload", "Recovered payload mismatch");
});
await Test("AMSI provider availability and coverage are explicit", async () =>
{
    try
    {
        var report = await ArchiveScanner.ScanAsync(service, System.IO.Path.Combine(root, "sample.zip"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(root, "amsi-report.json"), JsonSerializer.Serialize(new { ProviderAvailable = true, Report = report }, new JsonSerializerOptions { WriteIndented = true }));
        Check(report.Scanned + report.Skipped.Count == 3 && report.Detected == report.Findings.Count, "AMSI coverage counts");
    }
    catch (ArchiveOperationException error) when (error.ExitCode == -8)
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(root, "amsi-report.json"), JsonSerializer.Serialize(new { ProviderAvailable = false, ScanExecuted = false, Reason = error.Message, Details = error.Details }, new JsonSerializerOptions { WriteIndented = true }));
        Check(error.Message.Contains("没有执行"), "Unavailable scanner must not report a clean result");
    }
});
await Test("Unicode legacy ZIP filename integration", async () =>
{
    string bz = @"C:\Program Files\Bandizip\bz.exe"; if (!File.Exists(bz)) return;
    string path = System.IO.Path.Combine(root, "legacy-chinese.zip"), file = System.IO.Path.Combine(source, "说明 [readme].txt"); const string secret = "中文密码";
    var start = new ProcessStartInfo(bz) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
    foreach (string value in new[] { "c", "-y", "-p:" + secret, path, file }) start.ArgumentList.Add(value);
    using var process = Process.Start(start)!; process.StandardInput.Close(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); } catch { process.Kill(true); throw; }
    Check(process.ExitCode == 0, await output + await error); await service.Engine.TestAsync(path, secret);
    string dest = System.IO.Path.Combine(root, "legacy-chinese-out"); await service.ExtractAsync(path, dest, new ExtractOptions { Password = secret, Layout = ExtractLayout.Here });
    Check(await ArchiveService.HashAsync(file) == await ArchiveService.HashAsync(System.IO.Path.Combine(dest, System.IO.Path.GetFileName(file))), "Legacy Chinese filename or password mismatch");
});
if (args.Contains("--shell")) await Test("Windows user shell integration and removal", () =>
{
    string project = TestPaths.Repository, application = System.IO.Path.Combine(project, "release", "Hzip", "Hzip.exe"); Check(File.Exists(application), "Published app missing");
    using (var existing = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Hzip.Archive")) Check(existing == null, "Existing Hzip integration found; refusing to overwrite it in this test");
    string ReadDefault(string ext) { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice"); return key?.GetValue("ProgId")?.ToString() ?? ""; }
    string before = string.Join('|', new[] { ".zip", ".7z", ".rar" }.Select(ReadDefault));
    try
    {
        ShellIntegration.Install(application);
        using var command = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\SystemFileAssociations\.zip\shell\Hzip.Open\command");
        Check(command?.GetValue("")?.ToString() == $"\"{application}\"  \"%1\"", "Open command target or quoting mismatch");
        Check(before == string.Join('|', new[] { ".zip", ".7z", ".rar" }.Select(ReadDefault)), "Default application changed");
    }
    finally { ShellIntegration.Remove(); }
    using (var removed = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Hzip.Archive")) Check(removed == null, "Integration not removed");
    Check(before == string.Join('|', new[] { ".zip", ".7z", ".rar" }.Select(ReadDefault)), "Defaults changed on removal"); return Task.CompletedTask;
});
if (!args.Contains("--large-only")) { await ReliabilityTests.RunAsync(root, service, Test); await PauseTests.RunAsync(root, service, Test); await RepairTests.RunAsync(root, service, Test); await WorkflowTests.RunAsync(root, service, Test); await QuickMenuTests.RunAsync(root, service, Test); await GzipTests.RunAsync(root, service, Test); await ConflictTests.RunAsync(root, service, Test); }
if (args.Contains("--large") || args.Contains("--large-only")) await Test("ZIP64 >4GB real streaming roundtrip", async () =>
{
    string input = System.IO.Path.Combine(root, "over-4gb.bin"), zip = System.IO.Path.Combine(root, "over-4gb.zip"), dest = System.IO.Path.Combine(root, "large-out");
    try
    {
        using (var file = File.Create(input)) { NativeTest.DeviceIoControl(file.SafeFileHandle, 0x900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); file.SetLength((long)uint.MaxValue + 8193); file.Position = file.Length - 8; file.Write(Encoding.ASCII.GetBytes("HZIP64!!")); }
        string expected = await ArchiveService.HashAsync(input); await service.CreateAsync([input], zip, new CreateOptions { Format = "zip", Level = 1, Overwrite = true }); var info = await service.ListAsync(zip); Check(info.Entries.Single().Size > uint.MaxValue, "ZIP64 size truncated");
        await service.Engine.TestAsync(zip); await service.ExtractAsync(zip, dest, new ExtractOptions { Layout = ExtractLayout.Here }); string decoded = System.IO.Path.Combine(dest, System.IO.Path.GetFileName(input));
        Check(await ArchiveService.HashAsync(decoded) == expected, "ZIP64 roundtrip hash");
        await File.WriteAllTextAsync(System.IO.Path.Combine(root, "zip64-proof.json"), JsonSerializer.Serialize(new { Bytes = new FileInfo(input).Length, SHA256 = expected, CompressedBytes = new FileInfo(zip).Length, ExtractedHashMatches = true }, new JsonSerializerOptions { WriteIndented = true }));
    }
    finally { if (File.Exists(input)) File.Delete(input); if (Directory.Exists(dest)) Directory.Delete(dest, true); }
});
Console.WriteLine($"DONE {results.Count} checks / {failures} failures"); return failures == 0 ? 0 : 1;

sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
sealed class NonSeekOutput(Stream stream) : Stream
{
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => stream.Flush(); public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
}
static class NativeTest
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] public static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint control, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}

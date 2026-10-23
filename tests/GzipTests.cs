using System.IO.Compression;
using System.Text;
using Hzip.Core;

static class GzipTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static byte[] Gzip(byte[] bytes)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true)) gzip.Write(bytes);
        return memory.ToArray();
    }

    public static async Task RunAsync(string root, ArchiveService service, Func<string, Func<Task>, Task> test)
    {
        string area = Path.Combine(root, "gzip"); Directory.CreateDirectory(area);
        byte[] data = new byte[1024 * 1024 + 31]; new Random(2631).NextBytes(data);
        byte[] plain = Gzip(data), tail = Encoding.UTF8.GetBytes("\0\0\0\0{\"metadata_version\":\"1.1\",\"supported_devices\":[\"test\"]}\nFWx0");
        async Task<string> Save(string name, byte[] bytes) { string path = Path.Combine(area, name + ".gz"); await File.WriteAllBytesAsync(path, bytes); return path; }
        async Task Roundtrip(string name, byte[] bytes, byte[] expected, bool warning)
        {
            string path = await Save(name, bytes), hash = await ArchiveService.HashAsync(path);
            var tested = await service.Engine.TestAsync(path); Check(tested.HasWarning == warning, "Test warning mismatch");
            var extracted = await service.ExtractAsync(path, Path.Combine(area, name + "-out"), new ExtractOptions { Layout = ExtractLayout.Here });
            Check((extracted.Warnings.Count > 0) == warning, "Extraction warning mismatch");
            string output = Directory.GetFiles(extracted.Destination, "*", SearchOption.AllDirectories).Single();
            Check(File.ReadAllBytes(output).SequenceEqual(expected), "Decoded bytes differ");
            Check(hash == await ArchiveService.HashAsync(path), "Original GZIP changed");
        }
        await test("GZIP ordinary streaming CRC and size", () => Roundtrip("ordinary", plain, data, false));
        await test("GZIP empty member", () => Roundtrip("empty", Gzip([]), [], false));
        await test("GZIP firmware metadata after payload gives explicit warning", () => Roundtrip("firmware.img", [.. plain, .. tail], data, true));
        await test("GZIP concatenated members join payloads", () => Roundtrip("concat", [.. plain, .. Gzip([1, 2, 3])], [.. data, 1, 2, 3], false));
        await test("GZIP concatenated members plus external tail", () => Roundtrip("concat-tail", [.. plain, .. Gzip([1, 2, 3]), .. tail], [.. data, 1, 2, 3], true));
        byte[] header = [0x1f, 0x8b, 8, 0x1e, 0, 0, 0, 0, 0, 3, 3, 0, 5, 6, 7, .. Encoding.ASCII.GetBytes("named.img\0comment\0")];
        ushort headerCrc = (ushort)~Checksums.UpdateCrc32(uint.MaxValue, header);
        byte[] decorated = [.. header, (byte)headerCrc, (byte)(headerCrc >> 8), .. plain.AsSpan(10).ToArray(), .. tail];
        await test("GZIP optional header fields and header CRC", () => Roundtrip("headers", decorated, data, true));
        await test("GZIP TAR wrapper with external metadata still unwraps members", async () =>
        {
            string source = Path.Combine(area, "tar-source.txt"), archive = Path.Combine(area, "tar-footer.tar.gz");
            await File.WriteAllTextAsync(source, "tar member content");
            await service.CreateAsync([source], archive, new CreateOptions { Format = "tgz" });
            await using (var append = new FileStream(archive, FileMode.Append)) await append.WriteAsync(tail);
            var result = await service.ExtractAsync(archive, Path.Combine(area, "tar-out"), new ExtractOptions { Layout = ExtractLayout.Here });
            Check(result.Warnings.Count == 1 && File.ReadAllText(Path.Combine(result.Destination, "tar-source.txt")) == "tar member content", "TAR member or warning missing");
        });
        await test("GZIP existing same-name directory is preserved with automatic rename", async () =>
        {
            string path = await Save("directory-collision.img", [.. plain, .. tail]), destination = Path.Combine(area, "collision-out");
            string directory = Path.Combine(destination, "directory-collision.img"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "keep");
            var result = await service.ExtractAsync(path, destination, new ExtractOptions { Layout = ExtractLayout.Here });
            Check(result.Files == 1 && File.ReadAllBytes(Path.Combine(destination, "directory-collision (2).img")).SequenceEqual(data), "Directory collision was not renamed");
            Check(File.ReadAllText(Path.Combine(directory, "keep.txt")) == "keep", "Existing directory changed");
            foreach (var policy in new[] { OverwritePolicy.Skip, OverwritePolicy.Fail, OverwritePolicy.Overwrite })
            {
                bool failed = false;
                try
                {
                    var skipped = await service.ExtractAsync(path, destination, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = policy });
                    Check(policy == OverwritePolicy.Skip && skipped.Files == 0, "Unexpected directory overwrite behavior");
                }
                catch (IOException) { failed = true; }
                Check(failed == (policy != OverwritePolicy.Skip), "Directory conflict policy mismatch");
                Check(File.ReadAllText(Path.Combine(directory, "keep.txt")) == "keep", "Existing directory changed");
            }
        });

        byte[] crcBad = (byte[])plain.Clone(); crcBad[^8] ^= 1;
        byte[] sizeBad = (byte[])plain.Clone(); sizeBad[^4] ^= 1;
        byte[] headerBad = (byte[])decorated.Clone(); headerBad[header.Length] ^= 1;
        byte[] reserved = (byte[])plain.Clone(); reserved[3] = 0x20;
        byte[] methodBad = (byte[])plain.Clone(); methodBad[2] = 9;
        foreach (var (name, bytes) in new (string, byte[])[] {
            ("CRC mismatch with metadata", [.. crcBad, .. tail]), ("size mismatch with metadata", [.. sizeBad, .. tail]),
            ("truncated deflate", plain[..(plain.Length / 2)]), ("truncated trailer", plain[..^3]),
            ("invalid header CRC", headerBad), ("reserved flags", reserved), ("invalid method", methodBad),
            ("truncated second member", [.. plain, 0x1f, 0x8b, 8]), ("truncated member signature", [.. plain, 0x1f]),
            ("corrupt second member followed by tail", [.. plain, .. crcBad, .. tail]) })
        {
            await test("GZIP reject " + name, async () =>
            {
                string path = await Save(name, bytes), destination = Path.Combine(area, name + "-out");
                Directory.CreateDirectory(destination); string sentinel = Path.Combine(destination, "original.txt"); File.WriteAllText(sentinel, "keep");
                bool testFailed = false, extractionFailed = false;
                try { await service.Engine.TestAsync(path); } catch (ArchiveOperationException) { testFailed = true; }
                try { await service.ExtractAsync(path, destination, new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Overwrite }); }
                catch (ArchiveOperationException) { extractionFailed = true; }
                Check(testFailed && extractionFailed, "Damaged stream was accepted");
                Check(Directory.GetFiles(destination).Length == 1 && File.ReadAllText(sentinel) == "keep", "Failed GZIP committed partial output");
            });
        }
        await test("GZIP pause and resume managed decoding", async () =>
        {
            string path = await Save("pause", [.. plain, .. tail]); using var control = new JobControl(); Task? paused = null;
            var progress = new InlineProgress<JobProgress>(p => { if (p.Message.Contains("GZIP")) paused ??= control.PauseAsync(); });
            var task = Task.Run(async () => { using var scope = control.Enter(); return await service.ExtractAsync(path, Path.Combine(area, "pause-out"), new ExtractOptions { Layout = ExtractLayout.Here }, progress: progress); });
            while (paused == null) { if (task.IsCompleted) throw new Exception("No GZIP pause checkpoint"); await Task.Delay(5); }
            await paused.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(120); Check(control.IsPaused && !task.IsCompleted, "GZIP did not pause");
            control.Resume(); var result = await task.WaitAsync(TimeSpan.FromSeconds(10)); control.Complete();
            Check(File.ReadAllBytes(Directory.GetFiles(result.Destination).Single()).SequenceEqual(data), "Resume changed GZIP payload");
        });
        await test("GZIP cancellation while paused commits no output", async () =>
        {
            string path = await Save("cancel", plain), destination = Path.Combine(area, "cancel-out");
            using var cancellation = new CancellationTokenSource(); using var control = new JobControl(cancellation.Token); Task? paused = null;
            var progress = new InlineProgress<JobProgress>(p => { if (p.Message.Contains("GZIP")) paused ??= control.PauseAsync(); });
            var task = Task.Run(async () => { using var scope = control.Enter(); return await service.ExtractAsync(path, destination, new ExtractOptions { Layout = ExtractLayout.Here }, cancellation.Token, progress); });
            while (paused == null) { if (task.IsCompleted) throw new Exception("No GZIP pause checkpoint"); await Task.Delay(5); }
            await paused.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); bool canceled = false;
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && (!Directory.Exists(destination) || !Directory.EnumerateFiles(destination).Any()), "Canceled GZIP committed output");
        });
    }
}

using Hzip.Core;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

static class RepairTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Repair verification / 中文 😀 / intact payload.\n", 20)));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static int Signature(byte[] bytes, uint signature) { for (int i = 0; i <= bytes.Length - 4; i++) if (BitConverter.ToUInt32(bytes, i) == signature) return i; throw new Exception("Fixture signature missing"); }
    private static byte[] StoredZip(params (string Name, byte[] Data)[] items)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) foreach (var item in items) { using var stream = zip.CreateEntry(item.Name, CompressionLevel.NoCompression).Open(); stream.Write(item.Data); }
        return memory.ToArray();
    }
    private static byte[] BareDescriptor(bool wide, bool signed)
    {
        byte[] name = Encoding.UTF8.GetBytes("描述符/数据.txt"); byte[] compressed;
        using (var memory = new MemoryStream()) { using (var deflate = new DeflateStream(memory, CompressionLevel.Optimal, true)) deflate.Write(Body); compressed = memory.ToArray(); }
        using var result = new MemoryStream(); using var writer = new BinaryWriter(result);
        uint crc = ~Checksums.UpdateCrc32(uint.MaxValue, Body);
        writer.Write(0x04034b50U); writer.Write((ushort)(wide ? 45 : 20)); writer.Write((ushort)0x808); writer.Write((ushort)8); writer.Write(0U); writer.Write(0U); writer.Write(wide ? uint.MaxValue : 0U); writer.Write(wide ? uint.MaxValue : 0U);
        writer.Write((ushort)name.Length); writer.Write((ushort)(wide ? 20 : 0)); writer.Write(name);
        if (wide) { writer.Write((ushort)1); writer.Write((ushort)16); writer.Write(0UL); writer.Write(0UL); }
        writer.Write(compressed); if (signed) writer.Write(0x08074b50U); writer.Write(crc);
        if (wide) { writer.Write((ulong)compressed.Length); writer.Write((ulong)Body.Length); } else { writer.Write((uint)compressed.Length); writer.Write((uint)Body.Length); }
        return result.ToArray();
    }
    private static async Task<RepairResult> Recover(string folder, string label, byte[] damaged, string entry, byte[] expected, string? password = null, int codePage = 0)
    {
        string input = Path.Combine(folder, label + ".damaged.zip"), output = Path.Combine(folder, label + ".recovered.zip"); await File.WriteAllBytesAsync(input, damaged);
        string sourceHash = await ArchiveService.HashAsync(input); var result = await ZipRepair.RepairAsync(input, output, codePage: codePage, password: password);
        Check(sourceHash == await ArchiveService.HashAsync(input), "Repair changed the source archive");
        using var zip = ZipFile.OpenRead(output); var member = zip.GetEntry(entry); Check(member != null, "Recovered filename mismatch: " + entry);
        using var read = member!.Open(); using var payload = new MemoryStream(); await read.CopyToAsync(payload); Check(payload.ToArray().SequenceEqual(expected), "Recovered data differs");
        Check(result.ReportPath != null && File.Exists(result.ReportPath), "Recovery receipt missing"); using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath!));
        Check(report.RootElement.GetProperty("SourceSHA256").GetString() == sourceHash, "Receipt source fingerprint");
        return result;
    }
    public static async Task RunAsync(string root, ArchiveService service, Func<string, Func<Task>, Task> test)
    {
        string area = Path.Combine(root, "repair-plus"); Directory.CreateDirectory(area);
        await test("Repair+ central directory reconstructs a damaged local signature and size", async () =>
        {
            byte[] raw = StoredZip(("中央目录/说明.txt", Body)); raw[0] = 0; Array.Fill(raw, (byte)0xff, 14, 12);
            var report = await Recover(area, "bad-local", raw, "中央目录/说明.txt", Body); Check(report.Entries.Single().MetadataSource == "中央目录", "Central metadata not used");
        });
        await test("Repair+ valid local CRC overrides a damaged central CRC", async () =>
        {
            byte[] raw = StoredZip(("local.txt", Body)); int central = Signature(raw, 0x02014b50); raw[central + 16] ^= 0x40;
            var report = await Recover(area, "bad-central", raw, "local.txt", Body); Check(report.Recovered == 1, "Duplicate recovery");
        });
        await test("Repair+ self-extracting prefix correctly rebases central offsets", async () =>
        {
            byte[] original = StoredZip(("sfx.txt", Body)), prefix = new byte[128]; prefix[0] = (byte)'M'; prefix[1] = (byte)'Z';
            var result = await Recover(area, "sfx-prefix", prefix.Concat(original).ToArray(), "sfx.txt", Body);
            Check(result.Entries.Single().HeaderOffset == 128, "SFX header offset was not rebased");
        });
        await test("Repair+ malformed ZIP64 end record does not defeat intact local recovery", async () =>
        {
            byte[] original = StoredZip(("tail.txt", Body)); int end = Signature(original, 0x06054b50);
            using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory); writer.Write(original.AsSpan(0, end));
            writer.Write(0x06064b50U); writer.Write(44UL); writer.Write((ushort)45); writer.Write((ushort)45); writer.Write(0U); writer.Write(0U); writer.Write(1UL); writer.Write(1UL); writer.Write(ulong.MaxValue); writer.Write(ulong.MaxValue);
            byte[] eocd = original[end..]; Array.Fill(eocd, (byte)255, 12, 8); writer.Write(eocd);
            await Recover(area, "bad-zip64-tail", memory.ToArray(), "tail.txt", Body);
        });
        foreach (var (wide, signed) in new[] { (false, false), (true, false), (true, true) })
            await test($"Repair+ {(wide ? "64" : "32")}-bit {(signed ? "signed" : "unsigned")} descriptor at EOF", async () =>
            { await Recover(area, $"descriptor-{wide}-{signed}", BareDescriptor(wide, signed), "描述符/数据.txt", Body); });
        await test("Repair+ damaged member is skipped while healthy siblings survive", async () =>
        {
            byte[] raw = StoredZip(("bad.txt", Body), ("good.txt", Body)); int data = 30 + BitConverter.ToUInt16(raw, 26) + BitConverter.ToUInt16(raw, 28); raw[data] ^= 0x20;
            var report = await Recover(area, "partial", raw, "good.txt", Body); Check(report.Recovered == 1 && report.Skipped.Any(x => x.Contains("bad.txt")), "Bad entry not reported");
            using var zip = ZipFile.OpenRead(report.Output); Check(zip.GetEntry("bad.txt") == null, "Invalid payload was included");
        });
        string fixtures = Path.Combine(TestPaths.Repository, "tests", "fixtures", "compatibility");
        using var fixtureProof = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "expected.json")));
        foreach (string codec in new[] { "bzip2", "lzma" }) await test("Repair+ truncated central directory with " + codec + " payload", async () =>
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(fixtures, "python-" + codec + ".zip"));
            using var original = ZipFile.OpenRead(Path.Combine(fixtures, "python-deflate.zip")); using var stream = original.Entries[0].Open(); using var payload = new MemoryStream(); await stream.CopyToAsync(payload);
            await Recover(area, "algorithm-" + codec, raw[..Signature(raw, 0x02014b50)], "中文目录/日本語 😀.txt", payload.ToArray());
        });
        await test("Repair+ OEM 437 filenames remain correct", async () =>
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(fixtures, "oem437.zip")); using var original = ZipFile.OpenRead(Path.Combine(fixtures, "python-deflate.zip")); using var stream = original.Entries[0].Open(); using var payload = new MemoryStream(); await stream.CopyToAsync(payload);
            await Recover(area, "oem437", raw[..Signature(raw, 0x02014b50)], "café.txt", payload.ToArray(), codePage: 437);
        });
        await test("Repair+ verified Unicode-path extra field overrides legacy name", async () =>
        {
            byte[] rawName = Encoding.ASCII.GetBytes("fallback.txt"), unicode = Encoding.UTF8.GetBytes("正确目录/名称.txt");
            using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory); uint crc = ~Checksums.UpdateCrc32(uint.MaxValue, Body);
            writer.Write(0x04034b50U); writer.Write((ushort)20); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0U); writer.Write(crc); writer.Write((uint)Body.Length); writer.Write((uint)Body.Length); writer.Write((ushort)rawName.Length); writer.Write((ushort)(unicode.Length + 9)); writer.Write(rawName);
            writer.Write((ushort)0x7075); writer.Write((ushort)(unicode.Length + 5)); writer.Write((byte)1); writer.Write(~Checksums.UpdateCrc32(uint.MaxValue, rawName)); writer.Write(unicode); writer.Write(Body);
            await Recover(area, "unicode-extra", memory.ToArray(), "正确目录/名称.txt", Body);
        });
        foreach (string codec in new[] { "bzip2", "lzma" }) await test("Repair+ authenticated Unicode AES recovery: " + codec, async () =>
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(fixtures, "pyzipper-aes-" + codec + ".zip"));
            using var original = ZipFile.OpenRead(Path.Combine(fixtures, "python-deflate.zip")); using var stream = original.Entries[0].Open(); using var payload = new MemoryStream(); await stream.CopyToAsync(payload);
            var result = await Recover(area, "aes-" + codec, raw[..Signature(raw, 0x02014b50)], "独立样本.txt", payload.ToArray(), "中文 AES 兼容密码");
            Check(result.Entries.Single().Verification.Contains("AES/HMAC"), "Missing AES authentication evidence"); Check(!File.ReadAllText(result.ReportPath!).Contains("中文 AES 兼容密码"), "Password included in receipt");
        });
        await test("Repair+ incorrect AES password and tampered authentication yield no output", async () =>
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(fixtures, "pyzipper-aes-lzma.zip")); Array.Resize(ref raw, Signature(raw, 0x02014b50));
            foreach (bool corrupt in new[] { false, true })
            {
                byte[] data = raw.ToArray(); if (corrupt) data[30 + BitConverter.ToUInt16(data, 26) + BitConverter.ToUInt16(data, 28) + 22] ^= 0x20;
                string input = Path.Combine(area, "invalid-aes-" + corrupt + ".zip"), output = Path.Combine(area, "invalid-aes-out-" + corrupt + ".zip"); File.WriteAllBytes(input, data); bool failed = false;
                try { await ZipRepair.RepairAsync(input, output, password: corrupt ? "中文 AES 兼容密码" : "wrong"); } catch (ArchiveOperationException) { failed = true; }
                Check(failed && !File.Exists(output) && !File.Exists(output + ".repair.json"), "Unverified encrypted data accepted");
            }
        });
        await test("Repair+ AE-2 with omitted CRC is accepted only after authentication", async () =>
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(fixtures, "pyzipper-aes-lzma.zip")); Array.Resize(ref raw, Signature(raw, 0x02014b50));
            Array.Clear(raw, 14, 4); int extra = 30 + BitConverter.ToUInt16(raw, 26), end = extra + BitConverter.ToUInt16(raw, 28);
            for (int p = extra; p + 4 <= end; p += 4 + BitConverter.ToUInt16(raw, p + 2)) if (BitConverter.ToUInt16(raw, p) == 0x9901) { raw[p + 4] = 2; raw[p + 5] = 0; break; }
            using var original = ZipFile.OpenRead(Path.Combine(fixtures, "python-deflate.zip")); using var stream = original.Entries[0].Open(); using var payload = new MemoryStream(); await stream.CopyToAsync(payload);
            var result = await Recover(area, "aes-v2", raw, "独立样本.txt", payload.ToArray(), "中文 AES 兼容密码"); Check(result.Entries.Single().Verification == "AES/HMAC", "AE-2 incorrectly requires a stored CRC");
        });
        await test("Repair+ traditional encrypted descriptor retains password verification semantics", async () =>
        {
            string input = Path.Combine(area, "crypto-source.txt"), archive = Path.Combine(area, "crypto-original.zip"); File.WriteAllBytes(input, Body);
            await service.CreateAsync([input], archive, new CreateOptions { ZipEncryption = "ZipCrypto", Password = "repair-pass" }); byte[] raw = File.ReadAllBytes(archive);
            await Recover(area, "traditional", raw[..Signature(raw, 0x02014b50)], "crypto-source.txt", Body, "repair-pass");
        });
        await test("Repair+ intact nested ZIP bytes are not promoted to top-level entries", async () =>
        {
            byte[] nested = StoredZip(("inner.txt", Body)), raw = StoredZip(("nested.zip", nested));
            var result = await Recover(area, "nested", raw, "nested.zip", nested); Check(result.Recovered == 1, "Nested payload incorrectly flattened");
        });
        await test("Repair+ unsafe paths and known symlinks are omitted", async () =>
        {
            byte[] raw = StoredZip(("../escape.txt", Body), ("link", Body), ("safe.txt", Body)); int central = Signature(raw, 0x02014b50);
            int next = central + 46 + BitConverter.ToUInt16(raw, central + 28) + BitConverter.ToUInt16(raw, central + 30) + BitConverter.ToUInt16(raw, central + 32);
            raw[next + 5] = 3; BitConverter.GetBytes(unchecked((uint)0xa1ff0000)).CopyTo(raw, next + 38);
            var result = await Recover(area, "unsafe", raw, "safe.txt", Body); Check(result.Recovered == 1 && result.Skipped.Count >= 2, "Unsafe records included");
        });
        await test("Repair+ source, existing output and existing report cannot be overwritten", async () =>
        {
            string input = Path.Combine(area, "protected.zip"), output = Path.Combine(area, "protected-out.zip"); File.WriteAllBytes(input, StoredZip(("file.txt", Body))); string hash = await ArchiveService.HashAsync(input);
            foreach (string target in new[] { input, output })
            {
                if (target == output) File.WriteAllText(output + ".repair.json", "external receipt"); bool failed = false;
                try { await ZipRepair.RepairAsync(input, target); } catch (Exception error) when (error is ArchiveOperationException or IOException) { failed = true; }
                Check(failed && await ArchiveService.HashAsync(input) == hash, "Repair overwrote protected file");
            }
            Check(File.ReadAllText(output + ".repair.json") == "external receipt" && !File.Exists(output), "Existing report overwritten");
        });
        await test("Repair+ cancellation leaves source unchanged and no final output", async () =>
        {
            string input = Path.Combine(area, "cancel.zip"), output = Path.Combine(area, "cancel-out.zip"); File.WriteAllBytes(input, StoredZip(("file.txt", Body))); string hash = await ArchiveService.HashAsync(input);
            using var cancellation = new CancellationTokenSource(); bool canceled = false;
            try { await ZipRepair.RepairAsync(input, output, cancellation.Token, new Immediate(p => cancellation.Cancel())); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && !File.Exists(output) && !File.Exists(output + ".repair.json") && await ArchiveService.HashAsync(input) == hash, "Canceled repair committed output");
        });
    }
    private sealed class Immediate(Action<JobProgress> action) : IProgress<JobProgress> { public void Report(JobProgress value) => action(value); }
}

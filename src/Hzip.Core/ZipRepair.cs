using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Hzip.Core;

public sealed record RepairedEntry(string Name, long Size, string Crc32, string Verification, long HeaderOffset, string MetadataSource);
public sealed record RepairResult(string Output, int Recovered, IReadOnlyList<string> Skipped)
{
    public string? ReportPath { get; init; }
    public IReadOnlyList<RepairedEntry> Entries { get; init; } = [];
}
public static partial class ZipRepair
{
    public static async Task<RepairResult> RepairAsync(string source, string output, CancellationToken ct = default,
        IProgress<JobProgress>? progress = null, int codePage = 0, string? password = null)
    {
        source = Path.GetFullPath(source); output = Path.GetFullPath(output); string reportPath = output + ".repair.json";
        if (source.Equals(output, StringComparison.OrdinalIgnoreCase)) throw new ArchiveOperationException("修复结果必须另存为一个新文件。");
        if (File.Exists(output) || File.Exists(reportPath)) throw new IOException("修复输出或恢复清单已存在，请选择一个新名称。");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var workspace = new Workspace(); string staged = Path.Combine(workspace.Path, "repaired.zip"), payload = Path.Combine(workspace.Path, "candidate.bin");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var signatures = await ScanAsync(input, ct, progress); var skipped = new List<string>(); var entries = new List<RepairedEntry>();
        var descriptors = DescriptorIndex(input, signatures); var groups = new SortedDictionary<long, List<Record>>();
        void Add(Record? item) { if (item == null) return; if (!groups.TryGetValue(item.HeaderStart, out var group)) groups.Add(item.HeaderStart, group = []); group.Add(item); }
        var layout = CentralLayout(input, signatures);
        foreach (long position in signatures.Central)
        {
            await JobControl.CheckpointAsync(ct);
            if (layout is { } range && (position < range.Start || position >= range.End)) continue;
            try
            {
                var record = CentralRecord(input, position, codePage); if (record == null) continue;
                foreach (long prefix in new[] { layout?.Prefix ?? 0, 0, signatures.Local.FirstOrDefault() }.Distinct())
                    Add(LocalRecord(input, checked(record.HeaderStart + prefix), codePage, record));
            }
            catch (Exception error) when (error is IOException or ArgumentException or OverflowException) { }
        }
        foreach (long position in signatures.Local)
        {
            await JobControl.CheckpointAsync(ct);
            try { Add(LocalRecord(input, position, codePage)); }
            catch (Exception error) when (error is IOException or ArgumentException or OverflowException) { }
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long coveredUntil = -1;
        using (var destination = System.IO.Compression.ZipFile.Open(staged, ZipArchiveMode.Create))
        {
            foreach (var group in groups)
            {
                await JobControl.CheckpointAsync(ct);
                // Do not promote intact nested ZIP records from a recovered member's payload.
                if (group.Key < coveredUntil) continue;
                if (group.Value.All(header => header.Directory && header.Name.Replace('\\', '/').Split('/').All(part => part is "" or "."))) continue;
                if (group.Value.Any(record => record.Link)) { skipped.Add(group.Value[0].Name + "：符号链接条目不予恢复"); continue; }
                Record? verified = null; uint actualCrc = 0; string lastReason = "缺少完整且可校验的数据长度"; string label = group.Value[0].Name;
                foreach (var header in group.Value)
                {
                    try
                    {
                        if (header.Directory && header.Name.Replace('\\', '/').Split('/').All(part => part is "" or ".")) continue;
                        string name = SafePaths.Relative(header.Name); label = name;
                        if (Encoding.UTF8.GetByteCount(name) > 65530) throw new ArchiveOperationException("恢复后的 UTF-8 文件名过长");
                        if (header.Link) throw new ArchiveOperationException("符号链接条目不予恢复");
                        if ((header.Flags & 1) != 0 && string.IsNullOrEmpty(password)) throw new ArchiveOperationException("加密条目需要正确的原归档密码");
                        if (names.Contains(name) || !header.Directory && directories.Contains(name)) throw new ArchiveOperationException("重名或文件/目录冲突");
                        for (int slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                            if (files.Contains(name[..slash])) throw new ArchiveOperationException("父目录与已恢复文件冲突");
                        foreach (var candidate in Candidates(header, descriptors).Distinct())
                        {
                            await JobControl.CheckpointAsync(ct);
                            if (candidate.Packed < 0 || candidate.Size < 0 || candidate.DataStart < 0 || candidate.Packed > input.Length - candidate.DataStart) { lastReason = "数据不完整或长度超出文件范围"; continue; }
                            try
                            {
                                actualCrc = await DecodeCandidateAsync(input, candidate, payload, password, ct);
                                verified = candidate with { Name = name }; break;
                            }
                            catch (Exception error) when (error is InvalidDataException or IOException or ArchiveOperationException or ICSharpCode.SharpZipLib.SharpZipBaseException)
                            { lastReason = error.Message; }
                        }
                    }
                    catch (Exception error) when (error is ArchiveOperationException or ArgumentException) { lastReason = error.Message; }
                    if (verified != null) break;
                }
                if (verified == null) { if (skipped.Count < 10000) skipped.Add(label + "：" + lastReason); continue; }
                var item = destination.CreateEntry(verified.Name + (verified.Directory ? "/" : ""), CompressionLevel.Optimal);
                try { item.LastWriteTime = DosDate(verified.Stamp); } catch (ArgumentOutOfRangeException) { }
                if (!verified.Directory) { await using var body = item.Open(); await using var data = File.OpenRead(payload); await JobControl.CopyAsync(data, body, ct); }
                names.Add(verified.Name); if (verified.Directory) directories.Add(verified.Name); else files.Add(verified.Name);
                for (int slash = verified.Name.IndexOf('/'); slash >= 0; slash = verified.Name.IndexOf('/', slash + 1)) directories.Add(verified.Name[..slash]);
                string verification = verified.Method == 99 && (verified.Flags & 1) != 0 ? IsAesV2(verified) ? "AES/HMAC" : "CRC32 + AES/HMAC" : "CRC32";
                entries.Add(new RepairedEntry(verified.Name, verified.Size, actualCrc.ToString("X8"), verification, verified.HeaderStart, verified.MetadataSource));
                coveredUntil = verified.DataStart + verified.Packed;
                progress?.Report(new JobProgress(25 + 60.0 * Math.Max(0, group.Key) / Math.Max(1, input.Length), verified.Name, "恢复 ZIP", entries.Count));
            }
        }
        if (entries.Count == 0) throw new ArchiveOperationException("没有找到通过 CRC 或加密认证验证的完整条目。文件数据已经丢失时无法凭空修复。", details: string.Join('\n', skipped));
        progress?.Report(new JobProgress(null, "正在校验恢复结果…", "验证修复 ZIP"));
        await new ArchiveEngine().TestAsync(staged, cancellation: ct);
        string sourceHash = await ArchiveService.HashAsync(source, ct);
        string stagedReport = Path.Combine(workspace.Path, "repair.json");
        await File.WriteAllTextAsync(stagedReport, JsonSerializer.Serialize(new { Source = source, SourceSHA256 = sourceHash, SourceBytes = input.Length, Output = output, Recovered = entries, Skipped = skipped, OutputEncrypted = false }, new JsonSerializerOptions { WriteIndented = true }), ct);
        await JobControl.CheckpointAsync(ct);
        ArchiveService.AtomicCommit(staged, output, overwrite: false);
        string? savedReport = reportPath;
        try { ArchiveService.AtomicCommit(stagedReport, reportPath, overwrite: false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { savedReport = null; skipped.Add("恢复清单未能保存：" + error.Message); }
        progress?.Report(new JobProgress(100, "修复结果已另存，原文件保持不变", "完成"));
        return new RepairResult(output, entries.Count, skipped) { ReportPath = savedReport, Entries = entries };
    }
    private static bool IsAesV2(Record item) => item.Method == 99 && ExtraField(item.Extra, 0x9901) is { Length: >= 7 } aes && BitConverter.ToUInt16(aes) == 2;
    private static DateTime DosDate(uint stamp) => new(1980 + (int)(stamp >> 25), (int)(stamp >> 21 & 15), (int)(stamp >> 16 & 31), (int)(stamp >> 11 & 31), (int)(stamp >> 5 & 63), (int)(stamp & 31) * 2);
    private static async Task<uint> DecodeCandidateAsync(Stream input, Record item, string payload, string? password, CancellationToken ct)
    {
        if (item.Directory && item.Size != 0) throw new InvalidDataException("目录条目包含非空数据");
        if ((item.Flags & 1) == 0 && item.Method is 0 or 8)
        {
            input.Position = item.DataStart; using var slice = new LimitedInput(input, item.Packed); await using var output = File.Create(payload);
            long count = 0; uint crc = uint.MaxValue; byte[] encoded = new byte[128 * 1024], decoded = new byte[128 * 1024];
            async Task Save(int length)
            {
                count += length; if (count > item.Size) throw new InvalidDataException("解码数据超过声明长度");
                crc = Checksums.UpdateCrc32(crc, decoded.AsSpan(0, length)); await output.WriteAsync(decoded.AsMemory(0, length), ct);
            }
            if (item.Method == 0)
            {
                while (true) { await JobControl.CheckpointAsync(ct); int length = await slice.ReadAsync(decoded, ct); if (length == 0) break; await Save(length); }
                if (item.Packed != item.Size) throw new InvalidDataException("存储条目的长度不一致");
            }
            else
            {
                var inflater = new Inflater(true);
                while (!inflater.IsFinished)
                {
                    await JobControl.CheckpointAsync(ct);
                    if (inflater.IsNeedingInput) { int read = await slice.ReadAsync(encoded, ct); if (read == 0) throw new InvalidDataException("Deflate 数据被截断"); inflater.SetInput(encoded, 0, read); }
                    int length = inflater.Inflate(decoded);
                    if (length > 0) await Save(length);
                    else if (!inflater.IsFinished && !inflater.IsNeedingInput) throw new InvalidDataException("Deflate 数据无法解码");
                }
                if (inflater.TotalIn != item.Packed) throw new InvalidDataException("Deflate 压缩长度不匹配");
            }
            if (count != item.Size || ~crc != item.Crc) throw new InvalidDataException("CRC 或大小验证失败");
            return ~crc;
        }
        using var candidateWork = new Workspace(); string wrapper = Path.Combine(candidateWork.Path, "candidate.zip"), unpacked = Path.Combine(candidateWork.Path, "out");
        await MakeWrapperAsync(input, item, wrapper, ct);
        var service = new ArchiveService(); await service.ExtractAsync(wrapper, unpacked, new ExtractOptions { Layout = ExtractLayout.Here, Password = password }, ct);
        string file = Path.Combine(unpacked, "payload.bin");
        if (!File.Exists(file) || new FileInfo(file).Length != item.Size) throw new InvalidDataException("恢复数据大小不匹配");
        uint checksum = await Checksums.Crc32Async(file, ct);
        if (!IsAesV2(item) && checksum != item.Crc) throw new InvalidDataException("恢复数据 CRC 校验失败");
        await using var from = File.OpenRead(file); await using var to = File.Create(payload); await JobControl.CopyAsync(from, to, ct); return checksum;
    }
}

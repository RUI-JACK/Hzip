using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Hzip.Core;

internal sealed record SourceItem(string FullPath, string RelativePath, bool Directory, long Length, DateTime Modified);
public static class Checksums
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n; for (int bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xedb88320U ^ c >> 1 : c >> 1; return c;
    }).ToArray();
    public static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> buffer) { foreach (byte b in buffer) crc = CrcTable[(crc ^ b) & 255] ^ crc >> 8; return crc; }
    public static async Task<uint> Crc32Async(string file, CancellationToken ct = default)
    {
        await using var input = File.OpenRead(file); byte[] buffer = new byte[1024 * 1024]; uint crc = uint.MaxValue;
        while (true) { await JobControl.CheckpointAsync(ct); int n = await input.ReadAsync(buffer, ct); if (n == 0) break; crc = UpdateCrc32(crc, buffer.AsSpan(0, n)); }
        return ~crc;
    }
    public static async Task<ushort> Crc16Async(string file, CancellationToken ct)
    {
        await using var input = File.OpenRead(file); byte[] buffer = new byte[1024 * 1024]; ushort crc = 0;
        while (true)
        {
            await JobControl.CheckpointAsync(ct);
            int n = await input.ReadAsync(buffer, ct); if (n == 0) break;
            for (int i = 0; i < n; i++) { crc ^= buffer[i]; for (int bit = 0; bit < 8; bit++) crc = (ushort)(((crc & 1) != 0) ? crc >> 1 ^ 0xa001 : crc >> 1); }
        }
        return crc;
    }
    internal static uint DosTime(DateTime date)
    {
        if (date.Year < 1980) date = new DateTime(1980, 1, 1); if (date.Year > 2107) date = new DateTime(2107, 12, 31);
        return (uint)((date.Year - 1980) << 25 | date.Month << 21 | date.Day << 16 | date.Hour << 11 | date.Minute << 5 | date.Second / 2);
    }
}
internal static class SpecialFormats
{
    internal static IReadOnlyList<SourceItem> Sources(IReadOnlyList<string> inputs)
    {
        string common = ArchiveService.CommonParent(inputs); var items = new Dictionary<string, SourceItem>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            JobControl.Checkpoint();
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArchiveOperationException("此格式的写入器不支持源目录中的链接，请单独选择普通文件。");
            bool directory = (attributes & FileAttributes.Directory) != 0;
            string relative = SafePaths.Relative(System.IO.Path.GetRelativePath(common, path));
            items[relative] = new SourceItem(path, relative, directory, directory ? 0 : new FileInfo(path).Length, File.GetLastWriteTime(path));
            if (directory) foreach (string child in Directory.EnumerateFileSystemEntries(path)) Add(child);
        }
        foreach (var input in inputs) Add(input);
        return items.Values.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
    }
    public static async Task CreateAsync(ArchiveEngine engine, IReadOnlyList<string> inputs, string output, CreateOptions options, CancellationToken ct, IProgress<JobProgress>? progress)
    {
        var sources = Sources(inputs);
        if (options.Format == "lzh") await WriteLhaAsync(sources, output, ct, progress);
        else if (options.Format == "zipx") await WriteZipXAsync(engine, sources, output, options.Level, ct, progress);
        else if (options.Format == "iso") await WriteIsoAsync(sources, output, ct, progress);
        else throw new NotSupportedException(options.Format);
    }
    private static async Task WriteLhaAsync(IReadOnlyList<SourceItem> sources, string output, CancellationToken ct, IProgress<JobProgress>? progress)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); var encoding = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        await using var destination = File.Create(output); int done = 0;
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            byte[] name = encoding.GetBytes(source.RelativePath.Replace('/', '\\') + (source.Directory ? "\\" : ""));
            if (name.Length > 233 || source.Length > uint.MaxValue) throw new ArchiveOperationException("LZH 兼容写入要求路径不超过 233 个编码字节、单文件小于 4 GB。请改用 ZIP 或 7Z。");
            ushort crc = source.Directory ? (ushort)0 : await Checksums.Crc16Async(source.FullPath, ct);
            byte[] header = new byte[24 + name.Length];
            using (var memory = new MemoryStream(header))
            using (var writer = new BinaryWriter(memory))
            {
                writer.Write((byte)(header.Length - 2)); writer.Write((byte)0); writer.Write(Encoding.ASCII.GetBytes(source.Directory ? "-lhd-" : "-lh0-"));
                writer.Write((uint)source.Length); writer.Write((uint)source.Length); writer.Write(Checksums.DosTime(source.Modified));
                writer.Write((byte)(source.Directory ? 0x10 : 0x20)); writer.Write((byte)0); writer.Write((byte)name.Length); writer.Write(name); writer.Write(crc);
            }
            header[1] = (byte)header.AsSpan(2).ToArray().Sum(b => b);
            await destination.WriteAsync(header, ct);
            if (!source.Directory) { await using var file = File.OpenRead(source.FullPath); await JobControl.CopyAsync(file, destination, ct); }
            progress?.Report(new JobProgress(100.0 * ++done / Math.Max(1, sources.Count), source.RelativePath, "创建 LZH"));
        }
        await destination.WriteAsync(new byte[] { 0 }, ct);
    }
    private sealed record ZipRecord(byte[] Name, uint Crc, ulong Size, ulong Packed, ulong Offset, uint Time, bool Directory);
    private static async Task WriteZipXAsync(ArchiveEngine engine, IReadOnlyList<SourceItem> sources, string output, int level, CancellationToken ct, IProgress<JobProgress>? progress)
    {
        using var work = new Workspace(); var records = new List<ZipRecord>();
        await using var destination = File.Create(output); using var writer = new BinaryWriter(destination, Encoding.UTF8, true);
        int done = 0;
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested(); byte[] name = Encoding.UTF8.GetBytes(source.RelativePath + (source.Directory ? "/" : ""));
            if (name.Length > ushort.MaxValue) throw new ArchiveOperationException("ZIPX 文件名过长。");
            string packedFile = System.IO.Path.Combine(work.Path, "payload.xz"); ulong packed = 0; uint crc = 0;
            if (!source.Directory)
            {
                if (File.Exists(packedFile)) File.Delete(packedFile);
                crc = await Checksums.Crc32Async(source.FullPath, ct);
                await engine.RunAsync(["a", "-txz", "-mx=" + level, "-mmt=on", "-sccUTF-8", "-y", "--", packedFile, source.FullPath], ct, progress);
                packed = (ulong)new FileInfo(packedFile).Length;
            }
            ulong size = (ulong)source.Length, offset = (ulong)destination.Position; bool big = size >= uint.MaxValue || packed >= uint.MaxValue;
            uint stamp = Checksums.DosTime(source.Modified); byte[] extra;
            using (var stream = new MemoryStream())
            {
                using var extraWriter = new BinaryWriter(stream, Encoding.UTF8, true);
                if (big) { extraWriter.Write((ushort)1); extraWriter.Write((ushort)16); extraWriter.Write(size); extraWriter.Write(packed); }
                extra = stream.ToArray();
            }
            writer.Write(0x04034b50U); writer.Write((ushort)63); writer.Write((ushort)0x800); writer.Write((ushort)(source.Directory ? 0 : 95));
            writer.Write(stamp); writer.Write(crc); writer.Write(big ? uint.MaxValue : (uint)packed); writer.Write(big ? uint.MaxValue : (uint)size);
            writer.Write((ushort)name.Length); writer.Write((ushort)extra.Length); writer.Write(name); writer.Write(extra);
            if (!source.Directory) { await using var input = File.OpenRead(packedFile); await JobControl.CopyAsync(input, destination, ct); }
            records.Add(new ZipRecord(name, crc, size, packed, offset, stamp, source.Directory));
            progress?.Report(new JobProgress(100.0 * ++done / Math.Max(1, sources.Count), source.RelativePath, "创建 ZIPX · XZ"));
        }
        ulong directoryOffset = (ulong)destination.Position;
        foreach (var item in records)
        {
            byte[] extra;
            using (var stream = new MemoryStream())
            {
                using var ew = new BinaryWriter(stream, Encoding.UTF8, true);
                int bytes = (item.Size >= uint.MaxValue ? 8 : 0) + (item.Packed >= uint.MaxValue ? 8 : 0) + (item.Offset >= uint.MaxValue ? 8 : 0);
                if (bytes > 0)
                {
                    ew.Write((ushort)1); ew.Write((ushort)bytes);
                    if (item.Size >= uint.MaxValue) ew.Write(item.Size); if (item.Packed >= uint.MaxValue) ew.Write(item.Packed); if (item.Offset >= uint.MaxValue) ew.Write(item.Offset);
                }
                extra = stream.ToArray();
            }
            writer.Write(0x02014b50U); writer.Write((ushort)63); writer.Write((ushort)63); writer.Write((ushort)0x800); writer.Write((ushort)(item.Directory ? 0 : 95));
            writer.Write(item.Time); writer.Write(item.Crc); writer.Write(item.Packed >= uint.MaxValue ? uint.MaxValue : (uint)item.Packed); writer.Write(item.Size >= uint.MaxValue ? uint.MaxValue : (uint)item.Size);
            writer.Write((ushort)item.Name.Length); writer.Write((ushort)extra.Length); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(item.Directory ? 0x10U : 0x20U);
            writer.Write(item.Offset >= uint.MaxValue ? uint.MaxValue : (uint)item.Offset); writer.Write(item.Name); writer.Write(extra);
        }
        ulong directorySize = (ulong)destination.Position - directoryOffset;
        if (records.Count >= ushort.MaxValue || directoryOffset >= uint.MaxValue || directorySize >= uint.MaxValue)
        {
            ulong recordOffset = (ulong)destination.Position;
            writer.Write(0x06064b50U); writer.Write(44UL); writer.Write((ushort)63); writer.Write((ushort)63); writer.Write(0U); writer.Write(0U);
            writer.Write((ulong)records.Count); writer.Write((ulong)records.Count); writer.Write(directorySize); writer.Write(directoryOffset);
            writer.Write(0x07064b50U); writer.Write(0U); writer.Write(recordOffset); writer.Write(1U);
        }
        writer.Write(0x06054b50U); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)Math.Min(records.Count, ushort.MaxValue)); writer.Write((ushort)Math.Min(records.Count, ushort.MaxValue));
        writer.Write((uint)Math.Min(directorySize, uint.MaxValue)); writer.Write((uint)Math.Min(directoryOffset, uint.MaxValue)); writer.Write((ushort)0);
    }
    private static Task WriteIsoAsync(IReadOnlyList<SourceItem> sources, string output, CancellationToken ct, IProgress<JobProgress>? progress)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ISO 创建需要 Windows IMAPI2。");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            if (!OperatingSystem.IsWindows()) { completion.SetException(new PlatformNotSupportedException()); return; }
            object? image = null, root = null, result = null, rawStream = null;
            try
            {
                using var work = new Workspace(); int done = 0;
                foreach (var source in sources)
                {
                    JobControl.Checkpoint(ct); string target = SafePaths.Within(work.Path, source.RelativePath);
                    if (source.Directory) Directory.CreateDirectory(target);
                    else { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!); File.Copy(source.FullPath, target); }
                    progress?.Report(new JobProgress(40.0 * ++done / Math.Max(1, sources.Count), source.RelativePath, "准备 ISO"));
                }
                var type = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage") ?? throw new ArchiveOperationException("系统未提供 ISO 创建组件。");
                image = Activator.CreateInstance(type)!; dynamic fs = image;
                fs.ChooseImageDefaultsForMediaType(12); fs.FileSystemsToCreate = 3; fs.VolumeName = "HZIP";
                root = fs.Root; ((dynamic)root).AddTree(work.Path, false);
                ct.ThrowIfCancellationRequested(); progress?.Report(new JobProgress(null, "正在生成 ISO 9660 / Joliet 镜像…", "创建 ISO"));
                result = fs.CreateResultImage(); rawStream = ((dynamic)result).ImageStream; var input = (IStream)rawStream;
                input.Stat(out var stat, 1); long total = stat.cbSize, written = 0;
                using var destination = File.Create(output); byte[] buffer = new byte[1024 * 1024]; IntPtr read = Marshal.AllocHGlobal(4);
                try
                {
                    while (true)
                    {
                        JobControl.Checkpoint(ct); input.Read(buffer, buffer.Length, read); int count = Marshal.ReadInt32(read); if (count == 0) break;
                        destination.Write(buffer, 0, count); written += count; progress?.Report(new JobProgress(40 + 60.0 * written / Math.Max(1, total), TextFormat.Bytes(written), "写入 ISO"));
                    }
                }
                finally { Marshal.FreeHGlobal(read); }
                completion.SetResult();
            }
            catch (OperationCanceledException) { completion.SetCanceled(ct); }
            catch (Exception error) { completion.SetException(error); }
            finally
            {
                foreach (object? value in new[] { rawStream, result, root, image }) if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
            }
        }) { IsBackground = true, Name = "Hzip ISO writer" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
}

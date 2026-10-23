using System.Text;

namespace Hzip.Core;

public static partial class ZipRepair
{
    private sealed record Record(string Name, ushort Flags, ushort Method, uint Stamp, uint Crc, long Packed, long Size,
        long HeaderStart, long DataStart, byte[] Extra, bool Directory, bool Link, string MetadataSource);
    private sealed record Descriptor(long DataStart, long Position, uint Crc, long Packed, long Size);
    private sealed record SignatureIndex(List<long> Local, List<long> Central, List<long> Descriptor, List<long> End, List<long> End64);
    private static async Task<SignatureIndex> ScanAsync(Stream input, CancellationToken ct, IProgress<JobProgress>? progress)
    {
        input.Position = 0; var result = new SignatureIndex([], [], [], [], []); byte[] buffer = new byte[1024 * 1024]; uint rolling = 0; long offset = 0;
        while (true)
        {
            await JobControl.CheckpointAsync(ct); int length = await input.ReadAsync(buffer, ct); if (length == 0) break;
            for (int i = 0; i < length; i++, offset++)
            {
                rolling = rolling >> 8 | (uint)buffer[i] << 24;
                switch (rolling)
                {
                    case 0x04034b50: result.Local.Add(offset - 3); break;
                    case 0x02014b50: result.Central.Add(offset - 3); break;
                    case 0x08074b50: result.Descriptor.Add(offset - 3); break;
                    case 0x06054b50: result.End.Add(offset - 3); break;
                    case 0x06064b50: result.End64.Add(offset - 3); break;
                }
            }
            if (result.Local.Count + result.Central.Count + result.Descriptor.Count + result.End.Count + result.End64.Count > 500_000)
                throw new ArchiveOperationException("文件包含过多候选记录，已停止恢复，请使用其他专用工具检查。");
            progress?.Report(new JobProgress(25.0 * input.Position / Math.Max(1, input.Length), TextFormat.Bytes(input.Position), "扫描 ZIP"));
        }
        return result;
    }
    private static byte[]? ExtraField(byte[] extra, ushort wanted)
    {
        for (int start = 0; start + 4 <= extra.Length;)
        {
            ushort id = BitConverter.ToUInt16(extra, start), length = BitConverter.ToUInt16(extra, start + 2); start += 4;
            if (length > extra.Length - start) return null;
            if (id == wanted) return extra.AsSpan(start, length).ToArray();
            start += length;
        }
        return null;
    }
    private static string DecodeName(byte[] bytes, ushort flags, byte[] extra, int codePage)
    {
        if ((flags & 0x800) != 0) return new UTF8Encoding(false, true).GetString(bytes);
        byte[]? unicode = ExtraField(extra, 0x7075);
        if (unicode is { Length: >= 5 } && unicode[0] == 1 && BitConverter.ToUInt32(unicode, 1) == ~Checksums.UpdateCrc32(uint.MaxValue, bytes))
            return new UTF8Encoding(false, true).GetString(unicode, 5, unicode.Length - 5);
        return Encoding.GetEncoding(codePage == 0 ? AppPaths.SystemCodePage : codePage).GetString(bytes);
    }
    private static long U64(BinaryReader reader) => checked((long)reader.ReadUInt64());
    private static void ReadZip64(byte[] extra, ref long size, ref long packed, ref long offset, bool needSize, bool needPacked, bool needOffset)
    {
        if (!needSize && !needPacked && !needOffset) return;
        byte[]? bytes = ExtraField(extra, 1);
        if (bytes == null) { if (needSize) size = -1; if (needPacked) packed = -1; if (needOffset) offset = -1; return; }
        using var memory = new MemoryStream(bytes); using var reader = new BinaryReader(memory);
        if (needSize) size = U64(reader); if (needPacked) packed = U64(reader); if (needOffset) offset = U64(reader);
    }
    private static Record? LocalRecord(Stream input, long position, int codePage, Record? directory = null)
    {
        if (position < 0 || position > input.Length - 30) return null;
        input.Position = position; using var reader = new BinaryReader(input, Encoding.UTF8, true);
        uint signature = reader.ReadUInt32(); if (directory == null && signature != 0x04034b50) return null;
        reader.ReadUInt16(); ushort flags = reader.ReadUInt16(), method = reader.ReadUInt16(); uint stamp = reader.ReadUInt32(), crc = reader.ReadUInt32(), packed32 = reader.ReadUInt32(), size32 = reader.ReadUInt32();
        ushort names = reader.ReadUInt16(), extras = reader.ReadUInt16();
        if (names == 0 || input.Position + names + extras > input.Length) return null;
        byte[] name = reader.ReadBytes(names), extra = reader.ReadBytes(extras); long dataStart = input.Position;
        if (directory != null) return directory with { HeaderStart = position, DataStart = dataStart };
        long size = size32, packed = packed32, unused = 0;
        ReadZip64(extra, ref size, ref packed, ref unused, size32 == uint.MaxValue, packed32 == uint.MaxValue, false);
        string decoded = DecodeName(name, flags, extra, codePage);
        return new Record(decoded, flags, method, stamp, crc, packed, size, position, dataStart, extra, decoded.EndsWith('/') || decoded.EndsWith('\\'), false, "本地文件头");
    }
    private static Record? CentralRecord(Stream input, long position, int codePage)
    {
        if (position < 0 || position > input.Length - 46) return null;
        input.Position = position; using var reader = new BinaryReader(input, Encoding.UTF8, true);
        if (reader.ReadUInt32() != 0x02014b50) return null;
        ushort made = reader.ReadUInt16(); reader.ReadUInt16(); ushort flags = reader.ReadUInt16(), method = reader.ReadUInt16();
        uint stamp = reader.ReadUInt32(), crc = reader.ReadUInt32(), packed32 = reader.ReadUInt32(), size32 = reader.ReadUInt32();
        ushort names = reader.ReadUInt16(), extras = reader.ReadUInt16(), comments = reader.ReadUInt16(), disk = reader.ReadUInt16();
        reader.ReadUInt16(); uint attributes = reader.ReadUInt32(), offset32 = reader.ReadUInt32();
        if (names == 0 || input.Position + names + extras + comments > input.Length || disk != 0) return null;
        byte[] name = reader.ReadBytes(names), extra = reader.ReadBytes(extras); long size = size32, packed = packed32, offset = offset32;
        ReadZip64(extra, ref size, ref packed, ref offset, size32 == uint.MaxValue, packed32 == uint.MaxValue, offset32 == uint.MaxValue);
        string decoded = DecodeName(name, flags, extra, codePage);
        bool directory = decoded.EndsWith('/') || decoded.EndsWith('\\') || (attributes & 0x10) != 0;
        bool link = made >> 8 == 3 && (attributes >> 16 & 0xf000) == 0xa000 || (attributes & 0x400) != 0;
        return new Record(decoded, flags, method, stamp, crc, packed, size, offset, -1, extra, directory, link, "中央目录");
    }
    private static (long Start, long End, long Prefix)? CentralLayout(Stream input, SignatureIndex signatures)
    {
        foreach (long end in signatures.End.AsEnumerable().Reverse())
        {
            try
            {
            if (end > input.Length - 22) continue;
            input.Position = end + 12; using var reader = new BinaryReader(input, Encoding.UTF8, true);
            long size = reader.ReadUInt32(), offset = reader.ReadUInt32(), boundary = end;
            if (size == uint.MaxValue || offset == uint.MaxValue)
            {
                long zip64 = signatures.End64.LastOrDefault(p => p < end, -1); if (zip64 < 0 || zip64 > input.Length - 56) continue;
                input.Position = zip64 + 40; size = U64(reader); offset = U64(reader); boundary = zip64;
            }
            long start = boundary - size;
            if (start < 0 || !signatures.Central.Contains(start)) continue;
            return (start, boundary, start - offset);
            }
            catch (Exception error) when (error is IOException or OverflowException) { }
        }
        return null;
    }
    private static Dictionary<long, List<Descriptor>> DescriptorIndex(Stream input, SignatureIndex signatures)
    {
        var result = new Dictionary<long, List<Descriptor>>();
        void Add(long position, bool signed, bool zip64)
        {
            long bytes = (signed ? 4 : 0) + (zip64 ? 20 : 12);
            if (position < 0 || position > input.Length - bytes) return;
            try
            {
                input.Position = position + (signed ? 4 : 0); using var reader = new BinaryReader(input, Encoding.UTF8, true);
                uint crc = reader.ReadUInt32(); long packed = zip64 ? U64(reader) : reader.ReadUInt32(), size = zip64 ? U64(reader) : reader.ReadUInt32();
                long data = position - packed; if (data < 30 || packed < 0 || size < 0) return;
                if (!result.TryGetValue(data, out var list)) result.Add(data, list = []);
                var item = new Descriptor(data, position, crc, packed, size); if (!list.Contains(item)) list.Add(item);
            }
            catch (Exception error) when (error is IOException or OverflowException) { }
        }
        foreach (long position in signatures.Descriptor) { JobControl.Checkpoint(); Add(position, true, false); Add(position, true, true); }
        foreach (long boundary in signatures.Local.Concat(signatures.Central).Concat(signatures.End).Concat(signatures.End64).Append(input.Length).Distinct())
        { JobControl.Checkpoint(); Add(boundary - 12, false, false); Add(boundary - 20, false, true); }
        return result;
    }
    private static IEnumerable<Record> Candidates(Record header, Dictionary<long, List<Descriptor>> descriptors)
    {
        if (header.MetadataSource == "中央目录") yield return header;
        if (descriptors.TryGetValue(header.DataStart, out var records))
            foreach (var descriptor in records) yield return header with { Packed = descriptor.Packed, Size = descriptor.Size, Crc = descriptor.Crc, MetadataSource = "数据描述符" };
        if (header.MetadataSource != "中央目录" && ((header.Flags & 8) == 0 || header.Directory || header.Packed > 0 && header.Size > 0 && header.Crc != 0)) yield return header;
    }
}

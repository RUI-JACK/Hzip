using System.Buffers.Binary;
using System.Diagnostics;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Hzip.Core;

// A GZIP stream can carry an external firmware footer. Validate every member
// before accepting its payload; never treat a broken member as harmless padding.
internal static class GzipReader
{
    internal static bool HasSignature(string path)
    {
        using var input = File.OpenRead(path);
        return input.ReadByte() == 0x1f && input.ReadByte() == 0x8b;
    }

    internal static string TailWarning(long bytes) =>
        $"GZIP 主体完整性校验通过；末尾另有 {bytes:N0} 字节附加数据，未写入解压文件。原压缩包保持不变；附加数据的内容与完整性未作校验。";

    internal static async Task<long> ReadAsync(string path, Stream output, CancellationToken ct, IProgress<JobProgress>? progress)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        byte[] compressed = new byte[64 * 1024], decoded = new byte[128 * 1024], header = new byte[10], trailer = new byte[8];
        var clock = Stopwatch.StartNew(); long lastReport = -100;
        try
        {
            do
            {
                await JobControl.CheckpointAsync(ct);
                await input.ReadExactlyAsync(header, ct);
                if (header[0] != 0x1f || header[1] != 0x8b || header[2] != 8 || (header[3] & 0xe0) != 0)
                    throw Invalid("压缩头无效或包含不支持的标志。");
                uint headerCrc = Checksums.UpdateCrc32(uint.MaxValue, header); int headerBytes = 10;
                byte[] one = new byte[1];
                int ReadHeaderByte()
                {
                    if ((++headerBytes & 0x3fff) == 0) JobControl.Checkpoint(ct);
                    int value = input.ReadByte(); if (value < 0) throw Invalid("压缩头不完整。");
                    one[0] = (byte)value; headerCrc = Checksums.UpdateCrc32(headerCrc, one); return value;
                }
                int flags = header[3];
                if ((flags & 4) != 0)
                {
                    int length = ReadHeaderByte() | ReadHeaderByte() << 8;
                    for (int i = 0; i < length; i++) ReadHeaderByte();
                }
                if ((flags & 8) != 0) while (ReadHeaderByte() != 0) { }
                if ((flags & 16) != 0) while (ReadHeaderByte() != 0) { }
                if ((flags & 2) != 0)
                {
                    int low = input.ReadByte(), high = input.ReadByte();
                    if (low < 0 || high < 0 || (low | high << 8) != (ushort)~headerCrc)
                        throw Invalid("压缩头校验失败。");
                }

                var inflater = new Inflater(true); uint crc = uint.MaxValue, size = 0;
                while (!inflater.IsFinished)
                {
                    await JobControl.CheckpointAsync(ct);
                    if (inflater.IsNeedingInput)
                    {
                        int count = await input.ReadAsync(compressed, ct);
                        if (count == 0) throw Invalid("压缩数据不完整。");
                        inflater.SetInput(compressed, 0, count);
                    }
                    int countOut = inflater.Inflate(decoded);
                    if (countOut > 0)
                    {
                        crc = Checksums.UpdateCrc32(crc, decoded.AsSpan(0, countOut)); size = unchecked(size + (uint)countOut);
                        await output.WriteAsync(decoded.AsMemory(0, countOut), ct);
                    }
                    else if (!inflater.IsFinished && !inflater.IsNeedingInput) throw Invalid("压缩数据无效。");
                    if (clock.ElapsedMilliseconds - lastReport >= 80)
                    {
                        long consumed = input.Position - inflater.RemainingInput;
                        progress?.Report(new JobProgress(Math.Min(99, 100.0 * consumed / input.Length), "正在解压并校验 GZIP 数据…", "解压"));
                        lastReport = clock.ElapsedMilliseconds;
                    }
                }
                input.Position -= inflater.RemainingInput;
                await input.ReadExactlyAsync(trailer, ct);
                if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != ~crc || BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(4)) != size)
                    throw Invalid("文件内容校验失败（CRC 或解压长度不匹配）。");

                long end = input.Position;
                if (end == input.Length) return 0;
                // A next-member signature must be parsed, even if its header or
                // trailer is truncated. Other bytes are external trailing data.
                int first = input.ReadByte(), second = input.ReadByte(); input.Position = end;
                if (first == 0x1f && (second == 0x8b || second < 0)) continue;
                return input.Length - end;
            } while (true);
        }
        catch (EndOfStreamException) { throw Invalid("文件被截断，压缩头或校验尾部不完整。"); }
        catch (SharpZipBaseException error) { throw Invalid("压缩数据无效：" + error.Message); }
    }

    private static ArchiveOperationException Invalid(string reason) => new("GZIP 完整性校验失败：" + reason);
}

using System.Text;

namespace Hzip.Core;

public static partial class ZipRepair
{
    private static async Task MakeWrapperAsync(Stream source, Record item, string output, CancellationToken ct)
    {
        await using var file = File.Create(output); using var writer = new BinaryWriter(file, Encoding.UTF8, true);
        byte[] name = Encoding.UTF8.GetBytes("payload.bin"); bool big = item.Size >= uint.MaxValue || item.Packed >= uint.MaxValue;
        byte[] Extra()
        {
            using var memory = new MemoryStream(); using var fields = new BinaryWriter(memory);
            byte[]? aes = ExtraField(item.Extra, 0x9901);
            if (aes != null) { fields.Write((ushort)0x9901); fields.Write((ushort)aes.Length); fields.Write(aes); }
            if (big) { fields.Write((ushort)1); fields.Write((ushort)16); fields.Write((ulong)item.Size); fields.Write((ulong)item.Packed); }
            return memory.ToArray();
        }
        byte[] extra = Extra(); ushort flags = (ushort)(item.Flags | 0x800);
        ushort version = (ushort)(item.Method == 99 ? 51 : item.Method is 14 or 95 ? 63 : item.Method == 12 ? 46 : big ? 45 : 20);
        writer.Write(0x04034b50U); writer.Write(version); writer.Write(flags); writer.Write(item.Method); writer.Write(item.Stamp); writer.Write(item.Crc);
        writer.Write(big ? uint.MaxValue : (uint)item.Packed); writer.Write(big ? uint.MaxValue : (uint)item.Size);
        writer.Write((ushort)name.Length); writer.Write((ushort)extra.Length); writer.Write(name); writer.Write(extra);
        source.Position = item.DataStart; using (var slice = new LimitedInput(source, item.Packed)) await JobControl.CopyAsync(slice, file, ct);
        if ((item.Flags & 8) != 0)
        {
            writer.Write(0x08074b50U); writer.Write(item.Crc);
            if (big) { writer.Write((ulong)item.Packed); writer.Write((ulong)item.Size); }
            else { writer.Write((uint)item.Packed); writer.Write((uint)item.Size); }
        }
        long central = file.Position;
        writer.Write(0x02014b50U); writer.Write((ushort)63); writer.Write(version); writer.Write(flags); writer.Write(item.Method); writer.Write(item.Stamp); writer.Write(item.Crc);
        writer.Write(big ? uint.MaxValue : (uint)item.Packed); writer.Write(big ? uint.MaxValue : (uint)item.Size);
        writer.Write((ushort)name.Length); writer.Write((ushort)extra.Length); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0U); writer.Write(0U); writer.Write(name); writer.Write(extra);
        long centralSize = file.Position - central;
        if (central >= uint.MaxValue)
        {
            long position = file.Position; writer.Write(0x06064b50U); writer.Write(44UL); writer.Write((ushort)63); writer.Write((ushort)63);
            writer.Write(0U); writer.Write(0U); writer.Write(1UL); writer.Write(1UL); writer.Write((ulong)centralSize); writer.Write((ulong)central);
            writer.Write(0x07064b50U); writer.Write(0U); writer.Write((ulong)position); writer.Write(1U);
        }
        writer.Write(0x06054b50U); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((uint)centralSize); writer.Write(central >= uint.MaxValue ? uint.MaxValue : (uint)central); writer.Write((ushort)0);
    }
    private sealed class LimitedInput(Stream source, long remaining) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { int read = source.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { int read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken); remaining -= read; return read; }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

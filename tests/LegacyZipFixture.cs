using System.IO;
using System.Text;
using Hzip.Core;

internal static class LegacyZipFixture
{
    public static string Create(string folderPath, string name)
    {
            string path = Path.Combine(folderPath, name); using var writer = new BinaryWriter(File.Create(path));
            var entries = new List<(byte[] Name, byte[] Data, uint Crc, uint Offset)>();
            foreach (var pair in new[] { ("日本語/説明.txt", "original Japanese entry"), ("残す.txt", "keep sibling") })
            {
                byte[] rawName = Encoding.GetEncoding(932).GetBytes(pair.Item1), bytes = Encoding.UTF8.GetBytes(pair.Item2); uint crc = ~Checksums.UpdateCrc32(uint.MaxValue, bytes), offset = (uint)writer.BaseStream.Position;
                writer.Write(0x04034b50u); writer.Write((ushort)20); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)33);
                writer.Write(crc); writer.Write((uint)bytes.Length); writer.Write((uint)bytes.Length); writer.Write((ushort)rawName.Length); writer.Write((ushort)0); writer.Write(rawName); writer.Write(bytes);
                entries.Add((rawName, bytes, crc, offset));
            }
            uint directory = (uint)writer.BaseStream.Position;
            foreach (var e in entries)
            {
                writer.Write(0x02014b50u); writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)33);
                writer.Write(e.Crc); writer.Write((uint)e.Data.Length); writer.Write((uint)e.Data.Length); writer.Write((ushort)e.Name.Length); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u); writer.Write(e.Offset); writer.Write(e.Name);
            }
            uint directorySize = (uint)writer.BaseStream.Position - directory;
            writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)entries.Count); writer.Write((ushort)entries.Count); writer.Write(directorySize); writer.Write(directory); writer.Write((ushort)0);
            return path;
    }
}

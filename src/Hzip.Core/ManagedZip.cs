using ICSharpCode.SharpZipLib.Zip;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using System.Security.Cryptography;

namespace Hzip.Core;

internal static class ManagedZip
{
    static ManagedZip() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private static async Task<T> TryEncodings<T>(Func<Encoding, Task<T>> action)
    {
        Exception? last = null;
        foreach (int codePage in new[] { 65001, AppPaths.SystemCodePage, 936, 932, 950, 437, 1252 }.Distinct())
        {
            try { return await action(Encoding.GetEncoding(codePage)); }
            catch (Exception error) when (error is ZipException or CryptographicException || error is ArchiveOperationException { NeedsPassword: true }) { last = error; }
        }
        throw PasswordError(last ?? new ZipException("ZIP password verification failed"));
    }
    private static StringCodec Codec(Encoding encoding, int filenameCodePage = 0) => StringCodec.FromCodePage(filenameCodePage == 0 ? AppPaths.SystemCodePage : filenameCodePage).WithZipCryptoEncoding(encoding);
    public static bool Needed(string? password) => password?.Any(c => c > 127) == true;
    public static bool IsZip(string path)
    {
        try { using var file = File.OpenRead(path); Span<byte> header = stackalloc byte[4]; return file.Read(header) == 4 && (BitConverter.ToUInt32(header) is 0x04034b50U or 0x06054b50U); } catch (IOException) { return false; }
    }
    public static async Task CreateAsync(IReadOnlyList<string> inputs, string output, CreateOptions options, CancellationToken ct, IProgress<JobProgress>? progress)
    {
        var sources = SpecialFormats.Sources(inputs); var encoding = Encoding.GetEncoding(options.ZipPasswordCodePage); using var scope = new ZipPasswordEncodingScope(encoding);
        await using var destination = File.Create(output); using var zip = new ZipOutputStream(destination, Codec(encoding)) { IsStreamOwner = false, UseZip64 = UseZip64.Dynamic };
        zip.SetLevel(options.Level); int done = 0;
        foreach (var source in sources)
        {
            await JobControl.CheckpointAsync(ct); zip.Password = source.Directory ? null : options.Password;
            var entry = NewEntry(source.RelativePath + (source.Directory ? "/" : ""), source.Length, source.Modified, !source.Directory && !string.IsNullOrEmpty(options.Password), options.Level == 0);
            if (options.ZipEncryption.Equals("ZipCrypto", StringComparison.OrdinalIgnoreCase)) entry.AESKeySize = 0;
            zip.PutNextEntry(entry);
            if (!source.Directory) { await using var input = File.OpenRead(source.FullPath); await JobControl.CopyAsync(input, zip, ct); }
            zip.CloseEntry(); progress?.Report(new JobProgress(100.0 * ++done / Math.Max(1, sources.Count), source.RelativePath, "压缩 ZIP"));
        }
        zip.Finish();
    }
    private static ZipEntry NewEntry(string name, long length, DateTime date, bool encrypted, bool store = false) => new(name)
    {
        Size = length, DateTime = date, IsUnicodeText = true, AESKeySize = encrypted ? 256 : 0,
        CompressionMethod = store ? CompressionMethod.Stored : CompressionMethod.Deflated
    };
    public static Task<EngineResult> TestAsync(string path, string password, CancellationToken ct, IProgress<JobProgress>? progress) => TryEncodings(encoding => TestWithEncodingAsync(path, password, encoding, ct, progress));
    private static async Task<EngineResult> TestWithEncodingAsync(string path, string password, Encoding encoding, CancellationToken ct, IProgress<JobProgress>? progress)
    {
        using var scope = new ZipPasswordEncodingScope(encoding);
        using var archive = await InputAsync(path, ct); using var zip = new ZipFile(archive.File, Codec(encoding)) { Password = password }; int done = 0;
        try
        {
            foreach (ZipEntry entry in zip)
            {
                await JobControl.CheckpointAsync(ct); if (entry.IsDirectory) continue;
                using var input = await OpenMemberAsync(zip, entry, ct); await CopyVerifiedAsync(input.Input, Stream.Null, entry, ct);
                progress?.Report(new JobProgress(100.0 * ++done / Math.Max(1, zip.Count), entry.Name, "验证 ZIP"));
            }
            return new EngineResult(0, "All OK (Hzip managed ZIP validation)", "") { ManagedZipCompatibility = true };
        }
        catch (ZipException error) { throw PasswordError(error); }
    }
    public static Task ExtractToAsync(string path, string staging, string password, IReadOnlyList<string>? selected, CancellationToken ct, IProgress<JobProgress>? progress, int filenameCodePage = 0) => TryEncodings(async encoding => { await ExtractWithEncodingAsync(path, staging, password, selected, encoding, ct, progress, filenameCodePage); return true; });
    private static async Task ExtractWithEncodingAsync(string path, string staging, string password, IReadOnlyList<string>? selected, Encoding encoding, CancellationToken ct, IProgress<JobProgress>? progress, int filenameCodePage)
    {
        using var scope = new ZipPasswordEncodingScope(encoding);
        using var archive = await InputAsync(path, ct); using var zip = new ZipFile(archive.File, Codec(encoding, filenameCodePage)) { Password = password }; int done = 0;
        try
        {
            foreach (ZipEntry entry in zip)
            {
                await JobControl.CheckpointAsync(ct); string normalized = SafePaths.Relative(entry.Name);
                if (selected is { Count: > 0 } && !selected.Any(p => normalized == p.TrimEnd('/', '\\').Replace('\\', '/') || normalized.StartsWith(p.TrimEnd('/', '\\').Replace('\\', '/') + "/", StringComparison.Ordinal))) continue;
                if (entry.IsDOSEntry == false && (entry.ExternalFileAttributes >> 16 & 0xf000) == 0xa000) throw new ArchiveOperationException("归档中包含符号链接。");
                string target = SafePaths.Within(staging, normalized); SafePaths.NoReparseAncestors(target);
                if (entry.IsDirectory) Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    using (var input = await OpenMemberAsync(zip, entry, ct)) await using (var output = File.Create(target)) await CopyVerifiedAsync(input.Input, output, entry, ct);
                    File.SetLastWriteTime(target, entry.DateTime);
                }
                progress?.Report(new JobProgress(100.0 * ++done / Math.Max(1, zip.Count), entry.Name, "解压 ZIP"));
            }
        }
        catch (ZipException error) { throw PasswordError(error); }
    }
    public static async Task RewriteAsync(string archive, string password, IReadOnlyList<string>? additions, IReadOnlyList<string>? deletions,
        string? renameOld, string? renameNew, string? replacementEntry, string? replacementFile, CancellationToken ct, IProgress<JobProgress>? progress, int codePage = 0)
    {
        using var work = new Workspace(); string output = System.IO.Path.Combine(work.Path, "rewritten.zip");
        var encoding = await TryEncodings(async value => { await TestWithEncodingAsync(archive, password, value, ct, null); return value; });
        using var scope = new ZipPasswordEncodingScope(encoding);
        var extra = (additions is { Count: > 0 } ? SpecialFormats.Sources(additions) : []).ToDictionary(x => x.RelativePath.TrimEnd('/'), x => x, StringComparer.OrdinalIgnoreCase);
        if (replacementEntry != null && replacementFile != null) extra[SafePaths.Relative(replacementEntry)] = new SourceItem(replacementFile, replacementEntry, false, new FileInfo(replacementFile).Length, File.GetLastWriteTime(replacementFile));
        using (var original = new ZipFile(archive, Codec(encoding, codePage)) { Password = password })
        await using (var file = File.Create(output))
        using (var writer = new ZipOutputStream(file, Codec(encoding)) { IsStreamOwner = false, UseZip64 = UseZip64.Dynamic })
        {
            writer.SetLevel(5); int done = 0;
            try
            {
                foreach (ZipEntry entry in original)
                {
                    await JobControl.CheckpointAsync(ct); string path = SafePaths.Relative(entry.Name);
                    if (deletions?.Any(p => path == p.TrimEnd('/', '\\').Replace('\\', '/') || path.StartsWith(p.TrimEnd('/', '\\').Replace('\\', '/') + "/", StringComparison.Ordinal)) == true || extra.ContainsKey(path)) continue;
                    string renamed = path;
                    if (renameOld != null && renameNew != null)
                    {
                        string old = SafePaths.Relative(renameOld), target = SafePaths.Relative(renameNew);
                        if (path == old) renamed = target; else if (path.StartsWith(old + "/", StringComparison.Ordinal)) renamed = target + path[old.Length..];
                    }
                    writer.Password = entry.IsCrypted && !entry.IsDirectory ? password : null;
                    var next = NewEntry(renamed + (entry.IsDirectory ? "/" : ""), entry.Size, entry.DateTime, entry.IsCrypted && !entry.IsDirectory); next.Comment = entry.Comment;
                    writer.PutNextEntry(next);
                    if (!entry.IsDirectory) { using var input = await OpenMemberAsync(original, entry, ct); await CopyVerifiedAsync(input.Input, writer, entry, ct); }
                    writer.CloseEntry(); progress?.Report(new JobProgress(100.0 * ++done / Math.Max(1, original.Count + extra.Count), path, "更新 ZIP"));
                }
                foreach (var item in extra.Values)
                {
                    await JobControl.CheckpointAsync(ct); writer.Password = item.Directory ? null : password; writer.PutNextEntry(NewEntry(item.RelativePath + (item.Directory ? "/" : ""), item.Length, item.Modified, !item.Directory));
                    if (!item.Directory) { await using var input = File.OpenRead(item.FullPath); await JobControl.CopyAsync(input, writer, ct); } writer.CloseEntry();
                }
                writer.Finish();
            }
            catch (ZipException error) { throw PasswordError(error); }
        }
        ct.ThrowIfCancellationRequested(); ArchiveService.AtomicCommit(output, archive);
    }
    private static async Task<MemberLease> OpenMemberAsync(ZipFile zip, ZipEntry entry, CancellationToken ct)
    {
        if (entry.CompressionMethod != CompressionMethod.LZMA) return new MemberLease(zip.GetInputStream(entry));
        var workspace = new Workspace();
        try
        {
            string lzma = System.IO.Path.Combine(workspace.Path, "payload.lzma");
            using (var raw = zip.GetRawLzmaInputStreamForHzip(entry))
            await using (var output = File.Create(lzma))
            {
                byte[] header = new byte[9]; await raw.ReadExactlyAsync(header, ct);
                if (header[2] != 5 || header[3] != 0 || entry.Size < 0) throw new ZipException("Unsupported ZIP LZMA property header");
                // ZIP-LZMA has a 4-byte version/property prefix; .lzma uses a size field.
                await output.WriteAsync(header.AsMemory(4, 5), ct);
                byte[] length = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, entry.Size);
                await output.WriteAsync(length, ct);
                await JobControl.CopyAsync(raw, output, ct); // Reaching EOF verifies the AES authentication code.
            }
            string decoded = System.IO.Path.Combine(workspace.Path, "decoded");
            await new ArchiveEngine().RunAsync(["x", "-tlzma", "-o" + decoded, "-y", "--", lzma], ct);
            string payload = System.IO.Path.Combine(decoded, "payload");
            if (!File.Exists(payload) || new FileInfo(payload).Length != entry.Size) throw new ZipException("ZIP LZMA size mismatch");
            return new MemberLease(File.OpenRead(payload), workspace);
        }
        catch { workspace.Dispose(); throw; }
    }
    private sealed class MemberLease(Stream input, Workspace? workspace = null) : IDisposable
    {
        public Stream Input { get; } = input;
        public void Dispose() { Input.Dispose(); workspace?.Dispose(); }
    }
    private static async Task CopyVerifiedAsync(Stream input, Stream output, ZipEntry entry, CancellationToken ct)
    {
        byte[] buffer = new byte[128 * 1024]; long length = 0; uint crc = uint.MaxValue;
        while (true)
        {
            await JobControl.CheckpointAsync(ct); int count = await input.ReadAsync(buffer, ct); if (count == 0) break; length += count; if (entry.Size >= 0 && length > entry.Size) throw new ZipException("ZIP uncompressed size mismatch");
            crc = Checksums.UpdateCrc32(crc, buffer.AsSpan(0, count)); await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (entry.Size >= 0 && length != entry.Size) throw new ZipException("ZIP uncompressed size mismatch");
        if ((entry.AESKeySize == 0 || entry.Crc > 0) && entry.Crc >= 0 && ~crc != (uint)entry.Crc) throw new ZipException("ZIP CRC verification failed");
    }
    private static ArchiveOperationException PasswordError(Exception error) => new("ZIP 密码不正确，或归档数据无法通过校验。", 2, "ZIP password / integrity verification: " + error.Message);
    public static async Task SplitAsync(string output, string volume, CancellationToken ct)
    {
        var match = Regex.Match(volume, @"^([1-9]\d*)([bkmgBKMG]?)$"); if (!match.Success) throw new ArgumentException("Invalid volume size");
        long bytes = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); int power = match.Groups[2].Value.ToLowerInvariant() switch { "k" => 1, "m" => 2, "g" => 3, _ => 0 }; for (int n = 0; n < power; n++) bytes = checked(bytes * 1024);
        await using (var input = File.OpenRead(output))
        {
            byte[] buffer = new byte[1024 * 1024]; int part = 1;
            while (input.Position < input.Length)
            {
                await using var file = File.Create(output + "." + (part++).ToString("D3")); long remaining = bytes;
                while (remaining > 0) { await JobControl.CheckpointAsync(ct); int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct); if (count == 0) break; await file.WriteAsync(buffer.AsMemory(0, count), ct); remaining -= count; }
            }
        }
        File.Delete(output);
    }
    private sealed class InputLease(string file, Workspace? workspace) : IDisposable { public string File { get; } = file; public void Dispose() => workspace?.Dispose(); }
    private static async Task<InputLease> InputAsync(string path, CancellationToken ct)
    {
        if (!path.EndsWith(".001", StringComparison.OrdinalIgnoreCase)) return new InputLease(path, null);
        var workspace = new Workspace(); string merged = System.IO.Path.Combine(workspace.Path, "merged.zip");
        try
        {
            await using var output = File.Create(merged); string prefix = path[..^3];
            for (int n = 1; ; n++) { string part = prefix + n.ToString("D3"); if (!File.Exists(part)) break; await using var input = File.OpenRead(part); await JobControl.CopyAsync(input, output, ct); }
            return new InputLease(merged, workspace);
        }
        catch { workspace.Dispose(); throw; }
    }
}

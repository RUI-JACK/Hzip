using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hzip.Core;

public sealed record VaultEntry(string Id, string Label, string Ciphertext, DateTimeOffset Created);
public sealed class PasswordVault(string? directory = null)
{
    private readonly string path = System.IO.Path.Combine(directory ?? AppPaths.DataRoot, "passwords.json");
    private readonly object gate = new();
    public IReadOnlyList<VaultEntry> List()
    {
        lock (gate) return File.Exists(path) ? JsonSerializer.Deserialize<List<VaultEntry>>(File.ReadAllText(path)) ?? [] : [];
    }
    public VaultEntry Save(string label, string password)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrEmpty(password)) throw new ArgumentException("请填写名称和密码。");
        lock (gate)
        {
            var entries = List().ToList(); var entry = new VaultEntry(Guid.NewGuid().ToString("N"), label.Trim(), Convert.ToBase64String(WindowsProtection.Protect(Encoding.UTF8.GetBytes(password))), DateTimeOffset.Now);
            entries.Add(entry); Write(entries); return entry;
        }
    }
    public string Reveal(VaultEntry entry)
    {
        byte[] bytes = WindowsProtection.Unprotect(Convert.FromBase64String(entry.Ciphertext));
        try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Delete(string id) { lock (gate) Write(List().Where(x => x.Id != id).ToArray()); }
    private void Write(IEnumerable<VaultEntry> entries)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); string temporary = path + "." + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
internal static class WindowsProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, out IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] value) => Transform(value, false);
    public static byte[] Unprotect(byte[] value) => Transform(value, true);
    private static byte[] Transform(byte[] value, bool decrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密码库需要 Windows 数据保护 API。");
        var input = new Blob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) }; Blob output = default; IntPtr description = IntPtr.Zero;
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            bool success = decrypt ? CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptProtectData(ref input, "Hzip password vault", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法访问密码库，请使用保存密码时的 Windows 账户。");
            byte[] result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero) { Marshal.Copy(new byte[input.Size], 0, input.Data, input.Size); Marshal.FreeHGlobal(input.Data); }
            if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size); LocalFree(output.Data); }
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }
}
public sealed class UserSettings
{
    public string DefaultFormat { get; set; } = "zip";
    public string DefaultFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public int CompressionLevel { get; set; } = 5;
    public int Threads { get; set; }
    public int CodePage { get; set; }
    public bool KeepHistory { get; set; } = true;
    public List<string> RecentFiles { get; set; } = [];
    public static string Path => System.IO.Path.Combine(AppPaths.DataRoot, "settings.json");
    public static UserSettings Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataRoot); string temporary = Path + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, Path, true);
    }
    public void Remember(string file)
    {
        if (KeepHistory) { RecentFiles.RemoveAll(x => x.Equals(file, StringComparison.OrdinalIgnoreCase)); RecentFiles.Insert(0, file); RecentFiles = RecentFiles.Take(12).ToList(); }
        Save();
    }
}

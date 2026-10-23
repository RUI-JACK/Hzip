using System.Net.Http;
using System.Text.Json;

namespace Hzip.Core;

public sealed record ReleaseUpdate(Version Version, Uri ReleasePage, Uri DownloadUrl, string Notes);

public sealed class GitHubUpdateClient(HttpClient client)
{
    public const string ApiUrl = "https://api.github.com/repos/RUI-JACK/Hzip/releases/latest";
    public async Task<ReleaseUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.UserAgent.ParseAdd("Hzip/" + Normalize(currentVersion).ToString(3));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryStableVersion(tag, out var latest) || Normalize(latest!) <= Normalize(currentVersion)) return null;
        string releasePath = "/RUI-JACK/Hzip/releases/tag/" + Uri.EscapeDataString(tag);
        Uri page = TrustedUrl(root.GetProperty("html_url").GetString(), releasePath)
            ?? throw new InvalidDataException("更新发布页不属于 Hzip 仓库。");
        Uri download = page;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != "Hzip-Setup-x64.exe") continue;
                download = TrustedUrl(asset.GetProperty("browser_download_url").GetString(),
                    "/RUI-JACK/Hzip/releases/download/" + Uri.EscapeDataString(tag) + "/Hzip-Setup-x64.exe") ?? page;
                break;
            }
        }
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        if (notes.Length > 3000) notes = notes[..3000] + "\n…完整说明请查看发布页。";
        return new(latest!, page, download, notes);
    }
    private static Uri? TrustedUrl(string? value, string path) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.AbsolutePath == path && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : null;
    public static bool TryStableVersion(string tag, out Version? version)
    {
        string value = tag.StartsWith('v') ? tag[1..] : tag;
        version = null;
        return value.Length <= 64 && value.All(c => char.IsAsciiDigit(c) || c == '.') && Version.TryParse(value, out version);
    }
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}

public sealed class UpdateNotificationState
{
    public DateTimeOffset LastCheckedUtc { get; set; }
    public string? SkippedVersion { get; set; }
    public bool CheckDue(DateTimeOffset now) => LastCheckedUtc > now || now - LastCheckedUtc >= TimeSpan.FromDays(1);
    public bool ShouldNotify(ReleaseUpdate update) => SkippedVersion != update.Version.ToString();
    public static UpdateNotificationState Load(string path)
    {
        try { return JsonSerializer.Deserialize<UpdateNotificationState>(File.ReadAllText(path)) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

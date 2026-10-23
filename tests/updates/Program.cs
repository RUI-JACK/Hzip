using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hzip.App;
using Hzip.Core;

internal static class Program
{
    private static readonly List<object> results = [];
    private static string root = "";
    private static int failures;
    [STAThread] public static int Main(string[] args)
    {
        root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HZIP_DATA_DIR", Path.Combine(root, "profile"));
        var app = new Hzip.App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Load the actual application resources without invoking the production startup handler.
        var startup = typeof(Hzip.App.App).GetMethod("Application_Startup", BindingFlags.Instance | BindingFlags.NonPublic)!;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, startup);
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await RunAsync(); }
            catch (Exception error) { failures++; File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString()); }
            finally { File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { Failures = failures, Results = results }, new JsonSerializerOptions { WriteIndented = true })); app.Shutdown(failures == 0 ? 0 : 1); }
        });
        return app.Run();
    }
    private static void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    private static async Task Test(string name, Func<Task> action)
    {
        try { await action(); results.Add(new { Name = name, Passed = true }); }
        catch (Exception error) { failures++; results.Add(new { Name = name, Passed = false, Error = error.ToString() }); }
    }
    private static string Release(string tag = "v0.6.0", bool draft = false, bool prerelease = false, string? page = null, string? download = null) =>
        JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, html_url = page ?? $"https://github.com/RUI-JACK/Hzip/releases/tag/{tag}", body = "修复与改进\n新的更新提醒", assets = new[] { new { name = "Hzip-Setup-x64.exe", browser_download_url = download ?? $"https://github.com/RUI-JACK/Hzip/releases/download/{tag}/Hzip-Setup-x64.exe" } } });
    private sealed class Handler(Func<CancellationToken, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; Assert(request.RequestUri!.AbsoluteUri == GitHubUpdateClient.ApiUrl); Assert(request.Headers.UserAgent.Count > 0); return Task.FromResult(response(cancellationToken)); }
    }
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value) };
    private static async Task<ReleaseUpdate?> Check(string json, string current = "0.5.1")
    { using var http = new HttpClient(new Handler(_ => Json(json))); return await new GitHubUpdateClient(http).CheckAsync(Version.Parse(current)); }
    private static async Task RunAsync()
    {
        await Test("New stable release is offered with the official installer URL", async () => { var update = await Check(Release()); Assert(update?.Version == new Version(0, 6, 0)); Assert(update!.DownloadUrl.AbsoluteUri.EndsWith("/v0.6.0/Hzip-Setup-x64.exe")); });
        await Test("Equal versions with different component counts do not prompt", async () => Assert(await Check(Release("v0.5.1"), "0.5.1.0") == null));
        await Test("Older releases do not prompt", async () => Assert(await Check(Release("v0.5.0")) == null));
        await Test("Version comparison is numeric", async () => Assert((await Check(Release("v0.10.0"), "0.9.9")) != null));
        await Test("Draft releases do not prompt", async () => Assert(await Check(Release(draft: true)) == null));
        await Test("Pre-releases do not prompt", async () => Assert(await Check(Release(prerelease: true)) == null));
        await Test("Malformed and pre-release version tags do not prompt", async () => { foreach (string tag in new[] { "v0.6.0-beta", "latest", "v999999999999999.0.0" }) Assert(await Check(Release(tag)) == null); });
        await Test("Untrusted release pages are rejected", async () => { try { await Check(Release(page: "https://example.com/fake")); throw new Exception("Untrusted URL accepted"); } catch (InvalidDataException) { } });
        await Test("Untrusted download falls back to official release page", async () => { var update = (await Check(Release(download: "https://example.com/fake.exe")))!; Assert(update.DownloadUrl == update.ReleasePage); });
        await Test("GitHub API live response has a valid stable release", async () => { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 }; var update = await new GitHubUpdateClient(http).CheckAsync(new Version(0, 0, 0)); Assert(update != null); });
        await Test("State persists skip choice and daily check interval", () =>
        { string path = Path.Combine(root, "state-test.json"); var state = new UpdateNotificationState { LastCheckedUtc = DateTimeOffset.UtcNow, SkippedVersion = "0.6.0" }; state.Save(path); var saved = UpdateNotificationState.Load(path); Assert(saved.SkippedVersion == "0.6.0" && !saved.CheckDue(DateTimeOffset.UtcNow)); Assert(saved.CheckDue(DateTimeOffset.UtcNow.AddDays(2))); Assert(saved.CheckDue(DateTimeOffset.UtcNow.AddDays(-2))); return Task.CompletedTask; });
        await Test("Corrupt state does not prevent future checks", () => { string path = Path.Combine(root, "corrupt.json"); File.WriteAllText(path, "broken"); Assert(UpdateNotificationState.Load(path).CheckDue(DateTimeOffset.UtcNow)); return Task.CompletedTask; });
        await PromptTest("Later", "Real startup notification displays version and later choice", false, false);
        await PromptTest("Skip", "Real startup notification persists skipped version", true, false);
        await PromptTest("Download", "Download opens only the official URL on explicit click", false, true);
        await Test("Network error remains silent and leaves owner usable", async () =>
        { var owner = Owner(); using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("offline"))); await new StartupUpdateNotifier(new GitHubUpdateClient(http), new Version(0, 5, 1), Path.Combine(root, "offline.json")).CheckAndNotifyAsync(owner); Assert(owner.IsVisible && owner.IsEnabled && owner.OwnedWindows.Count == 0); owner.Close(); });
        await Test("Malformed API response remains silent", async () =>
        { var owner = Owner(); using var http = new HttpClient(new Handler(_ => Json("{}"))); await new StartupUpdateNotifier(new GitHubUpdateClient(http), new Version(0, 5, 1), Path.Combine(root, "malformed.json")).CheckAndNotifyAsync(owner); Assert(owner.IsEnabled && owner.OwnedWindows.Count == 0); owner.Close(); });
        await Test("Rate limits remain silent", async () =>
        { var owner = Owner(); using var http = new HttpClient(new Handler(_ => Json("{}", HttpStatusCode.Forbidden))); await new StartupUpdateNotifier(new GitHubUpdateClient(http), new Version(0, 5, 1), Path.Combine(root, "rate.json")).CheckAndNotifyAsync(owner); Assert(owner.IsEnabled && owner.OwnedWindows.Count == 0); owner.Close(); });
        await Test("Cancellation does not show a late notification", async () =>
        { var owner = Owner(); using var cancel = new CancellationTokenSource(); cancel.Cancel(); using var http = new HttpClient(new Handler(c => { c.ThrowIfCancellationRequested(); return Json(Release()); })); await new StartupUpdateNotifier(new GitHubUpdateClient(http), new Version(0, 5, 1), Path.Combine(root, "cancel.json")).CheckAndNotifyAsync(owner, cancel.Token); Assert(owner.OwnedWindows.Count == 0); owner.Close(); });
    }
    private static Window Owner() { var window = new MainWindow { Title = "Hzip 更新验证" }; window.Show(); window.Activate(); return window; }
    private static IEnumerable<DependencyObject> Tree(DependencyObject node)
    { yield return node; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(node, i))) yield return child; }
    private static async Task PromptTest(string choice, string title, bool skip, bool download)
    {
        await Test(title, async () =>
        {
            var owner = Owner(); string path = Path.Combine(root, choice + ".json"); Uri? opened = null; Exception? failure = null; bool shown = false;
            var handler = new Handler(_ => Json(Release())); using var http = new HttpClient(handler);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) }; int attempts = 0;
            timer.Tick += (_, _) =>
            {
                try
                {
                    var dialog = owner.OwnedWindows.OfType<UpdateNotificationWindow>().SingleOrDefault();
                    if (dialog == null) { if (++attempts > 40) throw new Exception("Update dialog did not appear"); return; }
                    shown = true; timer.Stop(); dialog.UpdateLayout();
                    Assert(Tree(dialog).OfType<TextBlock>().Any(t => t.Text.Contains("0.5.1") && t.Text.Contains("0.6.0")));
                    if (choice == "Later")
                    {
                        var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(root, "update-dialog.png")); encoder.Save(stream);
                    }
                    var checkbox = Tree(dialog).OfType<CheckBox>().Single(b => AutomationProperties.GetAutomationId(b) == "SkipUpdateVersion"); checkbox.IsChecked = skip;
                    (download ? dialog.Accept : dialog.Cancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error) { failure = error; timer.Stop(); foreach (var dialog in owner.OwnedWindows.Cast<Window>().ToArray()) dialog.Close(); owner.Close(); }
            };
            timer.Start();
            try
            {
                var notifier = new StartupUpdateNotifier(new GitHubUpdateClient(http), new Version(0, 5, 1), path);
                await notifier.CheckAndNotifyAsync(owner, open: uri => opened = uri);
                if (failure != null) throw failure;
                Assert(shown); Assert((opened != null) == download);
                if (download) Assert(opened!.AbsoluteUri == "https://github.com/RUI-JACK/Hzip/releases/download/v0.6.0/Hzip-Setup-x64.exe");
                Assert((UpdateNotificationState.Load(path).SkippedVersion == "0.6.0") == skip);
                await notifier.CheckAndNotifyAsync(owner); Assert(handler.Calls == 1, "Daily suppression failed");
                if (skip)
                {
                    var state = UpdateNotificationState.Load(path); state.LastCheckedUtc = DateTimeOffset.UtcNow.AddDays(-2); state.Save(path);
                    await notifier.CheckAndNotifyAsync(owner); Assert(handler.Calls == 2 && owner.OwnedWindows.Count == 0, "Skipped version was offered again");
                    Assert(state.ShouldNotify(new ReleaseUpdate(new Version(0, 7, 0), opened ?? new Uri("https://github.com"), new Uri("https://github.com"), "")), "Next version should be offered");
                }
            }
            finally { timer.Stop(); owner.Close(); }
        });
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Hzip.Core;

namespace Hzip.App;

public sealed class UpdateNotificationWindow : FormWindow
{
    public bool SkipVersion { get; private set; }
    public UpdateNotificationWindow(ReleaseUpdate update, Version currentVersion, Action<Uri>? open = null)
        : base("发现 Hzip 新版本", $"当前版本 {currentVersion.ToString(3)}    →    新版本 {update.Version}", 560)
    {
        AutomationProperties.SetAutomationId(this, "UpdateNotification");
        Label("更新说明");
        Body.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(update.Notes) ? "新版已发布，点击下载查看最新功能和修复。" : update.Notes, TextWrapping = TextWrapping.Wrap, MaxHeight = 230 });
        Note("点击下载会打开浏览器。安装新版前请先完成任务并退出 Hzip。");
        var skip = new CheckBox { Content = "跳过此版本", Margin = new Thickness(0, 14, 0, 0) };
        AutomationProperties.SetAutomationId(skip, "SkipUpdateVersion"); Body.Children.Add(skip);
        Accept.Content = "下载新版"; Cancel.Content = "稍后提醒";
        AutomationProperties.SetAutomationId(Accept, "DownloadUpdate"); AutomationProperties.SetAutomationId(Cancel, "RemindUpdateLater");
        Accept.Click += (_, _) =>
        {
            try { (open ?? OpenDownload)(update.DownloadUrl); DialogResult = true; }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { Error("无法打开浏览器，请访问 " + update.ReleasePage); }
        };
        Closed += (_, _) => SkipVersion = DialogResult != true && skip.IsChecked == true;
    }
    private static void OpenDownload(Uri url) => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
}

public sealed class StartupUpdateNotifier(GitHubUpdateClient client, Version currentVersion, string statePath)
{
    public async Task CheckAndNotifyAsync(Window owner, CancellationToken cancellation = default, Action<Uri>? open = null)
    {
        try
        {
            var state = UpdateNotificationState.Load(statePath);
            if (!state.CheckDue(DateTimeOffset.UtcNow)) return;
            // Separate processes must not present duplicate startup notifications.
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(statePath))!);
            ReleaseUpdate? update;
            using (var gate = new FileStream(statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                state = UpdateNotificationState.Load(statePath);
                if (!state.CheckDue(DateTimeOffset.UtcNow)) return;
                update = await client.CheckAsync(currentVersion, cancellation);
                cancellation.ThrowIfCancellationRequested();
                state.LastCheckedUtc = DateTimeOffset.UtcNow; state.Save(statePath);
            }
            if (update == null || !state.ShouldNotify(update)) return;
            while (owner.IsVisible && (!owner.IsActive || !owner.IsEnabled || owner.OwnedWindows.Cast<Window>().Any(w => w.IsVisible)))
                await Task.Delay(500, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!owner.IsVisible) return;
            var dialog = new UpdateNotificationWindow(update, currentVersion, open) { Owner = owner };
            dialog.ShowDialog();
            if (dialog.SkipVersion) { state.SkippedVersion = update.Version.ToString(); state.Save(statePath); }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
    }

    public static async Task OnStartupAsync(Window owner)
    {
        using var shutdown = new CancellationTokenSource();
        void Closed(object? sender, EventArgs args) => shutdown.Cancel();
        owner.Closed += Closed;
        try
        {
            var config = JsonDocument.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "update.json"), shutdown.Token));
            using (config)
                if (config.RootElement.GetProperty("updateMode").GetString() != "startup-notify") return;
            await Task.Delay(TimeSpan.FromSeconds(3), shutdown.Token);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 1024 * 1024 };
            var notifier = new StartupUpdateNotifier(new GitHubUpdateClient(http), typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0),
                System.IO.Path.Combine(AppPaths.DataRoot, "updates.json"));
            await notifier.CheckAndNotifyAsync(owner, shutdown.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException) { }
        finally { owner.Closed -= Closed; }
    }
}

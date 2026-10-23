using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hzip.App;
using Hzip.Core;
using Microsoft.Win32;

internal static class UiWorkflowTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [STAThread] public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--editor")
        { Thread.Sleep(650); File.WriteAllText(args[1] + ".new", "saved by controlled external editor"); File.Move(args[1] + ".new", args[1], true); return 0; }
        string root = Path.GetFullPath(args[0]), repo = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("HZIP_DATA_DIR", Path.Combine(root, "profile"));
        Environment.SetEnvironmentVariable("HZIP_ENGINE", Path.Combine(repo, "release/Hzip/engine/7z.exe"));
        Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.Combine(repo, "tools/dotnet")); Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", Path.Combine(repo, "tools/dotnet"));
        var app = new Hzip.App.App(); app.InitializeComponent(); int code = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await RunAsync(root, repo); }
            catch (Exception error) { File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString()); code = 1; }
            finally { app.Shutdown(); }
        });
        app.Run(); return code;
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => Tree(root).OfType<T>().Single(x => AutomationProperties.GetAutomationId(x) == id);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> condition, string message)
    { var timer = Stopwatch.StartNew(); while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(message); await Task.Delay(80); } }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(path); png.Save(file);
    }
    private static async Task RunAsync(string root, string repo)
    {
        var results = new List<string>(); var service = new ArchiveService();
        const string password = "UI-test@中文2026"; new PasswordVault().Save("界面验证用密码", password);
        string input = Path.Combine(root, "create-input.txt"); File.WriteAllText(input, "encrypted GUI archive");
        var create = new CreateWindow([input], new UserSettings());
        create.Loaded += (_, _) =>
        {
            Find<ComboBox>(create, "CreateSavedPassword").SelectedIndex = 0;
            Check(Find<PasswordBox>(create, "CreatePassword").Password == password && Find<PasswordBox>(create, "CreatePasswordConfirm").Password == password, "Vault selection did not populate both password fields");
            Capture(create, Path.Combine(root, "create-vault.png")); Click(create.Accept);
        };
        Check(create.ShowDialog() == true, "Create dialog did not accept");
        await service.CreateAsync(create.Inputs, create.OutputPath, create.Options); await service.Engine.TestAsync(create.OutputPath, password);
        bool rejected = false; try { await service.Engine.TestAsync(create.OutputPath, "wrong"); } catch (ArchiveOperationException) { rejected = true; }
        Check(rejected, "UI archive is not encrypted"); results.Add("Create vault selection fills confirmation and creates a genuinely encrypted archive");

        string legacy = LegacyZipFixture.Create(root, "legacy-ui.zip");
        var legacyWindow = new MainWindow([legacy], initialCodePage: 932); legacyWindow.Show();
        var legacyGrid = (DataGrid)legacyWindow.FindName("EntriesGrid"); await Until(() => legacyGrid.Items.Count > 0, "Legacy archive UI not loaded");
        // Root listing includes a synthetic folder and a raw Shift-JIS sibling name.
        legacyGrid.SelectedItem = legacyGrid.Items.Cast<ArchiveEntry>().Single(e => e.NormalizedPath == "残す.txt");
        Click(Find<Button>(legacyWindow, "PreviewButton")); await Until(() => Application.Current.Windows.OfType<PreviewWindow>().Any(), "Legacy preview did not open");
        var legacyPreview = Application.Current.Windows.OfType<PreviewWindow>().Single(); var legacyEditor = Find<TextBox>(legacyPreview, "PreviewEditor");
        Check(legacyEditor.Text == "keep sibling", "Selected codepage did not reach GUI preview"); legacyEditor.Text = "GUI edited Shift-JIS member";
        Click(Find<Button>(legacyPreview, "SavePreviewButton")); await Until(() => (string)Find<Button>(legacyPreview, "SavePreviewButton").Content == "已保存", "Legacy GUI edit failed");
        using (var copy = await service.PreviewAsync(legacy, (await service.ListAsync(legacy, codePage: 932)).Entries.Single(e => e.NormalizedPath == "残す.txt"), codePage: 932))
            Check(File.ReadAllText(copy.FilePath) == "GUI edited Shift-JIS member", "Legacy GUI save content mismatch");
        Capture(legacyPreview, Path.Combine(root, "legacy-preview.png")); legacyPreview.Close(); await Until(() => !legacyPreview.IsVisible, "Legacy preview stayed open"); legacyWindow.Close();
        results.Add("Real GUI uses Shift-JIS for preview and writes edited content back without duplicating names");

        string extension = ".hzip-uitest-" + Guid.NewGuid().ToString("N"), progId = "Hzip.UiTest." + Guid.NewGuid().ToString("N");
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension)) key.SetValue("", progId);
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + progId + @"\shell\open\command"))
            key.SetValue("", "\"" + Path.Combine(AppContext.BaseDirectory, "Hzip.Ui.Tests.exe") + "\" --editor \"%1\"");
        try
        {
            string editInput = Path.Combine(root, "external document" + extension), archive = Path.Combine(root, "external-edit.zip"); File.WriteAllText(editInput, "original external content");
            await service.CreateAsync([editInput], archive, new CreateOptions());
            var window = new MainWindow([archive]); window.Show();
            var grid = (DataGrid)window.FindName("EntriesGrid"); await Until(() => grid.Items.Count == 1, "Archive UI not loaded");
            grid.SelectedIndex = 0; Click(Find<Button>(window, "PreviewButton"));
            await Until(() => Application.Current.Windows.OfType<PreviewWindow>().Any(), "Preview did not open");
            var preview = Application.Current.Windows.OfType<PreviewWindow>().Single(); string originalHash = await ArchiveService.HashAsync(archive);
            Click(Find<Button>(preview, "OpenExternalEditor"));
            await Until(() => Find<TextBlock>(preview, "ExternalEditStatus").Text.Contains("检测到外部修改"), "External save not detected");
            Check(await ArchiveService.HashAsync(archive) == originalHash, "Changed archive before user confirmation");
            Capture(preview, Path.Combine(root, "external-pending.png")); results.Add("Controlled external application saves by atomic replace; GUI detects change without automatically overwriting the archive");
            Click(Find<Button>(preview, "SaveExternalEdit"));
            await Until(() => !Find<Button>(preview, "SaveExternalEdit").IsEnabled, "External save did not start");
            var entry = (await service.ListAsync(archive)).Entries.Single();
            var clock = Stopwatch.StartNew(); bool saved = false;
            while (!saved)
            {
                await Task.Delay(150); using var copy = await service.PreviewAsync(archive, entry); saved = File.ReadAllText(copy.FilePath) == "saved by controlled external editor";
                if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Confirmed edit was not saved");
            }
            Capture(preview, Path.Combine(root, "external-saved.png")); results.Add("Confirmed external write-back changes the actual archive content");
            var session = (ExternalEditSession)typeof(PreviewWindow).GetField("externalSession", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
            File.WriteAllText(session.FilePath, "pending edit must survive close"); string committedHash = await ArchiveService.HashAsync(archive);
            var answer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            answer.Tick += (_, _) =>
            {
                IntPtr dialog = FindWindow(null, "外部编辑尚未写回");
                if (dialog != IntPtr.Zero) { GetWindowThreadProcessId(dialog, out uint pid); if (pid == Environment.ProcessId) { PostMessage(dialog, 0x111, (IntPtr)7, IntPtr.Zero); answer.Stop(); } }
            };
            answer.Start(); preview.Close(); await Until(() => !preview.IsVisible, "Preview did not close after keep-copy choice"); answer.Stop();
            Check(File.ReadAllText(session.FilePath) == "pending edit must survive close" && await ArchiveService.HashAsync(archive) == committedHash, "Closing lost pending content or modified archive");
            results.Add("Closing without write-back retains pending external edits and preserves archive bytes");
            using var export = await ArchiveExportLease.CreateAsync(service, archive, [entry.Path]);
            var data = new DataObject(DataFormats.FileDrop, export.Paths.ToArray());
            Check(data.GetFileDropList().Count == 1 && File.ReadAllText(data.GetFileDropList()[0]!) == "saved by controlled external editor", "Invalid Shell file-drop data");
            results.Add("Real extracted files are exposed through the Windows FileDrop data format; physical Explorer mouse drop remains separate acceptance");
            window.Close();
        }
        finally
        { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + extension, false); Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + progId, false); }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { Failures = 0, Results = results, PhysicalExplorerMouseDropTested = false, OfficeApplicationsTested = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

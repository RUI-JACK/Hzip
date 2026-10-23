using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Windows.Threading;
using Hzip.Core;

namespace Hzip.App;

public sealed class PreviewWindow : Window
{
    private readonly PreviewLease lease; private readonly ArchiveEntry entry; private readonly Func<string, Task> save;
    private readonly TextBox editor = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, Padding = new Thickness(17), BorderThickness = new Thickness(0), IsReadOnly = true };
    private Encoding encoding = new UTF8Encoding(false); private double scale = 1; private int angle;
    private readonly Image image = new() { Stretch = Stretch.Uniform, Margin = new Thickness(18) };
    private bool dirty, closeApproved, closeHandling, busy, polling;
    private readonly string sourceArchive; private readonly int codePage; private readonly bool canWrite;
    private ExternalEditSession? externalSession;
    private readonly DispatcherTimer externalTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly TextBlock externalStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(22, 0, 22, 14), Visibility = Visibility.Collapsed };
    private readonly Button externalSave = new() { Content = "确认写回归档", Margin = new Thickness(0, 0, 9, 7), Padding = new Thickness(12, 7, 12, 7), Visibility = Visibility.Collapsed };
    private Button? internalSave;
    public bool HasUnsavedChanges => dirty || externalSession != null;
    public void ApproveOwnerClose() => closeApproved = true;
    public PreviewWindow(PreviewLease file, ArchiveEntry archiveEntry, Func<string, Task> saveBack, string sourceArchive, int codePage = 0, bool canWrite = true)
    {
        lease = file; entry = archiveEntry; save = saveBack; this.sourceArchive = sourceArchive; this.codePage = codePage; this.canWrite = canWrite; Title = entry.Name + " · Hzip 预览"; Width = 890; Height = 680; MinWidth = 600; MinHeight = 420;
        var root = new DockPanel(); var header = new Grid { Margin = new Thickness(22, 19, 22, 16) }; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new StackPanel(); name.Children.Add(new TextBlock { Text = entry.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }); name.Children.Add(new TextBlock { Text = entry.DisplaySize + " · " + entry.Path, Foreground = Brushes.SlateGray, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }); header.Children.Add(name);
        var copy = new Button { Content = "保存副本…", VerticalAlignment = VerticalAlignment.Center }; copy.Click += (_, _) => { var picker = new SaveFileDialog { FileName = entry.Name }; if (picker.ShowDialog(this) == true) File.Copy(file.FilePath, picker.FileName, true); }; Grid.SetColumn(copy, 1); header.Children.Add(copy); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(externalStatus, Dock.Top); root.Children.Add(externalStatus);
        var tools = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 20, 13) }; DockPanel.SetDock(tools, Dock.Bottom); root.Children.Add(tools);
        void Tool(string title, Action action) { var button = new Button { Content = title, Margin = new Thickness(0, 0, 9, 0), FontSize = 11, Padding = new Thickness(12, 7, 12, 7) }; button.Click += (_, _) => action(); tools.Children.Add(button); }
        var external = new Button { Content = "用默认应用打开", Margin = new Thickness(0, 0, 9, 0), FontSize = 11, Padding = new Thickness(12, 7, 12, 7) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(external, "OpenExternalEditor");
        System.Windows.Automation.AutomationProperties.SetAutomationId(externalStatus, "ExternalEditStatus");
        System.Windows.Automation.AutomationProperties.SetAutomationId(externalSave, "SaveExternalEdit");
        external.Click += async (_, _) => await OpenExternalAsync();
        externalSave.Click += async (_, _) => await SaveExternalAsync();
        tools.Children.Add(external); tools.Children.Add(externalSave);
        var reveal = new Button { Content = "打开编辑副本文件夹", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 9, 7), Padding = new Thickness(12, 7, 12, 7) };
        reveal.Click += (_, _) => { if (externalSession != null) Process.Start(new ProcessStartInfo("explorer.exe", System.IO.Path.GetDirectoryName(externalSession.FilePath)!) { UseShellExecute = true }); };
        tools.Children.Add(reveal);
        externalTimer.Tick += async (_, _) => { if (externalSession != null) { reveal.Visibility = Visibility.Visible; await PollExternalAsync(); } };
        var container = new Border { Background = new SolidColorBrush(Color.FromRgb(236, 241, 243)), BorderBrush = new SolidColorBrush(Color.FromRgb(220, 228, 232)), BorderThickness = new Thickness(0, 1, 0, 1), Margin = new Thickness(0, 0, 0, 15) }; root.Children.Add(container); Content = root;
        if (PreviewKind.IsImage(entry.Path))
        {
            try
            {
                using var stream = File.OpenRead(file.FilePath); var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); image.Source = decoder.Frames[0]; container.Child = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                void Transform() { var group = new TransformGroup(); group.Children.Add(new ScaleTransform(scale, scale)); group.Children.Add(new RotateTransform(angle)); image.LayoutTransform = group; }
                Tool("放大", () => { scale = Math.Min(5, scale + .25); Transform(); }); Tool("缩小", () => { scale = Math.Max(.25, scale - .25); Transform(); }); Tool("旋转", () => { angle = (angle + 90) % 360; Transform(); });
                if (decoder.Frames.Count > 1) { int frame = 0; Tool("下一帧", () => { frame = (frame + 1) % decoder.Frames.Count; image.Source = decoder.Frames[frame]; }); }
            }
            catch (Exception error) { container.Child = new TextBlock { Text = "系统图像解码器无法预览此图片。\n" + error.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(25) }; }
        }
        else if (new FileInfo(file.FilePath).Length <= 4 * 1024 * 1024 && TryReadText(file.FilePath, out string text))
        {
            editor.Text = text; editor.IsReadOnly = false; container.Child = editor;
            var write = new Button { Content = "保存回归档", Style = (Style)Application.Current.FindResource("Primary"), Padding = new Thickness(12, 7, 12, 7), FontSize = 11, Visibility = canWrite ? Visibility.Visible : Visibility.Collapsed };
            internalSave = write; editor.IsReadOnly = !canWrite;
            System.Windows.Automation.AutomationProperties.SetAutomationId(editor, "PreviewEditor"); System.Windows.Automation.AutomationProperties.SetAutomationId(write, "SavePreviewButton");
            editor.TextChanged += (_, _) => { if (externalSession == null) { dirty = true; write.Content = "保存回归档"; } };
            write.Click += async (_, _) => await SaveInternalAsync(); tools.Children.Add(write);

        }
        else container.Child = new TextBlock { Text = "此文件暂不支持内置预览\n\n可以保存副本，或用系统默认应用打开。", FontSize = 15, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Closing += async (_, e) =>
        {
            if (closeApproved) return; e.Cancel = true; if (closeHandling) return; closeHandling = true;
            try { if (await PrepareOwnerCloseAsync()) { closeApproved = true; _ = Dispatcher.BeginInvoke(new Action(Close)); } }
            finally { closeHandling = false; }
        };
        Closed += (_, _) => { externalTimer.Stop(); lease.Dispose(); };
        Activated += async (_, _) => await PollExternalAsync();
    }
    private async Task<bool> SaveInternalAsync()
    {
        if (busy || !canWrite || externalSession != null) return false;
        busy = true; editor.IsReadOnly = true; if (internalSave != null) internalSave.IsEnabled = false;
        try { await File.WriteAllTextAsync(lease.FilePath, editor.Text, encoding); await save(lease.FilePath); dirty = false; if (internalSave != null) internalSave.Content = "已保存"; return true; }
        catch (Exception error) { MessageBox.Show(this, error.Message, "保存未完成", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        finally { busy = false; editor.IsReadOnly = !canWrite || externalSession != null; if (internalSave != null) internalSave.IsEnabled = true; }
    }
    private async Task OpenExternalAsync()
    {
        if (busy) return;
        if (dirty && (!FormWindow.Ask(this, "先保存内置编辑的修改，再用外部应用打开吗？") || !await SaveInternalAsync())) return;
        try
        {
            bool executable = new[] { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".lnk", ".scr", ".hta", ".reg" }.Contains(System.IO.Path.GetExtension(lease.FilePath).ToLowerInvariant());
            if (executable && !FormWindow.Ask(this, "这个条目可能执行程序或脚本。确认使用系统默认应用打开它吗？")) return;
            busy = true;
            externalSession ??= await ExternalEditSession.CreateAsync(lease.FilePath, sourceArchive, entry.Path, codePage);
            editor.IsReadOnly = true; if (internalSave != null) internalSave.Visibility = Visibility.Collapsed;
            externalSave.Visibility = canWrite ? Visibility.Visible : Visibility.Collapsed;
            externalStatus.Visibility = Visibility.Visible;
            externalStatus.Text = "外部编辑副本会保留在本机。请在外部应用保存后回到这里，确认写回归档。\n" + externalSession.FilePath;
            if (!canWrite) externalStatus.Text = "此归档格式只读；外部编辑副本会保留在下方路径，不会写回归档。\n" + externalSession.FilePath;
            externalTimer.Start();
            Process.Start(new ProcessStartInfo(externalSession.FilePath) { UseShellExecute = true });
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "无法打开外部应用", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { busy = false; }
    }
    private async Task PollExternalAsync()
    {
        if (externalSession == null || polling || busy || closeApproved) return; polling = true;
        try
        {
            bool changed = await externalSession.HasChangesAsync(); externalSave.IsEnabled = changed && canWrite;
            externalStatus.Text = (changed ? canWrite ? "检测到外部修改。请先在外部应用保存并关闭文件，再点击“确认写回归档”。" : "检测到外部修改；此归档只读，修改保存在编辑副本。" : "编辑副本与已保存版本一致。外部修改后会在这里提示。") + "\n编辑副本保留在：" + externalSession.FilePath;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { externalStatus.Text = "编辑副本暂时无法读取，请保存并关闭外部文件后重试。\n" + externalSession.FilePath; externalSave.IsEnabled = canWrite; }
        finally { polling = false; }
    }
    private async Task<bool> SaveExternalAsync()
    {
        if (externalSession == null || busy || !canWrite) return false; busy = true; externalSave.IsEnabled = false;
        try
        {
            await externalSession.SaveAsync(save);
            if (TryReadText(externalSession.FilePath, out string text)) editor.Text = text;
            dirty = false; externalStatus.Text = "修改已写回归档；编辑副本继续保留。\n" + externalSession.FilePath; return true;
        }
        catch (Exception error)
        { MessageBox.Show(this, error.Message + "\n\n编辑内容没有删除，副本保留在：\n" + externalSession.FilePath, "写回未完成", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        finally { busy = false; await PollExternalAsync(); }
    }
    public async Task<bool> PrepareOwnerCloseAsync()
    {
        if (busy) return false;
        if (dirty)
        {
            var result = MessageBox.Show(this, "这个条目有未保存的修改。保存回归档后再关闭吗？\n" + entry.Name, "Hzip", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes && !await SaveInternalAsync()) return false;
            if (result == MessageBoxResult.No) dirty = false;
        }
        if (externalSession == null) return true;
        bool changed;
        try { changed = await externalSession.HasChangesAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return FormWindow.Ask(this, "外部文件暂时无法读取。关闭此预览？编辑副本仍会保留在：\n" + externalSession.FilePath); }
        if (!changed || !canWrite) return true;
        var externalResult = MessageBox.Show(this, "检测到外部修改。现在写回归档吗？\n\n“是”：写回成功后关闭。\n“否”：关闭，但保留编辑副本。\n“取消”：继续编辑。\n\n" + externalSession.FilePath, "外部编辑尚未写回", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (externalResult == MessageBoxResult.Cancel) return false;
        if (externalResult != MessageBoxResult.Yes) return true;
        if (!await SaveExternalAsync()) return false;
        try { return !await externalSession.HasChangesAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    private bool TryReadText(string file, out string text)
    {
        byte[] bytes = File.ReadAllBytes(file); text = "";
        if (bytes.Take(4096).Any(b => b == 0))
        {
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = Encoding.Unicode; text = encoding.GetString(bytes, 2, bytes.Length - 2); return true; }
            return false;
        }
        try { encoding = new UTF8Encoding(false, true); text = encoding.GetString(bytes); }
        catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(936); text = encoding.GetString(bytes); }
        if (text.StartsWith('\ufeff')) text = text[1..]; return true;
    }
}

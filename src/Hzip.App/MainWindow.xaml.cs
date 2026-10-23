using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Hzip.Core;

namespace Hzip.App;

public partial class MainWindow : Window
{
    private readonly ArchiveService service = new();
    private readonly UserSettings settings = UserSettings.Load();
    private readonly ObservableCollection<JobItem> jobs = [];
    private readonly SemaphoreSlim worker = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly string[] startupArgs;
    private readonly PreviewLease? ownedPreview;
    private ArchiveInfo? archive;
    private string folder = "", password = "";
    private int codePage;
    private JobsWindow? jobsWindow;
    private JobItem? activeJob;
    private bool closeHandling, closeApproved;
    public MainWindow(string[]? args = null, PreviewLease? previewLease = null, int? initialCodePage = null)
    {
        startupArgs = args ?? []; ownedPreview = previewLease; InitializeComponent(); codePage = initialCodePage ?? settings.CodePage; RefreshRecent();
        Loaded += async (_, _) => await GuardAsync(async () =>
        {
            SetButtonNames(this);
            if (startupArgs.FirstOrDefault() == "--open-from")
            {
                string? selected = startupArgs.Skip(1).FirstOrDefault();
                string? initial = selected == null ? null : Directory.Exists(selected) ? selected : System.IO.Path.GetDirectoryName(selected);
                string? chosen = FormWindow.Archive(this, initial); if (chosen != null) await OpenArchiveAsync(chosen);
            }
            else if (startupArgs.Length > 0 && File.Exists(startupArgs[0])) await OpenArchiveAsync(startupArgs[0], previewLease == null);
        });
    }
    private static void SetButtonNames(DependencyObject root)
    {
        if (root is Button { Content: StackPanel stack } button)
        {
            string name = string.Join(" ", stack.Children.OfType<TextBlock>().Select(x => x.Text).Where(s => s.Any(char.IsLetterOrDigit)));
            if (!string.IsNullOrEmpty(name)) System.Windows.Automation.AutomationProperties.SetName(button, name);
        }
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) SetButtonNames(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }
    private async Task<T> EnqueueAsync<T>(string title, Func<CancellationToken, IProgress<JobProgress>, Task<T>> action)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); var job = new JobItem(title, source); jobs.Insert(0, job); bool acquired = false; UpdateJobPanel();
        job.Control.StateChanged += () => Dispatcher.InvokeAsync(() =>
        {
            if (!job.IsActive || job.State == "等待中") return;
            job.State = job.Control.IsPaused ? "已暂停" : job.Control.IsPauseRequested ? "正在暂停" : "处理中";
            job.Message = job.Control.IsPaused ? "已暂停，点击继续可接着处理" : job.Control.IsPauseRequested ? "等待当前操作到达暂停点…" : "正在继续处理…";
            UpdateJobPanel();
        });
        var progress = new Progress<JobProgress>(p => { if (job.State != "处理中") return; job.Message = p.Stage + " · " + p.Message; job.Percent = p.Percent.HasValue ? Math.Max(job.Percent ?? 0, Math.Min(99, p.Percent.Value)) : null; UpdateJobPanel(); });
        try
        {
            await worker.WaitAsync(source.Token); acquired = true; job.State = "处理中"; UpdateJobPanel();
            T result = await Task.Run(async () => { using var scope = job.Control.Enter(); return await action(source.Token, progress); }, source.Token);
            job.State = "已完成"; job.Percent = 100; job.Message = "任务已完成"; StatusLabel.Text = title + " · 已完成"; return result;
        }
        catch (OperationCanceledException) { job.State = "已取消"; job.Message = "任务已取消"; throw; }
        catch (Exception error) { job.State = "未完成"; job.Message = error.Message; job.Details = error is ArchiveOperationException detail ? detail.Message + "\n\n" + detail.Details : error.ToString(); throw; }
        finally { job.Control.Complete(); if (acquired) worker.Release(); UpdateJobPanel(); }
    }
    private Task<bool> EnqueueAsync(string title, Func<CancellationToken, IProgress<JobProgress>, Task> action) => EnqueueAsync(title, async (ct, progress) => { await action(ct, progress); return true; });
    private void UpdateJobPanel()
    {
        int pending = jobs.Count(j => j.IsActive); JobsLabel.Text = pending > 0 ? $"任务记录 · {pending}" : "任务记录";
        activeJob = jobs.LastOrDefault(j => j.IsActive && j.State != "等待中") ?? jobs.LastOrDefault(j => j.State == "等待中"); ActiveJobPanel.Visibility = activeJob == null ? Visibility.Collapsed : Visibility.Visible;
        if (activeJob == null) return; ActiveJobTitle.Text = activeJob.Title + " · " + activeJob.Message; ActiveJobProgress.IsIndeterminate = !activeJob.Percent.HasValue && !activeJob.Control.IsPauseRequested; ActiveJobProgress.Value = activeJob.Percent ?? 0;
        PauseActiveButton.Content = activeJob.Control.IsPauseRequested ? "继续" : "暂停"; PauseActiveButton.IsEnabled = activeJob.State != "等待中";
    }
    private async Task GuardAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { StatusLabel.Text = "任务已取消"; }
        catch (Exception error) { ShowError(error); }
    }
    private void ShowError(Exception error)
    {
        if (shutdown.IsCancellationRequested) return;
        var dialog = new FormWindow("操作未完成", error.Message, 590) { Owner = this }; dialog.Cancel.Visibility = Visibility.Collapsed; dialog.Accept.Content = "知道了"; dialog.Accept.Click += (_, _) => dialog.DialogResult = true;
        string details = error is ArchiveOperationException archiveError ? archiveError.Details : error.ToString();
        if (!string.IsNullOrWhiteSpace(details)) dialog.Body.Children.Add(new Expander { Header = "查看详细信息", Content = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 200, FontSize = 11 }, Margin = new Thickness(0, 8, 0, 0) });
        dialog.ShowDialog();
    }
    private async Task OpenArchiveAsync(string path, bool remember = true, string? knownPassword = null)
    {
        string attemptPassword = knownPassword ?? "";
        while (true)
        {
            try
            {
                var info = await EnqueueAsync("读取 " + System.IO.Path.GetFileName(path), (ct, _) => service.ListAsync(path, attemptPassword, ct, codePage));
                archive = info; password = attemptPassword; folder = ""; SearchBox.Text = ""; Title = System.IO.Path.GetFileName(path) + " · Hzip";
                PageTitle.Text = System.IO.Path.GetFileName(path); PageSubtitle.Text = ownedPreview == null ? path : "嵌套归档 · " + System.IO.Path.GetFileName(path); FormatLabel.Text = info.Format.ToUpperInvariant();
                PageTitle.ToolTip = path; AddFilesButton.IsEnabled = info.Format is "zip" or "7z" or "tar" or "wim";
                ArchiveMetrics.Text = $"{info.Entries.Count(e => !e.IsDirectory):N0} 个文件   ·   归档 {TextFormat.Bytes(info.ArchiveBytes)}   ·   原始 {TextFormat.Bytes(info.UnpackedBytes)}";
                HomePanel.Visibility = Visibility.Collapsed; ArchivePanel.Visibility = Visibility.Visible; RenderEntries(); if (remember) { settings.Remember(path); RefreshRecent(); } return;
            }
            catch (ArchiveOperationException error) when (error.NeedsPassword)
            {
                var prompt = new PasswordWindow(path) { Owner = this }; if (prompt.ShowDialog() != true) return; attemptPassword = prompt.Password;
            }
        }
    }
    private string? GetPassword()
    {
        if (!string.IsNullOrEmpty(password)) return password;
        if (archive?.HasEncryption != true) return null;
        var dialog = new PasswordWindow(archive.FilePath) { Owner = this }; if (dialog.ShowDialog() != true) throw new OperationCanceledException(); password = dialog.Password; return password;
    }
    private void RenderEntries()
    {
        if (archive == null) return; string query = SearchBox.Text.Trim(); SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        List<ArchiveEntry> visible;
        if (query.Length > 0) visible = archive.Entries.Where(e => e.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        else
        {
            var children = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase); string prefix = folder.Length > 0 ? folder.TrimEnd('/') + "/" : "";
            foreach (var entry in archive.Entries)
            {
                string path = entry.NormalizedPath.TrimEnd('/'); if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue; string rest = path[prefix.Length..]; if (rest.Length == 0) continue;
                int separator = rest.IndexOf('/');
                if (separator < 0) children[path] = entry;
                else
                {
                    string key = prefix + rest[..separator]; if (!children.ContainsKey(key)) children[key] = new ArchiveEntry(key + "/", 0, 0, true, null, false, "", "", "D");
                }
            }
            visible = children.Values.ToList();
        }
        visible = visible.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); EntriesGrid.ItemsSource = visible;
        Breadcrumb.Text = query.Length > 0 ? "搜索结果 · " + query : folder.Length == 0 ? "归档根目录" : "归档根目录  ›  " + folder.Replace("/", "  ›  ");
        UpButton.IsEnabled = folder.Length > 0; NoEntriesLabel.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed; NoEntriesLabel.Text = query.Length > 0 ? "没有找到匹配的文件" : "此文件夹没有内容";
        StatusLabel.Text = $"{visible.Count:N0} 个条目";
    }
    private IReadOnlyList<string> Selection() => EntriesGrid.SelectedItems.Cast<ArchiveEntry>().Select(e => e.Path).ToArray();
    private void RefreshRecent()
    {
        var recent = settings.RecentFiles.Where(File.Exists).Select(path => new RecentFile(path, System.IO.Path.GetFileName(path), System.IO.Path.GetDirectoryName(path)!, TextFormat.Bytes(new FileInfo(path).Length))).ToArray();
        RecentList.ItemsSource = recent; NoRecentLabel.Visibility = recent.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RecentList.Visibility = recent.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task CreateForAsync(IReadOnlyList<string> inputs)
    {
        var dialog = new CreateWindow(inputs, settings) { Owner = this }; if (dialog.ShowDialog() != true) return;
        var chosenInputs = dialog.Inputs; var chosenOptions = dialog.Options; string chosenOutput = dialog.OutputPath;
        var output = await EnqueueAsync("压缩 " + System.IO.Path.GetFileName(chosenOutput), (ct, p) => service.CreateAsync(chosenInputs, chosenOutput, chosenOptions, ct, p));
        string main = output[0];
        if (dialog.RecycleSources)
        {
            await EnqueueAsync("校验并回收源文件", async (ct, p) =>
            {
                await service.Engine.TestAsync(main, chosenOptions.Password, ct, p);
                foreach (string input in chosenInputs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(input)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(input, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(input, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            });
        }
        await OpenArchiveAsync(main, true, chosenOptions.Password);
    }
    private async Task ExtractCurrentAsync(bool selectedOnly = false)
    {
        if (archive == null) return; string input = archive.FilePath; string? known = GetPassword(); var dialog = new ExtractWindow(input, Selection(), known, codePage, selectedOnly) { Owner = this }; if (dialog.ShowDialog() != true) return;
        var conflicts = new ExtractionConflictSession(this);
        var result = await EnqueueAsync("解压 " + System.IO.Path.GetFileName(input), (ct, p) => service.ExtractAsync(input, dialog.Destination, dialog.Options, ct, p, conflicts.ResolveAsync));
        StatusLabel.Text = $"解压完成 · {result.Files:N0} 个文件" + (result.Warnings.Count > 0 ? $" · {result.Warnings.Count} 项提示" : "");
        if (dialog.Reveal) OpenFolder(result.Destination);
        if (result.Warnings.Count > 0) ShowText("解压提示", string.Join("\n", result.Warnings));
    }
    private async Task PreviewSelectedAsync()
    {
        if (archive == null || EntriesGrid.SelectedItem is not ArchiveEntry entry) return;
        if (entry.IsDirectory) { folder = entry.NormalizedPath.TrimEnd('/'); SearchBox.Text = ""; RenderEntries(); return; }
        string source = archive.FilePath; string? key = entry.Encrypted ? GetPassword() : string.IsNullOrEmpty(password) ? null : password;
        if (PreviewKind.IsArchive(entry.Path))
        {
            var lease = await EnqueueAsync("打开嵌套归档", (ct, _) => service.PreviewAsync(source, entry, key, ct, long.MaxValue, codePage));
            var nested = new MainWindow([lease.FilePath], lease, codePage); nested.Show(); return;
        }
        bool canWrite = archive.Format is "zip" or "7z" or "tar" or "wim"; int selectedCodePage = codePage;
        var file = await EnqueueAsync("预览 " + entry.Name, (ct, _) => service.PreviewAsync(source, entry, key, ct, codePage: selectedCodePage));
        string memberHash = await ArchiveService.HashAsync(file.FilePath);
        var preview = new PreviewWindow(file, entry, async edited => { await EnqueueAsync("保存 " + entry.Name, (ct, p) => service.UpdateMemberAsync(source, entry.Path, edited, key, ct, p, selectedCodePage, expectedMemberHash: memberHash)); memberHash = await ArchiveService.HashAsync(edited); if (archive?.FilePath == source) await ReloadAsync(); }, source, selectedCodePage, canWrite) { Owner = this }; preview.Show();
    }
    private async Task ReloadAsync()
    {
        if (archive == null) return; string path = archive.FilePath, oldFolder = folder, oldQuery = SearchBox.Text; string known = password;
        archive = await EnqueueAsync("刷新归档", (ct, _) => service.ListAsync(path, known, ct, codePage)); folder = oldFolder; SearchBox.Text = oldQuery;
        ArchiveMetrics.Text = $"{archive.Entries.Count(e => !e.IsDirectory):N0} 个文件   ·   归档 {TextFormat.Bytes(archive.ArchiveBytes)}   ·   原始 {TextFormat.Bytes(archive.UnpackedBytes)}"; RenderEntries();
    }
    private void ShowText(string title, string content)
    {
        var window = new TextPrompt(title, "", content, true, "关闭") { Owner = this }; window.Cancel.Visibility = Visibility.Collapsed; window.ShowDialog();
    }
    private static void OpenFolder(string directory) { var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; info.ArgumentList.Add(directory); Process.Start(info); }
    private async void Open_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => { string? path = FormWindow.Archive(this); if (path != null) await OpenArchiveAsync(path); });
    private async void Create_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => CreateForAsync([]));
    private async void Extract_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => ExtractCurrentAsync());
    private async void ExtractSelected_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => ExtractCurrentAsync(true));
    private async void Preview_Click(object sender, RoutedEventArgs e) => await GuardAsync(PreviewSelectedAsync);
    private async void Entry_DoubleClick(object sender, MouseButtonEventArgs e) { if (EntriesGrid.SelectedItem != null && e.OriginalSource is not System.Windows.Controls.Primitives.Thumb) await GuardAsync(PreviewSelectedAsync); }
    private async void Test_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => { if (archive == null) return; string source = archive.FilePath; string? key = GetPassword(); var result = await EnqueueAsync("测试 " + System.IO.Path.GetFileName(source), (ct, p) => service.Engine.TestAsync(source, key, ct, p)); MessageBox.Show(this, result.HasWarning ? result.Error : "归档完整性校验通过。", "Hzip", MessageBoxButton.OK, result.HasWarning ? MessageBoxImage.Warning : MessageBoxImage.Information); });
    private async void Add_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (archive == null) return; var picker = new OpenFileDialog { Multiselect = true, Title = "添加文件到归档" }; if (picker.ShowDialog(this) != true) return;
        string source = archive.FilePath; string? key = GetPassword(); await EnqueueAsync("添加文件", (ct, p) => service.AddAsync(source, picker.FileNames, key, ct, p, codePage)); await ReloadAsync();
    });
    private async void AddFolder_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (archive == null) return; string? folderToAdd = FormWindow.Folder(this); if (folderToAdd == null) return;
        string source = archive.FilePath; string? key = GetPassword(); await EnqueueAsync("添加文件夹", (ct, p) => service.AddAsync(source, [folderToAdd], key, ct, p, codePage)); await ReloadAsync();
    });
    private async void Rename_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (archive == null || EntriesGrid.SelectedItems.Count != 1 || EntriesGrid.SelectedItem is not ArchiveEntry entry) return;
        var dialog = new TextPrompt("重命名条目", "填写归档内的新名称或相对路径", entry.Path.TrimEnd('/')) { Owner = this }; if (dialog.ShowDialog() != true || dialog.Value == entry.Path) return;
        string source = archive.FilePath; string? key = GetPassword(); await EnqueueAsync("重命名 " + entry.Name, (ct, p) => service.RenameAsync(source, entry.Path.TrimEnd('/'), dialog.Value, key, ct, p, codePage)); await ReloadAsync();
    });
    private async void Delete_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (archive == null || Selection().Count == 0) return; var selected = Selection(); if (!FormWindow.Ask(this, $"从归档中删除这 {selected.Count} 个条目？\n文件夹中的内容也会被删除。", "删除归档条目")) return;
        string source = archive.FilePath; string? key = GetPassword(); await EnqueueAsync("删除归档条目", (ct, p) => service.DeleteAsync(source, selected, key, ct, p, codePage)); await ReloadAsync();
    });
    private async void Hash_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => { if (archive == null) return; string source = archive.FilePath; string hash = await EnqueueAsync("计算 SHA-256", (ct, _) => ArchiveService.HashAsync(source, ct)); ShowText("SHA-256", System.IO.Path.GetFileName(source) + "\n\n" + hash); });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await GuardAsync(ReloadAsync);
    private void Encoding_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FormWindow("文件名编码", "文件名显示异常时，可尝试切换旧式归档的编码。", 450) { Owner = this };
        var choices = new[] { new { Value = 0, Title = "自动检测" }, new { Value = 936, Title = "简体中文 · GBK (936)" }, new { Value = 932, Title = "日文 · Shift-JIS (932)" }, new { Value = 950, Title = "繁体中文 · Big5 (950)" }, new { Value = 437, Title = "西文 · OEM (437)" }, new { Value = 65001, Title = "UTF-8" } };
        var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Title", SelectedItem = choices.FirstOrDefault(x => x.Value == codePage) ?? choices[0] }; dialog.Body.Children.Add(combo);
        dialog.Accept.Click += async (_, _) => { codePage = choices[combo.SelectedIndex].Value; dialog.DialogResult = true; await GuardAsync(ReloadAsync); }; dialog.ShowDialog();
    }
    private void Home_Click(object sender, RoutedEventArgs e) { archive = null; folder = password = ""; Title = "Hzip · 压缩文件管理器"; PageTitle.Text = "归档工作台"; PageSubtitle.Text = "打开、整理与打包你的文件"; ArchivePanel.Visibility = Visibility.Collapsed; HomePanel.Visibility = Visibility.Visible; RefreshRecent(); StatusLabel.Text = "就绪"; }
    private void CloseArchive_Click(object sender, RoutedEventArgs e) => Home_Click(sender, e);
    private void Up_Click(object sender, RoutedEventArgs e) { int cut = folder.TrimEnd('/').LastIndexOf('/'); folder = cut < 0 ? "" : folder[..cut]; SearchBox.Text = ""; RenderEntries(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized && archive != null) RenderEntries(); }
    private void Entry_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (archive != null && EntriesGrid.SelectedItems.Count > 0) StatusLabel.Text = $"已选 {EntriesGrid.SelectedItems.Count:N0} 项 · {TextFormat.Bytes(EntriesGrid.SelectedItems.Cast<ArchiveEntry>().Sum(x => x.Size))}"; }
    private async void Recent_DoubleClick(object sender, MouseButtonEventArgs e) { if (RecentList.SelectedItem is RecentFile file) await GuardAsync(() => OpenArchiveAsync(file.Path)); }
    private void ClearHistory_Click(object sender, RoutedEventArgs e) { settings.RecentFiles.Clear(); settings.Save(); RefreshRecent(); }
    private void More_Click(object sender, RoutedEventArgs e) { var button = (Button)sender; button.ContextMenu.PlacementTarget = button; button.ContextMenu.IsOpen = true; }
    private void CopyPath_Click(object sender, RoutedEventArgs e) { if (Selection().Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, Selection())); }
    private void Reveal_Click(object sender, RoutedEventArgs e) { if (archive != null) OpenFolder(System.IO.Path.GetDirectoryName(archive.FilePath)!); }
    private void Jobs_Click(object sender, RoutedEventArgs e) { if (jobsWindow == null || !jobsWindow.IsVisible) { jobsWindow = new JobsWindow(jobs) { Owner = this }; jobsWindow.Show(); } else jobsWindow.Activate(); }
    private void CancelActive_Click(object sender, RoutedEventArgs e) => activeJob?.Cancellation.Cancel();
    private async void PauseActive_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => { if (activeJob != null) await activeJob.TogglePauseAsync(); });
    private void Vault_Click(object sender, RoutedEventArgs e) => new VaultWindow { Owner = this }.ShowDialog();
    private void Settings_Click(object sender, RoutedEventArgs e) { new SettingsWindow(settings) { Owner = this }.ShowDialog(); codePage = settings.CodePage; RefreshRecent(); }
    private async void Repair_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var dialog = new RepairWindow(archive?.FilePath, password) { Owner = this }; if (dialog.ShowDialog() != true) return;
        string input = dialog.Source, output = dialog.Output; string? key = dialog.Password; int page = dialog.CodePage;
        var report = await EnqueueAsync("修复 ZIP", (ct, p) => ZipRepair.RepairAsync(input, output, ct, p, page, key));
        ShowText("ZIP 修复完成", $"恢复 {report.Recovered} 个条目\n输出：{report.Output}\n恢复清单：{report.ReportPath ?? "未能保存"}\n\n" + (report.Skipped.Count > 0 ? "跳过或提示：\n" + string.Join("\n", report.Skipped) : "所有恢复的条目均通过 CRC 或加密认证验证。")); await OpenArchiveAsync(report.Output);
    });
    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        string? file = archive?.FilePath ?? FormWindow.Archive(this); if (file != null) new RecoveryWindow(service.Engine, file) { Owner = this }.ShowDialog();
    }
    private async void Batch_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var picker = new OpenFileDialog { Multiselect = true, Title = "选择要批量解压的归档", Filter = "归档文件|*.zip;*.zipx;*.7z;*.rar;*.tar;*.gz;*.tgz;*.xz;*.bz2;*.iso;*.lzh;*.001|所有文件|*.*" };
        if (picker.ShowDialog(this) != true) return; string? target = FormWindow.Folder(this, settings.DefaultFolder); if (target == null) return;
        var conflicts = new ExtractionConflictSession(this);
        foreach (string file in picker.FileNames)
        {
            string? key = null;
            while (true)
            {
                try { await EnqueueAsync("批量解压 " + System.IO.Path.GetFileName(file), (ct, p) => service.ExtractAsync(file, target, new ExtractOptions { Layout = ExtractLayout.Subfolder, Password = key, CodePage = codePage, Overwrite = OverwritePolicy.Ask }, ct, p, conflicts.ResolveAsync)); break; }
                catch (ArchiveOperationException error) when (error.NeedsPassword) { var prompt = new PasswordWindow(file) { Owner = this }; if (prompt.ShowDialog() != true) break; key = prompt.Password; }
            }
        }
        OpenFolder(target);
    });
    private void Window_DragOver(object sender, DragEventArgs e) { if (IsOwnDrag(e.Data)) { e.Effects = DragDropEffects.None; e.Handled = true; return; } e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e) => await GuardAsync(async () =>
    {
        if (IsOwnDrag(e.Data)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (paths.Length == 1 && File.Exists(paths[0]) && (PreviewKind.IsArchive(paths[0]) || paths[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) { await OpenArchiveAsync(paths[0]); return; }
        if (archive != null)
        {
            var decision = MessageBox.Show(this, "将这些文件添加到当前归档？\n\n选择“是”添加到当前归档，选择“否”创建一个新归档。", "Hzip", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (decision == MessageBoxResult.Cancel) return;
            if (decision == MessageBoxResult.Yes) { string source = archive.FilePath; string? key = GetPassword(); await EnqueueAsync("添加拖入的文件", (ct, p) => service.AddAsync(source, paths, key, ct, p, codePage)); await ReloadAsync(); return; }
        }
        await CreateForAsync(paths);
    });
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.O) { e.Handled = true; Open_Click(sender, e); }
        else if (ctrl && e.Key == Key.N) { e.Handled = true; Create_Click(sender, e); }
        else if (ctrl && e.Key == Key.E) { e.Handled = true; await GuardAsync(() => ExtractCurrentAsync()); }
        else if (ctrl && e.Key == Key.F && archive != null) { e.Handled = true; SearchBox.Focus(); SearchBox.SelectAll(); }
        else if (e.Key == Key.F5) { e.Handled = true; await GuardAsync(ReloadAsync); }
        else if (e.OriginalSource is not (TextBox or PasswordBox))
        {
            if (e.Key == Key.Delete) { e.Handled = true; Delete_Click(sender, e); }
            else if (e.Key == Key.F2) { e.Handled = true; Rename_Click(sender, e); }
            else if (e.Key == Key.Space) { e.Handled = true; await GuardAsync(PreviewSelectedAsync); }
            else if (e.Key == Key.System && e.SystemKey == Key.Up) { e.Handled = true; Up_Click(sender, e); }
        }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeHandling) { e.Cancel = true; return; }
        if (!closeApproved && OwnedWindows.OfType<PreviewWindow>().Any(p => p.HasUnsavedChanges))
        {
            e.Cancel = true; closeHandling = true;
            try
            {
                foreach (var preview in OwnedWindows.OfType<PreviewWindow>().ToArray()) if (!await preview.PrepareOwnerCloseAsync()) return;
                closeApproved = true;
            }
            finally { closeHandling = false; }
            _ = Dispatcher.BeginInvoke(new Action(Close)); return;
        }
        if (jobs.Any(j => j.IsActive) && !FormWindow.Ask(this, "仍有未完成任务。退出 Hzip 会取消这些任务，确认退出吗？")) { e.Cancel = true; closeApproved = false; return; }
        foreach (var preview in OwnedWindows.OfType<PreviewWindow>().ToArray()) preview.ApproveOwnerClose();
        shutdown.Cancel(); ownedPreview?.Dispose();
    }
    private sealed record RecentFile(string Path, string Name, string Directory, string Size);
}

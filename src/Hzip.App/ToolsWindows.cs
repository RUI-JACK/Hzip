using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Hzip.Core;

namespace Hzip.App;

public sealed class RepairWindow : FormWindow
{
    private readonly TextBox source = Input(), output = Input(); public string Source => source.Text; public string Output => output.Text;
    private readonly PasswordBox password = new(); private readonly ComboBox encoding = new(); private static readonly int[] CodePages = [0, 437, 936, 932, 950, 65001];
    public string? Password => string.IsNullOrEmpty(password.Password) ? null : password.Password;
    public int CodePage => CodePages[Math.Max(0, encoding.SelectedIndex)];
    public RepairWindow(string? initial = null, string? knownPassword = null) : base("修复 ZIP 归档", "综合中央目录、文件头和数据描述符，逐项验证后另存。原归档保持不变。")
    {
        System.Windows.Automation.AutomationProperties.SetAutomationId(source, "RepairSourcePath"); System.Windows.Automation.AutomationProperties.SetAutomationId(output, "RepairOutputPath");
        Label("受损 ZIP 文件"); Body.Children.Add(BrowseRow(source, () => { var picker = new OpenFileDialog { Title = "选择需要修复的 ZIP", Filter = "ZIP 文件|*.zip|所有文件|*.*" }; if (picker.ShowDialog(this) == true) { source.Text = picker.FileName; SetOutput(); } }));
        Label("修复结果另存为"); Body.Children.Add(BrowseRow(output, () => { var picker = new SaveFileDialog { Filter = "ZIP 文件|*.zip", FileName = System.IO.Path.GetFileName(output.Text) }; if (picker.ShowDialog(this) == true) output.Text = picker.FileName; }));
        Label("原归档密码（可选）"); password.Password = knownPassword ?? ""; System.Windows.Automation.AutomationProperties.SetAutomationId(password, "RepairPassword"); Body.Children.Add(password);
        Label("旧式文件名编码"); encoding.ItemsSource = new[] { "本机默认编码", "OEM 437", "简体中文 GBK", "日文 Shift-JIS", "繁体中文 Big5", "UTF-8" }; encoding.SelectedIndex = 0; Body.Children.Add(encoding);
        Note("支持更多 ZIP 算法、ZIP64 描述符和部分文件头损坏；有正确密码时可尝试加密条目。损坏或无法校验的内容会跳过，并记录到恢复清单。修复结果另存为未加密 ZIP，请妥善保存；数据丢失时不能保证恢复。");
        if (initial != null) { source.Text = initial; SetOutput(); }
        Accept.Content = "开始修复"; System.Windows.Automation.AutomationProperties.SetAutomationId(Accept, "StartRepairButton"); Accept.Click += (_, _) => { if (!File.Exists(source.Text)) { Error("请选择一个存在的受损 ZIP 文件。"); return; } if (!System.IO.Path.IsPathFullyQualified(output.Text) || File.Exists(output.Text) || File.Exists(output.Text + ".repair.json")) { Error("请为修复结果和恢复清单选择一个尚不存在的完整路径。"); return; } DialogResult = true; };
    }
    private void SetOutput() => output.Text = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source.Text)!, TextFormat.ArchiveStem(source.Text) + ".repair.zip");
}
public sealed class VaultWindow : FormWindow
{
    private readonly PasswordVault vault = new(); private readonly ListBox entries = new() { Height = 156, DisplayMemberPath = "Label" }; private readonly TextBox label = Input(); private readonly PasswordBox password = new();
    public VaultWindow() : base("密码管理", "密码由 Windows 当前账户加密保存在本机。", 530)
    {
        Body.Children.Add(entries); Reload();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) }; Button copy = new() { Content = "复制密码", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 9, 0) }, remove = new() { Content = "删除", Padding = new Thickness(12, 6, 12, 6) }; actions.Children.Add(copy); actions.Children.Add(remove); Body.Children.Add(actions);
        Label("新增密码 · 名称"); Body.Children.Add(label); Label("密码"); Body.Children.Add(password);
        Note("复制后 30 秒会清除仍未被替换的该条剪贴板内容。密码库只适用于当前 Windows 账户，不会随便携程序自动迁移。");
        copy.Click += (_, _) =>
        {
            try
            {
                if (entries.SelectedItem is not VaultEntry selected) return; string value = vault.Reveal(selected); Clipboard.SetText(value);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; timer.Tick += (_, _) => { timer.Stop(); try { if (Clipboard.ContainsText() && Clipboard.GetText() == value) Clipboard.Clear(); } catch { } }; timer.Start();
            }
            catch (Exception error) { Error(error.Message); }
        };
        remove.Click += (_, _) => { if (entries.SelectedItem is VaultEntry selected && Ask(this, "从密码库中删除“" + selected.Label + "”？")) { vault.Delete(selected.Id); Reload(); } };
        Accept.Content = "保存密码"; Accept.Click += (_, _) => { try { vault.Save(label.Text, password.Password); label.Clear(); password.Clear(); Reload(); } catch (Exception error) { Error(error.Message); } }; Cancel.Content = "关闭";
    }
    private void Reload() => entries.ItemsSource = vault.List();
}
public sealed class SettingsWindow : FormWindow
{
    public SettingsWindow(UserSettings settings) : base("设置与系统集成", "Hzip 的程序文件可直接复制使用；系统菜单由你选择启用。", 570)
    {
        Label("默认归档格式"); var format = new ComboBox { ItemsSource = CreateWindow.Formats, SelectedItem = CreateWindow.Formats.FirstOrDefault(x => x.Key == settings.DefaultFormat) ?? CreateWindow.Formats[0] }; Body.Children.Add(format);
        Label("默认文件夹"); var folder = Input(settings.DefaultFolder); Body.Children.Add(BrowseRow(folder, () => { string? path = Folder(this, folder.Text); if (path != null) folder.Text = path; }));
        Label("默认旧式文件名编码"); var pages = new[] { new { Id = 0, Name = "自动" }, new { Id = 936, Name = "简体中文 GBK" }, new { Id = 932, Name = "日文 Shift-JIS" }, new { Id = 950, Name = "繁体中文 Big5" }, new { Id = 437, Name = "OEM 437" }, new { Id = 65001, Name = "UTF-8" } };
        var code = new ComboBox { ItemsSource = pages, DisplayMemberPath = "Name", SelectedItem = pages.FirstOrDefault(x => x.Id == settings.CodePage) ?? pages[0] }; Body.Children.Add(code);
        var history = new CheckBox { Content = "记录最近打开的归档", IsChecked = settings.KeepHistory, Margin = new Thickness(0, 17, 0, 7) }; Body.Children.Add(history);
        Label("Windows 资源管理器"); var controls = new WrapPanel();
        void AddButton(string title, Action action) { var button = new Button { Content = title, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 7), FontSize = 11 }; button.Click += (_, _) => { try { action(); } catch (Exception error) { Error(error.Message); } }; controls.Children.Add(button); }
        AddButton("启用经典菜单", () => { ShellIntegration.Install(Environment.ProcessPath!); MessageBox.Show(this, "已注册经典右键菜单和“打开方式”。Windows 11 中位于“显示更多选项”。", "Hzip"); });
        AddButton("移除经典菜单", () => { ShellIntegration.Remove(); MessageBox.Show(this, "已移除 Hzip 的经典菜单与注册项。", "Hzip"); });
        AddButton("默认应用设置", () => Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true })); Body.Children.Add(controls);
        var modernControls = new WrapPanel();
        void ModernButton(string title, bool install)
        {
            var button = new Button { Content = title, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 7), FontSize = 11 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, install ? "InstallModernMenu" : "RemoveModernMenu");
            button.Click += async (_, _) =>
            {
                modernControls.IsEnabled = false;
                try { await ModernShellIntegration.ManageAsync(Environment.ProcessPath!, install); MessageBox.Show(this, install ? "已启用 Windows 11 新版 Hzip 菜单。已有文件夹窗口若未刷新，请关闭后重新打开。" : "已移除 Windows 11 新版 Hzip 菜单。", "Hzip"); }
                catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223) { Error("已取消 Windows 管理员确认，菜单设置未更改。"); }
                catch (Exception error) { Error(error.Message); }
                finally { modernControls.IsEnabled = true; }
            };
            modernControls.Children.Add(button);
        }
        ModernButton("启用 Windows 11 新菜单", true); ModernButton("移除 Windows 11 新菜单", false); Body.Children.Add(modernControls);
        Note("新版菜单直接显示 Hzip 子菜单，支持多选压缩和多选智能解压。此本地开发版需一次管理员确认，以信任 Hzip 菜单包的开发证书；移除时会清理本次添加的信任。普通压缩解压不需要管理员权限。");
        Note("启用菜单不会强制更改现有默认打开方式。程序移动到新位置后，可再次启用菜单以更新路径。");
        Label("关于 Hzip"); Note("Hzip 0.5 · Windows x64\n归档引擎：7-Zip 26.04（独立组件）\n许可证与对应源码随程序包附带。高级功能和格式差异详见程序包内的功能对照表。");
        var licenses = new Button { Content = "查看许可证与功能说明", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 11, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 8, 0, 0) }; licenses.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", AppContext.BaseDirectory) { UseShellExecute = true }); Body.Children.Add(licenses);
        Accept.Content = "保存设置"; Accept.Click += (_, _) => { if (!System.IO.Path.IsPathFullyQualified(folder.Text)) { Error("请选择完整的默认文件夹路径。"); return; } settings.DefaultFormat = ((FormatChoice)format.SelectedItem).Key; settings.DefaultFolder = folder.Text; settings.CodePage = pages[code.SelectedIndex].Id; settings.KeepHistory = history.IsChecked == true; if (!settings.KeepHistory) settings.RecentFiles.Clear(); settings.Save(); DialogResult = true; };
    }
}
public sealed class RecoveryWindow : FormWindow
{
    private readonly CancellationTokenSource cancellation = new(); private bool running, closed;
    public RecoveryWindow(ArchiveEngine engine, string archive) : base("密码恢复", System.IO.Path.GetFileName(archive), 580)
    {
        Label("恢复方式"); var mode = new ComboBox { ItemsSource = new[] { "数字范围", "字典文件 · 每行一个候选密码" }, SelectedIndex = 0 }; Body.Children.Add(mode);
        var numeric = new StackPanel(); numeric.Children.Add(new TextBlock { Text = "数字位数（保留前导零，1–12 位）", Style = (Style)Application.Current.FindResource("Label") }); var digits = Input("4"); numeric.Children.Add(digits);
        numeric.Children.Add(new TextBlock { Text = "起始数字", Style = (Style)Application.Current.FindResource("Label") }); var first = Input("0"); numeric.Children.Add(first);
        numeric.Children.Add(new TextBlock { Text = "结束数字", Style = (Style)Application.Current.FindResource("Label") }); var last = Input("9999"); numeric.Children.Add(last); Body.Children.Add(numeric);
        var dictionary = new StackPanel { Visibility = Visibility.Collapsed }; dictionary.Children.Add(new TextBlock { Text = "UTF-8 字典文件", Style = (Style)Application.Current.FindResource("Label") }); var file = Input(); dictionary.Children.Add(BrowseRow(file, () => { var picker = new OpenFileDialog { Filter = "文本字典|*.txt|所有文件|*.*" }; if (picker.ShowDialog(this) == true) file.Text = picker.FileName; })); Body.Children.Add(dictionary);
        mode.SelectionChanged += (_, _) => { numeric.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; dictionary.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; };
        Note("对本机归档逐个验证候选密码；字典长度不设四位限制。复杂密码可能耗时很长，无法保证找回。此版本未实现专用 GPU/算法加速。");
        var progress = new ProgressBar { Height = 5, Margin = new Thickness(0, 17, 0, 9), Minimum = 0, Maximum = 100, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Accent") }; var status = new TextBlock { Text = "准备就绪", TextWrapping = TextWrapping.Wrap, FontSize = 12 }; Body.Children.Add(progress); Body.Children.Add(status);
        var resultBox = Input(); resultBox.IsReadOnly = true; resultBox.Visibility = Visibility.Collapsed; resultBox.Margin = new Thickness(0, 12, 0, 0); Body.Children.Add(resultBox);
        var copy = new Button { Content = "复制找回的密码", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) }; copy.Click += (_, _) => Clipboard.SetText(resultBox.Text); Body.Children.Add(copy);
        Accept.Content = "开始恢复"; Cancel.Content = "关闭";
        Accept.Click += async (_, _) =>
        {
            if (running) return; IEnumerable<string> candidates; long total;
            if (mode.SelectedIndex == 0)
            {
                if (!int.TryParse(digits.Text, out int count) || count is < 1 or > 12 || !long.TryParse(first.Text, out long start) || !long.TryParse(last.Text, out long end) || start < 0 || end < start || end >= Math.Pow(10, count)) { Error("数字位数或范围无效。"); return; }
                candidates = PasswordRecovery.Numeric(count, start, end); total = end - start + 1;
            }
            else { if (!File.Exists(file.Text)) { Error("请选择一个 UTF-8 字典文件。"); return; } string dictionaryPath = file.Text; candidates = File.ReadLines(dictionaryPath, Encoding.UTF8); total = 0; }
            running = true; Accept.IsEnabled = false; mode.IsEnabled = numeric.IsEnabled = dictionary.IsEnabled = false; Cancel.Content = "停止并关闭";
            try
            {
                long lastUpdate = 0; var reporter = new Progress<RecoveryProgress>(p => { if (closed || Environment.TickCount64 - lastUpdate < 150) return; lastUpdate = Environment.TickCount64; progress.IsIndeterminate = p.Total <= 0; progress.Value = p.Total > 0 ? 100.0 * p.Attempts / p.Total : 0; status.Text = $"已尝试 {p.Attempts:N0} 个候选 · {p.PerSecond:0.0} 个/秒"; });
                var result = await Task.Run(() => PasswordRecovery.SearchAsync(engine, archive, candidates, total, 2, cancellation.Token, reporter));
                if (closed) return; progress.IsIndeterminate = false; progress.Value = result.Found ? 100 : progress.Value;
                status.Text = result.Found ? $"已找回密码 · 尝试 {result.Attempts:N0} 次 · 用时 {result.Elapsed.TotalSeconds:0.0} 秒" : $"本次候选中未找到密码 · 共尝试 {result.Attempts:N0} 次";
                if (result.Found) { resultBox.Text = result.Password; resultBox.Visibility = copy.Visibility = Visibility.Visible; }
            }
            catch (OperationCanceledException) { if (!closed) status.Text = "恢复已停止"; }
            catch (Exception error) { if (!closed) Error(error.Message); }
            finally { running = false; if (!closed) { Accept.IsEnabled = true; mode.IsEnabled = numeric.IsEnabled = dictionary.IsEnabled = true; Cancel.Content = "关闭"; } }
        };
        Closing += (_, _) => { closed = true; cancellation.Cancel(); };
    }
}

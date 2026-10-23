using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Hzip.Core;

namespace Hzip.App;

public class FormWindow : Window
{
    public StackPanel Body { get; } = new();
    public Button Accept { get; } = new() { Content = "确定", MinWidth = 96, IsDefault = true };
    public Button Cancel { get; } = new() { Content = "取消", MinWidth = 82, IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
    protected TextBlock ErrorLabel { get; } = new() { Foreground = new SolidColorBrush(Color.FromRgb(177, 58, 58)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    public FormWindow(string title, string subtitle, double width = 560)
    {
        Title = title + " · Hzip"; Width = width; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; MaxHeight = SystemParameters.WorkArea.Height - 60; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(27, 24, 27, 22), LastChildFill = true };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(subtitle)) header.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 21, 0, 0) };
        Accept.Style = (Style)Application.Current.FindResource("Primary"); footer.Children.Add(Cancel); footer.Children.Add(Accept); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var scroll = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Min(580, SystemParameters.WorkArea.Height - 235) }; root.Children.Add(scroll); Content = root;
        Cancel.Click += (_, _) => DialogResult = false;
    }
    public void Label(string text) => Body.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("Label") });
    public void Note(string text) => Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 11, Margin = new Thickness(0, 8, 0, 3) });
    protected void Error(string text) { ErrorLabel.Text = text; ErrorLabel.Visibility = Visibility.Visible; if (!Body.Children.Contains(ErrorLabel)) Body.Children.Add(ErrorLabel); }
    public static TextBox Input(string value = "") => new() { Text = value, MinHeight = 35 };
    public static FrameworkElement BrowseRow(TextBox input, Action browse)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(input); var button = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(13, 7, 13, 7) }; button.Click += (_, _) => browse(); Grid.SetColumn(button, 1); grid.Children.Add(button); return grid;
    }
    public static bool Ask(Window owner, string message, string title = "Hzip") => MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public static string? Folder(Window owner, string? initial = null)
    {
        var picker = new OpenFolderDialog { Title = "选择文件夹" }; if (Directory.Exists(initial)) picker.InitialDirectory = initial!;
        return picker.ShowDialog(owner) == true ? picker.FolderName : null;
    }
    public static string? Archive(Window owner, string? initialDirectory = null)
    {
        var picker = new OpenFileDialog { Title = "打开归档", Filter = "归档文件|*.zip;*.zipx;*.7z;*.rar;*.tar;*.gz;*.tgz;*.xz;*.bz2;*.iso;*.cab;*.lzh;*.wim;*.arj;*.dmg;*.001;*.exe|所有文件|*.*" };
        if (Directory.Exists(initialDirectory)) picker.InitialDirectory = initialDirectory!;
        return picker.ShowDialog(owner) == true ? picker.FileName : null;
    }
}
public sealed record FormatChoice(string Key, string Title, string Extension, string Description) { public override string ToString() => Title; }
public sealed class CreateWindow : FormWindow
{
    public static readonly FormatChoice[] Formats =
    [new("zip", "ZIP · 通用兼容", ".zip", "适合分享；支持 AES-256、中文密码、分卷与大文件。"), new("7z", "7Z · 更高压缩率", ".7z", "支持中文密码、文件名加密与固实压缩。"), new("zipx", "ZIPX · XZ 压缩", ".zipx", "使用 XZ 算法。此写入器不支持加密或分卷。"), new("exe", "EXE · 自解压归档", ".exe", "生成含解压模块的 7Z 自解压程序，接收方无需安装 Hzip。"), new("tar", "TAR · 文件打包", ".tar", "打包多个文件，保留目录结构，不压缩内容。"), new("tgz", "TAR.GZ · 跨平台", ".tar.gz", "先打包 TAR，再使用 GZIP 压缩。"), new("txz", "TAR.XZ · 紧凑打包", ".tar.xz", "先打包 TAR，再使用 XZ 压缩。"), new("tbz2", "TAR.BZ2", ".tar.bz2", "先打包 TAR，再使用 BZIP2 压缩。"), new("iso", "ISO · 光盘镜像", ".iso", "创建 ISO 9660 / Joliet 数据镜像。"), new("lzh", "LZH · 兼容打包", ".lzh", "使用 LH0 存储，不压缩内容；单文件小于 4 GB，路径编码为 GBK。"), new("gz", "GZIP · 单文件", ".gz", "压缩一个文件；多个文件请使用 TAR.GZ。"), new("xz", "XZ · 单文件", ".xz", "压缩一个文件；多个文件请使用 TAR.XZ。"), new("bz2", "BZIP2 · 单文件", ".bz2", "压缩一个文件；多个文件请使用 TAR.BZ2。"), new("wim", "WIM · Windows 映像", ".wim", "使用 WIM 容器打包文件与文件夹。")];
    private readonly ListBox files = new() { Height = 112, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox format = new() { ItemsSource = Formats, MinWidth = 210 };
    private readonly ComboBox level = new() { ItemsSource = new[] { "0 · 仅存储", "1 · 最快", "3 · 快速", "5 · 标准", "7 · 较高", "9 · 极限" } };
    private readonly TextBox output = Input(), volume = Input(), threads = Input("0");
    private readonly PasswordBox password = new(), confirm = new();
    private readonly CheckBox headers = new() { Content = "加密文件名（7Z）", IsChecked = true }, solid = new() { Content = "固实压缩（7Z）", IsChecked = true };
    private readonly CheckBox recycle = new() { Content = "压缩并校验成功后，将源文件放入回收站" };
    private readonly ComboBox zipEncryption = new() { ItemsSource = new[] { "AES-256 · 推荐", "传统 ZIP 加密 · 旧软件兼容" }, SelectedIndex = 0 };
    private readonly ComboBox zipPasswordEncoding = new() { ItemsSource = new[] { "UTF-8 · 跨平台", "本机编码 · 与旧版工具兼容" }, SelectedIndex = AppPaths.SystemCodePage == 65001 ? 0 : 1 };
    private readonly TextBlock formatNote = new() { Foreground = Brushes.SlateGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    public IReadOnlyList<string> Inputs => files.Items.Cast<string>().ToArray();
    public string OutputPath { get; private set; } = "";
    public CreateOptions Options { get; private set; } = new();
    public bool RecycleSources => recycle.IsChecked == true;
    private string priorExtension = ".zip";
    public CreateWindow(IEnumerable<string> inputs, UserSettings settings, string? initialFormat = null) : base("创建归档", "选择文件、归档格式与保存位置", 600)
    {
        Label("文件与文件夹"); Body.Children.Add(files); foreach (string input in inputs) files.Items.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        void Button(string title, Action action) { var button = new Button { Content = title, Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 8, 0), FontSize = 11 }; button.Click += (_, _) => action(); buttons.Children.Add(button); }
        Button("添加文件", () => { var picker = new OpenFileDialog { Multiselect = true, Title = "添加要压缩的文件" }; if (picker.ShowDialog(this) == true) foreach (string path in picker.FileNames) if (!files.Items.Contains(path)) files.Items.Add(path); });
        Button("添加文件夹", () => { string? path = Folder(this); if (path != null && !files.Items.Contains(path)) files.Items.Add(path); }); Button("移除", () => { if (files.SelectedItem != null) files.Items.Remove(files.SelectedItem); }); Body.Children.Add(buttons);
        Label("保存为"); output.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "CreateOutputPath");
        Body.Children.Add(BrowseRow(output, () => { var picker = new SaveFileDialog { FileName = System.IO.Path.GetFileName(output.Text), Filter = "归档文件|*" + ((FormatChoice?)format.SelectedItem)?.Extension + "|所有文件|*.*", OverwritePrompt = false }; if (Directory.Exists(System.IO.Path.GetDirectoryName(output.Text))) picker.InitialDirectory = System.IO.Path.GetDirectoryName(output.Text); if (picker.ShowDialog(this) == true) output.Text = picker.FileName; }));
        var optionsGrid = new Grid(); optionsGrid.ColumnDefinitions.Add(new ColumnDefinition()); optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); optionsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var f = new StackPanel(); f.Children.Add(new TextBlock { Text = "格式", Style = (Style)Application.Current.FindResource("Label") }); f.Children.Add(format); optionsGrid.Children.Add(f);
        var l = new StackPanel(); l.Children.Add(new TextBlock { Text = "压缩级别", Style = (Style)Application.Current.FindResource("Label") }); l.Children.Add(level); Grid.SetColumn(l, 2); optionsGrid.Children.Add(l); Body.Children.Add(optionsGrid); Body.Children.Add(formatNote);
        format.SelectionChanged += (_, _) => { if (format.SelectedItem is not FormatChoice choice) return; formatNote.Text = choice.Description; if (output.Text.EndsWith(priorExtension, StringComparison.OrdinalIgnoreCase)) output.Text = output.Text[..^priorExtension.Length] + choice.Extension; priorExtension = choice.Extension; headers.IsEnabled = solid.IsEnabled = choice.Key is "7z" or "exe"; };
        var passwordGrid = new Grid(); passwordGrid.ColumnDefinitions.Add(new ColumnDefinition()); passwordGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); passwordGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var p = new StackPanel(); p.Children.Add(new TextBlock { Text = "密码（可留空）", Style = (Style)Application.Current.FindResource("Label") }); p.Children.Add(password); passwordGrid.Children.Add(p);
        var c = new StackPanel(); c.Children.Add(new TextBlock { Text = "确认密码", Style = (Style)Application.Current.FindResource("Label") }); c.Children.Add(confirm); Grid.SetColumn(c, 2); passwordGrid.Children.Add(c); Body.Children.Add(passwordGrid);
        System.Windows.Automation.AutomationProperties.SetAutomationId(password, "CreatePassword");
        System.Windows.Automation.AutomationProperties.SetAutomationId(confirm, "CreatePasswordConfirm");
        System.Windows.Automation.AutomationProperties.SetAutomationId(format, "CreateFormat");
        var vault = new PasswordVault(); var savedEntries = vault.List();
        if (savedEntries.Count > 0)
        {
            Label("使用已保存的密码"); var saved = new ComboBox { ItemsSource = savedEntries, DisplayMemberPath = "Label" };
            System.Windows.Automation.AutomationProperties.SetAutomationId(saved, "CreateSavedPassword");
            saved.SelectionChanged += (_, _) => { try { if (saved.SelectedItem is VaultEntry entry) password.Password = confirm.Password = vault.Reveal(entry); } catch (Exception error) { saved.SelectedIndex = -1; Error(error.Message); } };
            Body.Children.Add(saved);
        }
        var advanced = new StackPanel(); advanced.Children.Add(headers); advanced.Children.Add(solid);
        advanced.Children.Add(new TextBlock { Text = "ZIP 加密方式", Style = (Style)Application.Current.FindResource("Label") }); advanced.Children.Add(zipEncryption);
        advanced.Children.Add(new TextBlock { Text = "ZIP 中文密码编码（英文密码不受影响）", Style = (Style)Application.Current.FindResource("Label") }); advanced.Children.Add(zipPasswordEncoding);
        advanced.Children.Add(new TextBlock { Text = "分卷大小（留空为不分卷，例如 100M、4G）", Style = (Style)Application.Current.FindResource("Label") }); advanced.Children.Add(volume);
        advanced.Children.Add(new TextBlock { Text = "压缩线程数（0 为自动）", Style = (Style)Application.Current.FindResource("Label") }); advanced.Children.Add(threads); advanced.Children.Add(recycle);
        Body.Children.Add(new Expander { Header = "高级选项", Content = advanced, Margin = new Thickness(0, 17, 0, 0), Padding = new Thickness(0, 6, 0, 0) });
        format.SelectedItem = Formats.FirstOrDefault(x => x.Key == (initialFormat ?? settings.DefaultFormat)) ?? Formats[0];
        level.SelectedIndex = settings.CompressionLevel switch { 0 => 0, 1 => 1, 3 => 2, 7 => 4, 9 => 5, _ => 3 }; threads.Text = settings.Threads.ToString();
        string name = Inputs.Count > 0 ? System.IO.Path.GetFileName(Inputs[0].TrimEnd('\\')) : "新建归档"; if (Inputs.Count == 1 && File.Exists(Inputs[0])) name = System.IO.Path.GetFileNameWithoutExtension(name);
        string parent = Inputs.Count > 0 ? System.IO.Path.GetDirectoryName(Inputs[0].TrimEnd('\\')) ?? settings.DefaultFolder : settings.DefaultFolder;
        output.Text = System.IO.Path.Combine(parent, name + ((FormatChoice)format.SelectedItem).Extension);
        Accept.Content = "开始压缩"; Accept.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "StartCreateButton");
        Accept.Click += (_, _) =>
        {
            if (Inputs.Count == 0) { Error("请先添加文件或文件夹。"); return; }
            if (string.IsNullOrWhiteSpace(output.Text) || !System.IO.Path.IsPathFullyQualified(output.Text)) { Error("请选择完整的归档保存路径。"); return; }
            if (password.Password != confirm.Password) { Error("两次输入的密码不一致。"); return; }
            if (!int.TryParse(threads.Text, out int count) || count < 0 || count > 256) { Error("线程数应为 0–256。"); return; }
            bool overwrite = File.Exists(output.Text) || File.Exists(output.Text + ".001"); if (overwrite && !Ask(this, "目标归档已经存在。确认替换它吗？")) return;
            string chosenFormat = ((FormatChoice)format.SelectedItem).Key;
            if (password.Password.Length > 0 && chosenFormat is not ("zip" or "7z" or "exe")) { Error("此格式不能在 Hzip 中设置密码，请使用 ZIP 或 7Z。"); return; }
            OutputPath = output.Text; Options = new CreateOptions { Format = chosenFormat, Level = new[] { 0, 1, 3, 5, 7, 9 }[level.SelectedIndex], Password = password.Password, EncryptHeaders = headers.IsChecked == true, Solid = solid.IsChecked == true, VolumeSize = volume.Text.Trim(), Threads = count, Overwrite = overwrite, ZipEncryption = zipEncryption.SelectedIndex == 0 ? "AES256" : "ZipCrypto", ZipPasswordCodePage = zipPasswordEncoding.SelectedIndex == 0 ? 65001 : AppPaths.SystemCodePage }; DialogResult = true;
        };
    }
}
public sealed class ExtractWindow : FormWindow
{
    private readonly TextBox target = Input(); private readonly PasswordBox password = new();
    private readonly ComboBox layout = new() { ItemsSource = new[] { "智能解压 · 多个顶层条目时创建同名文件夹", "始终解压到归档同名文件夹", "直接解压到目标目录" }, SelectedIndex = 0 };
    private readonly ComboBox overwrite = new() { ItemsSource = new[] { "遇到同名时询问", "自动重命名", "跳过已有文件", "覆盖已有文件", "遇到重名时停止" }, SelectedIndex = 0 };
    private readonly CheckBox selected = new(), reveal = new() { Content = "完成后打开目标文件夹", IsChecked = true };
    public string Destination { get; private set; } = ""; public ExtractOptions Options { get; private set; } = new(); public bool Reveal => reveal.IsChecked == true;
    public ExtractWindow(string archive, IReadOnlyList<string> selectedPaths, string? knownPassword, int codePage, bool selectionOnly = false, bool revealAfter = true, int archiveCount = 1) : base("解压归档", archiveCount > 1 ? $"已选择 {archiveCount} 个归档，设置将应用于全部归档" : System.IO.Path.GetFileName(archive))
    {
        reveal.IsChecked = revealAfter;
        System.Windows.Automation.AutomationProperties.SetAutomationId(overwrite, "ExtractOverwritePolicy");
        if (archiveCount > 1) layout.SelectedIndex = 1;
        target.Text = System.IO.Path.GetDirectoryName(archive) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); password.Password = knownPassword ?? "";
        Label("目标文件夹"); target.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "ExtractDestination"); Body.Children.Add(BrowseRow(target, () => { string? path = Folder(this, target.Text); if (path != null) target.Text = path; }));
        Label("目录组织"); Body.Children.Add(layout); Label("重名文件"); Body.Children.Add(overwrite); Label("归档密码（如有）"); Body.Children.Add(password);
        selected.Content = $"仅解压选中的 {selectedPaths.Count} 个条目"; selected.IsEnabled = selectedPaths.Count > 0; selected.IsChecked = selectionOnly && selectedPaths.Count > 0; selected.Margin = new Thickness(0, 17, 0, 2); Body.Children.Add(selected); Body.Children.Add(reveal);
        Accept.Content = "开始解压"; Accept.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "StartExtractButton"); Accept.Click += (_, _) =>
        {
            if (!System.IO.Path.IsPathFullyQualified(target.Text)) { Error("请选择完整的目标文件夹路径。"); return; }
            Destination = target.Text; Options = new ExtractOptions { Password = password.Password, Layout = layout.SelectedIndex switch { 1 => ExtractLayout.Subfolder, 2 => ExtractLayout.Here, _ => ExtractLayout.Smart }, Overwrite = overwrite.SelectedIndex switch { 1 => OverwritePolicy.Rename, 2 => OverwritePolicy.Skip, 3 => OverwritePolicy.Overwrite, 4 => OverwritePolicy.Fail, _ => OverwritePolicy.Ask }, SelectedPaths = selected.IsChecked == true ? selectedPaths : null, CodePage = codePage }; DialogResult = true;
        };
    }
}
public sealed class PasswordWindow : FormWindow
{
    private readonly PasswordBox password = new(); public string Password => password.Password;
    public PasswordWindow(string archive, string? explanation = null) : base("输入归档密码", System.IO.Path.GetFileName(archive) + (explanation == null ? "" : "\n" + explanation), 450)
    {
        System.Windows.Automation.AutomationProperties.SetAutomationId(password, "ArchivePassword");
        System.Windows.Automation.AutomationProperties.SetAutomationId(Accept, "SubmitArchivePassword");
        Label("密码"); Body.Children.Add(password); var vault = new PasswordVault(); var entries = vault.List();
        if (entries.Count > 0) { Label("使用已保存的密码"); var saved = new ComboBox { ItemsSource = entries, DisplayMemberPath = "Label" }; System.Windows.Automation.AutomationProperties.SetAutomationId(saved, "ChooseArchivePassword"); saved.SelectionChanged += (_, _) => { if (saved.SelectedItem is VaultEntry entry) password.Password = vault.Reveal(entry); }; Body.Children.Add(saved); }
        var remember = new CheckBox { Content = "将此密码保存到密码库", Margin = new Thickness(0, 17, 0, 0) }; Body.Children.Add(remember); Accept.Content = "打开归档";
        Accept.Click += (_, _) => { if (string.IsNullOrEmpty(password.Password)) { Error("请输入密码。"); return; } if (remember.IsChecked == true) vault.Save(System.IO.Path.GetFileName(archive), password.Password); DialogResult = true; };
        Loaded += (_, _) => password.Focus();
    }
}
public sealed class TextPrompt : FormWindow
{
    private readonly TextBox input; public string Value => input.Text;
    public TextPrompt(string title, string label, string initial = "", bool multiline = false, string accept = "确定") : base(title, label, multiline ? 620 : 460)
    {
        input = Input(initial); input.AcceptsReturn = multiline; input.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap; if (multiline) { input.Height = 240; input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
        Body.Children.Add(input); Accept.Content = accept; Accept.Click += (_, _) => DialogResult = true; Loaded += (_, _) => { input.Focus(); if (!multiline) input.SelectAll(); };
    }
}

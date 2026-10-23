using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hzip.Core;

namespace Hzip.App;

/// <summary>A standalone shell task window. It never creates the archive manager.</summary>
public sealed class ShellProgressWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly JobControl control;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock heading = new() { FontSize = 21, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock itemLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 15) };
    private readonly TextBlock currentLabel = new(), totalLabel = new(), elapsed = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ProgressBar current = new() { Height = 17, Maximum = 100 }, total = new() { Height = 17, Maximum = 100 };
    private readonly TextBlock file = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 14, 0, 12) };
    private readonly TextBox details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 72 };
    private readonly Button pause = new() { Content = "暂停", MinWidth = 80 }, cancel = new() { Content = "取消", MinWidth = 86 }, open = new() { Content = "打开目标文件夹", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly string operation;
    private string name = "", destination = "", lastFile = "";
    private int itemIndex, itemCount = 1, generation;
    private double itemPercent;
    private bool finished, closeAfterCancel, waiting;
    private string waitingTitle = "等待密码";

    public ShellProgressWindow(bool compress)
    {
        operation = compress ? "压缩" : "解压";
        control = new JobControl(cancellation.Token);
        Title = operation + "进度 · Hzip"; Width = 640; Height = 440; MinWidth = 520; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AutomationProperties.SetAutomationId(this, "ShellProgressWindow");
        void Id(DependencyObject value, string id) => AutomationProperties.SetAutomationId(value, id);
        Id(current, "ShellCurrentProgress"); Id(total, "ShellTotalProgress"); Id(currentLabel, "ShellCurrentPercent");
        Id(totalLabel, "ShellTotalPercent"); Id(file, "ShellProgressMessage"); Id(details, "ShellProgressResult");
        Id(heading, "ShellProgressState"); Id(pause, "ShellPauseButton"); Id(cancel, "ShellCancelCloseButton"); Id(open, "ShellOpenDestination");
        var accent = (Brush)Application.Current.FindResource("Accent"); current.Foreground = total.Foreground = accent;
        var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        void Row(UIElement element, int index) { Grid.SetRow(element, index); root.Children.Add(element); }
        Row(heading, 0); Row(itemLabel, 1);
        var labels = new Grid { Margin = new Thickness(0, 0, 0, 7) }; labels.Children.Add(currentLabel); labels.Children.Add(elapsed);
        Row(labels, 2); Row(current, 3); totalLabel.Margin = new Thickness(0, 15, 0, 7); Row(totalLabel, 4); Row(total, 5); Row(file, 6);
        Grid.SetRow(details, 7); Grid.SetRowSpan(details, 2); root.Children.Add(details);
        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        pause.Margin = new Thickness(0, 0, 9, 0); actions.Children.Add(pause); actions.Children.Add(cancel); DockPanel.SetDock(actions, Dock.Right); buttons.Children.Add(actions); buttons.Children.Add(open); Row(buttons, 9);
        Content = root;
        heading.Text = "正在" + operation; details.Text = "正在准备任务…";
        pause.Click += async (_, _) =>
        {
            try { if (control.IsPauseRequested) control.Resume(); else await control.PauseAsync(cancellation.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { details.Text = "暂停未成功：" + error.Message; }
        };
        cancel.Click += (_, _) => { if (finished) Close(); else Cancel(); };
        open.Click += (_, _) =>
        {
            if (!Directory.Exists(destination)) return;
            try { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add(destination); Process.Start(start); }
            catch (Exception error) { details.Text = "打开目录失败：" + error.Message; }
        };
        control.StateChanged += () => Dispatcher.InvokeAsync(UpdateState);
        timer.Tick += (_, _) => elapsed.Text = "已用时 " + clock.Elapsed.ToString(@"hh\:mm\:ss"); timer.Start();
        Closing += (_, e) => { if (!finished) { e.Cancel = true; closeAfterCancel = true; Cancel(); } };
        Closed += (_, _) => { timer.Stop(); control.Dispose(); cancellation.Dispose(); closed.TrySetResult(); };
    }

    public Task WaitClosedAsync() => closed.Task;

    public void BeginItem(string path, int index, int count)
    {
        generation++; name = Path.GetFileName(path); lastFile = ""; itemIndex = index; itemCount = Math.Max(1, count); itemPercent = 0;
        itemLabel.Text = path; itemLabel.ToolTip = path; current.IsIndeterminate = true; current.Value = 0;
        currentLabel.Text = "当前归档 · 正在准备"; total.Value = 100.0 * itemIndex / itemCount;
        totalLabel.Text = $"总进度 {Math.Floor(total.Value):0}% · 归档 {itemIndex + 1}/{itemCount}";
        file.Text = "正在读取归档信息…"; details.Text = "正在" + operation + "，可以暂停或取消。";
        Title = "正在" + operation + " " + name + " · Hzip";
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, IProgress<JobProgress>, Task<T>> action)
    {
        int taskGeneration = generation;
        var reporter = new Progress<JobProgress>(value => { if (!finished && generation == taskGeneration) Report(value); });
        return await Task.Run(async () => { using var scope = control.Enter(); return await action(cancellation.Token, reporter); }, cancellation.Token);
    }

    public void WaitingForPassword(bool value)
        => WaitingForInput(value, "等待密码", "等待输入归档密码…");

    public void WaitingForConflict(bool value)
        => WaitingForInput(value, "等待选择", "等待处理同名文件…");

    private void WaitingForInput(bool value, string title, string message)
    {
        waiting = value; waitingTitle = title;
        if (value) { control.Resume(); file.Text = message; }
        UpdateState();
    }

    private void Report(JobProgress value)
    {
        if (cancellation.IsCancellationRequested || control.IsPauseRequested) return;
        file.Text = value.Stage + " · " + value.Message;
        if (value.Stage == "写入") lastFile = value.Message;
        current.IsIndeterminate = !value.Percent.HasValue;
        if (!value.Percent.HasValue) { currentLabel.Text = "当前归档 · " + value.Stage; return; }
        double raw = Math.Clamp(value.Percent.Value, 0, 100);
        current.Value = raw; currentLabel.Text = $"当前阶段 {raw:0}%";
        // Engine percentages reset between phases. Never label the whole task 100%
        // until Create/Extract has committed its outputs and returned successfully.
        double mapped = value.Stage == "写入" ? raw : raw * .9;
        itemPercent = Math.Max(itemPercent, Math.Min(99, mapped));
        total.Value = 100.0 * (itemIndex + itemPercent / 100) / itemCount;
        totalLabel.Text = $"总进度 {Math.Floor(total.Value):0}% · 归档 {itemIndex + 1}/{itemCount}";
        Title = $"{Math.Floor(total.Value):0}% 正在{operation} {name} · Hzip";
    }

    private void Cancel()
    {
        if (finished || cancellation.IsCancellationRequested) return;
        cancel.IsEnabled = pause.IsEnabled = false; cancellation.Cancel(); control.Resume();
        heading.Text = "正在取消"; file.Text = "正在停止处理并恢复未完成的写入，请稍候…";
        Title = "正在取消 " + name + " · Hzip";
    }

    private void UpdateState()
    {
        if (finished || cancellation.IsCancellationRequested) return;
        pause.IsEnabled = !waiting;
        pause.Content = control.IsPauseRequested ? "继续" : "暂停";
        if (control.IsPaused) current.IsIndeterminate = false;
        heading.Text = waiting ? waitingTitle : control.IsPaused ? "已暂停" : control.IsPauseRequested ? "正在暂停" : "正在" + operation;
        Title = $"{Math.Floor(total.Value):0}% {heading.Text} {name} · Hzip";
        if (control.IsPauseRequested) file.Text = control.IsPaused ? "任务已暂停，点击继续即可恢复。" : "等待当前步骤到达暂停点…";
    }

    public void Finish(string status, string result, string? outputDirectory = null)
    {
        if (finished) return;
        finished = true; generation++; control.Complete(); clock.Stop(); timer.Stop();
        elapsed.Text = "已用时 " + clock.Elapsed.ToString(@"hh\:mm\:ss");
        current.IsIndeterminate = false;
        heading.Text = status == "completed" ? operation + "已完成" : status == "cancelled" ? "任务已取消" : operation + "未完成";
        if (status == "completed") { current.Value = total.Value = 100; currentLabel.Text = "当前阶段 100%"; totalLabel.Text = $"总进度 100% · 已完成 {itemCount}/{itemCount}"; }
        Title = (status == "completed" ? "100% " : "") + heading.Text + " " + name + " · Hzip";
        details.Text = result; pause.IsEnabled = false; cancel.IsEnabled = true; cancel.Content = "关闭";
        if (status == "completed" && lastFile.Length > 0) file.Text = lastFile;
        destination = outputDirectory ?? ""; open.IsEnabled = Directory.Exists(destination);
        if (closeAfterCancel) Close();
    }
}

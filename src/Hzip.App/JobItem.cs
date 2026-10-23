using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Hzip.Core;

namespace Hzip.App;
public sealed class JobItem(string title, CancellationTokenSource cancellation) : INotifyPropertyChanged
{
    private string state = "等待中", message = "已加入队列", details = ""; private double? percent;
    public string Title { get; } = title;
    public CancellationTokenSource Cancellation { get; } = cancellation;
    public JobControl Control { get; } = new(cancellation.Token);
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public string Time => Started.ToString("HH:mm:ss");
    public string State { get => state; set { state = value; Changed(); Changed(nameof(IsActive)); } }
    public string Message { get => message; set { message = value; Changed(); } }
    public string Details { get => details; set { details = value; Changed(); } }
    public double? Percent { get => percent; set { percent = value; Changed(); Changed(nameof(PercentText)); } }
    public string PercentText => Percent.HasValue ? Percent.Value.ToString("0") + "%" : "—";
    public bool IsActive => State is "等待中" or "处理中" or "正在暂停" or "已暂停";
    public async Task TogglePauseAsync()
    {
        if (!IsActive || State == "等待中") return;
        try { if (Control.IsPauseRequested) Control.Resume(); else await Control.PauseAsync(Cancellation.Token); }
        catch (OperationCanceledException) { }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed class JobsWindow : Window
{
    public JobsWindow(ObservableCollection<JobItem> jobs)
    {
        Title = "任务记录 · Hzip"; Width = 780; Height = 480; MinWidth = 620; MinHeight = 360;
        var root = new Grid { Margin = new Thickness(22) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(94) });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) }; header.Children.Add(new TextBlock { Text = "任务记录", FontSize = 21, FontWeight = FontWeights.SemiBold });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var pause = new Button { Content = "暂停 / 继续", Margin = new Thickness(0, 0, 9, 0) }; System.Windows.Automation.AutomationProperties.SetAutomationId(pause, "PauseSelectedJob"); actions.Children.Add(pause);
        var cancel = new Button { Content = "取消选中任务" }; actions.Children.Add(cancel); DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions); root.Children.Add(header);
        var grid = new DataGrid { ItemsSource = jobs, SelectionMode = DataGridSelectionMode.Single }; grid.Columns.Add(new DataGridTextColumn { Header = "任务", Binding = new Binding("Title"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding("State"), Width = 90 }); grid.Columns.Add(new DataGridTextColumn { Header = "进度", Binding = new Binding("PercentText"), Width = 85 }); grid.Columns.Add(new DataGridTextColumn { Header = "开始时间", Binding = new Binding("Time"), Width = 100 }); Grid.SetRow(grid, 1); root.Children.Add(grid);
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 13, 0, 0) }; Grid.SetRow(details, 2); root.Children.Add(details);
        grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is JobItem job) details.Text = string.IsNullOrEmpty(job.Details) ? job.Message : job.Details; };
        cancel.Click += (_, _) => { if (grid.SelectedItem is JobItem { IsActive: true } job) job.Cancellation.Cancel(); };
        pause.Click += async (_, _) => { if (grid.SelectedItem is JobItem job) { try { await job.TogglePauseAsync(); } catch (Exception error) { MessageBox.Show(this, error.Message, "Hzip"); } } };
        Content = root;
    }
}

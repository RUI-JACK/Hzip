using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hzip.Core;

namespace Hzip.App;

public sealed class ExtractionConflictWindow : Window
{
    public ConflictResolution Resolution { get; private set; } = new(ConflictChoice.Cancel);
    public ExtractionConflictWindow(ExtractionConflict conflict)
    {
        Title = "同名文件处理 · Hzip"; Width = Math.Min(660, SystemParameters.WorkArea.Width - 32);
        SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "ExtractionConflictWindow");
        var root = new DockPanel { Margin = new Thickness(25, 21, 25, 21) }; Content = root;
        var footer = new StackPanel { Margin = new Thickness(0, 19, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new Grid();
        for (int i = 0; i < 3; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 2; i++) buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Action(string label, string id, ConflictChoice choice, bool all, int row, int column)
        {
            var button = new Button { Content = new AccessText { Text = label }, Margin = new Thickness(column == 0 ? 0 : 8, row == 0 ? 0 : 8, 0, 0), MinHeight = 39, Padding = new Thickness(9, 8, 9, 8) };
            AutomationProperties.SetAutomationId(button, id);
            if (choice == ConflictChoice.Replace) button.IsEnabled = conflict.CanReplace;
            if (choice == ConflictChoice.Skip && !all) button.IsDefault = true;
            button.Click += (_, _) => { Resolution = new(choice, all); DialogResult = true; };
            Grid.SetRow(button, row); Grid.SetColumn(button, column); buttons.Children.Add(button);
        }
        Action("替换(_Y)", "ConflictReplace", ConflictChoice.Replace, false, 0, 0);
        Action("跳过(_N)", "ConflictSkip", ConflictChoice.Skip, false, 0, 1);
        Action("重命名(_R)", "ConflictRename", ConflictChoice.Rename, false, 0, 2);
        Action("全部替换(_A)", "ConflictReplaceAll", ConflictChoice.Replace, true, 1, 0);
        Action("全部跳过(_L)", "ConflictSkipAll", ConflictChoice.Skip, true, 1, 1);
        Action("全部重命名(_E)", "ConflictRenameAll", ConflictChoice.Rename, true, 1, 2);
        footer.Children.Add(buttons);
        var cancel = new Button { Content = new AccessText { Text = "取消(_C)" }, IsCancel = true, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 11, 0, 0) };
        AutomationProperties.SetAutomationId(cancel, "ConflictCancel"); cancel.Click += (_, _) => DialogResult = false; footer.Children.Add(cancel);
        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height - 270) });
        body.Children.Add(new TextBlock { Text = "目标位置已有同名内容", FontSize = 22, FontWeight = FontWeights.SemiBold });
        var path = new TextBlock { Text = conflict.DestinationPath, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18), Foreground = Brushes.SlateGray, ToolTip = conflict.DestinationPath };
        AutomationProperties.SetAutomationId(path, "ConflictDestinationPath"); body.Children.Add(path);
        void Card(string title, string detail, long? size, DateTime modified, bool directory, string id)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            content.Children.Add(new TextBlock { Text = detail, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = detail, Foreground = Brushes.SlateGray, FontSize = 12, Margin = new Thickness(0, 5, 0, 10) });
            var metadata = new TextBlock { Text = (directory ? "文件夹" : $"{size:N0} 字节（{TextFormat.Bytes(size ?? 0)}）") + "\n修改时间：" + modified.ToString("yyyy-MM-dd HH:mm:ss"), TextWrapping = TextWrapping.Wrap, LineHeight = 23 };
            AutomationProperties.SetAutomationId(metadata, id); content.Children.Add(metadata);
            body.Children.Add(new Border { Child = content, Padding = new Thickness(16, 13, 16, 13), Background = Brushes.White, BorderBrush = (Brush)Application.Current.FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 0, 10) });
        }
        Card("解压出来的内容", System.IO.Path.GetFileName(conflict.ArchivePath) + " → " + conflict.RelativePath, conflict.IncomingSize, conflict.IncomingModified, conflict.IncomingIsDirectory, "ConflictIncomingMetadata");
        Card("已经存在的内容", conflict.DestinationPath, conflict.ExistingSize, conflict.ExistingModified, conflict.ExistingIsDirectory, "ConflictExistingMetadata");
        string note = !conflict.CanReplace ? "文件和文件夹不能直接相互替换，请跳过或重命名。" : conflict.IncomingSize == conflict.ExistingSize && conflict.IncomingModified == conflict.ExistingModified ? "大小和修改时间相同，不代表文件内容相同。" : "替换会使用解压出来的文件；重命名会自动添加编号，保留已有内容。";
        body.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 12 });
    }
}

// One instance per user operation, so "all" also spans a batch of archives.
public sealed class ExtractionConflictSession(Window owner, Action<bool>? waiting = null)
{
    private ConflictResolution? remembered;
    public async Task<ConflictResolution> ResolveAsync(ExtractionConflict conflict, CancellationToken ct)
    {
        return await owner.Dispatcher.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (remembered is { } choice && (choice.Choice != ConflictChoice.Replace || conflict.CanReplace)) return choice;
            waiting?.Invoke(true);
            try
            {
                var dialog = new ExtractionConflictWindow(conflict) { Owner = owner };
                using var registration = ct.Register(() => owner.Dispatcher.BeginInvoke(new Action(() => { if (dialog.IsVisible) dialog.Close(); })));
                dialog.ShowDialog(); ct.ThrowIfCancellationRequested();
                if (dialog.Resolution.ApplyToAll && dialog.Resolution.Choice != ConflictChoice.Cancel) remembered = dialog.Resolution;
                return dialog.Resolution;
            }
            finally { waiting?.Invoke(false); }
        }, DispatcherPriority.Normal, ct).Task;
    }
}

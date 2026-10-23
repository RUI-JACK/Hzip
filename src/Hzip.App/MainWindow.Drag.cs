using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hzip.Core;

namespace Hzip.App;

public partial class MainWindow
{
    private Point? dragOrigin;
    private bool preparingDrag;
    private readonly string dragIdentity = Guid.NewGuid().ToString("N");
    private bool IsOwnDrag(IDataObject data) => data.GetDataPresent("Hzip.ArchiveDragSource") && data.GetData("Hzip.ArchiveDragSource") as string == dragIdentity;
    private void EntriesGrid_DragStart(object sender, MouseButtonEventArgs e)
    {
        dragOrigin = null;
        DependencyObject? cursor = e.OriginalSource as DependencyObject;
        while (cursor != null && cursor is not DataGridRow)
            cursor = cursor is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cursor) : LogicalTreeHelper.GetParent(cursor);
        if (cursor is DataGridRow && e.ClickCount == 1) dragOrigin = e.GetPosition(EntriesGrid);
    }
    private async void EntriesGrid_DragMove(object sender, MouseEventArgs e)
    {
        if (preparingDrag || dragOrigin == null || archive == null || e.LeftButton != MouseButtonState.Pressed) return;
        Point point = e.GetPosition(EntriesGrid), start = dragOrigin.Value;
        if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragOrigin = null; var selection = Selection(); if (selection.Count == 0) return;
        string source = archive.FilePath; int page = codePage; preparingDrag = true;
        await GuardAsync(async () =>
        {
            try
            {
                string? key = GetPassword();
                using var export = await EnqueueAsync("准备拖出所选条目", (ct, progress) => ArchiveExportLease.CreateAsync(service, source, selection, key, page, ct, progress));
                if (Mouse.LeftButton != MouseButtonState.Pressed) { StatusLabel.Text = "已取消拖出；请按住鼠标等待条目准备完成。"; return; }
                var data = new DataObject(); var paths = new StringCollection(); paths.AddRange(export.Paths.ToArray()); data.SetFileDropList(paths);
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
                data.SetData("Hzip.ArchiveDragSource", dragIdentity);
                // Keep the source valid even if the Shell performs an asynchronous copy or cancels late.
                export.RetainForShellCopy();
                DragDropEffects result = DragDrop.DoDragDrop(EntriesGrid, data, DragDropEffects.Copy);
                StatusLabel.Text = result == DragDropEffects.None ? "拖出已取消，原归档保持不变" : "所选条目已交给目标文件夹复制";
            }
            finally { preparingDrag = false; }
        });
    }
}

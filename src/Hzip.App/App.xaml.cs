using System.Windows;
using System.Windows.Threading;
using System.Text;

namespace Hzip.App;
public partial class App : Application
{
    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _ = Task.Run(() => { try { Hzip.Core.ArchiveExportLease.CleanupExpired(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } });
        if (ShellTaskRunner.Handles(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exitCode = await ShellTaskRunner.RunAsync(e.Args);
            Shutdown(exitCode);
            return;
        }
        var window = new MainWindow(e.Args); MainWindow = window; window.Show();
    }
    private void Application_Error(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { Directory.CreateDirectory(Hzip.Core.AppPaths.DataRoot); File.AppendAllText(System.IO.Path.Combine(Hzip.Core.AppPaths.DataRoot, "errors.log"), DateTimeOffset.Now + " " + e.Exception + Environment.NewLine); } catch { }
        MessageBox.Show("操作遇到了问题，请在任务记录中检查结果。\n\n" + e.Exception.Message, "Hzip", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true;
    }
}

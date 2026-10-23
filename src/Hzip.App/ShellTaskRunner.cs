using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Hzip.Core;

namespace Hzip.App;

/// <summary>Explorer tasks have their own lifetime and never construct MainWindow.</summary>
internal static class ShellTaskRunner
{
    public static bool Handles(IReadOnlyList<string> args) => args.Count > 0 && args[0] is
        "--quick-zip" or "--quick-7z" or "--create" or "--extract-here" or
        "--extract-many-here" or "--extract-direct" or "--extract-subfolder" or "--extract-to";

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var completed = new List<object>();
        string command = args[0], status = "running";
        string receipt = Path.Combine(AppPaths.DataRoot, "ShellJobs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        string? errorMessage = null;
        ShellProgressWindow? progressWindow = null;
        string? outputDirectory = null;
        var outputPaths = new List<string>();
        var warnings = new List<string>();
        ExtractionConflictSession? conflicts = null;
        ShellProgressWindow Begin(string path, int index = 0, int count = 1)
        {
            progressWindow ??= new ShellProgressWindow(command is "--quick-zip" or "--quick-7z" or "--create");
            progressWindow.BeginItem(path, index, count);
            if (!progressWindow.IsVisible) progressWindow.Show();
            return progressWindow;
        }
        void Record()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
                string temporary = receipt + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new { Command = command, Status = status, ProcessId = Environment.ProcessId, Completed = completed, Error = errorMessage }, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, receipt, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Debug.WriteLine(error); }
        }
        Record();
        try
        {
            var service = new ArchiveService();
            var settings = UserSettings.Load();
            string[] inputs = args.Skip(1).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (inputs.Length == 0) throw new ArgumentException("没有选择要处理的文件或文件夹。");
            foreach (string input in inputs)
                if (!File.Exists(input) && !Directory.Exists(input)) throw new FileNotFoundException("所选文件或文件夹不存在。", input);

            if (command is "--quick-zip" or "--quick-7z")
            {
                var plan = QuickArchivePlan.Build(inputs, command == "--quick-zip" ? "zip" : "7z");
                var options = new CreateOptions { Format = plan.Format, Level = settings.CompressionLevel, Threads = settings.Threads };
                var progress = Begin(plan.OutputPath);
                var outputs = await progress.ExecuteAsync((ct, p) => service.CreateAsync(plan.Inputs, plan.OutputPath, options, ct, p));
                completed.Add(new { Inputs = plan.Inputs, Outputs = outputs });
                outputPaths.AddRange(outputs); outputDirectory = Path.GetDirectoryName(outputs[0]);
            }
            else if (command == "--create")
            {
                var dialog = new CreateWindow(inputs, settings) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
                if (dialog.ShowDialog() != true) throw new OperationCanceledException();
                var chosenInputs = dialog.Inputs;
                var options = dialog.Options;
                var progress = Begin(dialog.OutputPath);
                var outputs = await progress.ExecuteAsync((ct, p) => service.CreateAsync(chosenInputs, dialog.OutputPath, options, ct, p));
                completed.Add(new { Inputs = chosenInputs, Outputs = outputs });
                outputPaths.AddRange(outputs); outputDirectory = Path.GetDirectoryName(outputs[0]);
                Record();
                if (dialog.RecycleSources)
                {
                    await progress.ExecuteAsync(async (ct, p) =>
                    {
                        await service.Engine.TestAsync(outputs[0], options.Password, ct, p);
                        foreach (string input in chosenInputs)
                        {
                            await JobControl.CheckpointAsync(ct);
                            if (Directory.Exists(input)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(input, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                            else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(input, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        }
                        return true;
                    });
                }
            }
            else
            {
                if (inputs.Any(Directory.Exists)) throw new ArgumentException("请选择压缩文件进行解压。");
                ExtractWindow? dialog = null;
                if (command == "--extract-to")
                {
                    dialog = new ExtractWindow(inputs[0], [], null, settings.CodePage, revealAfter: false, archiveCount: inputs.Length)
                    { WindowStartupLocation = WindowStartupLocation.CenterScreen };
                    if (dialog.ShowDialog() != true) throw new OperationCanceledException();
                }
                var layout = command == "--extract-direct" ? ExtractLayout.Here : command == "--extract-subfolder" ? ExtractLayout.Subfolder : ExtractLayout.Smart;
                for (int index = 0; index < inputs.Length; index++)
                {
                    string archive = inputs[index];
                    var progress = Begin(archive, index, inputs.Length);
                    string destination = dialog?.Destination ?? Path.GetDirectoryName(archive)!;
                    var options = dialog?.Options ?? new ExtractOptions { Layout = layout, CodePage = settings.CodePage, Overwrite = OverwritePolicy.Ask };
                    conflicts ??= new ExtractionConflictSession(progress, progress.WaitingForConflict);
                    var result = await ExtractAsync(service, archive, destination, options, progress, conflicts.ResolveAsync);
                    completed.Add(new { Input = archive, result.Destination, result.Files, result.Warnings });
                    outputPaths.Add(result.Destination); outputDirectory = result.Destination; warnings.AddRange(result.Warnings);
                    Record();
                    if (dialog?.Reveal == true)
                    {
                        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                        start.ArgumentList.Add(result.Destination); Process.Start(start);
                    }
                }
            }
            status = "completed"; Record();
            if (progressWindow != null)
            {
                string result = "操作已成功完成。\n\n输出位置：\n" + string.Join('\n', outputPaths);
                if (warnings.Count > 0) result += "\n\n提示：\n" + string.Join('\n', warnings);
                progressWindow.Finish(status, result, outputDirectory);
                await progressWindow.WaitClosedAsync();
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            status = "cancelled"; Record();
            if (progressWindow != null)
            {
                progressWindow.Finish(status, "任务已取消。" + (completed.Count > 0 ? $"\n此前完成的 {completed.Count} 项结果已保留。" : "\n未完成的文件不会作为成功结果提交。"), outputDirectory);
                await progressWindow.WaitClosedAsync();
            }
            return 1;
        }
        catch (Exception error)
        {
            status = "failed"; errorMessage = error.Message; Record();
            if (progressWindow != null)
            {
                progressWindow.Finish(status, error.Message + (completed.Count > 0 ? $"\n此前完成的 {completed.Count} 项结果已保留。" : ""), outputDirectory);
                await progressWindow.WaitClosedAsync();
                return 2;
            }
            var dialog = new FormWindow("右键任务未完成", error.Message, 580) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
            AutomationProperties.SetAutomationId(dialog, "ShellTaskError");
            if (completed.Count > 0) dialog.Note($"已有 {completed.Count} 项完成，其结果保留在原位置。");
            if (error is FileNotFoundException missing && !string.IsNullOrEmpty(missing.FileName)) dialog.Note(missing.FileName);
            dialog.Cancel.Visibility = Visibility.Collapsed;
            dialog.Accept.Content = "知道了";
            AutomationProperties.SetAutomationId(dialog.Accept, "DismissShellError");
            dialog.Accept.Click += (_, _) => dialog.DialogResult = true;
            dialog.ShowDialog(); return 2;
        }
    }

    private static async Task<ExtractResult> ExtractAsync(ArchiveService service, string archive, string destination, ExtractOptions options, ShellProgressWindow progress, ConflictResolver resolveConflict)
    {
        string? password = options.Password;
        while (true)
        {
            try
            {
                var info = await progress.ExecuteAsync((ct, _) => service.ListAsync(archive, password, ct, options.CodePage));
                if (info.HasEncryption && string.IsNullOrEmpty(password)) password = RequestPassword(archive, false, progress);
                return await progress.ExecuteAsync((ct, p) => service.ExtractAsync(archive, destination, options with { Password = password }, ct, p, resolveConflict));
            }
            catch (ArchiveOperationException error) when (error.NeedsPassword)
            {
                password = RequestPassword(archive, !string.IsNullOrEmpty(password), progress);
            }
        }
    }

    private static string RequestPassword(string archive, bool retry, ShellProgressWindow progress)
    {
        progress.WaitingForPassword(true);
        try
        {
        var prompt = new PasswordWindow(archive, retry ? "密码不正确，请重新输入。" : null)
        { Owner = progress, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (prompt.ShowDialog() != true) throw new OperationCanceledException();
        return prompt.Password;
        }
        finally { progress.WaitingForPassword(false); }
    }
}

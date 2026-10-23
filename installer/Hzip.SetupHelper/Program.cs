using System.Text.Json;
using Hzip.Core;
using Microsoft.Win32;

string root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
string application = Path.Combine(root, "Hzip.exe");
try
{
    if (args.Length != 1) throw new ArgumentException("安装组件参数无效。");
    switch (args[0])
    {
        case "classic": ShellIntegration.Install(application); break;
        case "modern": await ModernShellIntegration.ManageAsync(application, true); break;
        case "cleanup":
            // Another portable/installed copy may have taken ownership since
            // installation. Only remove integration still targeting this copy.
            string receipt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hzip", "modern-menu.json");
            if (File.Exists(receipt))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
                if (document.RootElement.TryGetProperty("Root", out var registeredRoot) &&
                    string.Equals(Path.GetFullPath(registeredRoot.GetString()!).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                    await ModernShellIntegration.ManageAsync(application, false);
            }
            CleanupClassic(); break;
        default: throw new ArgumentException("未知的安装组件操作。");
    }
    return 0;
}
catch (Exception error)
{
    string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hzip", "InstallerLogs");
    Directory.CreateDirectory(logs); await File.AppendAllTextAsync(Path.Combine(logs, "integration.log"), $"{DateTimeOffset.Now:o} {args.FirstOrDefault()} {root}\n{error}\n");
    Console.Error.WriteLine(error.Message); return 2;
}

void CleanupClassic()
{
    const string classes = @"Software\Classes\";
    bool Same(object? value) => string.Equals(value?.ToString(), application, StringComparison.OrdinalIgnoreCase);
    void RemoveVerb(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(classes + path);
        if (key?.GetValue("HzipOwner")?.ToString() == "Hzip.Desktop.v1" && Same(key.GetValue("Icon")))
            Registry.CurrentUser.DeleteSubKeyTree(classes + path, false);
    }
    foreach (string kind in new[] { "*", "Directory" })
        foreach (string verb in new[] { "Hzip.Compress", "Hzip.QuickZip", "Hzip.Quick7z" }) RemoveVerb(kind + @"\shell\" + verb);
    RemoveVerb(@"Directory\shell\Hzip.Open");
    foreach (string extension in ShellIntegration.Extensions)
        foreach (string verb in new[] { "Hzip.Open", "Hzip.Extract", "Hzip.ExtractDirect", "Hzip.ExtractSubfolder", "Hzip.ExtractTo" })
            RemoveVerb(@"SystemFileAssociations\" + extension + @"\shell\" + verb);
    using var icon = Registry.CurrentUser.OpenSubKey(classes + @"Hzip.Archive\DefaultIcon");
    if (Same(icon?.GetValue("")))
    {
        foreach (string extension in ShellIntegration.Extensions)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey(classes + extension + @"\OpenWithProgids", true);
            openWith?.DeleteValue("Hzip.Archive", false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(classes + "Hzip.Archive", false);
    }
    using var capabilities = Registry.CurrentUser.OpenSubKey(@"Software\Hzip\Capabilities");
    if (Same(capabilities?.GetValue("ApplicationIcon")))
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Hzip\Capabilities", false);
        using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true);
        if (registered?.GetValue("Hzip")?.ToString() == @"Software\Hzip\Capabilities") registered.DeleteValue("Hzip", false);
    }
}

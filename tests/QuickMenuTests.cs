using Hzip.Core;

internal static class QuickMenuTests
{
    public static async Task RunAsync(string root, ArchiveService service, Func<string, Func<Task>, Task> test)
    {
        string parent = Path.Combine(root, "quick-menu"); Directory.CreateDirectory(parent);
        string folder = Path.Combine(parent, "Clash Verge"), file = Path.Combine(parent, "说明 & [notes].txt"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "配置.txt"), "native menu source 中文"); File.WriteAllText(file, "separate file & literal path");
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        await test("Quick menu file folder and multi-selection names", () =>
        {
            Check(Path.GetFileName(QuickArchivePlan.Build([folder], "zip").OutputPath) == "Clash Verge.zip", "Folder naming");
            Check(Path.GetFileName(QuickArchivePlan.Build([folder], "7z").OutputPath) == "Clash Verge.7z", "7z format naming");
            Check(Path.GetFileName(QuickArchivePlan.Build([file], "zip").OutputPath) == "说明 & [notes].zip", "File extension naming");
            Check(Path.GetFileName(QuickArchivePlan.Build([file, folder], "zip").OutputPath) == "quick-menu.zip", "Multi-selection name"); return Task.CompletedTask;
        });
        foreach (string format in new[] { "zip", "7z" }) await test("Quick menu actual " + format.ToUpperInvariant() + " roundtrip keeps source", async () =>
        {
            var plan = QuickArchivePlan.Build([folder], format); string original = await ArchiveService.HashAsync(Path.Combine(folder, "配置.txt"));
            var created = await service.CreateAsync(plan.Inputs, plan.OutputPath, new CreateOptions { Format = plan.Format });
            Check((await service.ListAsync(created[0])).Format == format, "Wrong actual archive format");
            string destination = Path.Combine(parent, format + "-out"); await service.ExtractAsync(created[0], destination, new ExtractOptions { Layout = ExtractLayout.Here });
            Check(await ArchiveService.HashAsync(Path.Combine(destination, "Clash Verge", "配置.txt")) == original && await ArchiveService.HashAsync(Path.Combine(folder, "配置.txt")) == original, "Quick compression altered source or content");
        });
        await test("Quick menu existing archives and directories receive a safe new name", async () =>
        {
            string original = Path.Combine(parent, "Clash Verge.zip"), hash = await ArchiveService.HashAsync(original);
            Directory.CreateDirectory(Path.Combine(parent, "Clash Verge (2).zip"));
            var plan = QuickArchivePlan.Build([folder], "zip"); Check(Path.GetFileName(plan.OutputPath) == "Clash Verge (3).zip", "Collision name");
            await service.CreateAsync(plan.Inputs, plan.OutputPath, new CreateOptions { Format = plan.Format }); Check(await ArchiveService.HashAsync(original) == hash, "Existing archive overwritten");
        });
        await test("Quick menu multi-selection packs all selected content", async () =>
        {
            var plan = QuickArchivePlan.Build([file, folder], "zip"); await service.CreateAsync(plan.Inputs, plan.OutputPath, new CreateOptions());
            var entries = (await service.ListAsync(plan.OutputPath)).Entries; Check(entries.Any(e => e.Name == "说明 & [notes].txt") && entries.Any(e => e.Name == "配置.txt"), "Lost multi-selection content");
        });
        await test("Quick menu dotfiles archive-like folders and duplicate inputs", () =>
        {
            string dot = Path.Combine(parent, ".gitignore"), archiveFolder = Path.Combine(parent, "folder.zip"); File.WriteAllText(dot, "*.tmp"); Directory.CreateDirectory(archiveFolder);
            Check(Path.GetFileName(QuickArchivePlan.Build([dot], "zip").OutputPath) == ".gitignore.zip", "Dotfile name lost");
            Check(Path.GetFileName(QuickArchivePlan.Build([archiveFolder], "zip").OutputPath) == "folder.zip.zip", "Folder extension stripped");
            Check(QuickArchivePlan.Build([file, file], "zip").Inputs.Count == 1, "Duplicate inputs not normalized"); return Task.CompletedTask;
        });
        await test("Quick menu refuses missing inputs unsupported formats and drive roots", () =>
        {
            int errors = 0;
            foreach (var action in new Action[] { () => QuickArchivePlan.Build([], "zip"), () => QuickArchivePlan.Build([file], "rar"), () => QuickArchivePlan.Build([file + ".missing"], "7z"), () => QuickArchivePlan.Build([Path.GetPathRoot(parent)!], "zip") })
                try { action(); } catch (Exception error) when (error is ArgumentException or IOException or ArchiveOperationException) { errors++; }
            Check(errors == 4, "Invalid quick action allowed"); return Task.CompletedTask;
        });
    }
}

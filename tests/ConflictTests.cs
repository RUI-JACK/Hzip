using System.IO.Compression;
using Hzip.Core;

static class ConflictTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync(string root, ArchiveService service, Func<string, Func<Task>, Task> test)
    {
        string area = Path.Combine(root, "conflicts"); Directory.CreateDirectory(area);
        var options = new ExtractOptions { Layout = ExtractLayout.Here, Overwrite = OverwritePolicy.Ask };
        async Task<(string Archive, string Output)> Fixture(string name, params string[] members)
        {
            string folder = Path.Combine(area, name); Directory.CreateDirectory(folder);
            string archive = Path.Combine(folder, "sample.zip"), output = Path.Combine(folder, "output"); Directory.CreateDirectory(output);
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                foreach (string member in members)
                {
                    var entry = zip.CreateEntry(member); entry.LastWriteTime = new DateTimeOffset(2026, 10, 18, 2, 6, 4, TimeSpan.Zero);
                    if (!member.EndsWith('/')) { using var writer = new StreamWriter(entry.Open()); await writer.WriteAsync("new:" + member); }
                }
            return (archive, output);
        }
        foreach (var choice in new[] { ConflictChoice.Replace, ConflictChoice.Skip, ConflictChoice.Rename })
            await test("Conflict apply all " + choice + " processes actual files with one prompt", async () =>
            {
                var (archive, output) = await Fixture("all-" + choice, "a.txt", "b.txt", "c.txt");
                foreach (string name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(output, name), "old:" + name);
                int prompts = 0;
                var result = await service.ExtractAsync(archive, output, options, resolveConflict: (conflict, ct) => { prompts++; Check(conflict.CanReplace, "File conflict is not replaceable"); return Task.FromResult(new ConflictResolution(choice, true)); });
                Check(prompts == 1 && result.Files == (choice == ConflictChoice.Skip ? 0 : 3), "All decision did not apply");
                foreach (string name in new[] { "a.txt", "b.txt", "c.txt" })
                {
                    Check(File.ReadAllText(Path.Combine(output, name)) == (choice == ConflictChoice.Replace ? "new:" : "old:") + name, "Existing content mismatch");
                    if (choice == ConflictChoice.Rename) Check(File.ReadAllText(Path.Combine(output, Path.GetFileNameWithoutExtension(name) + " (2).txt")) == "new:" + name, "Renamed content mismatch");
                }
            });
        await test("Conflict per-file choices show accurate sizes times paths and do not persist", async () =>
        {
            var (archive, output) = await Fixture("mixed", "a.txt", "b.txt", "c.txt");
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt" }) { string p = Path.Combine(output, name); File.WriteAllText(p, "old"); File.SetLastWriteTime(p, new DateTime(2025, 1, 2, 3, 4, 6)); }
            int prompts = 0;
            await service.ExtractAsync(archive, output, options, resolveConflict: (conflict, ct) =>
            {
                prompts++; Check(conflict.ExistingSize == 3 && conflict.ExistingModified.Year == 2025 && conflict.IncomingSize > 3 && conflict.IncomingModified.Year == 2026, "Comparison metadata mismatch");
                Check(conflict.ArchivePath == archive && conflict.DestinationPath == Path.Combine(output, conflict.RelativePath), "Comparison path mismatch");
                return Task.FromResult(new ConflictResolution(conflict.RelativePath switch { "a.txt" => ConflictChoice.Replace, "b.txt" => ConflictChoice.Skip, _ => ConflictChoice.Rename }));
            });
            Check(prompts == 3 && File.ReadAllText(Path.Combine(output, "a.txt")) == "new:a.txt" && File.ReadAllText(Path.Combine(output, "b.txt")) == "old" && File.Exists(Path.Combine(output, "c (2).txt")), "Per-file decision leaked");
        });
        await test("Conflict cancellation rolls back earlier replacements and preserves archive", async () =>
        {
            var (archive, output) = await Fixture("cancel", "a.txt", "b.txt", "c.txt");
            var originalTime = new DateTime(2024, 1, 2, 3, 4, 6);
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt" }) { string p = Path.Combine(output, name); File.WriteAllText(p, "old:" + name); File.SetLastWriteTime(p, originalTime); }
            string hash = await ArchiveService.HashAsync(archive); int count = 0; bool canceled = false;
            try { await service.ExtractAsync(archive, output, options, resolveConflict: (_, _) => Task.FromResult(new ConflictResolution(++count == 1 ? ConflictChoice.Replace : ConflictChoice.Cancel))); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && count == 2, "Cancel choice did not stop extraction");
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt" }) Check(File.ReadAllText(Path.Combine(output, name)) == "old:" + name && File.GetLastWriteTime(Path.Combine(output, name)) == originalTime, "Cancellation did not roll back content and modification time");
            Check(await ArchiveService.HashAsync(archive) == hash, "Source archive changed");
        });
        await test("Conflict cancellation token interrupts pending resolver without output", async () =>
        {
            var (archive, output) = await Fixture("token", "a.txt"); File.WriteAllText(Path.Combine(output, "a.txt"), "old");
            using var cancellation = new CancellationTokenSource(); bool canceled = false;
            try { await service.ExtractAsync(archive, output, options, cancellation.Token, resolveConflict: async (_, ct) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct); return new(ConflictChoice.Replace); }); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && File.ReadAllText(Path.Combine(output, "a.txt")) == "old", "Canceled prompt wrote output");
        });
        await test("Conflict changed destination during prompt is not blindly replaced", async () =>
        {
            var (archive, output) = await Fixture("changed", "a.txt"); string target = Path.Combine(output, "a.txt"); File.WriteAllText(target, "old"); bool failed = false;
            try { await service.ExtractAsync(archive, output, options, resolveConflict: (_, _) => { File.WriteAllText(target, "changed by another program"); return Task.FromResult(new ConflictResolution(ConflictChoice.Replace)); }); }
            catch (IOException) { failed = true; }
            Check(failed && File.ReadAllText(target) == "changed by another program", "Stale comparison replaced external changes");
        });
        await test("Conflict asking without a resolver refuses collision but permits new files", async () =>
        {
            var (archive, output) = await Fixture("no-resolver", "a.txt"); await service.ExtractAsync(archive, output, options); bool failed = false;
            try { await service.ExtractAsync(archive, output, options); } catch (ArchiveOperationException) { failed = true; }
            Check(failed && File.ReadAllText(Path.Combine(output, "a.txt")) == "new:a.txt", "Ask without UI silently overwrote");
        });
        foreach (var choice in new[] { ConflictChoice.Skip, ConflictChoice.Rename })
            await test("Conflict incoming folder against existing file " + choice, async () =>
            {
                var (archive, output) = await Fixture("folder-" + choice, "folder/", "folder/nested/", "folder/nested/a.txt", "sibling.txt");
                File.WriteAllText(Path.Combine(output, "folder"), "old file"); int prompts = 0;
                await service.ExtractAsync(archive, output, options, resolveConflict: (conflict, ct) => { prompts++; Check(conflict.IncomingIsDirectory && !conflict.CanReplace && !conflict.ExistingIsDirectory, "Folder conflict information"); return Task.FromResult(new ConflictResolution(choice)); });
                Check(prompts == 1 && File.ReadAllText(Path.Combine(output, "folder")) == "old file", "Folder decision lost existing file");
                Check(File.Exists(Path.Combine(output, "folder (2)/nested/a.txt")) == (choice == ConflictChoice.Rename), "Descendant mapping mismatch");
                Check(File.ReadAllText(Path.Combine(output, "sibling.txt")) == "new:sibling.txt", "Sibling was skipped");
            });
        await test("Conflict incoming file against existing directory can rename without touching children", async () =>
        {
            var (archive, output) = await Fixture("file-folder", "a.txt"); string folder = Path.Combine(output, "a.txt"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "keep"), "keep");
            await service.ExtractAsync(archive, output, options, resolveConflict: (conflict, ct) => { Check(conflict.ExistingIsDirectory && !conflict.CanReplace && conflict.ExistingSize == null, "Existing directory metadata"); return Task.FromResult(new ConflictResolution(ConflictChoice.Rename)); });
            Check(File.ReadAllText(Path.Combine(folder, "keep")) == "keep" && File.ReadAllText(Path.Combine(output, "a (2).txt")) == "new:a.txt", "File-folder rename lost content");
        });
        await test("Conflict replace choice cannot remove an existing directory", async () =>
        {
            var (archive, output) = await Fixture("reject-folder", "a.txt"); Directory.CreateDirectory(Path.Combine(output, "a.txt")); bool failed = false;
            try { await service.ExtractAsync(archive, output, options, resolveConflict: (_, _) => Task.FromResult(new ConflictResolution(ConflictChoice.Replace))); }
            catch (IOException) { failed = true; }
            Check(failed && Directory.Exists(Path.Combine(output, "a.txt")), "File replaced a directory");
        });
        await test("Conflict apply-all state is reset for a new extraction", async () =>
        {
            var (archive, output) = await Fixture("reset", "a.txt"); File.WriteAllText(Path.Combine(output, "a.txt"), "old"); int prompts = 0;
            ConflictResolver resolver = (_, _) => { prompts++; return Task.FromResult(new ConflictResolution(ConflictChoice.Skip, true)); };
            await service.ExtractAsync(archive, output, options, resolveConflict: resolver); await service.ExtractAsync(archive, output, options, resolveConflict: resolver);
            Check(prompts == 2, "Decision leaked to a new operation");
        });
    }
}

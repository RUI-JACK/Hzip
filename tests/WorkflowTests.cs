using System.Text;
using Hzip.Core;

internal static class WorkflowTests
{
    public static async Task RunAsync(string root, ArchiveService service, Func<string, Func<Task>, Task> test)
    {
        string area = Path.Combine(root, "workflow"); Directory.CreateDirectory(area); Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        async Task Fails(Func<Task> action) { bool failed = false; try { await action(); } catch (Exception e) when (e is IOException or ArchiveOperationException or OperationCanceledException) { failed = true; } Check(failed, "Expected safe failure"); }
        string Legacy(string name) => LegacyZipFixture.Create(area, name);

        await test("Workflow legacy Shift-JIS preview and edit preserve names and siblings", async () =>
        {
            string path = Legacy("legacy-edit.zip"); var info = await service.ListAsync(path, codePage: 932);
            Check(info.Entries.Any(e => e.NormalizedPath == "日本語/説明.txt"), "Fixture not Shift-JIS decoded");
            using var preview = await service.PreviewAsync(path, info.Entries.Single(e => e.NormalizedPath == "日本語/説明.txt"), codePage: 932);
            Check(await File.ReadAllTextAsync(preview.FilePath) == "original Japanese entry", "Wrong preview");
            await File.WriteAllTextAsync(preview.FilePath, "edited Japanese content");
            await service.UpdateMemberAsync(path, "日本語/説明.txt", preview.FilePath, codePage: 932);
            var after = await service.ListAsync(path, codePage: 932); Check(after.Entries.Count(e => !e.IsDirectory) == 2, "Edited entry duplicated");
            using var changed = await service.PreviewAsync(path, after.Entries.Single(e => e.NormalizedPath == "日本語/説明.txt"), codePage: 932);
            using var sibling = await service.PreviewAsync(path, after.Entries.Single(e => e.NormalizedPath == "残す.txt"), codePage: 932);
            Check(File.ReadAllText(changed.FilePath) == "edited Japanese content" && File.ReadAllText(sibling.FilePath) == "keep sibling", "Content mismatch");
        });
        await test("Workflow legacy rename delete and export use the selected codepage", async () =>
        {
            string path = Legacy("legacy-actions.zip");
            await service.RenameAsync(path, "日本語/説明.txt", "日本語/改名.txt", codePage: 932);
            using (var export = await ArchiveExportLease.CreateAsync(service, path, ["日本語/改名.txt", "残す.txt"], codePage: 932))
                Check(export.Paths.Count == 2 && export.Paths.All(File.Exists), "Wrong file-drop paths");
            await service.DeleteAsync(path, ["日本語/改名.txt"], codePage: 932);
            Check((await service.ListAsync(path, codePage: 932)).Entries.Count(e => !e.IsDirectory) == 1, "Wrong deletion");
        });
        string zipPath = Path.Combine(area, "drag.zip"), source = Path.Combine(area, "source"); Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
        File.WriteAllText(Path.Combine(source, "nested", "hello.txt"), "drag payload"); File.WriteAllText(Path.Combine(source, ".hzip-drag-cache"), "user content must survive");
        await service.CreateAsync([source], zipPath, new CreateOptions { Overwrite = true });
        await test("Workflow file-drop payload preserves folders, removes overlapping roots and stays copy-only", async () =>
        {
            string[] paths;
            using (var export = await ArchiveExportLease.CreateAsync(service, zipPath, ["source/nested", "source/nested/hello.txt", "source/.hzip-drag-cache"]))
            {
                paths = export.Paths.ToArray(); Check(paths.Length == 2 && Directory.Exists(Path.Combine(paths[0], "empty")), "Empty folder or overlap lost");
                Check(File.ReadAllText(paths[1]) == "user content must survive", "Marker clobbered user file"); export.RetainForShellCopy();
            }
            Check(paths.All(p => Directory.Exists(p) || File.Exists(p)), "Shell payload deleted too early");
            Check(File.ReadAllText(paths[1]) == "user content must survive", "Retention marker clobbered payload");
            Check((await service.ListAsync(zipPath)).Entries.Any(e => e.Path.EndsWith("hello.txt")), "Drag removed source member");
        });
        await test("Workflow cancelled or disposed file-drop payload leaves no materialized files", async () =>
        {
            string file; using (var export = await ArchiveExportLease.CreateAsync(service, zipPath, ["source/nested/hello.txt"])) file = export.Paths.Single();
            Check(!File.Exists(file), "Cancelled payload leaked"); using var stop = new CancellationTokenSource(); stop.Cancel();
            await Fails(() => ArchiveExportLease.CreateAsync(service, zipPath, ["source/nested"], ct: stop.Token));
        });
        var entry = (await service.ListAsync(zipPath)).Entries.Single(e => e.Path.EndsWith("hello.txt"));
        async Task<ExternalEditSession> Session()
        { using var preview = await service.PreviewAsync(zipPath, entry); return await ExternalEditSession.CreateAsync(preview.FilePath, zipPath, entry.Path); }
        await test("Workflow external editor atomic-replace save is detected and written back", async () =>
        {
            var session = await Session(); Check(!await session.HasChangesAsync(), "Initial file falsely dirty");
            string originalHash = await ArchiveService.HashAsync(zipPath), replacement = session.FilePath + ".new";
            await File.WriteAllTextAsync(replacement, "external editor saved"); File.Move(replacement, session.FilePath, true);
            Check(await session.HasChangesAsync(), "Atomic rename not detected");
            await session.SaveAsync(file => service.UpdateMemberAsync(zipPath, entry.Path, file, expectedArchiveHash: originalHash));
            Check(!await session.HasChangesAsync(), "Saved file still dirty");
            using var result = await service.PreviewAsync(zipPath, entry); Check(File.ReadAllText(result.FilePath) == "external editor saved", "Write-back failed");
            Check(File.Exists(session.FilePath), "Durable editing copy deleted");
            Check(!File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")).Contains("Password"), "Manifest contains password data");
        });
        await test("Workflow external write-back refuses an archive changed since preview", async () =>
        {
            var session = await Session(); string before = await ArchiveService.HashAsync(zipPath); File.WriteAllText(session.FilePath, "valuable local edit");
            string addition = Path.Combine(area, "other.txt"); File.WriteAllText(addition, "another writer"); await service.AddAsync(zipPath, [addition]); string changedHash = await ArchiveService.HashAsync(zipPath);
            await Fails(() => session.SaveAsync(file => service.UpdateMemberAsync(zipPath, entry.Path, file, expectedArchiveHash: before)));
            Check(await ArchiveService.HashAsync(zipPath) == changedHash && await session.HasChangesAsync() && File.ReadAllText(session.FilePath) == "valuable local edit", "Conflict lost archive or edit");
        });
        await test("Workflow failed and cancelled external saves retain the user's working copy", async () =>
        {
            var session = await Session(); File.WriteAllText(session.FilePath, "retain this edit");
            await Fails(() => session.SaveAsync(_ => throw new IOException("Simulated destination failure")));
            Check(File.ReadAllText(session.FilePath) == "retain this edit" && await session.HasChangesAsync(), "Failed save lost copy");
            using var stop = new CancellationTokenSource(); stop.Cancel(); bool called = false;
            await Fails(() => session.SaveAsync(_ => { called = true; return Task.CompletedTask; }, stop.Token)); Check(!called && File.Exists(session.FilePath), "Cancelled save called writer");
        });
        await test("Workflow external locked-file snapshot refuses partial content safely", async () =>
        {
            var session = await Session(); File.WriteAllText(session.FilePath, "locked content"); bool called = false;
            using (var locked = new FileStream(session.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Fails(() => session.SaveAsync(_ => { called = true; return Task.CompletedTask; }));
            Check(!called && File.ReadAllText(session.FilePath) == "locked content", "Locked file was written or lost");
        });
        await test("Workflow member conflict guard rejects changed content but permits unrelated edits", async () =>
        {
            using var original = await service.PreviewAsync(zipPath, entry); string baseline = await ArchiveService.HashAsync(original.FilePath);
            string unrelated = Path.Combine(area, "unrelated.txt"); File.WriteAllText(unrelated, "preserve unrelated change"); await service.AddAsync(zipPath, [unrelated]);
            File.WriteAllText(original.FilePath, "guarded edit");
            await service.UpdateMemberAsync(zipPath, entry.Path, original.FilePath, expectedMemberHash: baseline);
            using var latest = await service.PreviewAsync(zipPath, entry); Check(File.ReadAllText(latest.FilePath) == "guarded edit", "Unrelated change blocked edit");
            string committed = await ArchiveService.HashAsync(zipPath); File.WriteAllText(original.FilePath, "stale editor version");
            await Fails(() => service.UpdateMemberAsync(zipPath, entry.Path, original.FilePath, expectedMemberHash: baseline));
            Check(await ArchiveService.HashAsync(zipPath) == committed && File.ReadAllText(original.FilePath) == "stale editor version", "Conflict guard overwrote archive or edited copy");
        });
        await test("Workflow save uses a stable snapshot and detects the editor's next revision", async () =>
        {
            var session = await Session(); File.WriteAllText(session.FilePath, "revision one"); string? written = null;
            await session.SaveAsync(async snapshot => { written = await File.ReadAllTextAsync(snapshot); await File.WriteAllTextAsync(session.FilePath, "revision two"); });
            Check(written == "revision one" && await session.HasChangesAsync() && File.ReadAllText(session.FilePath) == "revision two", "Concurrent revision was marked saved");
        });
        await test("Workflow external editing copy retains Internet origin information", async () =>
        {
            using var preview = await service.PreviewAsync(zipPath, entry); string marker = "[ZoneTransfer]\r\nZoneId=3\r\n";
            await File.WriteAllTextAsync(preview.FilePath + ":Zone.Identifier", marker);
            var session = await ExternalEditSession.CreateAsync(preview.FilePath, zipPath, entry.Path);
            Check(File.ReadAllText(session.FilePath + ":Zone.Identifier") == marker, "Origin marker lost");
        });
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hzip.Core;

public sealed class ArchiveService
{
    public ArchiveEngine Engine { get; }
    public ArchiveService(ArchiveEngine? engine = null) => Engine = engine ?? new ArchiveEngine();
    public Task<ArchiveInfo> ListAsync(string path, string? password = null, CancellationToken ct = default, int codePage = 0) => Engine.ListAsync(path, password, ct, codePage);
    public async Task<IReadOnlyList<string>> CreateAsync(IReadOnlyList<string> inputs, string output, CreateOptions options,
        CancellationToken ct = default, IProgress<JobProgress>? progress = null)
    {
        await JobControl.CheckpointAsync(ct);
        if (inputs.Count == 0) throw new ArchiveOperationException("请先添加要压缩的文件或文件夹。");
        inputs = inputs.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string input in inputs) if (!File.Exists(input) && !Directory.Exists(input)) throw new FileNotFoundException("源文件不存在。", input);
        output = System.IO.Path.GetFullPath(output); string parent = System.IO.Path.GetDirectoryName(output)!;
        if (inputs.Any(input => string.Equals(input, output, StringComparison.OrdinalIgnoreCase)))
            throw new ArchiveOperationException("输出归档不能与源文件使用同一路径，请选择另一个文件名。");
        SafePaths.NoReparseAncestors(parent); Directory.CreateDirectory(parent);
        if ((File.Exists(output) || File.Exists(output + ".001")) && !options.Overwrite) throw new IOException("目标归档已存在。");
        foreach (string input in inputs.Where(Directory.Exists))
            if (output.StartsWith(input.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArchiveOperationException("请把输出归档保存在所选文件夹之外，避免归档包含自身。");
        using var work = new Workspace();
        foreach (string input in inputs.Where(Directory.Exists))
            if (work.Path.StartsWith(input.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArchiveOperationException("所选源目录包含 Hzip 的任务暂存目录，请单独选择其中的文件。");
        string staged = System.IO.Path.Combine(work.Path, System.IO.Path.GetFileName(output));
        string format = options.Format.ToLowerInvariant();
        if (format == "lha") format = "lzh";
        if (!string.Equals(options.ZipEncryption, "AES256", StringComparison.OrdinalIgnoreCase) && !string.Equals(options.ZipEncryption, "ZipCrypto", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("ZIP 加密方式应为 AES256 或 ZipCrypto。");
        options = options with { Format = format, ZipEncryption = options.ZipEncryption.Equals("ZipCrypto", StringComparison.OrdinalIgnoreCase) ? "ZipCrypto" : "AES256" };
        if (options.Level is < 0 or > 9 || options.Threads < 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (!string.IsNullOrWhiteSpace(options.VolumeSize) && !Regex.IsMatch(options.VolumeSize, @"^[1-9]\d*[bkmgBKMG]?$"))
            throw new ArchiveOperationException("分卷大小格式无效，请使用 100M、4G 或字节数。");
        if (format == "zip" && ManagedZip.Needed(options.Password))
        {
            await ManagedZip.CreateAsync(inputs, staged, options, ct, progress);
            if (!string.IsNullOrWhiteSpace(options.VolumeSize)) await ManagedZip.SplitAsync(staged, options.VolumeSize, ct);
        }
        else if (format is "iso" or "lzh" or "zipx")
        {
            if (!string.IsNullOrEmpty(options.Password) || !string.IsNullOrWhiteSpace(options.VolumeSize)) throw new ArchiveOperationException("此格式的 Hzip 写入器暂不支持加密或分卷，请选 ZIP 或 7Z。");
            await SpecialFormats.CreateAsync(Engine, inputs, staged, options, ct, progress);
        }
        else if (format is "tgz" or "txz" or "tbz2")
        {
            if (!string.IsNullOrEmpty(options.Password)) throw new ArchiveOperationException("TAR 压缩流不支持密码，请选 ZIP 或 7Z。");
            string tar = System.IO.Path.Combine(work.Path, TextFormat.ArchiveStem(output) + ".tar");
            await CreateWithEngine(inputs, tar, options with { Format = "tar", VolumeSize = null }, ct, progress);
            await CreateWithEngine([tar], staged, options with { Format = format == "tgz" ? "gzip" : format == "txz" ? "xz" : "bzip2" }, ct, progress);
        }
        else
        {
            string actual = format switch { "gz" => "gzip", "bz2" => "bzip2", "exe" => "7z", _ => format };
            if (actual is "gzip" or "xz" or "bzip2" && (inputs.Count != 1 || Directory.Exists(inputs[0]))) throw new ArchiveOperationException("此格式只能压缩一个文件。多个文件请使用 TAR.GZ / TAR.XZ 或 ZIP。");
            if (actual is not ("zip" or "7z" or "tar" or "gzip" or "xz" or "bzip2" or "wim")) throw new ArchiveOperationException("此创建格式尚不受支持。");
            if (!string.IsNullOrEmpty(options.Password) && actual is not ("zip" or "7z")) throw new ArchiveOperationException("此格式不支持密码加密，请选 ZIP 或 7Z。");
            await CreateWithEngine(inputs, staged, options with { Format = actual }, ct, progress, format == "exe");
        }
        ct.ThrowIfCancellationRequested();
        var outputs = Directory.GetFiles(work.Path).Where(f => f == staged || f.StartsWith(staged + ".", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (outputs.Length == 0) throw new ArchiveOperationException("没有生成归档文件。");
        var newTargets = outputs.Select(file => System.IO.Path.Combine(parent, System.IO.Path.GetFileName(file))).ToArray();
        var oldVolumes = Directory.EnumerateFiles(parent, System.IO.Path.GetFileName(output) + ".*")
            .Where(file => file.Length >= output.Length + 4 && int.TryParse(file[(output.Length + 1)..], out int part) && part > 0).ToArray();
        var obsolete = oldVolumes.Concat(File.Exists(output) ? [output] : Array.Empty<string>()).Where(file => !newTargets.Contains(file, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (newTargets.Concat(obsolete).Any(target => inputs.Any(input => string.Equals(input, target, StringComparison.OrdinalIgnoreCase))))
            throw new ArchiveOperationException("输出归档或分卷会覆盖源文件，操作已停止。请更换输出文件名。");
        // Test the staged archive (or first split volume) before touching any destination.
        progress?.Report(new JobProgress(null, "正在验证新归档完整性…", "校验"));
        await Engine.TestAsync(outputs[0], options.Password, ct, progress);
        // Native split volumes are committed as one rollback-capable group.
        using var backups = new Workspace(); var committed = new List<(string Target, string? Backup)>();
        try
        {
            foreach (string file in outputs)
            {
                await JobControl.CheckpointAsync(ct); string target = System.IO.Path.Combine(parent, System.IO.Path.GetFileName(file));
                if (File.Exists(target) && !options.Overwrite) throw new IOException("目标文件已存在：" + target);
                string? backup = null;
                if (File.Exists(target)) { backup = System.IO.Path.Combine(backups.Path, Guid.NewGuid().ToString("N")); File.Copy(target, backup); }
                AtomicCommit(file, target, options.Overwrite); committed.Add((target, backup));
            }
            if (options.Overwrite)
                foreach (string old in obsolete)
                {
                    await JobControl.CheckpointAsync(ct); SafePaths.NoReparseAncestors(old); string saved = System.IO.Path.Combine(backups.Path, Guid.NewGuid().ToString("N"));
                    File.Copy(old, saved); File.Delete(old); committed.Add((old, saved));
                }
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            RollbackOrPreserve(committed, backups, error);
            throw;
        }
        progress?.Report(new JobProgress(100, "归档已创建", "完成"));
        return newTargets;
    }
    private async Task CreateWithEngine(IReadOnlyList<string> inputs, string output, CreateOptions options, CancellationToken ct, IProgress<JobProgress>? progress, bool sfx = false)
    {
        string common = CommonParent(inputs);
        using var manifest = new Workspace(); string list = System.IO.Path.Combine(manifest.Path, "files.txt");
        await File.WriteAllLinesAsync(list, inputs.Select(p => System.IO.Path.GetRelativePath(common, p)), new UTF8Encoding(false), ct);
        var args = new List<string> { "a", "-t" + options.Format, "-mx=" + options.Level, "-sccUTF-8", "-scsUTF-8", "-bsp1", "-y", "-spd" };
        if (options.Format == "zip") args.Add("-mcu=on");
        if (options.Format != "tar") args.Add("-mmt=" + (options.Threads == 0 ? "on" : options.Threads));
        if (options.Format == "7z") args.Add(options.Solid ? "-ms=on" : "-ms=off");
        if (!string.IsNullOrEmpty(options.Password))
        {
            args.Add("-p" + options.Password);
            if (options.Format == "zip") args.Add("-mem=" + options.ZipEncryption);
            if (options.Format == "7z" && options.EncryptHeaders) args.Add("-mhe=on");
        }
        if (!string.IsNullOrWhiteSpace(options.VolumeSize)) args.Add("-v" + options.VolumeSize);
        if (sfx)
        {
            string stub = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Engine.Executable)!, "7z.sfx");
            if (!File.Exists(stub)) throw new FileNotFoundException("缺少自解压模块。", stub);
            args.Add("-sfx" + stub);
        }
        args.Add("-i@" + list); args.Add("--"); args.Add(output);
        progress?.Report(new JobProgress(0, "正在压缩文件…", "压缩"));
        await Engine.RunAsync(args, ct, progress, common);
    }
    public async Task<ExtractResult> ExtractAsync(string archive, string destination, ExtractOptions options, CancellationToken ct = default, IProgress<JobProgress>? progress = null, ConflictResolver? resolveConflict = null)
    {
        progress?.Report(new JobProgress(null, "正在读取归档目录…", "检查"));
        var info = await ListAsync(archive, options.Password, ct, options.CodePage);
        var selectedPrefixes = options.SelectedPaths?.Select(SafePaths.Relative).ToArray();
        var checkedEntries = selectedPrefixes is { Length: > 0 } ? info.Entries.Where(e => selectedPrefixes.Any(p => e.NormalizedPath == p || e.NormalizedPath.StartsWith(p + "/", StringComparison.Ordinal))).ToArray() : info.Entries;
        SafePaths.ValidateEntries(checkedEntries);
        using var work = new Workspace(); string staging = System.IO.Path.Combine(work.Path, "unpacked"); Directory.CreateDirectory(staging);
        var args = new List<string> { "x", "-o" + staging, "-aoa", "-y", "-sccUTF-8", "-scsUTF-8", "-bsp1", "-spd" };
        if (!string.IsNullOrEmpty(options.Password)) args.Add("-p" + options.Password);
        if (options.CodePage != 0) args.Add("-mcp=" + options.CodePage);
        if (options.SelectedPaths is { Count: > 0 })
        {
            string list = System.IO.Path.Combine(work.Path, "selection.txt");
            var selected = options.SelectedPaths.Select(SafePaths.Relative).ToArray();
            var include = info.Entries.Where(e => selected.Any(p => e.NormalizedPath.TrimEnd('/') == p || e.NormalizedPath.StartsWith(p + "/", StringComparison.Ordinal))).Select(e => e.Path).ToArray();
            if (include.Length == 0) throw new ArchiveOperationException("没有找到选中的条目。");
            await File.WriteAllLinesAsync(list, include, new UTF8Encoding(false), ct); args.Add("-i@" + list);
        }
        args.Add("--"); args.Add(System.IO.Path.GetFullPath(archive));
        progress?.Report(new JobProgress(0, "正在解压到任务暂存区…", "解压"));
        var warnings = new List<string>();
        if (info.Format == "gzip" && checkedEntries.Count() == 1)
        {
            var entry = checkedEntries.Single();
            if (entry.IsDirectory) throw new ArchiveOperationException("GZIP 条目无效。");
            string target = SafePaths.Within(staging, entry.Path); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            long trailing;
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan))
                trailing = await GzipReader.ReadAsync(archive, output, ct, progress);
            if (entry.Modified is { } modified) File.SetLastWriteTime(target, modified);
            if (trailing > 0) warnings.Add(GzipReader.TailWarning(trailing));
        }
        else try { await Engine.RunAsync(args, ct, progress); }
        catch (ArchiveOperationException error) when (error.NeedsPassword && info.Format == "zip" && ManagedZip.Needed(options.Password))
        { await ManagedZip.ExtractToAsync(archive, staging, options.Password!, options.SelectedPaths, ct, progress, options.CodePage); }
        _ = SafePaths.WalkRegularFiles(staging);
        // Handle common tar streams so .tar.gz/.tgz extraction yields their actual members.
        if (options.SelectedPaths is not { Count: > 0 } && info.Entries.Count == 1 && info.Entries[0].Path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) && info.Format is "gzip" or "xz" or "bzip2")
        {
            string tar = SafePaths.Within(staging, info.Entries[0].Path);
            var inner = await ListAsync(tar, options.Password, ct); SafePaths.ValidateEntries(inner.Entries);
            string nested = System.IO.Path.Combine(work.Path, "tar-members"); Directory.CreateDirectory(nested);
            await Engine.RunAsync(["x", "-o" + nested, "-y", "-aoa", "-sccUTF-8", "-bsp1", "--", tar], ct, progress);
            staging = nested; info = inner;
        }
        var files = SafePaths.WalkRegularFiles(staging);
        string fullDestination = System.IO.Path.GetFullPath(destination);
        bool singleFolder = info.Entries.Count > 0 && info.Entries.Select(e => e.NormalizedPath.TrimEnd('/').Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 && info.Entries.Any(e => e.IsDirectory || e.NormalizedPath.Contains('/'));
        if (options.Layout == ExtractLayout.Subfolder || options.Layout == ExtractLayout.Smart && !singleFolder && info.Entries.Count > 1)
            fullDestination = System.IO.Path.Combine(fullDestination, TextFormat.ArchiveStem(archive));
        SafePaths.NoReparseAncestors(fullDestination); Directory.CreateDirectory(fullDestination);
        using var backup = new Workspace(); var changes = new List<(string Target, string? Backup)>();
        ConflictChoice? allChoice = null;
        var relocatedDirectories = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? TargetFor(string relative)
        {
            for (string? parent = relative; !string.IsNullOrEmpty(parent); parent = System.IO.Path.GetDirectoryName(parent))
                if (relocatedDirectories.TryGetValue(parent, out var mapped))
                    return mapped == null ? null : relative.Length == parent.Length ? mapped : SafePaths.Within(mapped, relative[(parent.Length + 1)..]);
            return SafePaths.Within(fullDestination, relative);
        }
        async Task<OverwritePolicy> Decide(string source, string target, bool directory)
        {
            if (options.Overwrite != OverwritePolicy.Ask) return options.Overwrite;
            if (resolveConflict == null) throw new ArchiveOperationException("目标已存在，需要选择替换、跳过或重命名：" + target);
            bool existingDirectory = Directory.Exists(target);
            var existing = new FileInfo(target);
            long? size = existingDirectory ? null : existing.Length;
            DateTime modified = File.GetLastWriteTime(target), created = File.GetCreationTimeUtc(target);
            var conflict = new ExtractionConflict(System.IO.Path.GetFullPath(archive), System.IO.Path.GetRelativePath(staging, source), target,
                directory ? null : new FileInfo(source).Length, File.GetLastWriteTime(source), size, modified, directory, existingDirectory);
            ConflictChoice choice;
            if (allChoice is { } remembered && (remembered != ConflictChoice.Replace || conflict.CanReplace)) choice = remembered;
            else
            {
                progress?.Report(new JobProgress(null, "等待处理同名文件：" + System.IO.Path.GetFileName(target), "等待选择"));
                var decision = await resolveConflict(conflict, ct); choice = decision.Choice;
                if (!Enum.IsDefined(choice)) throw new ArchiveOperationException("同名文件处理选项无效。");
                if (decision.ApplyToAll && choice != ConflictChoice.Cancel) allChoice = choice;
            }
            if (choice == ConflictChoice.Cancel) throw new OperationCanceledException("已取消解压。", ct);
            await JobControl.CheckpointAsync(ct); SafePaths.NoReparseAncestors(target);
            if (choice == ConflictChoice.Replace)
            {
                if (!conflict.CanReplace) throw new IOException("文件与文件夹不能直接相互替换，请跳过或重命名：" + target);
                if (!File.Exists(target) || Directory.Exists(target) || new FileInfo(target).Length != size || File.GetLastWriteTime(target) != modified || File.GetCreationTimeUtc(target) != created)
                    throw new IOException("目标文件在等待选择时已发生变化，请重新解压并确认：" + target);
            }
            return choice switch { ConflictChoice.Replace => OverwritePolicy.Overwrite, ConflictChoice.Skip => OverwritePolicy.Skip, _ => OverwritePolicy.Rename };
        }
        string? zone = null; try { if (File.Exists(archive + ":Zone.Identifier")) zone = await File.ReadAllTextAsync(archive + ":Zone.Identifier", ct); } catch (IOException) { }
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories).OrderBy(d => d.Length))
            {
                await JobControl.CheckpointAsync(ct); string relative = System.IO.Path.GetRelativePath(staging, directory);
                string? target = TargetFor(relative); if (target == null) continue;
                SafePaths.NoReparseAncestors(target);
                if (File.Exists(target))
                {
                    var policy = await Decide(directory, target, true);
                    if (policy == OverwritePolicy.Skip) { relocatedDirectories[relative] = null; warnings.Add("已跳过文件夹：" + relative); continue; }
                    if (policy != OverwritePolicy.Rename) throw new IOException("目标位置存在同名文件，请选择自动改名或其他解压目录：" + target);
                    string next = target; for (int n = 2; File.Exists(next) || Directory.Exists(next); n++) next = target + $" ({n})";
                    relocatedDirectories[relative] = target = next;
                }
                Directory.CreateDirectory(target);
            }
            for (int index = 0; index < files.Count; index++)
            {
                await JobControl.CheckpointAsync(ct); string source = files[index]; string? target = TargetFor(System.IO.Path.GetRelativePath(staging, source)); if (target == null) continue;
                string originalArchive = System.IO.Path.GetFullPath(archive);
                string volumeBase = Regex.Replace(originalArchive, @"\.\d{3,}$", "");
                if (string.Equals(target, originalArchive, StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith(volumeBase + ".", StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(target[(volumeBase.Length + 1)..], @"^\d{3,}$"))
                    throw new ArchiveOperationException("解压内容会覆盖正在读取的归档或分卷，操作已停止。请选择另一个解压目录。");
                SafePaths.NoReparseAncestors(target); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                var policy = options.Overwrite;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    policy = await Decide(source, target, false);
                    if (policy == OverwritePolicy.Skip) { warnings.Add("已跳过：" + System.IO.Path.GetRelativePath(staging, source)); continue; }
                    if (policy == OverwritePolicy.Fail) throw new IOException("目标文件或文件夹已存在：" + target);
                    if (policy == OverwritePolicy.Rename) target = UniqueFile(target);
                    else if (Directory.Exists(target)) throw new IOException("目标位置存在同名文件夹，请选择自动改名或其他解压目录：" + target);
                }
                string? old = null;
                if (File.Exists(target)) { old = System.IO.Path.Combine(backup.Path, Guid.NewGuid().ToString("N")); File.Copy(target, old); }
                AtomicCommit(source, target, policy == OverwritePolicy.Overwrite); changes.Add((target, old));
                try { File.SetLastWriteTime(target, File.GetLastWriteTime(source)); if (zone != null) await File.WriteAllTextAsync(target + ":Zone.Identifier", zone, ct); } catch (IOException) { warnings.Add("无法保留元数据：" + System.IO.Path.GetFileName(target)); }
                progress?.Report(new JobProgress(95 + 5.0 * (index + 1) / Math.Max(1, files.Count), System.IO.Path.GetFileName(target), "写入", index + 1, files.Count));
            }
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            RollbackOrPreserve(changes, backup, error);
            throw;
        }
        progress?.Report(new JobProgress(100, "解压完成", "完成")); return new ExtractResult(fullDestination, changes.Count, warnings);
    }
    public async Task AddAsync(string archive, IReadOnlyList<string> inputs, string? password = null, CancellationToken ct = default, IProgress<JobProgress>? progress = null, int codePage = 0)
    {
        if (inputs.Count == 0) return;
        foreach (string input in inputs)
        {
            if (!File.Exists(input) && !Directory.Exists(input)) throw new FileNotFoundException("要添加的源文件不存在。", input);
            if (string.Equals(System.IO.Path.GetFullPath(input), System.IO.Path.GetFullPath(archive), StringComparison.OrdinalIgnoreCase)) throw new ArchiveOperationException("不能把归档添加到它自身。");
        }
        string common = CommonParent(inputs); using var list = new Workspace(); string manifest = System.IO.Path.Combine(list.Path, "add.txt");
        await File.WriteAllLinesAsync(manifest, inputs.Select(p => System.IO.Path.GetRelativePath(common, System.IO.Path.GetFullPath(p))), new UTF8Encoding(false), ct);
        await MutateAsync(archive, password, staged => ["a", "-sccUTF-8", "-scsUTF-8", "-spd", "-y", "-bsp1", "-i@" + manifest, .. PasswordArgs(password), "--", staged], ct, progress, common,
            staged => ManagedZip.RewriteAsync(staged, password!, inputs, null, null, null, null, null, ct, progress, codePage), codePage: codePage);
    }
    public Task DeleteAsync(string archive, IReadOnlyList<string> paths, string? password = null, CancellationToken ct = default, IProgress<JobProgress>? progress = null, int codePage = 0)
    {
        if (paths.Count == 0) return Task.CompletedTask;
        foreach (var path in paths) _ = SafePaths.Relative(path);
        return MutateAsync(archive, password, staged => ["d", "-sccUTF-8", "-spd", "-y", "-bsp1", .. PasswordArgs(password), "--", staged, .. paths], ct, progress,
            managed: staged => ManagedZip.RewriteAsync(staged, password!, null, paths, null, null, null, null, ct, progress, codePage), codePage: codePage);
    }
    public Task RenameAsync(string archive, string oldName, string newName, string? password = null, CancellationToken ct = default, IProgress<JobProgress>? progress = null, int codePage = 0)
    {
        _ = SafePaths.Relative(oldName); _ = SafePaths.Relative(newName);
        return MutateAsync(archive, password, staged => ["rn", "-sccUTF-8", "-spd", "-y", .. PasswordArgs(password), "--", staged, oldName, newName], ct, progress,
            managed: staged => ManagedZip.RewriteAsync(staged, password!, null, null, oldName, newName, null, null, ct, progress, codePage),
            validate: info =>
            {
                string oldPath = SafePaths.Relative(oldName), newPath = SafePaths.Relative(newName);
                bool Match(ArchiveEntry entry) => entry.NormalizedPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase) || entry.NormalizedPath.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase);
                if (!info.Entries.Any(Match)) throw new ArchiveOperationException("要改名的条目不存在，请刷新归档。");
                if (newPath.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)) throw new ArchiveOperationException("不能把文件夹移动到它自身的子目录中。");
                SafePaths.ValidateEntries(info.Entries.Select(entry => Match(entry) ? entry with { Path = newPath + entry.NormalizedPath[oldPath.Length..] } : entry).ToArray());
            }, codePage: codePage);
    }
    private async Task MutateAsync(string archive, string? password, Func<string, IEnumerable<string>> arguments, CancellationToken ct, IProgress<JobProgress>? progress, string? cwd = null, Func<string, Task>? managed = null, Action<ArchiveInfo>? validate = null, int codePage = 0, string? expectedArchiveHash = null, string? expectedMemberPath = null, string? expectedMemberHash = null)
    {
        archive = System.IO.Path.GetFullPath(archive); var originalInfo = await ListAsync(archive, password, ct, codePage);
        if (originalInfo.Format is not ("zip" or "7z" or "tar" or "wim")) throw new ArchiveOperationException("此格式支持读取；修改归档请使用 ZIP、7Z、TAR 或 WIM。");
        SafePaths.ValidateEntries(originalInfo.Entries);
        validate?.Invoke(originalInfo);
        var validation = await Engine.TestAsync(archive, password, ct, progress);
        var originalHash = await HashAsync(archive, ct);
        if (expectedArchiveHash != null && !string.Equals(originalHash, expectedArchiveHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("归档在预览后已改变，请保留编辑副本并重新打开归档再合并修改。");
        using var work = new Workspace(); string staged = System.IO.Path.Combine(work.Path, System.IO.Path.GetFileName(archive));
        File.Copy(archive, staged);
        if (expectedMemberPath != null && expectedMemberHash != null)
        {
            var member = originalInfo.Entries.SingleOrDefault(e => e.NormalizedPath == SafePaths.Relative(expectedMemberPath));
            if (member == null) throw new IOException("原条目已被删除或改名，编辑副本已保留，请重新打开归档。");
            using var current = await PreviewAsync(staged, member, password, ct, long.MaxValue, codePage);
            if (await HashAsync(current.FilePath, ct) != expectedMemberHash) throw new IOException("此条目在预览后已被修改。为避免覆盖另一份修改，请保留编辑副本并手动合并。");
        }
        if (validation.ManagedZipCompatibility && managed != null) await managed(staged);
        else await Engine.RunAsync(codePage == 0 || originalInfo.Format != "zip" ? arguments(staged) : arguments(staged).Prepend("-mcp=" + codePage), ct, progress, cwd);
        await Engine.TestAsync(staged, password, ct, progress); ct.ThrowIfCancellationRequested();
        if (await HashAsync(archive, ct) != originalHash) throw new IOException("归档已被另一个程序修改，请重新打开后再操作。");
        AtomicCommit(staged, archive); progress?.Report(new JobProgress(100, "归档已更新", "完成"));
    }
    public async Task UpdateMemberAsync(string archive, string entryPath, string editedFile, string? password = null, CancellationToken ct = default, IProgress<JobProgress>? progress = null, int codePage = 0, string? expectedArchiveHash = null, string? expectedMemberHash = null)
    {
        using var work = new Workspace(); string member = SafePaths.Within(work.Path, entryPath); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(member)!); File.Copy(editedFile, member);
        await MutateAsync(archive, password, staged => ["a", "-sccUTF-8", "-spd", "-y", .. PasswordArgs(password), "--", staged, entryPath], ct, progress, work.Path,
            staged => ManagedZip.RewriteAsync(staged, password!, null, null, null, null, entryPath, editedFile, ct, progress, codePage),
            validate: info => { if (!info.Entries.Any(entry => !entry.IsDirectory && entry.NormalizedPath == SafePaths.Relative(entryPath))) throw new ArchiveOperationException("要编辑的条目不存在，请刷新归档。"); }, codePage: codePage, expectedArchiveHash: expectedArchiveHash, expectedMemberPath: entryPath, expectedMemberHash: expectedMemberHash);
    }
    public async Task<PreviewLease> PreviewAsync(string archive, ArchiveEntry entry, string? password = null, CancellationToken ct = default, long maximumBytes = 32 * 1024 * 1024, int codePage = 0)
    {
        if (entry.IsDirectory) throw new ArchiveOperationException("请选择一个文件。");
        if (entry.Size > maximumBytes) throw new ArchiveOperationException("此文件较大，请解压后打开。");
        var workspace = new Workspace();
        try
        {
            await ExtractAsync(archive, workspace.Path, new ExtractOptions { Password = password, Layout = ExtractLayout.Here, SelectedPaths = [entry.Path], Overwrite = OverwritePolicy.Fail, CodePage = codePage }, ct);
            string file = SafePaths.Within(workspace.Path, entry.Path);
            if (!File.Exists(file) || new FileInfo(file).Length > maximumBytes) throw new ArchiveOperationException("无法预览此文件。");
            return new PreviewLease(workspace, file);
        }
        catch { workspace.Dispose(); throw; }
    }
    public static async Task<string> HashAsync(string file, CancellationToken ct = default)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[1024 * 1024];
        while (true) { await JobControl.CheckpointAsync(ct); int count = await stream.ReadAsync(buffer, ct); if (count == 0) break; hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    internal static string CommonParent(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new ArgumentException("No source files");
        string common = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(paths[0]))!;
        while (paths.Any(p => !System.IO.Path.GetFullPath(p).StartsWith(common.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            common = System.IO.Path.GetDirectoryName(common) ?? throw new ArchiveOperationException("请将不同磁盘中的文件先放进同一个文件夹。");
        return common;
    }
    private static IEnumerable<string> PasswordArgs(string? password) => string.IsNullOrEmpty(password) ? [] : ["-p" + password];
    private static void RollbackOrPreserve(IReadOnlyList<(string Target, string? Backup)> changes, Workspace backup, Exception cause)
    {
        var failed = new List<string>();
        foreach (var item in changes.Reverse())
        {
            try
            {
                if (item.Backup != null) { AtomicCommit(item.Backup, item.Target); File.SetLastWriteTime(item.Target, File.GetLastWriteTime(item.Backup)); }
                else File.Delete(item.Target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed.Add(item.Target); }
        }
        if (failed.Count == 0) return;
        string recovery = backup.PreserveForRecovery();
        try
        {
            var receipt = new { Cause = cause.Message, UnrestoredTargets = failed, Files = changes.Select(item => new { item.Target, Backup = item.Backup == null ? null : System.IO.Path.GetFileName(item.Backup) }) };
            File.WriteAllText(System.IO.Path.Combine(recovery, "recovery.json"), System.Text.Json.JsonSerializer.Serialize(receipt, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        throw new ArchiveOperationException("操作失败，部分目标文件被占用，无法自动恢复。原文件备份已保留，请勿删除此目录：\n" + recovery, details: string.Join('\n', failed));
    }
    public static void AtomicCommit(string staged, string target, bool overwrite = true)
    {
        SafePaths.NoReparseAncestors(target); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(target))!);
        string incoming = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, ".hzip-incoming-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Copy bytes rather than filesystem attributes. A workspace under an EFS
            // profile inherits encryption; CopyFile otherwise tries to encrypt the
            // user's output too, even in an ordinary unencrypted destination folder.
            using (var source = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (var destination = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            // File.Move without overwrite also protects files that appeared after preflight.
            if (overwrite && File.Exists(target)) File.Replace(incoming, target, null); else File.Move(incoming, target);
        }
        finally { if (File.Exists(incoming)) File.Delete(incoming); }
    }
    private static string UniqueFile(string file)
    {
        string directory = System.IO.Path.GetDirectoryName(file)!, name = System.IO.Path.GetFileNameWithoutExtension(file), ext = System.IO.Path.GetExtension(file);
        for (int n = 2; n < int.MaxValue; n++) { string next = System.IO.Path.Combine(directory, $"{name} ({n}){ext}"); if (!File.Exists(next) && !Directory.Exists(next)) return next; }
        throw new IOException("无法生成不重复的文件名。");
    }
}
public sealed class PreviewLease(Workspace workspace, string filePath) : IDisposable
{
    public string FilePath { get; } = filePath;
    public void Dispose() => workspace.Dispose();
}

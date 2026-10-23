using Hzip.Core;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8; Console.InputEncoding = Encoding.UTF8; Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
using var cancellation = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
const string help = """
Hzip 0.5 · Windows 归档工具

  hz create  <output> <file-or-folder>... [--format zip|7z|zipx|exe|tar|tgz|txz|tbz2|iso|lzh|gz|xz|bz2|wim]
  hz list    <archive> [--json]
  hz extract <archive> <directory> [--layout smart|here|subfolder] [--overwrite rename|skip|replace|fail]
  hz test    <archive>
  hz add     <archive> <files>...
  hz delete  <archive> <entry-paths>...
  hz rename  <archive> <old-entry> <new-entry>
  hz repair  <damaged.zip> <repaired.zip>
  hz recover <archive> --dictionary <utf8.txt>
  hz recover <archive> --digits 4 --start 0 --end 9999
  hz scan    <archive>
  hz hash    <file>
  hz formats

通用选项：--password <value>，--password-file <utf8.txt>，--json，--encoding <codepage>
压缩选项：--level 0..9，--volume 100M，--threads 0，--no-header-encryption，--no-solid，--replace
ZIP 选项：--encryption AES256|ZipCrypto，--password-encoding 65001|936|932（默认 UTF-8）
密码也可通过 HZIP_PASSWORD 环境变量提供。使用 Ctrl+C 取消。
命令别名：c=create，l=list，x=extract，t=test，a=add，d=delete，rn=rename。
""";
if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Console.WriteLine(help); return 0; }
try
{
    string command = args[0].ToLowerInvariant() switch { "c" => "create", "l" => "list", "x" => "extract", "t" => "test", "a" => "add", "d" => "delete", "rn" => "rename", var value => value };
    var positionals = new List<string>(); var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "json", "no-header-encryption", "no-solid", "replace" };
    for (int i = 1; i < args.Length; i++)
    {
        string arg = args[i];
        if (arg == "--") { positionals.AddRange(args[(i + 1)..]); break; }
        if (arg.StartsWith("--"))
        {
            string key = arg[2..]; int equal = key.IndexOf('='); if (equal >= 0) { options[key[..equal]] = key[(equal + 1)..]; continue; }
            if (flags.Contains(key)) options[key] = "true";
            else { if (++i >= args.Length) throw new ArgumentException("缺少选项值：" + arg); options[key] = args[i]; }
        }
        else positionals.Add(arg);
    }
    bool Flag(string name) => options.TryGetValue(name, out string? flagValue) && bool.Parse(flagValue);
    foreach (string flag in flags) if (options.TryGetValue(flag, out string? flagValue) && !bool.TryParse(flagValue, out _)) throw new ArgumentException("布尔选项应为 true 或 false：--" + flag);
    bool json = Flag("json"); string? password = options.GetValueOrDefault("password") ?? Environment.GetEnvironmentVariable("HZIP_PASSWORD");
    var validOptions = new HashSet<string>(new[] { "password", "password-file", "encoding", "format", "level", "volume", "threads", "encryption", "password-encoding", "layout", "overwrite", "dictionary", "digits", "start", "end" }.Concat(flags), StringComparer.OrdinalIgnoreCase);
    foreach (string option in options.Keys) if (!validOptions.Contains(option)) throw new ArgumentException("未知选项：--" + option);
    if (options.TryGetValue("password-file", out string? passwordFile)) password = (await File.ReadAllTextAsync(passwordFile, cancellation.Token)).TrimEnd('\r', '\n');
    string Pos(int index) => positionals.Count > index ? positionals[index] : throw new ArgumentException("缺少路径参数，请运行 hz --help。");
    int Number(string key, int fallback) => options.TryGetValue(key, out string? value) ? int.Parse(value) : fallback;
    var service = new ArchiveService(); long lastProgress = 0;
    var progress = new Progress<JobProgress>(p => { if (Environment.TickCount64 - lastProgress < 200) return; lastProgress = Environment.TickCount64; Console.Error.WriteLine($"{p.Stage}: {(p.Percent.HasValue ? p.Percent.Value.ToString("0") + "%" : "…")} {p.Message}"); });
    void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    switch (command)
    {
        case "formats": Console.WriteLine("创建：ZIP, 7Z, ZIPX(XZ), EXE(SFX), TAR, TAR.GZ, TAR.XZ, TAR.BZ2, ISO, LZH(LH0), GZIP, XZ, BZIP2, WIM\n读取：由随附 7-Zip 26.04 引擎提供，含 RAR/RAR5、CAB、ARJ、ISO、LZH、DMG 等。\n格式与功能限制见程序包内功能对照表。"); break;
        case "list":
            var info = await service.ListAsync(Pos(0), password, cancellation.Token, Number("encoding", 0));
            if (json) Print(info); else { Console.WriteLine($"{System.IO.Path.GetFileName(info.FilePath)} · {info.Format} · {info.Entries.Count} entries"); foreach (var entry in info.Entries) Console.WriteLine($"{entry.DisplaySize,12}  {(entry.IsDirectory ? "D" : " ")} {(entry.Encrypted ? "*" : " ")} {entry.Path}"); } break;
        case "create":
            string output = Pos(0), lower = output.ToLowerInvariant(); string inferred = lower.EndsWith(".tar.gz") ? "tgz" : lower.EndsWith(".tar.xz") ? "txz" : lower.EndsWith(".tar.bz2") ? "tbz2" : System.IO.Path.GetExtension(output).TrimStart('.');
            var created = await service.CreateAsync(positionals.Skip(1).ToArray(), output, new CreateOptions { Format = options.GetValueOrDefault("format", inferred), Level = Number("level", 5), Password = password, EncryptHeaders = !Flag("no-header-encryption"), Solid = !Flag("no-solid"), Threads = Number("threads", 0), VolumeSize = options.GetValueOrDefault("volume"), Overwrite = Flag("replace"), ZipEncryption = options.GetValueOrDefault("encryption", "AES256"), ZipPasswordCodePage = Number("password-encoding", 65001) }, cancellation.Token, progress);
            if (json) Print(created); else foreach (var path in created) Console.WriteLine("Created: " + path); break;
        case "extract":
            var result = await service.ExtractAsync(Pos(0), Pos(1), new ExtractOptions { Password = password, CodePage = Number("encoding", 0), Layout = options.GetValueOrDefault("layout", "smart") switch { "here" => ExtractLayout.Here, "subfolder" => ExtractLayout.Subfolder, _ => ExtractLayout.Smart }, Overwrite = options.GetValueOrDefault("overwrite", "rename") switch { "replace" => OverwritePolicy.Overwrite, "skip" => OverwritePolicy.Skip, "fail" => OverwritePolicy.Fail, _ => OverwritePolicy.Rename } }, cancellation.Token, progress);
            if (json) Print(result); else Console.WriteLine($"Extracted {result.Files} files: {result.Destination}\n" + string.Join("\n", result.Warnings)); break;
        case "test":
            var tested = await service.Engine.TestAsync(Pos(0), password, cancellation.Token, progress);
            if (json) Print(new { Valid = true, Archive = Pos(0), Warnings = tested.HasWarning ? new[] { tested.Error } : Array.Empty<string>() });
            else Console.WriteLine(tested.HasWarning ? tested.Error : "All OK"); break;
        case "add": _ = Pos(1); await service.AddAsync(Pos(0), positionals.Skip(1).ToArray(), password, cancellation.Token, progress, Number("encoding", 0)); Console.WriteLine("Updated"); break;
        case "delete": _ = Pos(1); await service.DeleteAsync(Pos(0), positionals.Skip(1).ToArray(), password, cancellation.Token, progress, Number("encoding", 0)); Console.WriteLine("Updated"); break;
        case "rename": await service.RenameAsync(Pos(0), Pos(1), Pos(2), password, cancellation.Token, progress, Number("encoding", 0)); Console.WriteLine("Updated"); break;
        case "repair": var repaired = await ZipRepair.RepairAsync(Pos(0), Pos(1), cancellation.Token, progress, Number("encoding", 0), password); if (json) Print(repaired); else Console.WriteLine($"Recovered {repaired.Recovered} entries: {repaired.Output}\nReport: {repaired.ReportPath}\n" + string.Join("\n", repaired.Skipped)); break;
        case "hash": string hash = await ArchiveService.HashAsync(Pos(0), cancellation.Token); if (json) Print(new { Path = Pos(0), SHA256 = hash }); else Console.WriteLine(hash); break;
        case "scan": var scanned = await ArchiveScanner.ScanAsync(service, Pos(0), password, cancellation.Token, progress); Print(scanned); return scanned.Detected > 0 ? 10 : scanned.Skipped.Count > 0 ? 3 : 0;
        case "recover":
            IEnumerable<string> candidates; long total;
            if (options.TryGetValue("dictionary", out string? dictionary)) { candidates = File.ReadLines(dictionary, Encoding.UTF8); total = 0; }
            else { int digits = Number("digits", 4); long start = long.Parse(options.GetValueOrDefault("start", "0")), end = long.Parse(options.GetValueOrDefault("end", ((long)Math.Pow(10, digits) - 1).ToString())); candidates = PasswordRecovery.Numeric(digits, start, end); total = end - start + 1; }
            var recovery = await PasswordRecovery.SearchAsync(service.Engine, Pos(0), candidates, total, Number("threads", 2), cancellation.Token); Print(recovery); return recovery.Found ? 0 : 4;
        default: throw new ArgumentException("未知命令：" + command + "。运行 hz --help 查看用法。");
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("已取消"); return 130; }
catch (Exception error) { Console.Error.WriteLine(error.Message); if (error is ArchiveOperationException archiveError && !string.IsNullOrWhiteSpace(archiveError.Details)) Console.Error.WriteLine(archiveError.Details); return 2; }

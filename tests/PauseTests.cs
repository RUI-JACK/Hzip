using Hzip.Core;
using System.Diagnostics;
using System.Text.Json;

static class PauseTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> condition, Task operation)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (operation.IsCompleted) { await operation; throw new Exception("Task completed before pause checkpoint"); } if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Pause setup"); await Task.Delay(10); }
    }
    public static async Task RunAsync(string root, ArchiveService service, Func<string, Func<Task>, Task> test)
    {
        string area = Path.Combine(root, "pause"); Directory.CreateDirectory(area); string source = Path.Combine(area, "large-random.bin");
        async Task EnsureSource()
        {
            if (File.Exists(source)) return;
            var random = new Random(12543); byte[] bytes = new byte[1024 * 1024]; await using var file = File.Create(source);
            for (int i = 0; i < 64; i++) { random.NextBytes(bytes); await file.WriteAsync(bytes); }
        }
        await test("Pause native compression freezes CPU and resumes without data changes", async () =>
        {
            await EnsureSource(); string archive = Path.Combine(area, "paused.zip"); using var control = new JobControl();
            var create = Task.Run(async () => { using var scope = control.Enter(); return await service.CreateAsync([source], archive, new CreateOptions { Level = 9, Threads = 2 }); });
            await Until(() => control.NativeWorkerCount > 0, create);
            await control.PauseAsync().WaitAsync(TimeSpan.FromSeconds(12)); Check(control.IsPaused, "Pause never acknowledged");
            int id = control.WorkerProcessIds.Single(); using var process = Process.GetProcessById(id);
            await Task.Delay(150); process.Refresh(); double before = process.TotalProcessorTime.TotalSeconds;
            await Task.Delay(600); process.Refresh(); double cpuDelta = process.TotalProcessorTime.TotalSeconds - before;
            Check(!create.IsCompleted && cpuDelta < .04, "Worker kept running while paused: CPU " + cpuDelta);
            control.Resume(); await Task.Delay(120);
            if (!create.IsCompleted) { await control.PauseAsync().WaitAsync(TimeSpan.FromSeconds(12)); Check(control.IsPaused, "Second pause"); control.Resume(); }
            await create.WaitAsync(TimeSpan.FromSeconds(90)); control.Complete();
            string output = Path.Combine(area, "native-out"); await service.ExtractAsync(archive, output, new ExtractOptions { Layout = ExtractLayout.Here });
            Check(await ArchiveService.HashAsync(source) == await ArchiveService.HashAsync(Path.Combine(output, Path.GetFileName(source))), "Resume changed data");
            await File.WriteAllTextAsync(Path.Combine(root, "native-pause-proof.json"), JsonSerializer.Serialize(new { WorkerProcessId = id, PausedCpuDeltaSeconds = cpuDelta, PausedMilliseconds = 600, PayloadBytes = new FileInfo(source).Length, ResumedHashMatches = true }));
        });
        await test("Pause managed encrypted compression stops writes and resumes", async () =>
        {
            await EnsureSource(); string archive = Path.Combine(area, "managed-pause.zip"); using var control = new JobControl();
            var create = Task.Run(async () => { using var scope = control.Enter(); return await service.CreateAsync([source], archive, new CreateOptions { Level = 5, Password = "暂停密码" }); });
            long Size() => Directory.GetFiles(AppPaths.TempRoot, "managed-pause.zip", SearchOption.AllDirectories).Select(p => new FileInfo(p).Length).DefaultIfEmpty().Sum();
            await Until(() => Size() > 1024 * 1024, create); await control.PauseAsync().WaitAsync(TimeSpan.FromSeconds(12));
            await Task.Delay(100); long before = Size(); await Task.Delay(400); Check(Size() == before && !create.IsCompleted, "Managed compression continued writing");
            control.Resume(); await create.WaitAsync(TimeSpan.FromSeconds(90)); control.Complete(); await service.Engine.TestAsync(archive, "暂停密码");
        });
        await test("Pause cancellation terminates held native worker and preserves existing output", async () =>
        {
            await EnsureSource(); string archive = Path.Combine(area, "cancel-paused.zip"); File.WriteAllText(archive, "original output"); using var cancellation = new CancellationTokenSource(); using var control = new JobControl(cancellation.Token);
            var create = Task.Run(async () => { using var scope = control.Enter(); return await service.CreateAsync([source], archive, new CreateOptions { Level = 9, Threads = 2, Overwrite = true }, cancellation.Token); });
            await Until(() => control.NativeWorkerCount > 0, create); await control.PauseAsync().WaitAsync(TimeSpan.FromSeconds(12));
            int id = control.WorkerProcessIds.Single(); cancellation.Cancel(); bool canceled = false;
            try { await create.WaitAsync(TimeSpan.FromSeconds(12)); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && File.ReadAllText(archive) == "original output", "Cancel while paused lost original output");
            bool alive; try { using var child = Process.GetProcessById(id); alive = !child.HasExited; } catch (ArgumentException) { alive = false; }
            Check(!alive, "Canceled paused worker is still running");
        });
        await test("Pause before process start blocks work until continued", async () =>
        {
            using var control = new JobControl(); var request = control.PauseAsync(); bool entered = false;
            var work = Task.Run(async () => { using var scope = control.Enter(); await JobControl.CheckpointAsync(); entered = true; });
            await request.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(150); Check(!entered, "Paused job started work"); control.Resume(); await work;
        });
        await test("Pause rapid resume and re-pause do not leave the worker frozen", async () =>
        {
            await EnsureSource(); using var control = new JobControl(); string archive = Path.Combine(area, "rapid-pause.zip");
            var create = Task.Run(async () => { using var scope = control.Enter(); return await service.CreateAsync([source], archive, new CreateOptions { Level = 1, Threads = 2 }); });
            await Until(() => control.NativeWorkerCount > 0, create); var first = control.PauseAsync(); control.Resume(); var second = control.PauseAsync();
            try { await first.WaitAsync(TimeSpan.FromSeconds(12)); } catch (OperationCanceledException) { }
            await second.WaitAsync(TimeSpan.FromSeconds(12)); Check(control.IsPaused && !create.IsCompleted, "Older pause completion canceled newer request");
            control.Resume(); await create.WaitAsync(TimeSpan.FromSeconds(90)); control.Complete(); await service.Engine.TestAsync(archive);
        });
        await test("Pause ZIP repair scanning stops at a checkpoint and continues", async () =>
        {
            await EnsureSource(); string input = Path.Combine(area, "paused.zip"), output = Path.Combine(area, "pause-repair.zip");
            if (!File.Exists(input)) await service.CreateAsync([source], input, new CreateOptions { Level = 1 });
            using var control = new JobControl(); Task? pause = null; int updates = 0;
            var reporter = new Immediate(p => { Interlocked.Increment(ref updates); if (p.Stage == "扫描 ZIP" && pause == null) pause = control.PauseAsync(); });
            var repair = Task.Run(async () => { using var scope = control.Enter(); return await ZipRepair.RepairAsync(input, output, progress: reporter); });
            await Until(() => pause != null, repair); await pause!.WaitAsync(TimeSpan.FromSeconds(12)); int count = updates;
            await Task.Delay(300); Check(updates == count && !repair.IsCompleted && !File.Exists(output), "Repair progressed while paused");
            control.Resume(); await repair.WaitAsync(TimeSpan.FromSeconds(90)); control.Complete(); await service.Engine.TestAsync(output);
        });
    }
    private sealed class Immediate(Action<JobProgress> action) : IProgress<JobProgress> { public void Report(JobProgress value) => action(value); }
}

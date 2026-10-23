using System.Diagnostics;

namespace Hzip.Core;

/// <summary>In-memory job pause. Managed work stops at checkpoints; owned engine workers stop at a Windows debug event.</summary>
public sealed class JobControl(CancellationToken cancellation = default) : IDisposable
{
    private static readonly AsyncLocal<JobControl?> ambient = new();
    private readonly object sync = new();
    private readonly HashSet<NativeWorkerPause> workers = [];
    private TaskCompletionSource resume = CompletedSignal(), boundary = NewSignal();
    private CancellationTokenSource? pendingPause;
    private bool requested, paused, completed;
    public static JobControl? Current => ambient.Value;
    public bool IsPauseRequested { get { lock (sync) return requested; } }
    public bool IsPaused { get { lock (sync) return paused; } }
    public int NativeWorkerCount { get { lock (sync) return workers.Count; } }
    public IReadOnlyList<int> WorkerProcessIds { get { lock (sync) return workers.Select(worker => worker.ProcessId).ToArray(); } }
    public event Action? StateChanged;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource CompletedSignal() { var signal = NewSignal(); signal.SetResult(); return signal; }
    public IDisposable Enter() { var previous = ambient.Value; ambient.Value = this; return new Scope(() => ambient.Value = previous); }
    public static Task CheckpointAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); return Current?.WaitAsync(ct) ?? Task.CompletedTask;
    }
    public static void Checkpoint(CancellationToken ct = default) => CheckpointAsync(ct).GetAwaiter().GetResult();
    private Task WaitAsync(CancellationToken ct)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!requested || completed) return Task.CompletedTask;
            boundary.TrySetResult(); return resume.Task.WaitAsync(ct.CanBeCanceled ? ct : cancellation);
        }
    }
    public async Task PauseAsync(CancellationToken ct = default)
    {
        NativeWorkerPause[] active; Task checkpoint; CancellationTokenSource request;
        lock (sync)
        {
            if (completed || requested) return;
            requested = true; paused = false; resume = NewSignal(); boundary = NewSignal();
            request = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellation); pendingPause = request;
            active = workers.ToArray(); checkpoint = boundary.Task;
        }
        StateChanged?.Invoke();
        try
        {
            if (active.Length > 0) await Task.WhenAll(active.Select(worker => worker.PauseAsync(request.Token)));
            else await checkpoint.WaitAsync(request.Token);
            lock (sync) { if (requested && !completed) paused = true; }
            StateChanged?.Invoke();
        }
        catch { bool ownsRequest; lock (sync) ownsRequest = ReferenceEquals(pendingPause, request); if (ownsRequest) Resume(); throw; }
        finally { lock (sync) { if (ReferenceEquals(pendingPause, request)) pendingPause = null; } request.Dispose(); }
    }
    public void Resume()
    {
        NativeWorkerPause[] active; CancellationTokenSource? request;
        lock (sync)
        {
            requested = paused = false; active = workers.ToArray(); resume.TrySetResult(); boundary.TrySetResult();
            request = pendingPause; pendingPause = null;
        }
        foreach (var worker in active) worker.Resume();
        try { request?.Cancel(); } catch (ObjectDisposedException) { }
        StateChanged?.Invoke();
    }
    internal bool TryStart(Process process, out NativeWorkerPause? worker)
    {
        lock (sync)
        {
            worker = null;
            if (requested && !completed) return false;
            cancellation.ThrowIfCancellationRequested();
            if (!process.Start()) throw new ArchiveOperationException("无法启动压缩任务。");
            worker = new NativeWorkerPause(process, value => { lock (sync) workers.Remove(value); });
            workers.Add(worker); return true;
        }
    }
    public void Complete()
    {
        lock (sync) completed = true;
        Resume();
    }
    public void Dispose() => Complete();
    internal static async Task CopyAsync(Stream source, Stream target, CancellationToken ct)
    {
        byte[] buffer = new byte[128 * 1024];
        while (true)
        {
            await CheckpointAsync(ct); int count = await source.ReadAsync(buffer, ct); if (count == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }
    private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
}

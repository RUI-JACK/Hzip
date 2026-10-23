using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hzip.Core;

/// <summary>Only attaches to the exact child process started by this ArchiveEngine, and only while pausing.</summary>
internal sealed class NativeWorkerPause(Process process, Action<NativeWorkerPause> removed) : IDisposable
{
    private readonly uint processId = (uint)process.Id;
    // Retain the process object until detachment, so a recycled PID can never target another process.
    private readonly SafeProcessHandle identity = RetainIdentity(process);
    public int ProcessId => (int)processId;
    private readonly SemaphoreSlim transition = new(1, 1);
    private readonly ManualResetEventSlim run = new(true);
    private TaskCompletionSource detached = Finished();
    private bool disposed;
    private static SafeProcessHandle RetainIdentity(Process value) { var handle = value.SafeHandle; bool held = false; handle.DangerousAddRef(ref held); return handle; }
    private static TaskCompletionSource Finished() { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
    public async Task PauseAsync(CancellationToken ct)
    {
        TaskCompletionSource stopped;
        await transition.WaitAsync(ct);
        try
        {
            await detached.Task.WaitAsync(ct);
            if (disposed || process.HasExited) return;
            ct.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("原生引擎暂停需要 Windows x64。");
            run.Reset();
            stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() => HoldWorker(stopped)) { IsBackground = true, Name = "Hzip engine pause" }.Start();
        }
        finally { transition.Release(); }
        using var cancellation = ct.Register(Resume);
        try { await stopped.Task.WaitAsync(ct); }
        catch { Resume(); throw; }
    }
    public void Resume() => run.Set();
    private void HoldWorker(TaskCompletionSource stopped)
    {
        bool attached = false; bool initialBreakpoint = true;
        try
        {
            if (!DebugActiveProcess(processId))
            {
                if (process.HasExited) { stopped.TrySetResult(); return; }
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法暂停此任务；任务将继续运行。");
            }
            attached = true;
            if (!DebugSetProcessKillOnExit(false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            while (true)
            {
                if (!WaitForDebugEvent(out var item, 100))
                {
                    if (process.HasExited) { stopped.TrySetResult(); break; }
                    continue;
                }
                uint status = 0x00010002; // DBG_CONTINUE
                bool finish = item.Code == 5; // EXIT_PROCESS_DEBUG_EVENT
                try
                {
                    if (item.Code is 3 or 6 && item.File != IntPtr.Zero) CloseHandle(item.File);
                    if (item.Code == 1)
                    {
                        if (item.ExceptionCode == 0x80000003 && initialBreakpoint)
                        {
                            initialBreakpoint = false; stopped.TrySetResult();
                            run.Wait(); finish = true;
                        }
                        else status = 0x80010001; // DBG_EXCEPTION_NOT_HANDLED: never swallow worker faults.
                    }
                }
                finally { ContinueDebugEvent(item.ProcessId, item.ThreadId, status); }
                if (finish) { stopped.TrySetResult(); break; }
            }
        }
        catch (Exception error) { stopped.TrySetException(error); }
        finally
        {
            if (attached) DebugActiveProcessStop(processId);
            detached.TrySetResult();
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Resume();
        transition.Wait();
        try
        {
            Resume(); removed(this);
            _ = detached.Task.ContinueWith(_ => identity.DangerousRelease(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        finally { transition.Release(); }
        // The background debugger detaches itself; do not block the UI or dispose its wait handle prematurely.
    }
    [StructLayout(LayoutKind.Explicit, Size = 176)]
    private struct DebugEvent
    {
        [FieldOffset(0)] public uint Code;
        [FieldOffset(4)] public uint ProcessId;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(16)] public uint ExceptionCode;
        [FieldOffset(16)] public IntPtr File;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugActiveProcess(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugActiveProcessStop(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WaitForDebugEvent(out DebugEvent item, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool ContinueDebugEvent(uint processId, uint threadId, uint status);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

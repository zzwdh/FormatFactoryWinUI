using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FormatFactoryWinUI.Services;

internal sealed class ProcessThreadSuspension : IDisposable
{
    private readonly List<SafeThreadHandle> _threads;
    private bool _resumed;

    private ProcessThreadSuspension(List<SafeThreadHandle> threads) => _threads = threads;

    public static ProcessThreadSuspension? TrySuspend(int processId)
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotThread, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return null;
        }

        var handles = new List<SafeThreadHandle>();
        try
        {
            var entry = new ThreadEntry32 { Size = (uint)Marshal.SizeOf<ThreadEntry32>() };
            if (!Thread32First(snapshot, ref entry))
            {
                return null;
            }

            do
            {
                if (entry.OwnerProcessId != processId)
                {
                    continue;
                }

                var thread = OpenThread(ThreadSuspendResume, false, entry.ThreadId);
                if (thread == IntPtr.Zero || thread == InvalidHandleValue)
                {
                    continue;
                }

                var safeThread = new SafeThreadHandle(thread);
                if (SuspendThread(safeThread) == uint.MaxValue)
                {
                    safeThread.Dispose();
                    continue;
                }

                handles.Add(safeThread);
            }
            while (Thread32Next(snapshot, ref entry));

            return handles.Count == 0 ? null : new ProcessThreadSuspension(handles);
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public void Resume()
    {
        if (_resumed)
        {
            return;
        }

        _resumed = true;
        foreach (var thread in _threads)
        {
            if (!thread.IsInvalid)
            {
                ResumeThread(thread);
            }
            thread.Dispose();
        }
        _threads.Clear();
    }

    public void Dispose() => Resume();

    private const uint SnapshotThread = 0x00000004;
    private const uint ThreadSuspendResume = 0x0002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    private sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeThreadHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(SafeThreadHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeThreadHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

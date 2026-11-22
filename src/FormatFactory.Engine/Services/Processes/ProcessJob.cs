using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FormatFactoryWinUI.Services;

internal sealed class ProcessJob : IDisposable
{
    private readonly SafeJobHandle _handle;
    private int _hasAssignedProcess;
    private int _disposed;

    private ProcessJob(SafeJobHandle handle) => _handle = handle;

    public static ProcessJob? TryCreate()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var safeHandle = new SafeJobHandle(handle);
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitFlags.KillOnJobClose
            }
        };
        if (!SetInformationJobObject(
                safeHandle,
                JobObjectInformationClass.ExtendedLimitInformation,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            safeHandle.Dispose();
            return null;
        }

        return new ProcessJob(safeHandle);
    }

    public bool TryAssign(Process process, bool applyBackgroundPolicy = true)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            if (process.HasExited || !AssignProcessToJobObject(_handle, process.Handle))
            {
                return false;
            }

            Volatile.Write(ref _hasAssignedProcess, 1);
            if (applyBackgroundPolicy)
            {
                ProcessResourcePolicy.ApplyBackgroundPolicy(process);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryTerminate(uint exitCode = 1)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _hasAssignedProcess) == 0)
        {
            return false;
        }

        try
        {
            return TerminateJobObject(_handle, exitCode);
        }
        catch (ObjectDisposedException)
        {
            // Dispose may win the race after the state check; callers can fall back to Process.Kill.
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _handle.Dispose();
    }

    [Flags]
    private enum JobObjectLimitFlags : uint
    {
        KillOnJobClose = 0x00002000
    }

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobObjectLimitFlags LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation jobObjectInformation,
        uint jobObjectInformationLength);

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

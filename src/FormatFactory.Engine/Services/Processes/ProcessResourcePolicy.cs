using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FormatFactoryWinUI.Services;

public enum FileIoPriority
{
    VeryLow = 0,
    Low = 1,
    Normal = 2
}

public readonly record struct ProcessResourcePolicyResult(
    bool EcoQosApplied,
    bool BelowNormalPriorityApplied);

/// <summary>Best-effort native resource policy for long-running conversion work.</summary>
public static class ProcessResourcePolicy
{
    public static ProcessResourcePolicyResult ApplyBackgroundPolicy(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows() || process.HasExited)
        {
            return default;
        }

        var ecoQos = false;
        var belowNormal = false;
        try
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ProcessPowerThrottlingExecutionSpeed,
                StateMask = ProcessPowerThrottlingExecutionSpeed
            };
            ecoQos = SetProcessInformation(
                process.Handle,
                ProcessInformationClass.ProcessPowerThrottling,
                ref state,
                checked((uint)Marshal.SizeOf<ProcessPowerThrottlingState>()));
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            belowNormal = SetPriorityClass(process.Handle, BelowNormalPriorityClass);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        NativeDiagnostics.Log.ProcessPolicyApplied(
            TryGetProcessId(process),
            ecoQos,
            belowNormal);
        return new ProcessResourcePolicyResult(ecoQos, belowNormal);
    }

    public static bool TrySetFileIoPriority(FileStream stream, FileIoPriority priority)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return TrySetFileIoPriority(stream.SafeFileHandle, priority);
    }

    public static bool TrySetFileIoPriority(SafeFileHandle handle, FileIoPriority priority)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsWindows() || handle.IsInvalid || handle.IsClosed)
        {
            return false;
        }

        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        var information = new FileIoPriorityHintInfo { PriorityHint = priority };
        return SetFileInformationByHandle(
            handle,
            FileInfoByHandleClass.FileIoPriorityHintInfo,
            ref information,
            checked((uint)Marshal.SizeOf<FileIoPriorityHintInfo>()));
    }

    private static int TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    private const uint BelowNormalPriorityClass = 0x00004000;

    private enum ProcessInformationClass
    {
        ProcessPowerThrottling = 4
    }

    private enum FileInfoByHandleClass
    {
        FileIoPriorityHintInfo = 12
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIoPriorityHintInfo
    {
        public FileIoPriority PriorityHint;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        nint process,
        ProcessInformationClass informationClass,
        ref ProcessPowerThrottlingState information,
        uint informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(nint process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileIoPriorityHintInfo fileInformation,
        uint bufferSize);
}

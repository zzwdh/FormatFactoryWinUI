using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

internal readonly record struct ApplicationRecoveryRegistrationResult(
    int RestartResult,
    int RecoveryResult)
{
    public bool RestartRegistered => RestartResult == 0;
    public bool RecoveryRegistered => RecoveryResult == 0;
}

internal delegate int ApplicationRecoveryCallback(nint parameter);

internal interface IApplicationRecoveryPlatform
{
    int RegisterRestart(string commandLine, uint flags);

    int UnregisterRestart();

    int RegisterRecovery(
        ApplicationRecoveryCallback callback,
        nint parameter,
        uint pingIntervalMilliseconds,
        uint flags);

    int UnregisterRecovery();

    void RecoveryFinished(bool success);
}

internal sealed class ApplicationRecoveryRegistrar
{
    private readonly object _gate = new();
    private readonly IApplicationRecoveryPlatform _platform;
    private readonly Action _checkpoint;
    private readonly ApplicationRecoveryCallback _callback;
    private bool _restartRegistered;
    private bool _recoveryRegistered;

    public ApplicationRecoveryRegistrar(
        IApplicationRecoveryPlatform platform,
        Action checkpoint)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        _callback = OnRecovery;
    }

    public ApplicationRecoveryRegistrationResult Register(
        string restartCommandLine,
        uint pingIntervalMilliseconds = 5000)
    {
        ArgumentNullException.ThrowIfNull(restartCommandLine);
        if (pingIntervalMilliseconds == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pingIntervalMilliseconds));
        }

        lock (_gate)
        {
            var restartResult = _restartRegistered
                ? 0
                : _platform.RegisterRestart(restartCommandLine, 0);
            _restartRegistered |= restartResult == 0;
            var recoveryResult = _recoveryRegistered
                ? 0
                : _platform.RegisterRecovery(
                    _callback,
                    0,
                    pingIntervalMilliseconds,
                    0);
            _recoveryRegistered |= recoveryResult == 0;
            NativeDiagnostics.Log.RecoveryRegistrationChanged(
                _restartRegistered || _recoveryRegistered,
                restartResult,
                recoveryResult);
            return new ApplicationRecoveryRegistrationResult(restartResult, recoveryResult);
        }
    }

    public void Unregister()
    {
        lock (_gate)
        {
            if (_recoveryRegistered)
            {
                _platform.UnregisterRecovery();
                _recoveryRegistered = false;
            }
            if (_restartRegistered)
            {
                _platform.UnregisterRestart();
                _restartRegistered = false;
            }
            NativeDiagnostics.Log.RecoveryRegistrationChanged(false, 0, 0);
        }
    }

    private int OnRecovery(nint parameter)
    {
        var success = false;
        try
        {
            _checkpoint();
            success = true;
        }
        catch
        {
            // Windows must still be told that the recovery callback has finished.
        }
        finally
        {
            _platform.RecoveryFinished(success);
        }
        return 0;
    }
}

internal sealed class NativeApplicationRecoveryPlatform : IApplicationRecoveryPlatform
{
    public int RegisterRestart(string commandLine, uint flags) =>
        OperatingSystem.IsWindows()
            ? RegisterApplicationRestart(commandLine, flags)
            : -1;

    public int UnregisterRestart() =>
        OperatingSystem.IsWindows() ? UnregisterApplicationRestart() : -1;

    public int RegisterRecovery(
        ApplicationRecoveryCallback callback,
        nint parameter,
        uint pingIntervalMilliseconds,
        uint flags) =>
        OperatingSystem.IsWindows()
            ? RegisterApplicationRecoveryCallback(
                callback,
                parameter,
                pingIntervalMilliseconds,
                flags)
            : -1;

    public int UnregisterRecovery() =>
        OperatingSystem.IsWindows() ? UnregisterApplicationRecoveryCallback() : -1;

    public void RecoveryFinished(bool success)
    {
        if (OperatingSystem.IsWindows())
        {
            ApplicationRecoveryFinished(success);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string commandLine, uint flags);

    [DllImport("kernel32.dll")]
    private static extern int UnregisterApplicationRestart();

    [DllImport("kernel32.dll")]
    private static extern int RegisterApplicationRecoveryCallback(
        ApplicationRecoveryCallback callback,
        nint parameter,
        uint pingInterval,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern int UnregisterApplicationRecoveryCallback();

    [DllImport("kernel32.dll")]
    private static extern void ApplicationRecoveryFinished(
        [MarshalAs(UnmanagedType.Bool)] bool success);
}

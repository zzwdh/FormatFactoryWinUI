using System.Diagnostics;

namespace FormatFactoryWinUI.Services;

internal sealed class ActiveConversionProcess(
    Process process,
    ProcessJob? job = null,
    Func<int, IDisposable?>? suspendProcess = null) : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Func<int, IDisposable?> _suspendProcess = suspendProcess ?? ProcessThreadSuspension.TrySuspend;
    private IDisposable? _suspension;
    private bool _disposed;
    private bool _terminationRequested;

    public bool TrySuspend()
    {
        lock (_syncRoot)
        {
            if (_disposed || _terminationRequested || _suspension is not null)
            {
                return false;
            }

            try
            {
                if (process.HasExited)
                {
                    return false;
                }

                _suspension = _suspendProcess(process.Id);
                return _suspension is not null;
            }
            catch
            {
                // The process may exit while its threads are being inspected.
                return false;
            }
        }
    }

    public bool TryResume()
    {
        lock (_syncRoot)
        {
            if (_disposed || _terminationRequested || _suspension is null)
            {
                return false;
            }

            return ResumeCore();
        }
    }

    public void TryTerminate()
    {
        lock (_syncRoot)
        {
            if (_disposed || _terminationRequested)
            {
                return;
            }

            // Reject a later pause while the OS is completing termination.
            _terminationRequested = true;
            ResumeCore();
            try
            {
                if (job?.TryTerminate() != true && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process may have exited naturally.
            }
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ResumeCore();
            job?.Dispose();
        }
    }

    private bool ResumeCore()
    {
        var suspension = _suspension;
        _suspension = null;
        try
        {
            suspension?.Dispose();
            return true;
        }
        catch
        {
            // A failed resume must not prevent termination or job cleanup.
            return false;
        }
    }
}

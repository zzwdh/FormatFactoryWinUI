using System.Diagnostics;

namespace FormatFactoryWinUI.Services;

internal enum ProcessLifetimeSignal
{
    CallerCancellationWait, TimeoutWait, PipeFailure, CompletionJobCleanup,
    CancellationJobCleanup, PipeDrainTimeout, DisposeCleanup
}

/// <summary>Owns one external process, its descendants and all redirected-pipe shutdown.</summary>
internal sealed class ExternalProcessLifetime : IDisposable
{
    private readonly Action<ProcessLifetimeSignal>? _observe;

    private ExternalProcessLifetime(Process process, ProcessJob? job, Action<ProcessLifetimeSignal>? observe)
    {
        Process = process;
        Job = job;
        _observe = observe;
        // The handshake and final pipe drain can both observe a short-lived worker.
        // Share one exit subscription so cancelling startup cannot detach the final waiter.
        Exited = process.WaitForExitAsync();
    }

    public Task Exited { get; }
    public Process Process { get; }
    public ProcessJob? Job { get; }

    public static ExternalProcessLifetime Start(ProcessStartInfo startInfo, CancellationToken cancellationToken,
        Action<ProcessLifetimeSignal>? observe = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        ProcessJob? job = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException(LocalizationService.T(
                "ConversionUiCannotStartExternalTool", Path.GetFileName(startInfo.FileName)));
            job = ProcessJob.TryCreate();
            if (job is not null && !job.TryAssign(process))
            {
                job.Dispose();
                job = null;
            }
            return new ExternalProcessLifetime(process, job, observe);
        }
        catch
        {
            job?.Dispose();
            ProcessTermination.TryTerminate(() => { if (!process.HasExited) process.Kill(entireProcessTree: true); });
            process.Dispose();
            throw;
        }
    }

    public async Task WaitAndDrainAsync(TimeSpan timeout, CancellationToken cancellationToken, params Task[] pipeTasks)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var guardedPipes = pipeTasks.Select(TerminateOnPipeFailureAsync).ToArray();
        try
        {
            await Exited.WaitAsync(waitSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Observe(cancellationToken.IsCancellationRequested
                ? ProcessLifetimeSignal.CallerCancellationWait : ProcessLifetimeSignal.TimeoutWait);
            Terminate();
            await TerminateAndDrainAsync(guardedPipes).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(LocalizationService.T("GeneralServiceProcessTimeout",
                timeout.TotalSeconds, Path.GetFileName(Process.StartInfo.FileName)));
        }

        // A parent may exit while a descendant still owns its redirected pipes.
        Observe(ProcessLifetimeSignal.CompletionJobCleanup);
        Job?.Dispose();
        try { await DrainCompletedAsync(Process, guardedPipes).ConfigureAwait(false); }
        catch (TimeoutException) { Observe(ProcessLifetimeSignal.PipeDrainTimeout); throw; }
    }

    private async Task TerminateOnPipeFailureAsync(Task pipeTask)
    {
        try { await pipeTask.ConfigureAwait(false); }
        catch { Observe(ProcessLifetimeSignal.PipeFailure); Terminate(); throw; }
    }

    public void Terminate()
    {
        ProcessTermination.TryTerminate(() =>
        {
            if (Job?.TryTerminate() != true && !Process.HasExited) Process.Kill(entireProcessTree: true);
        });
    }

    private async Task TerminateAndDrainAsync(Task[] pipeTasks)
    {
        try { await Exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { /* Preserve cancellation/timeout. */ }
        Observe(ProcessLifetimeSignal.CancellationJobCleanup);
        Job?.Dispose();
        try { await Task.WhenAll(pipeTasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* Preserve cancellation/timeout. */ }
        ClosePipes(Process);
        // Closing a pipe may complete a pending read after the bounded wait ends.
        _ = ObservePipesAsync(pipeTasks);
    }

    internal static async Task DrainCompletedAsync(Process process, params Task[] pipeTasks)
    {
        try { await Task.WhenAll(pipeTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException ex)
        {
            ClosePipes(process);
            _ = ObservePipesAsync(pipeTasks);
            throw new TimeoutException(LocalizationService.T("GeneralServiceProcessPipesTimeout",
                Path.GetFileName(process.StartInfo.FileName)), ex);
        }
    }

    private static async Task ObservePipesAsync(Task[] pipeTasks)
    {
        try { await Task.WhenAll(pipeTasks).ConfigureAwait(false); }
        catch { /* The original failure is already reported to the caller. */ }
    }

    private static void ClosePipes(Process process)
    {
        try { if (process.StartInfo.RedirectStandardInput) process.StandardInput.Close(); } catch { }
        try { if (process.StartInfo.RedirectStandardOutput) process.StandardOutput.Close(); } catch { }
        try { if (process.StartInfo.RedirectStandardError) process.StandardError.Close(); } catch { }
    }

    public void Dispose()
    {
        Observe(ProcessLifetimeSignal.DisposeCleanup);
        Terminate();
        Job?.Dispose();
        Process.Dispose();
    }

    private void Observe(ProcessLifetimeSignal signal)
    {
        try { _observe?.Invoke(signal); }
        catch (Exception diagnosticError) { Debug.WriteLine(diagnosticError); }
    }
}

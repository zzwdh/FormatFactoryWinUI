using System.Text.Json;

namespace FormatFactoryWinUI.Services;

internal sealed record SchedulingCandidate(Guid Id, ConversionWorkload? Cost, WorkloadEstimate? Source);
internal sealed record SchedulingGrant(Guid Id, ConversionWorkload Cost);
internal sealed record SchedulingDecision(bool Accepted, Guid[]? Ordered, SchedulingGrant[] Grants);

internal static class ConversionScheduler
{
    private static readonly object Gate = new();
    private static ExternalProcessLifetime? _session;

    internal static SchedulingDecision Schedule(IEnumerable<SchedulingCandidate> pending,
        IEnumerable<ConversionWorkload> running, int parallelLimit, ConversionResourceSnapshot resources,
        IReadOnlyList<Guid>? order = null, IEnumerable<Guid>? unavailable = null) =>
        Decide<SchedulingDecision>(new
        {
            Action = "Schedule", Pending = pending, Running = running, ParallelLimit = parallelLimit,
            Total = resources.TotalMemoryBytes, Available = resources.AvailableMemoryBytes,
            Order = order, Unavailable = unavailable ?? []
        });

    // Requests contain the host's current snapshot. Retrying the exact frame on a
    // fresh worker cannot reserve a second slot or replay a conversion.
    internal static T Decide<T>(object request)
    {
        var frame = JsonSerializer.Serialize(request);
        lock (Gate)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _session ??= ConversionBackend.Start("scheduler", CancellationToken.None);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    return ExchangeAsync<T>(_session, frame, timeout.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _session?.Dispose();
                    _session = null;
                    if (attempt > 0 && ex is OperationCanceledException) throw new TimeoutException("Conversion scheduler did not respond.", ex);
                    if (attempt > 0 || ex is not (IOException or OperationCanceledException)) throw;
                }
            }
        }
    }

    private static async Task<T> ExchangeAsync<T>(ExternalProcessLifetime session, string frame, CancellationToken token)
    {
        var process = session.Process;
        await process.StandardInput.WriteLineAsync(frame.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        var response = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)
            ?? throw new IOException("The conversion scheduler closed its pipe.");
        return JsonSerializer.Deserialize<T>(response) ?? throw new InvalidDataException("Missing scheduler decision.");
    }
}

using System.Diagnostics;
using System.Threading.Channels;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public enum TelemetryPage
{
    Home, Queue, Video, Audio, Image, Document, Ebook, Disc, Automation, Settings, History, Advanced
}

public enum TelemetryConversionResult { Succeeded, Failed, Cancelled }

/// <summary>Two independent, bounded background senders. Inactive until persisted preferences are applied.</summary>
public sealed class TelemetryService : IDisposable
{
    internal const int UsageQueueCapacity = 32;
    private readonly object _gate = new();
    private readonly FeedbackReportService _reports;
    private readonly string _appVersion;
    private readonly Func<TelemetryUsageSample, CancellationToken, Task>? _sendUsage;
    private readonly Func<FeedbackReport, CancellationToken, Task> _sendError;
    private readonly FeedbackDeliveryService? _delivery;
    private readonly SentryUsageTelemetry? _sentryUsage;
    private readonly Channel<UsageJob> _usage = Channel.CreateBounded<UsageJob>(new BoundedChannelOptions(UsageQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = false, AllowSynchronousContinuations = false
    });
    private readonly Channel<ErrorJob> _errors = Channel.CreateBounded<ErrorJob>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = false, AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _usageEpoch = new();
    private CancellationTokenSource _errorEpoch = new();
    private bool _usageEnabled;
    private bool _errorsEnabled;
    private bool _disposed;
    private int _usageGeneration;
    private int _errorGeneration;

    public TelemetryService(FeedbackReportService reports, FeedbackConfiguration configuration, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(configuration);
        _reports = reports;
        _appVersion = SafeVersion(appVersion);
        _delivery = new FeedbackDeliveryService();
        _sentryUsage = new SentryUsageTelemetry(configuration, _appVersion);
        _sendError = async (report, token) => { await _delivery.SendAsync(configuration, report, token); };
        Completion = Task.WhenAll(Task.Run(UsageLoopAsync), Task.Run(ErrorLoopAsync));
    }

    internal TelemetryService(FeedbackReportService reports, string appVersion,
        Func<TelemetryUsageSample, CancellationToken, Task> sendUsage,
        Func<FeedbackReport, CancellationToken, Task> sendError)
    {
        _reports = reports;
        _appVersion = SafeVersion(appVersion);
        _sendUsage = sendUsage;
        _sendError = sendError;
        Completion = Task.WhenAll(Task.Run(UsageLoopAsync), Task.Run(ErrorLoopAsync));
    }

    internal Task Completion { get; }

    /// <summary>Call only after the actual saved settings are loaded. Disabling immediately cancels that sender.</summary>
    public void Configure(bool usageEnabled, bool automaticErrorsEnabled)
    {
        CancellationTokenSource? cancelUsage = null;
        CancellationTokenSource? cancelErrors = null;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_usageEnabled != usageEnabled)
            {
                _usageEnabled = usageEnabled;
                _usageGeneration++;
                cancelUsage = _usageEpoch;
                _usageEpoch = new CancellationTokenSource();
                while (_usage.Reader.TryRead(out _)) { }
                if (_sentryUsage is not null)
                    _usage.Writer.TryWrite(new UsageJob(null, _usageGeneration, _usageEpoch.Token));
            }
            if (_errorsEnabled != automaticErrorsEnabled)
            {
                _errorsEnabled = automaticErrorsEnabled;
                _errorGeneration++;
                cancelErrors = _errorEpoch;
                _errorEpoch = new CancellationTokenSource();
                while (_errors.Reader.TryRead(out _)) { }
                if (_errorsEnabled)
                {
                    // Once on startup/enabling; failed reports stay local without an automatic retry loop.
                    _errors.Writer.TryWrite(new ErrorJob(_errorGeneration, _errorEpoch.Token));
                }
            }
        }
        CancelAndDispose(cancelUsage);
        CancelAndDispose(cancelErrors);
    }

    public void TrackAppLaunch() => EnqueueUsage(new TelemetryUsageSample("app_launch"));

    internal void TrackStartupReady(double elapsedMilliseconds) =>
        EnqueueUsage(new TelemetryUsageSample("startup_ready", ElapsedMilliseconds: elapsedMilliseconds));

    public void TrackPageVisit(TelemetryPage page)
    {
        if (Enum.IsDefined(page))
            EnqueueUsage(new TelemetryUsageSample("page_visit", Page: page.ToString().ToLowerInvariant()));
    }

    public void TrackConversionResult(TelemetryConversionResult result, MediaKind category, string? outputFormat)
    {
        if (!Enum.IsDefined(result) || !Enum.IsDefined(category))
            return;
        EnqueueUsage(new TelemetryUsageSample("conversion_result", Result: result.ToString().ToLowerInvariant(),
            Category: category.ToString().ToLowerInvariant(), OutputFormat: SafeOutputFormat(outputFormat)));
    }

    internal TelemetryConversionTrace? StartConversion(MediaKind category, string? outputFormat)
    {
        if (!Enum.IsDefined(category))
            return null;
        lock (_gate)
            return !_disposed && _usageEnabled
                ? _sentryUsage?.StartConversion(category.ToString().ToLowerInvariant(), SafeOutputFormat(outputFormat))
                : null;
    }

    internal static string SafeOutputFormat(string? outputFormat)
    {
        // A path or unknown arbitrary string never becomes an uploaded dimension.
        var descriptor = outputFormat is { Length: <= 128 } ? FormatCatalog.FindById(outputFormat) : null;
        return descriptor is { CanExport: true } ? descriptor.Id : "other";
    }

    /// <summary>Always keeps a bounded local error marker, including while automatic reporting is disabled.</summary>
    public void ReportError(string message, string? taskLogPath = null, Exception? exception = null)
        => ReportError(message, taskLogPath, exception, null);

    internal void ReportError(string message, string? taskLogPath, Exception? exception, ErrorCorrelationFacts? correlation)
    {
        if (exception is null) _reports.RecordError(message, taskLogPath, correlation);
        else _reports.RecordException(exception, taskLogPath, correlation);
        lock (_gate)
        {
            if (!_disposed && _errorsEnabled)
            {
                // One queued job represents the latest persisted error; bursts cannot create an unbounded backlog.
                _errors.Writer.TryWrite(new ErrorJob(_errorGeneration, _errorEpoch.Token));
            }
        }
    }

    private void EnqueueUsage(TelemetryUsageSample sample)
    {
        lock (_gate)
        {
            if (!_disposed && _usageEnabled)
                _usage.Writer.TryWrite(new UsageJob(sample, _usageGeneration, _usageEpoch.Token));
        }
    }

    private async Task UsageLoopAsync()
    {
        try
        {
            await foreach (var job in _usage.Reader.ReadAllAsync(_shutdown.Token))
            {
                bool enabled;
                lock (_gate)
                {
                    if (_disposed || job.Generation != _usageGeneration)
                        continue;
                    enabled = _usageEnabled;
                }
                try
                {
                    if (_sentryUsage is not null)
                    {
                        if (job.Sample is null)
                            _sentryUsage.Configure(enabled ? job.Epoch : null);
                        else if (enabled && !job.Epoch.IsCancellationRequested)
                            _sentryUsage.Record(job.Sample);
                        continue;
                    }
                    if (!enabled || job.Sample is null)
                        continue;
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(job.Epoch, _shutdown.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    await _sendUsage!(job.Sample, cancellation.Token);
                }
                catch (Exception ex)
                {
                    // Do not use application error reporting here: that would recursively report telemetry failures.
                    Debug.WriteLine($"Usage telemetry stopped or failed: {ex.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { _sentryUsage?.Dispose(); }
    }

    private async Task ErrorLoopAsync()
    {
        try
        {
            await foreach (var job in _errors.Reader.ReadAllAsync(_shutdown.Token))
            {
                lock (_gate)
                {
                    if (_disposed || !_errorsEnabled || job.Generation != _errorGeneration)
                        continue;
                }
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(job.Epoch, _shutdown.Token);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!_reports.HasPendingError)
                        continue;
                    LogService.FlushApp();
                    using var report = await _reports.CreateAsync("", includeLogs: true, _appVersion, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    // A manual report can acknowledge the marker between the check and snapshot creation.
                    if (report.ErrorId is null || report.ErrorMessage is null)
                        continue;
                    await _sendError(report, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        if (!_disposed && _errorsEnabled && job.Generation == _errorGeneration)
                            _reports.ClearPendingError(report);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Automatic error reporting stopped or failed: {ex.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        CancellationTokenSource usageEpoch;
        CancellationTokenSource errorEpoch;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _usageEnabled = false;
            _errorsEnabled = false;
            usageEpoch = _usageEpoch;
            errorEpoch = _errorEpoch;
            _usage.Writer.TryComplete();
            _errors.Writer.TryComplete();
            while (_usage.Reader.TryRead(out _)) { }
            while (_errors.Reader.TryRead(out _)) { }
        }
        CancelQuietly(usageEpoch);
        CancelQuietly(errorEpoch);
        CancelQuietly(_shutdown);
        // No network flush or worker wait on the caller/UI thread.
        _ = Completion.ContinueWith(_ =>
        {
            _delivery?.Dispose();
            usageEpoch.Dispose();
            errorEpoch.Dispose();
            _shutdown.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null)
            return;
        CancelQuietly(source);
        source.Dispose();
    }

    private static void CancelQuietly(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (AggregateException ex)
        {
            Debug.WriteLine($"Telemetry cancellation callback failed: {ex.GetType().Name}");
        }
    }

    private static string SafeVersion(string value) => Version.TryParse(value, out var version) ? version.ToString() : "unknown";

    // A job without a sample applies the corresponding preference on the background worker.
    private sealed record UsageJob(TelemetryUsageSample? Sample, int Generation, CancellationToken Epoch);
    private sealed record ErrorJob(int Generation, CancellationToken Epoch);
}

internal sealed record TelemetryUsageSample(string Event, string? Page = null, string? Result = null,
    string? Category = null, string? OutputFormat = null, double? ElapsedMilliseconds = null);

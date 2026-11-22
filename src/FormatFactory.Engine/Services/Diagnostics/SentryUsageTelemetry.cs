using System.Diagnostics;
using FormatFactoryWinUI.Models;
using Sentry;

namespace FormatFactoryWinUI.Services;

/// <summary>Owns the SDK for explicit operational signals collected with the usage preference.</summary>
internal sealed class SentryUsageTelemetry(
    FeedbackConfiguration configuration,
    string appVersion,
    Func<HttpMessageHandler>? createHandler = null,
    double traceSampleRate = 0.10,
    double profileSampleRate = 0.10) : IDisposable
{
    private const string ConversionName = "FluentFormatConverter conversion";
    private const string StartupLog = "FluentFormatConverter startup ready";
    private const string ConversionLog = "FluentFormatConverter conversion finished";
    private readonly object _gate = new();
    private readonly FeedbackDeliveryService.SentryDestination? _destination =
        FeedbackDeliveryService.ParseSentryDestination(configuration.SentryDsn);
    private IDisposable? _sdk;
    private CancellationToken _epoch;

    // Called by the telemetry worker. Closing the SDK and its profiler can block briefly.
    internal void Configure(CancellationToken? epoch)
    {
        IDisposable? previous;
        lock (_gate)
        {
            if (_sdk is not null && epoch == _epoch && !_epoch.IsCancellationRequested)
                return;
            previous = _sdk;
            _sdk = null;
        }
        previous?.Dispose();

        if (epoch is not { IsCancellationRequested: false } enabled || _destination is not { } destination)
            return;

        var options = new SentryOptions
        {
            Dsn = destination.Dsn,
            Release = appVersion,
            Environment = FeedbackDeliveryService.SentryEnvironment,
            // Global scope mode installs a persistent SDK-generated user ID even with PII off.
            IsGlobalModeEnabled = false,
            SendDefaultPii = false,
            IsEnvironmentUser = false,
            AutoSessionTracking = false,
            DisableFileWrite = true,
            CaptureFailedRequests = false,
            DisableSentryHttpMessageHandler = true,
            SendClientReports = false,
            MaxBreadcrumbs = 0,
            ShutdownTimeout = TimeSpan.Zero,
            TracesSampleRate = traceSampleRate,
            ProfilesSampleRate = profileSampleRate,
            CreateHttpMessageHandler = () => new UsageHttpHandler(destination.Endpoint, enabled,
                createHandler?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }),
            ConfigureClient = client => client.Timeout = TimeSpan.FromSeconds(30)
        };
        options.DisableAppDomainUnhandledExceptionCapture();
        options.DisableUnobservedTaskExceptionCapture();
        options.DisableWinUiUnhandledExceptionIntegration();
        options.DisableAppDomainProcessExitFlush();
        options.DisableDiagnosticSourceIntegration();
        options.DisableSystemDiagnosticsMetricsIntegration();
        options.TracePropagationTargets.Clear();
        options.SetBeforeSend(_ => null);
        options.SetBeforeSendFeedback(_ => null);
        options.SetBeforeSendTransaction(transaction => FilterTransaction(transaction, enabled));
        options.SetBeforeSendLog(log =>
            !enabled.IsCancellationRequested && log.Message is StartupLog or ConversionLog ? log : null);
        options.SetBeforeSendMetric(metric => !enabled.IsCancellationRequested && metric.Name is
            "formatfactory.app_launch" or "formatfactory.page_visit" or "formatfactory.conversion_result"
            or "formatfactory.startup_ready_duration" or "formatfactory.conversion_duration" ? metric : null);
        if (profileSampleRate > 0 && OperatingSystem.IsWindows())
            options.AddProfilingIntegration();

        // Publish the session only after initialization. The handler independently enforces the same
        // epoch for payloads already batched or being sent when the user turns telemetry off.
        if (enabled.IsCancellationRequested)
            return;
        var sdk = SentrySdk.Init(options);
        lock (_gate)
        {
            _epoch = enabled;
            _sdk = sdk;
        }
    }

    internal void Record(TelemetryUsageSample sample)
    {
        lock (_gate)
        {
            if (!IsActive)
                return;

            var attributes = new Dictionary<string, object> { ["app.version"] = appVersion };
            switch (sample.Event)
            {
                case "app_launch":
                    SentrySdk.Metrics.EmitCounter("formatfactory.app_launch", 1, attributes);
                    break;
                case "page_visit" when IsDimension<TelemetryPage>(sample.Page):
                    attributes["page"] = sample.Page!;
                    SentrySdk.Metrics.EmitCounter("formatfactory.page_visit", 1, attributes);
                    break;
                case "conversion_result" when IsDimension<MediaKind>(sample.Category)
                    && IsDimension<TelemetryConversionResult>(sample.Result):
                    attributes["category"] = sample.Category!;
                    attributes["result"] = sample.Result!;
                    attributes["output_format"] = TelemetryService.SafeOutputFormat(sample.OutputFormat);
                    SentrySdk.Metrics.EmitCounter("formatfactory.conversion_result", 1, attributes);
                    break;
                case "startup_ready" when sample.ElapsedMilliseconds is { } elapsed && IsDuration(elapsed):
                    SentrySdk.Metrics.EmitDistribution("formatfactory.startup_ready_duration", elapsed,
                        MeasurementUnit.Duration.Millisecond, attributes);
                    SentrySdk.Logger.LogInfo(log => log.SetAttribute("duration_ms", elapsed), StartupLog);
                    break;
            }
        }
    }

    internal TelemetryConversionTrace? StartConversion(string category, string format)
    {
        lock (_gate)
        {
            if (!IsActive || !IsDimension<MediaKind>(category))
                return null;
            try
            {
                var transaction = SentrySdk.StartTransaction(ConversionName, "conversion.execute");
                return new(this, transaction, _epoch, category, TelemetryService.SafeOutputFormat(format));
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Sentry conversion trace startup: {exception.GetType().Name}");
                return null;
            }
        }
    }

    internal void FinishConversion(ITransactionTracer transaction, CancellationToken epoch, string category,
        string format, TelemetryConversionResult? result, double elapsed)
    {
        lock (_gate)
        {
            try
            {
                if (!IsActive || epoch != _epoch || result is not { } outcome || !Enum.IsDefined(outcome)
                    || !IsDuration(elapsed))
                {
                    transaction.Name = "Discarded conversion";
                    transaction.Finish(SpanStatus.Cancelled);
                    return;
                }

                var resultValue = outcome.ToString().ToLowerInvariant();
                transaction.SetTag("category", category);
                transaction.SetTag("output_format", format);
                transaction.SetTag("result", resultValue);
                transaction.Finish(outcome switch
                {
                    TelemetryConversionResult.Succeeded => SpanStatus.Ok,
                    TelemetryConversionResult.Cancelled => SpanStatus.Cancelled,
                    _ => SpanStatus.InternalError
                });

                var attributes = new Dictionary<string, object>
                {
                    ["app.version"] = appVersion,
                    ["category"] = category,
                    ["output_format"] = format,
                    ["result"] = resultValue
                };
                SentrySdk.Metrics.EmitDistribution("formatfactory.conversion_duration", elapsed,
                    MeasurementUnit.Duration.Millisecond, attributes);
                void SetLogAttributes(SentryLog log)
                {
                    foreach (var (key, value) in attributes)
                        log.SetAttribute(key, value);
                    log.SetAttribute("duration_ms", elapsed);
                }
                if (outcome == TelemetryConversionResult.Failed)
                    SentrySdk.Logger.LogWarning(SetLogAttributes, ConversionLog);
                else
                    SentrySdk.Logger.LogInfo(SetLogAttributes, ConversionLog);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Sentry conversion telemetry: {exception.GetType().Name}");
            }
        }
    }

    private bool IsActive => _sdk is not null && !_epoch.IsCancellationRequested;

    internal Task FlushAsync()
    {
        lock (_gate)
            return IsActive ? SentrySdk.FlushAsync(TimeSpan.FromSeconds(5)) : Task.CompletedTask;
    }

    public void Dispose() => Configure(null);

    private static bool IsDuration(double elapsed) => double.IsFinite(elapsed) && elapsed >= 0;

    private static bool IsDimension<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var result) && Enum.IsDefined(result)
        && value == result.ToString().ToLowerInvariant();

    private static SentryTransaction? FilterTransaction(SentryTransaction transaction, CancellationToken epoch)
    {
        if (epoch.IsCancellationRequested || transaction.Name != ConversionName)
            return null;

        transaction.User = new SentryUser();
        transaction.Request = new SentryRequest();
        foreach (var key in transaction.Contexts.Keys.ToArray())
        {
            if (key is not ("trace" or "os" or "runtime"))
                transaction.Contexts.Remove(key);
        }
        return transaction;
    }

    private sealed class UsageHttpHandler(Uri endpoint, CancellationToken epoch, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            epoch.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Post || request.RequestUri != endpoint)
                throw new HttpRequestException("Unexpected destination for usage telemetry.");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(epoch, cancellationToken);
            return await base.SendAsync(request, cancellation.Token).ConfigureAwait(false);
        }
    }
}

internal sealed class TelemetryConversionTrace(SentryUsageTelemetry owner, ITransactionTracer transaction,
    CancellationToken epoch, string category, string format) : IDisposable
{
    private int _finished;

    internal void Finish(TelemetryConversionResult result, double elapsedMilliseconds)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
            owner.FinishConversion(transaction, epoch, category, format, result, elapsedMilliseconds);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
            owner.FinishConversion(transaction, epoch, category, format, null, 0);
    }
}

using System.Net.Http.Headers;
using System.Net.Mail;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

namespace FormatFactoryWinUI.Services;

public sealed record FeedbackConfiguration(Uri? UploadEndpoint, Uri? FeedbackUri, Uri? PrivacyUri,
    string? SentryDsn = null)
{
    public Uri? UploadDestination => FeedbackDeliveryService.ParseSentryDestination(SentryDsn)?.Endpoint ??
        (UploadEndpoint is { } endpoint && FeedbackDeliveryService.IsSafeHttps(endpoint) ? endpoint : null);
    public bool CanUpload => UploadDestination is not null;
}

/// <summary>Submits a bounded, sanitized report to the endpoint bundled with the application.</summary>
public sealed class FeedbackDeliveryService : IDisposable
{
    internal const long MaximumArchiveBytes = 1024 * 1024;
    private const int MaximumConfigurationBytes = 16 * 1024;
    private const int MaximumSentryResponseBytes = 4 * 1024;
    private const int MaximumEnvelopeBytes = 2 * 1024 * 1024;
    private const string SentrySdkName = "sentry.dotnet";
    private static readonly string SentrySdkVersion = GetSentrySdkVersion();
    private static readonly Regex FfmpegLoggerAddress = new(
        @"(\[[A-Za-z0-9_#:/\.\-]+ @ )(?:0x)?[0-9A-Fa-f]{8,16}(\])",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private readonly HttpClient _client;

    internal static string SentryEnvironment { get; } =
#if DEBUG
        "development";
#else
        "production";
#endif

    public FeedbackDeliveryService()
        : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
    }

    internal FeedbackDeliveryService(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static async Task<FeedbackConfiguration> LoadConfigurationAsync(string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return new FeedbackConfiguration(null, null, null);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaximumConfigurationBytes)
            throw new InvalidDataException("The feedback configuration is too large.");
        var values = await JsonSerializer.DeserializeAsync<ConfigurationValues>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
        return new FeedbackConfiguration(ParseHttps(values?.UploadEndpoint),
            ParseFeedback(values?.FeedbackUri), ParseHttps(values?.PrivacyUri),
            ParseSentryDestination(values?.SentryDsn)?.Dsn);
    }

    /// <summary>Submits this report. Sentry returns its accepted event ID.</summary>
    public async Task<string?> SendAsync(FeedbackConfiguration configuration, FeedbackReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(report);
        if (ParseSentryDestination(configuration.SentryDsn) is { } sentry)
            return await SendSentryAsync(sentry, report, cancellationToken);
        if (configuration.UploadEndpoint is { } endpoint)
        {
            await SendAsync(endpoint, report, cancellationToken);
            return null;
        }
        throw new InvalidOperationException("No feedback upload destination is configured.");
    }

    private async Task<string> SendSentryAsync(SentryDestination destination, FeedbackReport report,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var archiveBytes = await ReadArchiveAsync(report.ArchivePath, token);
        var eventId = Guid.NewGuid().ToString("N");
        // This delivery path sends only the sanitized report and requires a matching Sentry receipt.
        var sentryEvent = SentryEvent.FromJson(JsonSerializer.SerializeToElement(new
        {
            event_id = eventId, timestamp = report.CreatedAt,
            sdk = new { name = SentrySdkName, version = SentrySdkVersion }
        }));
        sentryEvent.Platform = "csharp";
        sentryEvent.Release = report.AppVersion;
        sentryEvent.Environment = SentryEnvironment;
        sentryEvent.Level = report.ErrorMessage is null ? SentryLevel.Info : SentryLevel.Error;
        if (report.ErrorMessage is { } error)
        {
            sentryEvent.Message = new SentryMessage { Message = error[..Math.Min(error.Length, 1000)], Formatted = error };
            sentryEvent.SetExtra("description", report.Description);
            sentryEvent.Fingerprint = ["formatfactory-user-error", CreateErrorFingerprint(error)];
        }
        else
        {
            // This appears under Sentry User Feedback, without introducing user identity or contact data.
            var message = string.IsNullOrWhiteSpace(report.Description) ? "FluentFormatConverter feedback report" : report.Description;
            sentryEvent.Contexts.Feedback = new SentryFeedback(message);
        }

        var eventItem = report.ErrorMessage is null
            ? EnvelopeItem.FromFeedback(sentryEvent) : EnvelopeItem.FromEvent(sentryEvent);
        var attachment = new SentryAttachment(AttachmentType.Default, new ByteAttachmentContent(archiveBytes),
            "feedback-report.zip", "application/zip");
        using var envelope = new Envelope(new Dictionary<string, object?>
        {
            ["event_id"] = eventId,
            ["dsn"] = destination.Dsn,
            ["sdk"] = new Dictionary<string, string> { ["name"] = SentrySdkName, ["version"] = SentrySdkVersion }
        }, [eventItem, EnvelopeItem.FromAttachment(attachment)]);
        var body = await SerializeEnvelopeAsync(envelope, token);
        token.ThrowIfCancellationRequested();
        using var request = CreateSentryRequest(destination, body);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await VerifySentryReceiptAsync(response, eventId, token);
        token.ThrowIfCancellationRequested();
        return eventId;
    }

    private static string CreateErrorFingerprint(string error)
    {
        // Only FFmpeg logger pointers vary between otherwise identical failures.
        // Preserve codec names, stream indices, error codes and the displayed report.
        var stableError = error;
        try { stableError = FfmpegLoggerAddress.Replace(error, "$1[address]$2"); }
        catch (RegexMatchTimeoutException) { }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stableError)));
    }

    private static string GetSentrySdkVersion()
    {
        var sdkAssembly = typeof(SentrySdk).Assembly;
        return sdkAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', 2)[0]
            ?? sdkAssembly.GetName().Version?.ToString(3)
            ?? "unknown";
    }

    private static async Task<byte[]> SerializeEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(envelope.Header), cancellationToken);
        await body.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken);
        // Envelope.SerializeAsync catches item exceptions and could silently omit an attachment.
        // Serializing each official item directly makes every failure/cancellation abort this submission.
        foreach (var item in envelope.Items)
        {
            await item.SerializeAsync(body, logger: null, cancellationToken);
            await body.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken);
            if (body.Length > MaximumEnvelopeBytes)
                throw new InvalidDataException("The feedback envelope is too large.");
        }
        return body.ToArray();
    }

    private static HttpRequestMessage CreateSentryRequest(SentryDestination destination, byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, destination.Endpoint);
        request.Headers.TryAddWithoutValidation("X-Sentry-Auth",
            $"Sentry sentry_version=7, sentry_client={SentrySdkName}/{SentrySdkVersion}, sentry_key={destination.PublicKey}");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
        return request;
    }

    private static async Task<byte[]> ReadArchiveAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length == 0 || stream.Length > MaximumArchiveBytes)
            throw new InvalidDataException("The feedback archive size is invalid.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    private static async Task VerifySentryReceiptAsync(HttpResponseMessage response, string eventId,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumSentryResponseBytes)
            throw new InvalidDataException("The Sentry response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[MaximumSentryResponseBytes + 1];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken);
            if (count == 0)
                break;
            read += count;
        }
        if (read > MaximumSentryResponseBytes)
            throw new InvalidDataException("The Sentry response is too large.");
        try
        {
            using var json = JsonDocument.Parse(bytes.AsMemory(0, read));
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String && Guid.TryParseExact(id.GetString(), "N", out _) &&
                string.Equals(id.GetString(), eventId, StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch (JsonException) { }
        throw new InvalidDataException("Sentry did not confirm receipt of this feedback report.");
    }

    public async Task SendAsync(Uri endpoint, FeedbackReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(report);
        if (!IsSafeHttps(endpoint))
            throw new ArgumentException("Feedback uploads require an HTTPS endpoint without credentials or a fragment.", nameof(endpoint));
        await using var stream = new FileStream(report.ArchivePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length == 0 || stream.Length > MaximumArchiveBytes)
            throw new InvalidDataException("The feedback archive size is invalid.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "report", "feedback-report.zip");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
    }

    internal static bool IsSafeHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && !string.IsNullOrWhiteSpace(uri.Host);

    internal static SentryDestination? ParseSentryDestination(string? value)
    {
        if (value is null || value.Length > 2048 || value.Contains('%') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrWhiteSpace(uri.UserInfo) || uri.UserInfo.Length > 128 ||
            !uri.UserInfo.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            return null;
        var path = uri.AbsolutePath;
        var lastSlash = path.LastIndexOf('/');
        var project = path[(lastSlash + 1)..];
        if (project.Length == 0 || project.Length > 32 || !project.All(char.IsAsciiDigit) || path.Contains('%'))
            return null;
        var endpoint = new UriBuilder(uri)
        {
            UserName = string.Empty, Password = string.Empty,
            Path = $"{path[..lastSlash]}/api/{project}/envelope/"
        }.Uri;
        return new SentryDestination(uri.AbsoluteUri, endpoint, uri.UserInfo);
    }

    private static Uri? ParseHttps(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        IsSafeHttps(uri) ? uri : null;

    private static Uri? ParseFeedback(string? value)
    {
        var https = ParseHttps(value);
        if (https is not null)
            return https;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeMailto &&
            string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.Query) &&
            !value!.Contains('\r') && !value.Contains('\n') && !value.Contains('%'))
        {
            var address = value[(value.IndexOf(':') + 1)..];
            if (MailAddress.TryCreate(address, out var mailAddress) &&
                string.Equals(address, mailAddress.Address, StringComparison.OrdinalIgnoreCase))
                return uri;
        }
        return null;
    }

    public void Dispose() => _client.Dispose();

    internal sealed record SentryDestination(string Dsn, Uri Endpoint, string PublicKey);

    private sealed record ConfigurationValues(string? UploadEndpoint, string? FeedbackUri, string? PrivacyUri, string? SentryDsn);
}

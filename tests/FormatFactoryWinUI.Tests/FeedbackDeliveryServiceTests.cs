using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FormatFactoryWinUI.Services;
using Sentry;
using Sentry.Protocol.Envelopes;

namespace FormatFactoryWinUI.Tests;

public sealed class FeedbackDeliveryServiceTests
{
    private const string TestDsn = "https://0123456789abcdef0123456789abcdef@sentry.example/123";

    [Fact]
    public async Task LoadConfigurationAsync_MissingFileDisablesAllRemoteActions()
    {
        using var directory = new TemporaryDirectory();

        var configuration = await FeedbackDeliveryService.LoadConfigurationAsync(Path.Combine(directory.Path, "missing.json"));

        Assert.Equal(new FeedbackConfiguration(null, null, null), configuration);
    }

    [Fact]
    public async Task LoadConfigurationAsync_ReadsHttpsEndpointsAndSimpleMailto()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "feedback-config.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            uploadEndpoint = "https://feedback.example/report",
            feedbackUri = "mailto:feedback@example.com",
            privacyUri = "https://feedback.example/privacy"
        }));

        var configuration = await FeedbackDeliveryService.LoadConfigurationAsync(path);

        Assert.Equal(new Uri("https://feedback.example/report"), configuration.UploadEndpoint);
        Assert.Equal(new Uri("mailto:feedback@example.com"), configuration.FeedbackUri);
        Assert.Equal(new Uri("https://feedback.example/privacy"), configuration.PrivacyUri);
    }

    [Theory]
    [InlineData("http://feedback.example/report")]
    [InlineData("https://user:password@feedback.example/report")]
    [InlineData("https://feedback.example/report#fragment")]
    [InlineData("file:///C:/private")]
    [InlineData("/relative")]
    public async Task LoadConfigurationAsync_RejectsUnsafeRemoteUris(string value)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "feedback-config.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            uploadEndpoint = value, feedbackUri = value, privacyUri = value
        }));

        var configuration = await FeedbackDeliveryService.LoadConfigurationAsync(path);

        Assert.Equal(new FeedbackConfiguration(null, null, null), configuration);
    }

    [Theory]
    [InlineData("mailto:feedback@example.com?body=secret")]
    [InlineData("mailto:feedback@example.com%0d%0aBcc:other@example.com")]
    [InlineData("mailto:feedback@example.com#fragment")]
    public async Task LoadConfigurationAsync_RejectsMailHeaderInjection(string value)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "feedback-config.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { feedbackUri = value }));

        var configuration = await FeedbackDeliveryService.LoadConfigurationAsync(path);

        Assert.Null(configuration.FeedbackUri);
    }

    [Fact]
    public async Task SendAsync_PostsZipAsReportWithoutCredentials()
    {
        using var directory = new TemporaryDirectory();
        using var report = await new FeedbackReportService(directory.Path).CreateAsync("feedback text", false, "1.0");
        var calls = 0;
        using var service = new FeedbackDeliveryService(new FakeHandler(async (request, cancellationToken) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri("https://feedback.example/report"), request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var file = Assert.Single(multipart);
            Assert.Equal("report", file.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("feedback-report.zip", file.Headers.ContentDisposition.FileName!.Trim('"'));
            Assert.Equal("application/zip", file.Headers.ContentType!.MediaType);
            var bytes = await file.ReadAsByteArrayAsync(cancellationToken);
            Assert.Equal(await File.ReadAllBytesAsync(report.ArchivePath, cancellationToken), bytes);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }));

        await service.SendAsync(new Uri("https://feedback.example/report"), report);

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(400)]
    [InlineData(500)]
    public async Task SendAsync_NonSuccessAndRedirectsAreFailures(int status)
    {
        using var directory = new TemporaryDirectory();
        using var report = await new FeedbackReportService(directory.Path).CreateAsync("", false, "1.0");
        var calls = 0;
        using var service = new FeedbackDeliveryService(new FakeHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Location = new Uri("https://other.example/upload");
            return Task.FromResult(response);
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SendAsync(new Uri("https://feedback.example/report"), report));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SendAsync_RejectsUnsafeEndpointAndOversizedReportBeforeNetworkCall()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "oversized.zip");
        await using (var stream = File.Create(path))
            stream.SetLength(FeedbackDeliveryService.MaximumArchiveBytes + 1);
        using var report = new FeedbackReport(path, "", 0);
        using var service = new FeedbackDeliveryService(new FakeHandler((_, _) =>
            throw new InvalidOperationException("Network must not be called.")));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(new Uri("http://feedback.example/report"), report));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SendAsync(new Uri("https://feedback.example/report"), report));
    }

    [Fact]
    public async Task SendAsync_PropagatesCancellationToHttpRequest()
    {
        using var directory = new TemporaryDirectory();
        using var report = await new FeedbackReportService(directory.Path).CreateAsync("", false, "1.0");
        using var cancellation = new CancellationTokenSource();
        using var service = new FeedbackDeliveryService(new FakeHandler(async (_, cancellationToken) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SendAsync(new Uri("https://feedback.example/report"), report, cancellation.Token));
    }

    [Fact]
    public async Task LoadConfigurationAsync_PrefersSentryAndDisplaysDestinationWithoutPublicKey()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "feedback-config.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            sentryDsn = TestDsn, uploadEndpoint = "https://feedback.example/report"
        }));

        var configuration = await FeedbackDeliveryService.LoadConfigurationAsync(path);

        Assert.Equal(TestDsn, configuration.SentryDsn);
        Assert.True(configuration.CanUpload);
        Assert.Equal(new Uri("https://sentry.example/api/123/envelope/"), configuration.UploadDestination);
        Assert.Equal(string.Empty, configuration.UploadDestination!.UserInfo);
    }

    [Theory]
    [InlineData("http://0123456789abcdef0123456789abcdef@sentry.example/123")]
    [InlineData("https://0123456789abcdef0123456789abcdef:private-secret@sentry.example/123")]
    [InlineData("https://sentry.example/123")]
    [InlineData("https://0123456789abcdef0123456789abcdef@sentry.example/123?token=secret")]
    [InlineData("https://0123456789abcdef0123456789abcdef@sentry.example/123#fragment")]
    [InlineData("https://0123456789abcdef0123456789abcdef@sentry.example/")]
    [InlineData("https://0123456789abcdef0123456789abcdef@sentry.example/project-name")]
    [InlineData("https://public%0d%0aheader@sentry.example/123")]
    [InlineData("https://0123456789abcdef0123456789abcdef@sentry.example/%31%32%33")]
    public async Task LoadConfigurationAsync_InvalidSentryDsnCannotEnableNetwork(string dsn)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "feedback-config.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { sentryDsn = dsn }));

        var configuration = await FeedbackDeliveryService.LoadConfigurationAsync(path);

        Assert.Null(configuration.SentryDsn);
        Assert.Null(configuration.UploadDestination);
        Assert.False(configuration.CanUpload);
    }

    [Fact]
    public async Task SendAsync_SentryErrorContainsOnlyPreviewedMetadataAndExactZipWithConfirmedReceipt()
    {
        using var directory = new TemporaryDirectory();
        var reporter = new FeedbackReportService(directory.Path);
        reporter.RecordError("codec failure token=private-token\nC:\\Users\\private-user\\file.mp4");
        using var report = await reporter.CreateAsync("please investigate email=person@example.com", true, "1.2.3.4");
        var sdkWasEnabled = SentrySdk.IsEnabled;
        var calls = 0;
        string? expectedReceipt = null;
        using var service = new FeedbackDeliveryService(new FakeHandler(async (request, cancellationToken) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri("https://sentry.example/api/123/envelope/"), request.RequestUri);
            Assert.Equal("application/x-sentry-envelope", request.Content!.Headers.ContentType!.MediaType);
            Assert.Null(request.Headers.Authorization);
            var auth = Assert.Single(request.Headers.GetValues("X-Sentry-Auth"));
            Assert.Contains("sentry_key=0123456789abcdef0123456789abcdef", auth);
            var payload = await ReadSentryEnvelopeAsync(request, cancellationToken);
            Assert.Equal("event", payload.Type);
            Assert.Equal(TestDsn, payload.Dsn);
            Assert.Equal(await File.ReadAllBytesAsync(report.ArchivePath, cancellationToken), payload.Attachment);
            Assert.Equal(report.ErrorMessage, payload.Event.GetProperty("logentry").GetProperty("formatted").GetString());
            Assert.Equal(report.Description, payload.Event.GetProperty("extra").GetProperty("description").GetString());
            Assert.Equal(report.AppVersion, payload.Event.GetProperty("release").GetString());
            Assert.Equal(FeedbackDeliveryService.SentryEnvironment, payload.Event.GetProperty("environment").GetString());
            Assert.Equal(report.CreatedAt, payload.Event.GetProperty("timestamp").GetDateTimeOffset());
            Assert.Equal("error", payload.Event.GetProperty("level").GetString());
            var sdk = payload.Event.GetProperty("sdk");
            var version = typeof(SentrySdk).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion.Split('+', 2)[0];
            Assert.Equal("sentry.dotnet", sdk.GetProperty("name").GetString());
            Assert.Equal(version, sdk.GetProperty("version").GetString());
            Assert.Contains($"sentry_client=sentry.dotnet/{version},", auth);
            Assert.All(payload.Event.EnumerateObject(), property => Assert.Contains(property.Name,
                new[] { "event_id", "timestamp", "logentry", "platform", "release", "environment", "level", "sdk", "fingerprint", "extra" }));
            foreach (var secret in new[] { "private-token", "private-user", "person@example.com" })
                Assert.DoesNotContain(secret, payload.Event.GetRawText());
            expectedReceipt = payload.Id;
            return SentryAccepted(payload.Id);
        }));

        Assert.Equal(0, calls);
        var receipt = await service.SendAsync(new FeedbackConfiguration(new Uri("https://unused.example/report"), null, null, TestDsn), report);

        Assert.Equal(1, calls);
        Assert.Equal(expectedReceipt, receipt);
        Assert.True(reporter.HasPendingError); // Delivery itself never acknowledges any local marker.
        Assert.Equal(sdkWasEnabled, SentrySdk.IsEnabled);
    }

    [Fact]
    public async Task SendAsync_OrdinaryFeedbackUsesUserFeedbackAndExcludesUnselectedPendingError()
    {
        using var directory = new TemporaryDirectory();
        var reporter = new FeedbackReportService(directory.Path);
        reporter.RecordError("unselected-error-details");
        using var report = await reporter.CreateAsync("a feature request token=private-feedback-secret", false, "1.0");
        using var service = new FeedbackDeliveryService(new FakeHandler(async (request, cancellationToken) =>
        {
            var payload = await ReadSentryEnvelopeAsync(request, cancellationToken);
            Assert.Equal("feedback", payload.Type);
            var feedback = payload.Event.GetProperty("contexts").GetProperty("feedback");
            Assert.Equal(report.Description, feedback.GetProperty("message").GetString());
            Assert.Single(feedback.EnumerateObject());
            Assert.Equal("info", payload.Event.GetProperty("level").GetString());
            Assert.False(payload.Event.TryGetProperty("exception", out _));
            Assert.False(payload.Event.TryGetProperty("user", out _));
            Assert.False(payload.Event.TryGetProperty("server_name", out _));
            Assert.Equal(FeedbackDeliveryService.SentryEnvironment, payload.Event.GetProperty("environment").GetString());
            Assert.DoesNotContain("unselected-error-details", payload.Event.GetRawText());
            Assert.DoesNotContain("private-feedback-secret", payload.Event.GetRawText());
            Assert.Equal(await File.ReadAllBytesAsync(report.ArchivePath, cancellationToken), payload.Attachment);
            return SentryAccepted(payload.Id);
        }));

        await service.SendAsync(new FeedbackConfiguration(null, null, null, TestDsn), report);

        Assert.True(reporter.HasPendingError);
    }

    [Fact]
    public async Task SendAsync_SentryFingerprintIgnoresReportTimestampAndArchiveFileName()
    {
        using var directory = new TemporaryDirectory();
        var reporter = new FeedbackReportService(directory.Path);
        reporter.RecordError("same codec failure");
        using var first = await reporter.CreateAsync("first description", true, "1.0");
        reporter.RecordError("same codec failure");
        using var second = await reporter.CreateAsync("different description", true, "1.0");
        var fingerprints = new List<string>();
        using var service = new FeedbackDeliveryService(new FakeHandler(async (request, cancellationToken) =>
        {
            var payload = await ReadSentryEnvelopeAsync(request, cancellationToken);
            fingerprints.Add(payload.Event.GetProperty("fingerprint").GetRawText());
            return SentryAccepted(payload.Id);
        }));
        var configuration = new FeedbackConfiguration(null, null, null, TestDsn);

        await service.SendAsync(configuration, first);
        await service.SendAsync(configuration, second);

        Assert.NotEqual(first.ArchivePath, second.ArchivePath);
        Assert.Equal(fingerprints[0], fingerprints[1]);
    }

    [Fact]
    public async Task SendAsync_SentryFingerprintIgnoresFfmpegAddressesButKeepsFailureDetails()
    {
        using var directory = new TemporaryDirectory();
        var reporter = new FeedbackReportService(directory.Path);
        var messages = new[]
        {
            "[decoder @ 00000000abcd1234] Synthetic codec alpha failed.",
            "[decoder @ 00000000efab5678] Synthetic codec alpha failed.",
            "[decoder @ 00000000efab5678] Synthetic codec beta failed.",
            "Synthetic error code 0x1234.",
            "Synthetic error code 0x5678."
        };
        var fingerprints = new List<string>();
        var submittedMessages = new List<string>();
        using var service = new FeedbackDeliveryService(new FakeHandler(async (request, token) =>
        {
            var payload = await ReadSentryEnvelopeAsync(request, token);
            fingerprints.Add(payload.Event.GetProperty("fingerprint").GetRawText());
            submittedMessages.Add(payload.Event.GetProperty("logentry").GetProperty("formatted").GetString()!);
            return SentryAccepted(payload.Id);
        }));
        foreach (var message in messages)
        {
            reporter.RecordError(message);
            using var report = await reporter.CreateAsync("", true, "1.0");
            await service.SendAsync(new FeedbackConfiguration(null, null, null, TestDsn), report);
        }

        Assert.Equal(fingerprints[0], fingerprints[1]);
        Assert.NotEqual(fingerprints[1], fingerprints[2]);
        Assert.NotEqual(fingerprints[3], fingerprints[4]);
        Assert.Equal(messages, submittedMessages);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task SendAsync_SentryNonSuccessNeverAcknowledgesReport(int status)
    {
        using var directory = new TemporaryDirectory();
        var reporter = new FeedbackReportService(directory.Path);
        reporter.RecordError("pending error");
        using var report = await reporter.CreateAsync("", true, "1.0");
        var calls = 0;
        using var service = new FeedbackDeliveryService(new FakeHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Location = new Uri("https://other.example/envelope/");
            return Task.FromResult(response);
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.SendAsync(new FeedbackConfiguration(null, null, null, TestDsn), report));

        Assert.Equal(1, calls);
        Assert.True(reporter.HasPendingError);
    }

    [Theory]
    [InlineData(200, "<html>proxy success</html>")]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"id\":\"not-an-event-id\"}")]
    [InlineData(200, "{\"id\":\"00000000000000000000000000000000\"}")]
    [InlineData(204, "")]
    public async Task SendAsync_Sentry2xxWithoutMatchingReceiptIsFailure(int status, string responseBody)
    {
        using var directory = new TemporaryDirectory();
        using var report = await new FeedbackReportService(directory.Path).CreateAsync("feedback", false, "1.0");
        using var service = new FeedbackDeliveryService(new FakeHandler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(responseBody) })));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SendAsync(new FeedbackConfiguration(null, null, null, TestDsn), report));
    }

    [Fact]
    public async Task SendAsync_SentryOversizedResponseIsFailure()
    {
        using var directory = new TemporaryDirectory();
        using var report = await new FeedbackReportService(directory.Path).CreateAsync("feedback", false, "1.0");
        using var service = new FeedbackDeliveryService(new FakeHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 4097)) })));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SendAsync(new FeedbackConfiguration(null, null, null, TestDsn), report));
    }

    [Fact]
    public async Task SendAsync_SentryCancellationDuringReceiptReadKeepsPendingError()
    {
        using var directory = new TemporaryDirectory();
        var reporter = new FeedbackReportService(directory.Path);
        reporter.RecordError("pending error");
        using var report = await reporter.CreateAsync("", true, "1.0");
        using var cancellation = new CancellationTokenSource();
        using var service = new FeedbackDeliveryService(new FakeHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancelOnReadStream(cancellation))
            })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SendAsync(new FeedbackConfiguration(null, null, null, TestDsn), report, cancellation.Token));

        Assert.True(reporter.HasPendingError);
    }

    [Fact]
    public async Task SendAsync_ConfigurationRetainsMultipartFallback()
    {
        using var directory = new TemporaryDirectory();
        using var report = await new FeedbackReportService(directory.Path).CreateAsync("feedback", false, "1.0");
        using var service = new FeedbackDeliveryService(new FakeHandler((request, _) =>
        {
            Assert.Equal(new Uri("https://feedback.example/report"), request.RequestUri);
            Assert.IsType<MultipartFormDataContent>(request.Content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var configuration = new FeedbackConfiguration(new Uri("https://feedback.example/report"), null, null, "invalid-dsn");

        var receipt = await service.SendAsync(configuration, report);

        Assert.Null(receipt);
        Assert.True(configuration.CanUpload);
        Assert.Equal(configuration.UploadEndpoint, configuration.UploadDestination);
    }

    [Fact]
    public async Task SendAsync_ConfigurationWithoutDestinationCannotSubmit()
    {
        using var directory = new TemporaryDirectory();
        using var report = await new FeedbackReportService(directory.Path).CreateAsync("feedback", false, "1.0");
        using var service = new FeedbackDeliveryService(new FakeHandler((_, _) =>
            throw new InvalidOperationException("Network must not be called.")));
        var configuration = new FeedbackConfiguration(null, null, null);

        Assert.False(configuration.CanUpload);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(configuration, report));
    }

    private static HttpResponseMessage SentryAccepted(string id) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { id }), Encoding.UTF8, "application/json")
    };

    private static async Task<(string Id, string? Dsn, string Type, JsonElement Event, byte[] Attachment)>
        ReadSentryEnvelopeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        using var stream = new MemoryStream(bytes);
        using var envelope = await Envelope.DeserializeAsync(stream, cancellationToken);
        Assert.Equal(2, envelope.Items.Count);
        var attachment = envelope.Items.Single(item => item.TryGetType() == "attachment");
        Assert.Equal("feedback-report.zip", attachment.TryGetFileName());
        Assert.Equal("application/zip", attachment.Header["content_type"]);
        using var attachmentBuffer = new MemoryStream();
        await attachment.Payload.SerializeAsync(attachmentBuffer, logger: null, cancellationToken);
        var eventItem = envelope.Items.Single(item => item.TryGetType() is "event" or "feedback");
        using var eventBuffer = new MemoryStream();
        await eventItem.Payload.SerializeAsync(eventBuffer, logger: null, cancellationToken);
        using var json = JsonDocument.Parse(eventBuffer.ToArray());
        return ((string)envelope.Header["event_id"]!, (string?)envelope.Header["dsn"], eventItem.TryGetType()!,
            json.RootElement.Clone(), attachmentBuffer.ToArray());
    }

    private sealed class CancelOnReadStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellationToken);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            action(request, cancellationToken);
    }
}

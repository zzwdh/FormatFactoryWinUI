using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using FormatFactoryWinUI.Services;
using Sentry.Protocol.Envelopes;

namespace FormatFactoryWinUI.Tests;

[CollectionDefinition(nameof(SentryUsageTelemetryCollection), DisableParallelization = true)]
public sealed class SentryUsageTelemetryCollection;

[Collection(nameof(SentryUsageTelemetryCollection))]
public sealed class SentryUsageTelemetryTests
{
    private const string TestDsn = "https://0123456789abcdef0123456789abcdef@o1.ingest.us.sentry.io/123";
    private const string PrivatePath = "C:\\Users\\private-user\\private-input.mp4";

    [Fact]
    public async Task EnabledUsageEmitsStructuredLogsMetricsAndSampledConversionWithoutPrivateData()
    {
        var receiver = new EnvelopeReceiver();
        using var epoch = new CancellationTokenSource();
        using var service = CreateTelemetry(receiver);
        service.Configure(epoch.Token);

        service.Record(new TelemetryUsageSample("app_launch"));
        service.Record(new TelemetryUsageSample("startup_ready", ElapsedMilliseconds: 31));
        using var conversion = Assert.IsType<TelemetryConversionTrace>(service.StartConversion("video", PrivatePath));
        conversion.Finish(TelemetryConversionResult.Failed, 43.5);
        service.Record(new TelemetryUsageSample("conversion_result", Result: "failed", Category: "video",
            OutputFormat: PrivatePath, ElapsedMilliseconds: 43.5));

        await service.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var metrics = receiver.BatchItems("trace_metric");
        var launch = Assert.Single(metrics, item => HasString(item, "name", "formatfactory.app_launch"));
        Assert.Equal("counter", launch.GetProperty("type").GetString());
        Assert.Equal(1, launch.GetProperty("value").GetInt32());
        var result = Assert.Single(metrics, item => HasString(item, "name", "formatfactory.conversion_result"));
        Assert.Equal("other", result.GetProperty("attributes").GetProperty("output_format").GetProperty("value").GetString());
        var startup = Assert.Single(metrics, item => HasString(item, "name", "formatfactory.startup_ready_duration"));
        Assert.Equal(31, startup.GetProperty("value").GetDouble());
        var elapsed = Assert.Single(metrics, item => HasString(item, "name", "formatfactory.conversion_duration"));
        Assert.Equal("distribution", elapsed.GetProperty("type").GetString());
        Assert.Equal(43.5, elapsed.GetProperty("value").GetDouble());

        var log = Assert.Single(receiver.BatchItems("log"), item =>
            HasString(item, "body", "FluentFormatConverter conversion finished"));
        Assert.Equal("warn", log.GetProperty("level").GetString());
        var transaction = Assert.Single(receiver.Items("transaction"));
        Assert.Equal("FluentFormatConverter conversion", transaction.GetProperty("transaction").GetString());
        if (transaction.TryGetProperty("user", out var user))
            Assert.False(user.TryGetProperty("id", out _));

        Assert.All(receiver.Payloads, payload =>
        {
            Assert.DoesNotContain("private-user", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-input", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"device.name\"", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"user.email\"", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"user.id\"", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        });
        Assert.Empty(receiver.Items("event"));
    }

    [Fact]
    public async Task DisabledAndCancelledEpochsDoNotEmitOrReplayTheirRecords()
    {
        var receiver = new EnvelopeReceiver();
        using var oldEpoch = new CancellationTokenSource();
        using var nextEpoch = new CancellationTokenSource();
        using var service = CreateTelemetry(receiver);

        service.Record(new TelemetryUsageSample("page_visit", Page: "settings"));
        service.Configure(oldEpoch.Token);
        using var stale = Assert.IsType<TelemetryConversionTrace>(service.StartConversion("video", "mp4"));
        oldEpoch.Cancel();
        service.Configure(null);
        stale.Finish(TelemetryConversionResult.Succeeded, 30);
        service.Record(new TelemetryUsageSample("page_visit", Page: "audio"));

        service.Configure(nextEpoch.Token);
        service.Record(new TelemetryUsageSample("app_launch"));
        await service.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var metric = Assert.Single(receiver.BatchItems("trace_metric"));
        Assert.Equal("formatfactory.app_launch", metric.GetProperty("name").GetString());
        Assert.Empty(receiver.Items("transaction"));
    }

    private static SentryUsageTelemetry CreateTelemetry(EnvelopeReceiver receiver) =>
        new(new FeedbackConfiguration(null, null, null, TestDsn), "1.2.3.4", receiver.CreateHandler,
            traceSampleRate: 1, profileSampleRate: 0);

    private static bool HasString(JsonElement element, string name, string value) =>
        element.TryGetProperty(name, out var property) && property.GetString() == value;

    private sealed class EnvelopeReceiver
    {
        private readonly ConcurrentQueue<(string Type, JsonElement Payload)> _items = new();

        public HttpMessageHandler CreateHandler() => new ReceiverHandler(_items);

        public IEnumerable<JsonElement> Payloads => _items.Select(item => item.Payload);

        public IEnumerable<JsonElement> Items(string itemType) =>
            _items.Where(item => item.Type == itemType).Select(item => item.Payload);

        public IEnumerable<JsonElement> BatchItems(string itemType) =>
            Items(itemType).SelectMany(payload => payload.GetProperty("items").EnumerateArray());
    }

    private sealed class ReceiverHandler(ConcurrentQueue<(string Type, JsonElement Payload)> items) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = await request.Content!.ReadAsStreamAsync(token);
            using var gzip = request.Content.Headers.ContentEncoding.Contains("gzip")
                ? new GZipStream(body, CompressionMode.Decompress, leaveOpen: true)
                : null;
            using var bytes = new MemoryStream();
            await (gzip ?? body).CopyToAsync(bytes, token);
            bytes.Position = 0;
            using var envelope = await Envelope.DeserializeAsync(bytes, token);
            foreach (var item in envelope.Items)
            {
                using var payload = new MemoryStream();
                await item.Payload.SerializeAsync(payload, null, token);
                using var json = JsonDocument.Parse(payload.ToArray());
                items.Enqueue((item.TryGetType()!, json.RootElement.Clone()));
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

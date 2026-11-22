using System.IO.Compression;
using System.Text.Json;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class FeedbackErrorOriginTests
{
    [Fact]
    public async Task NewlyRecordedError_PersistsRecordingAssemblyAndSessionSeparatelyFromReportVersion()
    {
        using var directory = new TemporaryDirectory();
        var service = new FeedbackReportService(directory.Path);
        service.RecordException(new InvalidOperationException("Synthetic failure"));
        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory.Path, "feedback-error.json")));
        var version = typeof(FeedbackReportService).Assembly.GetName().Version!.ToString();
        Assert.Equal(version, marker.RootElement.GetProperty("OriginVersion").GetString());
        Assert.Equal(RuntimeFailureDiagnostics.SessionId, marker.RootElement.GetProperty("OriginSession").GetGuid());

        using var report = await new FeedbackReportService(directory.Path).CreateAsync("", true, "99.0.0.0");

        Assert.Equal("99.0.0.0", report.AppVersion);
        Assert.Contains($"Error origin: scope=marker_creation assembly_version={version} session={RuntimeFailureDiagnostics.SessionId:N}", report.Preview);
        AssertPreviewMatchesArchive(report);
    }

    [Fact]
    public async Task PersistedErrorFromOlderVersion_PreservesOriginalSessionAndVersionOnNewerReport()
    {
        using var directory = new TemporaryDirectory();
        var session = Guid.NewGuid();
        await WriteMarkerAsync(directory.Path, "1.2.3.4", session);
        var markerBefore = await File.ReadAllBytesAsync(Path.Combine(directory.Path, "feedback-error.json"));

        using var report = await new FeedbackReportService(directory.Path).CreateAsync("", true, "9.8.7.6");

        Assert.Equal("9.8.7.6", report.AppVersion);
        Assert.Contains($"Error origin: scope=marker_creation assembly_version=1.2.3.4 session={session:N}", report.Preview);
        Assert.Equal(markerBefore, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "feedback-error.json")));
        AssertPreviewMatchesArchive(report);
    }

    [Fact]
    public async Task LegacyMarkerWithoutOrigin_RemainsPendingAndExplicitlyUnknown()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "feedback-error.json"), JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow.AddDays(-1), Message = "Synthetic old failure"
        }));
        var service = new FeedbackReportService(directory.Path);

        using var report = await service.CreateAsync("", true, "9.8.7.6");

        Assert.True(service.HasPendingError);
        Assert.Contains("Error origin: scope=marker_creation assembly_version=unknown session=none", report.Preview);
        AssertPreviewMatchesArchive(report);
    }

    [Theory]
    [InlineData("1.2.3.4\nprivate-origin-value")]
    [InlineData("private-origin-value")]
    [InlineData(" 1.2.3.4 ")]
    [InlineData("1.2.3.4.5")]
    public async Task TamperedVersion_IsNotCopiedAsFreeText(string version)
    {
        using var directory = new TemporaryDirectory();
        await WriteMarkerAsync(directory.Path, version, Guid.Empty);

        using var report = await new FeedbackReportService(directory.Path).CreateAsync("", true, "9.8.7.6");

        Assert.Contains("Error origin: scope=marker_creation assembly_version=unknown session=none", report.Preview);
        Assert.DoesNotContain("private-origin-value", report.Preview);
        AssertPreviewMatchesArchive(report);
    }

    [Fact]
    public async Task ReportWithoutLogs_DoesNotDiscloseMarkerOriginOrAcknowledgePendingError()
    {
        using var directory = new TemporaryDirectory();
        var session = Guid.NewGuid();
        await WriteMarkerAsync(directory.Path, "1.2.3.4", session);
        var service = new FeedbackReportService(directory.Path);

        using var report = await service.CreateAsync("", false, "9.8.7.6");

        Assert.DoesNotContain("Error origin:", report.Preview);
        Assert.DoesNotContain("1.2.3.4", report.Preview);
        Assert.DoesNotContain(session.ToString("N"), report.Preview);
        Assert.Null(report.ErrorId);
        service.ClearPendingError(report);
        Assert.True(service.HasPendingError);
        AssertPreviewMatchesArchive(report);
    }

    private static Task WriteMarkerAsync(string root, string version, Guid session) =>
        File.WriteAllTextAsync(Path.Combine(root, "feedback-error.json"), JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow.AddDays(-1), Message = "Synthetic old failure",
            OriginVersion = version, OriginSession = session
        }));

    private static void AssertPreviewMatchesArchive(FeedbackReport report)
    {
        using var archive = ZipFile.OpenRead(report.ArchivePath);
        var sections = archive.Entries.Select(entry =>
        {
            using var reader = new StreamReader(entry.Open());
            return $"[{entry.FullName}]\n{reader.ReadToEnd()}";
        });
        Assert.Equal(report.Preview, string.Join("\n\n", sections));
    }
}

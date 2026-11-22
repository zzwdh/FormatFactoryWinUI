using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class FeedbackReportServiceTests
{
    [Fact]
    public async Task ErrorCorrelation_IsCapturedWithMarkerAndOnlyIncludedWithLogs()
    {
        using var directory = new TemporaryDirectory();
        var service = new FeedbackReportService(directory.Path);
        var operation = Guid.NewGuid();
        var task = Guid.NewGuid();
        service.RecordError("Source unavailable", null, new(ErrorCorrelationKind.SourceImport, operation, task));

        using var report = await service.CreateAsync("", true, "26.11.20.0");
        Assert.Contains($"Error correlation: scope=marker_creation kind=SourceImport operation={operation:N} task={task:N}", report.Preview);
        Assert.Equal("Source unavailable", report.ErrorMessage);
        using var noLogs = await service.CreateAsync("", false, "26.11.20.0");
        Assert.DoesNotContain(operation.ToString("N"), noLogs.Preview);
        Assert.DoesNotContain(task.ToString("N"), noLogs.Preview);

        service.RecordError("Unrelated failure");
        using var unrelated = await service.CreateAsync("", true, "26.11.20.0");
        Assert.DoesNotContain("Error correlation:", unrelated.Preview);
    }

    public static IEnumerable<object[]> WindowsPathsWithPunctuation()
    {
        foreach (var root in new[] { @"C:\Users\synthetic-user\", @"\\synthetic-server\synthetic-share\" })
        foreach (var punctuation in new[] { ",", ";", "'", " ,; O'Brien " })
        foreach (var quote in new[] { "", "'", "\"" })
            yield return [root + "folder-prefix" + punctuation + @"private-client-after\private-document.pdf", quote];
    }

    [Theory]
    [MemberData(nameof(WindowsPathsWithPunctuation))]
    public async Task CreateAsync_PunctuationInWindowsPathsIsRedactedOnEveryReportSurface(string path, string quote)
    {
        using var directory = new TemporaryDirectory();
        var logs = Directory.CreateDirectory(Path.Combine(directory.Path, "Logs")).FullName;
        var message = $"Could not read {quote}{path}{quote}; same-line diagnostics.\nnext-line diagnostics preserved";
        await File.WriteAllTextAsync(Path.Combine(logs, "application.log"), message);
        var service = new FeedbackReportService(directory.Path);
        service.RecordError(message);
        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory.Path, "feedback-error.json")));

        using var report = await service.CreateAsync(message, true, "1.0");

        var entries = ReadArchive(report.ArchivePath);
        var surfaces = new List<(string Name, string Content)>
        {
            ("persisted error marker", marker.RootElement.GetProperty("Message").GetString()!),
            ("reported error", report.ErrorMessage!),
            ("reported description", report.Description),
            ("report preview", report.Preview)
        };
        surfaces.AddRange(entries);
        Assert.All(surfaces, surface =>
        {
            Assert.DoesNotContain("synthetic-user", surface.Content);
            Assert.DoesNotContain("synthetic-server", surface.Content);
            Assert.DoesNotContain("private-client-after", surface.Content);
            Assert.DoesNotContain("private-document.pdf", surface.Content);
            Assert.Contains("[path]", surface.Content);
            Assert.Contains("next-line diagnostics preserved", surface.Content);
            if (quote.Length > 0) Assert.Contains("same-line diagnostics.", surface.Content);
        });
        Assert.Equal(string.Join("\n\n", entries.Select(entry => $"[{entry.Name}]\n{entry.Content}")), report.Preview);
    }

    [Fact]
    public async Task CreateAsync_UnquotedWindowsPathConservativelyOmitsAmbiguousLineSuffix()
    {
        using var directory = new TemporaryDirectory();
        const string message = "Failed at C:\\synthetic-user\\file.pdf; ambiguous line suffix\nnext-line diagnostics preserved";
        var service = new FeedbackReportService(directory.Path);
        service.RecordError(message);

        using var report = await service.CreateAsync("", true, "1.0");

        Assert.DoesNotContain("ambiguous line suffix", report.ErrorMessage);
        Assert.Equal("Failed at [path]\nnext-line diagnostics preserved", report.ErrorMessage);
    }

    [Theory]
    [InlineData(",private-client-after\\private-document.pdf")]
    [InlineData(";private-client-after\\private-document.pdf")]
    [InlineData("'private-client-after\\private-document.pdf")]
    [InlineData(", Private Client; private-client-after\\private-document.pdf")]
    [InlineData(",private-client-after.pdf")]
    [InlineData(";private-client-after.pdf")]
    [InlineData("'private-client-after.pdf")]
    public async Task LegacyErrorMarker_PathPlaceholderWithPunctuationSuffixIsRedactedAgain(string suffix)
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "feedback-error.json"), JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow,
            Message = "Could not read '[path]" + suffix + "'.\nnext-line diagnostics preserved",
            TaskLogRelativePath = (string?)null
        }));

        using var report = await new FeedbackReportService(directory.Path).CreateAsync("", true, "1.0");

        var surfaces = ReadArchive(report.ArchivePath).Select(entry => entry.Content)
            .Append(report.Preview).Append(report.ErrorMessage!);
        Assert.All(surfaces, content =>
        {
            Assert.DoesNotContain("private-client-after", content);
            Assert.DoesNotContain("private-document.pdf", content);
            Assert.Contains("[path]", content);
            Assert.Contains("next-line diagnostics preserved", content);
        });
    }

    [Theory]
    [InlineData("Failure: [path] ; error code=5")]
    [InlineData("Failure: \"[path]\"; error code=5")]
    public async Task CreateAsync_PathPlaceholderWithClearBoundaryPreservesDiagnostics(string message)
    {
        using var directory = new TemporaryDirectory();
        var service = new FeedbackReportService(directory.Path);
        service.RecordError(message);

        using var report = await service.CreateAsync("", true, "1.0");

        Assert.Equal(message, report.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public async Task RecordError_MissingDetailsRetainsNamedErrorAndTaskLog(string? message)
    {
        using var directory = new TemporaryDirectory();
        var logs = Directory.CreateDirectory(Path.Combine(directory.Path, "Logs")).FullName;
        var taskLog = Path.Combine(logs, "failed-task.log");
        await File.WriteAllTextAsync(taskLog, "synthetic decoder failure");
        var service = new FeedbackReportService(directory.Path);
        service.RecordError(message, taskLog);

        using var report = await service.CreateAsync("", true, "1.0");

        Assert.False(string.IsNullOrWhiteSpace(report.ErrorMessage));
        Assert.NotNull(report.ErrorId);
        Assert.Contains("synthetic decoder failure", report.Preview);
    }

    [Fact]
    public async Task LegacyEmptyErrorMarker_RemainsPendingWithDiagnosticName()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "feedback-error.json"), JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, Message = " \t", TaskLogRelativePath = (string?)null
        }));
        var service = new FeedbackReportService(directory.Path);

        using var report = await service.CreateAsync("", true, "1.0");

        Assert.True(service.HasPendingError);
        Assert.NotNull(report.ErrorId);
        Assert.False(string.IsNullOrWhiteSpace(report.ErrorMessage));
    }

    [Fact]
    public async Task CreateAsync_OnlyIncludesApprovedLogsAndPreviewMatchesRedactedArchive()
    {
        using var directory = new TemporaryDirectory();
        var logs = Directory.CreateDirectory(Path.Combine(directory.Path, "Logs")).FullName;
        var task = Path.Combine(logs, "task-private-name.log");
        await File.WriteAllTextAsync(Path.Combine(logs, "application.log"),
            "Authorization: Bearer bearer-secret\npassword=pass-secret\n{\"token\":\"token-secret\"}\n" +
            "email=person@example.com\nsource=C:\\Users\\private-person\\Documents\\movie.mp4\n" +
            "share=\\\\private-server\\private-share\\movie.mp4\nsource=/home/private-person/file.mp4\n" +
            "request=https://service.example/report?token=query-secret\n--api-key argument-secret\nordinary failure details");
        await File.WriteAllTextAsync(task, "task failure details\nsecret=task-secret");
        await File.WriteAllTextAsync(Path.Combine(logs, "older-task.log"), "old-unrelated-log-secret");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), "private-settings-secret");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "queue.json"), "private-queue-secret");
        var service = new FeedbackReportService(directory.Path);
        service.RecordError("Latest failure password=error-secret", task);

        using var report = await service.CreateAsync("My report token=description-secret", true, "1.2.3.4");

        var entries = ReadArchive(report.ArchivePath);
        Assert.Equal(new[] { "report.txt", "logs/application.log", "logs/last-error-task.log" }, entries.Select(entry => entry.Name));
        Assert.Equal(2, report.LogCount);
        Assert.Equal(string.Join("\n\n", entries.Select(entry => $"[{entry.Name}]\n{entry.Content}")), report.Preview);
        Assert.Contains("ordinary failure details", report.Preview);
        Assert.Contains("task failure details", report.Preview);
        Assert.Contains("App version: 1.2.3.4", report.Preview);
        foreach (var secret in new[] { "bearer-secret", "pass-secret", "token-secret", "person@example.com",
            "private-person", "private-server", "private-share", "query-secret", "argument-secret", "task-secret",
            "old-unrelated-log-secret", "private-settings-secret", "private-queue-secret", "error-secret", "description-secret" })
            Assert.DoesNotContain(secret, report.Preview);
    }

    [Fact]
    public async Task CreateAsync_WithoutConsentIncludesNoLogContentAndDisposeRemovesArchive()
    {
        using var directory = new TemporaryDirectory();
        var logs = Directory.CreateDirectory(Path.Combine(directory.Path, "Logs")).FullName;
        await File.WriteAllTextAsync(Path.Combine(logs, "application.log"), "log-content-must-not-be-attached");
        var service = new FeedbackReportService(directory.Path);
        service.RecordError("pending-private-error-details");
        var report = await service.CreateAsync("Description", false, "1.0");

        Assert.Equal(0, report.LogCount);
        Assert.Single(ReadArchive(report.ArchivePath));
        Assert.DoesNotContain("log-content-must-not-be-attached", report.Preview);
        Assert.DoesNotContain("pending-private-error-details", report.Preview);
        service.ClearPendingError(report);
        Assert.True(service.HasPendingError);
        var archivePath = report.ArchivePath;
        report.Dispose();
        report.Dispose();
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task LatestErrorPersistsAcrossInstancesAndClearRemovesPendingState()
    {
        using var directory = new TemporaryDirectory();
        var first = new FeedbackReportService(directory.Path);
        Assert.False(first.HasPendingError);
        first.RecordError("first error");
        first.RecordError("second error");
        var restarted = new FeedbackReportService(directory.Path);

        Assert.True(restarted.HasPendingError);
        using (var report = await restarted.CreateAsync("", true, "1.0"))
        {
            Assert.Contains("second error", report.Preview);
            Assert.DoesNotContain("first error", report.Preview);
        }
        restarted.ClearPendingError();
        Assert.False(restarted.HasPendingError);
        Assert.False(new FeedbackReportService(directory.Path).HasPendingError);
    }

    [Fact]
    public async Task ClearPendingError_OnlyAcknowledgesTheErrorIncludedInSubmittedSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var service = new FeedbackReportService(directory.Path);
        service.RecordError("first error");
        using var firstReport = await service.CreateAsync("", true, "1.0");
        service.RecordError("newer error");

        service.ClearPendingError(firstReport);

        Assert.True(service.HasPendingError);
        Assert.True(new FeedbackReportService(directory.Path).HasPendingError);
        using var latestReport = await service.CreateAsync("", true, "1.0");
        service.ClearPendingError(latestReport);
        Assert.False(service.HasPendingError);
        Assert.False(new FeedbackReportService(directory.Path).HasPendingError);
    }

    [Fact]
    public async Task MultipleServices_ObserveNewErrorsAndOldSnapshotCannotClearAnotherServiceError()
    {
        using var directory = new TemporaryDirectory();
        var first = new FeedbackReportService(directory.Path);
        var second = new FeedbackReportService(Path.Combine(directory.Path, "."));
        Assert.False(first.HasPendingError);
        second.RecordError("first error from second service");
        Assert.True(first.HasPendingError);
        using var oldReport = await first.CreateAsync("", true, "1.0");
        Assert.Contains("first error from second service", oldReport.Preview);
        second.RecordError("new error from second service");

        first.ClearPendingError(oldReport);

        Assert.True(second.HasPendingError);
        Assert.True(File.Exists(Path.Combine(directory.Path, "feedback-error.json")));
        using var latestReport = await first.CreateAsync("", true, "1.0");
        Assert.Contains("new error from second service", latestReport.Preview);
        Assert.DoesNotContain("first error from second service", latestReport.Preview);
        first.ClearPendingError(latestReport);
        Assert.False(second.HasPendingError);
    }

    [Fact]
    public async Task PendingErrorReads_ReloadExternalMarkerChangesAndDeletion()
    {
        using var directory = new TemporaryDirectory();
        var service = new FeedbackReportService(directory.Path);
        Assert.False(service.HasPendingError);
        var markerPath = Path.Combine(directory.Path, "feedback-error.json");
        await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow,
            Message = "error written by another process", TaskLogRelativePath = (string?)null
        }));

        Assert.True(service.HasPendingError);
        using var report = await service.CreateAsync("", true, "1.0");
        Assert.Contains("error written by another process", report.Preview);
        File.Delete(markerPath);
        Assert.False(service.HasPendingError);
    }

    [Fact]
    public async Task MultipleServices_FailedWriteKeepsNewErrorInsteadOfReloadingOlderDiskSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var first = new FeedbackReportService(directory.Path);
        var second = new FeedbackReportService(directory.Path);
        first.RecordError("persisted old error");
        var markerPath = Path.Combine(directory.Path, "feedback-error.json");
        // Windows denies replacement while this reader retains the file without delete sharing.
        using (var lockedMarker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            second.RecordError("new error with failed persistence");
            using var report = await first.CreateAsync("", true, "1.0");
            Assert.Contains("new error with failed persistence", report.Preview);
            Assert.DoesNotContain("persisted old error", report.Preview);
            Assert.True(first.HasPendingError);
        }
        using var latestReport = await first.CreateAsync("", true, "1.0");
        Assert.Contains("new error with failed persistence", latestReport.Preview);
        first.ClearPendingError(latestReport);
        Assert.False(second.HasPendingError);
    }

    [Fact]
    public async Task CreateAsync_OnlyIncludesLatestTaskStderrSidecarAndRedactsIt()
    {
        using var directory = new TemporaryDirectory();
        var logs = Directory.CreateDirectory(Path.Combine(directory.Path, "Logs")).FullName;
        var taskPath = Path.Combine(logs, "task-latest.log");
        await File.WriteAllTextAsync(Path.Combine(logs, "application.log"), "application diagnostics");
        await File.WriteAllTextAsync(taskPath, "task diagnostics");
        await File.WriteAllTextAsync(taskPath + ".stderr", "external tool failure\ntoken=stderr-secret\nfile=C:\\Users\\private-person\\file.pdf");
        await File.WriteAllTextAsync(Path.Combine(logs, "task-unrelated.log.stderr"), "unrelated-stderr-content");
        var service = new FeedbackReportService(directory.Path);
        service.RecordError("tool failed", taskPath);

        using var report = await service.CreateAsync("", true, "1.0");

        Assert.Equal(3, report.LogCount);
        var entries = ReadArchive(report.ArchivePath);
        var stderr = entries.Single(entry => entry.Name == "logs/last-error-task.stderr.log").Content;
        Assert.Contains("external tool failure", stderr);
        Assert.DoesNotContain("stderr-secret", stderr);
        Assert.DoesNotContain("private-person", stderr);
        Assert.DoesNotContain("unrelated-stderr-content", report.Preview);
        Assert.Equal(string.Join("\n\n", entries.Select(entry => $"[{entry.Name}]\n{entry.Content}")), report.Preview);
    }

    [Theory]
    [InlineData("Logs-other/outside.log")]
    [InlineData("Logs/../outside.log")]
    [InlineData("Logs/private-settings.json")]
    public async Task CreateAsync_RejectsErrorLogOutsideLogsOrWithNonLogExtension(string relativePath)
    {
        using var directory = new TemporaryDirectory();
        var logs = Directory.CreateDirectory(Path.Combine(directory.Path, "Logs")).FullName;
        await File.WriteAllTextAsync(Path.Combine(logs, "application.log"), "application diagnostics");
        var privatePath = Path.GetFullPath(Path.Combine(directory.Path, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
        await File.WriteAllTextAsync(privatePath, "private-unrelated-content");
        var service = new FeedbackReportService(directory.Path);
        service.RecordError("failure", privatePath);

        using var report = await service.CreateAsync("", true, "1.0");

        Assert.Equal(1, report.LogCount);
        Assert.DoesNotContain("private-unrelated-content", report.Preview);
    }

    [Fact]
    public async Task CreateAsync_BoundsLogTailAndDropsPartialCredentialLine()
    {
        using var directory = new TemporaryDirectory();
        var logs = Directory.CreateDirectory(Path.Combine(directory.Path, "Logs")).FullName;
        await File.WriteAllTextAsync(Path.Combine(logs, "application.log"),
            "older diagnostics\nAuthorization: Bearer " + new string('x', FeedbackReportService.MaximumLogBytes * 2) +
            "sensitive-boundary-value\nlast useful diagnostics\n");

        using var report = await new FeedbackReportService(directory.Path).CreateAsync("", true, "1.0");
        var log = ReadArchive(report.ArchivePath).Single(entry => entry.Name == "logs/application.log").Content;

        Assert.Contains("last useful diagnostics", log);
        Assert.DoesNotContain("sensitive-boundary-value", log);
        Assert.DoesNotContain("older diagnostics", log);
        Assert.True(Encoding.UTF8.GetByteCount(log) <= FeedbackReportService.MaximumLogBytes);
        Assert.True(new FileInfo(report.ArchivePath).Length < FeedbackDeliveryService.MaximumArchiveBytes);
    }

    [Fact]
    public void RecordError_DiskFailureDoesNotThrowOrHideInMemoryPendingError()
    {
        using var directory = new TemporaryDirectory();
        var fileRoot = Path.Combine(directory.Path, "file-is-not-directory");
        File.WriteAllText(fileRoot, "sentinel");
        var service = new FeedbackReportService(fileRoot);

        service.RecordError("conversion failed");

        Assert.True(service.HasPendingError);
        Assert.Equal("sentinel", File.ReadAllText(fileRoot));
        service.ClearPendingError();
        Assert.False(service.HasPendingError);
    }

    [Fact]
    public async Task CreateAsync_CancelledRequestCreatesNoReport()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FeedbackReportService(directory.Path).CreateAsync("", true, "1.0", cancellation.Token));
    }

    private static List<(string Name, string Content)> ReadArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(entry =>
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return (entry.FullName, reader.ReadToEnd());
        }).ToList();
    }
}

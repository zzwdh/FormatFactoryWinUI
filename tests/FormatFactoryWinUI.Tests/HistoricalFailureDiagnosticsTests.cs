using System.ComponentModel;
using System.IO.Compression;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class HistoricalFailureDiagnosticsTests
{
    [Fact]
    public void BudgetRejection_ReportsExistingAccountingWithoutQueryingAgainOrLeakingReservation()
    {
        const long mib = 1024 * 1024;
        var queries = 0;
        var writes = 0;
        var budget = new DiskSpaceBudgetService(_ => { queries++; return new("synthetic-volume", 150 * mib); },
            _ => { writes++; return 5 * mib; });
        using (budget.ReserveBytes(30 * mib, "C:\\SyntheticPrivate", "C:\\SyntheticPrivate\\temporary"))
        {
            var error = Assert.Throws<IOException>(() => budget.ReserveBytes(80 * mib, "C:\\SyntheticPrivate"));
            var facts = StorageFailureDiagnostics.Get(error)!;
            Assert.Equal(StorageFailureStage.BudgetReserve, facts.Stage);
            Assert.Equal(80 * mib, facts.Budget!.EstimatedBytes);
            Assert.Equal(144 * mib, facts.Budget.RequiredBytes);
            Assert.Equal(125 * mib, facts.Budget.AvailableAfterReservations);
            Assert.Equal(25 * mib, facts.Budget.OutstandingBytes);
            Assert.Equal(5 * mib, facts.Budget.WrittenBytes);
            Assert.Equal(1, facts.Budget.ActiveReservations);
            Assert.Equal(2, queries);
            Assert.Equal(1, writes);
            Assert.DoesNotContain("SyntheticPrivate", StorageFailureDiagnostics.Format(error)!);
        }
        using var released = budget.ReserveBytes(80 * mib, "C:\\SyntheticPrivate");
    }

    [Fact]
    public void DirectoryCreationFailure_IdentifiesStageAndLeavesExistingFileUntouched()
    {
        using var directory = new TemporaryDirectory();
        var existing = Path.Combine(directory.Path, "private-output");
        File.WriteAllText(existing, "existing sentinel");
        var task = new MediaTask { InputPath = "synthetic.wav", TargetFormatId = "mp3" };
        var error = Record.Exception(() => new OutputPathService().Reserve(task,
            new AppSettings { OutputMode = "custom", CustomOutputDirectory = existing }));
        Assert.NotNull(error);
        Assert.Equal(StorageFailureStage.CreateDirectory, StorageFailureDiagnostics.Get(error!)!.Stage);
        Assert.Equal(task.Id, StorageFailureDiagnostics.Get(error!)!.TaskId);
        Assert.Equal("existing sentinel", File.ReadAllText(existing));
        Assert.DoesNotContain("private-output", StorageFailureDiagnostics.Format(error!)!);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("queue")]
    [InlineData("history")]
    public async Task PersistenceCommitFailure_ReportsStoreAndStageWithoutReplacingExistingDirectory(string store)
    {
        using var directory = new TemporaryDirectory();
        var target = Directory.CreateDirectory(Path.Combine(directory.Path, "private-store.json")).FullName;
        var sentinel = Path.Combine(target, "sentinel");
        await File.WriteAllTextAsync(sentinel, "keep");
        var output = Path.Combine(directory.Path, "completed.bin");
        await File.WriteAllTextAsync(output, "completed artifact");
        var error = await Record.ExceptionAsync(async () =>
        {
            if (store == "settings") await new SettingsService(target).SaveAsync(new AppSettings());
            else if (store == "queue") await new QueueStore(target, () => [], _ => new MemoryStream("[]"u8.ToArray())).SaveAsync([
                new MediaTask { InputPath = "synthetic.wav", SourceFormatId = "wav", TargetFormatId = "mp3" }]);
            else await new ConversionHistoryStore(target).RecordCompletedAsync(new ConversionHistoryItem { TaskId = Guid.NewGuid(), OutputPath = output });
        });
        Assert.NotNull(error);
        var detail = PersistenceFailureDiagnostics.Format(error!);
        Assert.NotNull(detail);
        Assert.Contains("stage=CommitSnapshot", detail);
        Assert.Contains("store=" + char.ToUpperInvariant(store[0]) + store[1..], detail);
        Assert.DoesNotContain("private-store", detail);
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task ExceptionReport_KeepsTypedFactsAndMarkerOutsideVisibleMessageAndNoLogs()
    {
        using var directory = new TemporaryDirectory();
        var reportService = new FeedbackReportService(directory.Path);
        var original = new Win32Exception(15616, "C:\\SyntheticPrivate\\media.mov token=synthetic-private-token");
        var operation = Guid.NewGuid();
        RuntimeFailureDiagnostics.Record(original, new(RuntimeFailureStage.ProcessStart, operation, 2, 17));
        reportService.RecordException(original);
        using var report = await reportService.CreateAsync("", true, "synthetic");
        Assert.Contains("marker=" + report.ErrorId!.Value.ToString("N"), report.Preview);
        Assert.Contains("exception=Win32Exception", report.Preview);
        Assert.Contains("native=15616", report.Preview);
        Assert.Contains("operation=" + operation.ToString("N"), report.Preview);
        Assert.DoesNotContain("synthetic-private-token", report.Preview);
        Assert.DoesNotContain("media.mov", report.Preview);
        Assert.DoesNotContain("Reported exception facts:", report.ErrorMessage!);
        using var noLogs = await reportService.CreateAsync("", false, "synthetic");
        Assert.DoesNotContain("Reported exception facts:", noLogs.Preview);
        Assert.DoesNotContain("Report correlation:", noLogs.Preview);
        Assert.Null(noLogs.ErrorId);
    }

    [Fact]
    public async Task RecoveryLookupFailure_HasFixedStageAndNoCallerSuppliedText()
    {
        using var directory = new TemporaryDirectory();
        var recovery = new OutputRecoveryService(Path.Combine(directory.Path, "recovery"));
        var error = await Record.ExceptionAsync(() => recovery.SaveAsAsync(Guid.NewGuid().ToString("N"), directory.Path));
        Assert.NotNull(error);
        var line = StorageFailureDiagnostics.Format(error!);
        Assert.NotNull(line);
        Assert.Contains("stage=RecoveryLookup", line);
        Assert.DoesNotContain(directory.Path, line);
    }

    [Fact]
    public async Task TamperedMarkerFacts_CannotInjectFreeTextIntoReportMetadata()
    {
        using var directory = new TemporaryDirectory();
        var id = Guid.NewGuid();
        var payload = new
        {
            Id = id, OccurredAt = DateTimeOffset.UtcNow, Message = "synthetic failure", TaskLogRelativePath = (string?)null,
            Safety = new { Kind = "C:\\SyntheticPrivate\\secret.mov token=private-secret", HResult = 1, Native = (int?)null,
                Stage = 999, Operation = Guid.NewGuid(), Attempt = 1, ElapsedMs = 2, Recovered = false }
        };
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "feedback-error.json"), System.Text.Json.JsonSerializer.Serialize(payload));
        using var report = await new FeedbackReportService(directory.Path).CreateAsync("", true, "synthetic");
        Assert.Contains("exception=Other", report.Preview);
        Assert.Contains("stage=Unknown", report.Preview);
        Assert.DoesNotContain("SyntheticPrivate", report.Preview);
        Assert.DoesNotContain("private-secret", report.Preview);
        Assert.DoesNotContain("secret.mov", report.Preview);
    }

    [Fact]
    public void SafeFields_IgnoreExceptionDataAndFreeTextAndKeepEarliestStage()
    {
        var error = new IOException("C:\\SyntheticPrivate\\private.mov token=private-secret\nFailure context: fake");
        error.Data["native"] = 5;
        error.Data["stage"] = "private-secret";
        PersistenceFailureDiagnostics.Record(error, DiagnosticStore.Queue, DiagnosticPersistenceStage.AcquireLock);
        PersistenceFailureDiagnostics.Record(error, DiagnosticStore.Settings, DiagnosticPersistenceStage.CommitSnapshot);
        var line = PersistenceFailureDiagnostics.Format(error)!;
        Assert.Contains("store=Queue stage=AcquireLock", line);
        Assert.DoesNotContain("private", line);
        Assert.Contains("native=none", line);
        Assert.Equal("unknown", RuntimeFailureDiagnostics.Codec("private-secret"));
        Assert.Equal("unknown", RuntimeFailureDiagnostics.FormatId("private-secret"));
    }
}

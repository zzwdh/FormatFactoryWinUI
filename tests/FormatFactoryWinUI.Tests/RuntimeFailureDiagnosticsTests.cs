using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class RuntimeFailureDiagnosticsTests
{
    private const string Private = "C:\\SyntheticPrivate\\personal.mov token=synthetic-secret https://private.invalid/user";

    [Theory]
    [InlineData("recovered", 2, true)]
    [InlineData("exhausted", 2, false)]
    [InlineData("unexpected", 1, false)]
    public async Task ActivationFacts_PreserveBoundedAttemptsAndOriginalFailure(string scenario, int expectedAttempt, bool recovered)
    {
        Exception failure = scenario == "unexpected" ? new COMException(Private, unchecked((int)0x8000FFFF)) : new TimeoutException(Private);
        var calls = 0;
        Exception? reported = null;
        var value = new object();
        var result = await ActivationArgumentsReader.ReadAsync(() =>
        {
            calls++;
            if (scenario == "recovered" && calls == 2) return value;
            throw failure;
        }, error => reported = error, _ => Task.CompletedTask);
        Assert.Equal(expectedAttempt, calls);
        Assert.Same(failure, reported);
        var facts = RuntimeFailureDiagnostics.Get(failure)!;
        Assert.Equal(RuntimeFailureStage.ActivationRead, facts.Stage);
        Assert.Equal(expectedAttempt, facts.Attempt);
        Assert.Equal(recovered, facts.Recovered);
        Assert.NotEqual(Guid.Empty, facts.Operation);
        Assert.True(facts.ElapsedMs >= 0);
        Assert.Equal(recovered, result is not null);
        var safe = RuntimeFailureDiagnostics.Format(failure);
        Assert.DoesNotContain("SyntheticPrivate", safe);
        Assert.DoesNotContain("synthetic-secret", safe);
        Assert.DoesNotContain("private.invalid", safe);
    }

    [Fact]
    public async Task ActivationFallbackMarkerKeepsTheOriginalReadOperation()
    {
        using var directory = new TemporaryDirectory();
        var reports = new FeedbackReportService(directory.Path);
        var operation = Guid.NewGuid();
        var failure = new TimeoutException(Private);

        Assert.Null(await ActivationArgumentsReader.ReadAsync<object>(() => throw failure,
            error => reports.RecordException(error), _ => Task.CompletedTask, diagnosticOperation: operation));
        using var report = await reports.CreateAsync("", true, "26.11.20.0");

        Assert.Contains($"kind=Activation operation={operation:N} task=none", report.Preview);
        Assert.Contains("stage=ActivationRead", report.Preview);
        Assert.Contains("recovered=False", report.Preview);
        Assert.DoesNotContain("synthetic-secret", report.Preview);
    }

    [Fact]
    public void LastUiObservation_KeepsFiniteNumbersAndExplicitTimeWithoutContent()
    {
        var time = DateTimeOffset.UtcNow.AddMinutes(-3);
        RuntimeFailureDiagnostics.ObserveUi(new(DiagnosticPage.Queue, double.NaN, double.PositiveInfinity, 1.5, 3, true, time));
        var error = new NullReferenceException(Private);
        error.Data["stage"] = Private;
        var safe = RuntimeFailureDiagnostics.Format(error);
        Assert.Contains("scope=last_observation", safe);
        Assert.Contains($"observed_utc={time:O}", safe);
        Assert.Contains("page=Queue width=none height=none scale=1.5 active=3 open=True", safe);
        Assert.DoesNotContain("NaN", safe);
        Assert.DoesNotContain("Infinity", safe);
        Assert.DoesNotContain("SyntheticPrivate", safe);
        Assert.DoesNotContain("synthetic-secret", safe);
        RuntimeFailureDiagnostics.ObserveUi(new(DiagnosticPage.Unknown, 0, 0, 1, 0, false, DateTimeOffset.UtcNow));
        Assert.Contains("page=Unknown", RuntimeFailureDiagnostics.Format(new InvalidOperationException(Private)));
    }

    [Fact]
    public void ExceptionFacts_KeepRealNumericCodeAndRejectMessageDataAndArbitraryTypeNames()
    {
        var error = new COMException(Private, unchecked((int)0x80070005));
        error.Data["native"] = "99 " + Private;
        var safe = RuntimeFailureDiagnostics.ExceptionFields(error);
        Assert.Equal("exception=COMException hresult=0x80070005 native=5", safe);
        Assert.Equal("unknown", RuntimeFailureDiagnostics.Codec(Private));
        Assert.Equal("unknown", RuntimeFailureDiagnostics.FormatId(Private));
        Assert.Equal("other", RuntimeFailureDiagnostics.Tool("C:\\SyntheticPrivate\\personal.exe"));
        Assert.Equal("Other", RuntimeFailureDiagnostics.ExceptionType(new SyntheticPrivateException(Private)));
    }

    [Fact]
    public async Task PlatformObservation_PreservesFailureAndNeverIncludesClipboardActivationOrDocumentText()
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.FilePicker)
        {
            Stage = RuntimeFailureStage.PickerShow, WindowOpen = true, HandlePresent = false,
            Concurrent = 2, ResultCount = 0, ActivationKind = ActivationDiagnosticKind.File
        };
        var error = new COMException(Private, unchecked((int)0x80004005));
        var actual = await Assert.ThrowsAsync<COMException>(() => diagnostic.ObserveAsync<object>(() => Task.FromException<object>(error)));
        Assert.Same(error, actual);
        Assert.Equal(RuntimeFailureStage.PickerShow, RuntimeFailureDiagnostics.Get(error)!.Stage);
        var safe = diagnostic.Format(Private, error);
        Assert.Contains("outcome=unknown", safe);
        Assert.Contains("handle_present=False concurrent=2 count=0", safe);
        Assert.DoesNotContain("SyntheticPrivate", safe);
        Assert.DoesNotContain("synthetic-secret", safe);
        Assert.DoesNotContain("private.invalid", safe);
    }

    [Fact]
    public void SynchronousPlatformObservation_PreservesOriginalFailureAndSafeFields()
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.PdfOcr)
        { Stage = RuntimeFailureStage.OcrDecode, Phase = PlatformDiagnosticPhase.OcrEngine };
        var original = new COMException(Private, unchecked((int)0x80004005));
        var actual = Assert.Throws<COMException>(() => diagnostic.Observe<object>(() => throw original));
        Assert.Same(original, actual);
        Assert.Equal(RuntimeFailureStage.OcrDecode, RuntimeFailureDiagnostics.Get(original)!.Stage);
        var safe = diagnostic.Format("failed", original);
        Assert.Contains("phase=OcrEngine", safe);
        Assert.DoesNotContain("SyntheticPrivate", safe);
        Assert.DoesNotContain("synthetic-secret", safe);
        Assert.DoesNotContain("private.invalid", safe);

        var canceled = new OperationCanceledException(Private);
        Assert.Same(canceled, Assert.Throws<OperationCanceledException>(() => diagnostic.Observe<object>(() => throw canceled)));
        Assert.Null(RuntimeFailureDiagnostics.Get(canceled));
    }

    [Fact]
    public async Task PlatformCancellation_IsNotTurnedIntoFailureOrReplaced()
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.PdfOcr) { Stage = RuntimeFailureStage.OcrRecognize };
        var original = new OperationCanceledException(Private);
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => diagnostic.ObserveAsync<object>(() => Task.FromException<object>(original)));
        Assert.Same(original, actual);
        Assert.Null(RuntimeFailureDiagnostics.Get(original));
    }

    private sealed class SyntheticPrivateException(string message) : Exception(message);
}

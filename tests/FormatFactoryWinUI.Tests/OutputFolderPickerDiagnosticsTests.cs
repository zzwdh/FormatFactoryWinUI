using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class OutputFolderPickerDiagnosticsTests
{
    private const string Private = "C:\\SyntheticPrivate\\chosen-folder token=synthetic-secret";

    [Theory]
    [InlineData("create", "PickerCreate", 1)]
    [InlineData("initialize", "PickerInitialize", 2)]
    [InlineData("show-sync", "PickerShow", 3)]
    [InlineData("show-async", "PickerShow", 3)]
    public async Task NativeFailure_KeepsExactStageOriginalExceptionAndStopsFollowingSteps(
        string failureAt, string expectedStage, int expectedCalls)
    {
        var expected = Enum.Parse<RuntimeFailureStage>(expectedStage);
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.OutputFolderPicker)
        { WindowOpen = true, HandlePresent = true, Concurrent = 1 };
        var original = new COMException(Private, unchecked((int)0x800706BE));
        var calls = new List<string>();
        var picker = new object();

        var actual = await Assert.ThrowsAsync<COMException>(() => diagnostic.ObserveAsync(() =>
            OutputFolderPickerSteps.PickAsync(diagnostic,
                () =>
                {
                    calls.Add("create");
                    if (failureAt == "create") throw original;
                    return picker;
                },
                value =>
                {
                    Assert.Same(picker, value);
                    calls.Add("initialize");
                    if (failureAt == "initialize") throw original;
                },
                value =>
                {
                    Assert.Same(picker, value);
                    calls.Add("show");
                    if (failureAt == "show-sync") throw original;
                    return Task.FromException<string?>(original);
                })));

        Assert.Same(original, actual);
        Assert.Equal(expectedCalls, calls.Count);
        var detail = RuntimeFailureDiagnostics.Get(actual)!;
        Assert.Equal(expected, detail.Stage);
        Assert.Equal(diagnostic.Id, detail.Operation);
        Assert.Equal(1, detail.Attempt);
        Assert.False(detail.Recovered);
        Assert.Null(detail.Count);
        Assert.True(detail.ElapsedMs >= 0);
        var safe = diagnostic.Format("failed", actual);
        Assert.Contains("kind=OutputFolderPicker", safe);
        Assert.Contains("stage=" + expected, safe);
        Assert.Contains("hresult=0x800706BE native=1726", safe);
        Assert.DoesNotContain("SyntheticPrivate", safe);
        Assert.DoesNotContain("synthetic-secret", safe);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task Result_OnlyRecordsCountAndPreservesPathOrCancellation(bool cancelled, int count)
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.OutputFolderPicker);
        var expectedPath = cancelled ? null : Private;
        var order = new List<string>();
        var actual = await OutputFolderPickerSteps.PickAsync(diagnostic,
            () => { order.Add("create"); return new object(); },
            _ => order.Add("initialize"),
            _ => { order.Add("show"); return Task.FromResult(expectedPath); });

        Assert.Equal(expectedPath, actual);
        Assert.Equal(["create", "initialize", "show"], order);
        Assert.Equal(RuntimeFailureStage.PickerShow, diagnostic.Stage);
        Assert.Equal(count, diagnostic.ResultCount);
        var safe = diagnostic.Format(cancelled ? "cancelled" : "completed", null);
        Assert.Contains($"outcome={(cancelled ? "cancelled" : "completed")}", safe);
        Assert.Contains("count=" + count, safe);
        Assert.DoesNotContain("SyntheticPrivate", safe);
        Assert.DoesNotContain("synthetic-secret", safe);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("initialize")]
    [InlineData("show")]
    public async Task OperationCancellation_PropagatesOriginalWithoutFailureFacts(string cancelAt)
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.OutputFolderPicker);
        var original = new OperationCanceledException(Private);
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => diagnostic.ObserveAsync(() =>
            OutputFolderPickerSteps.PickAsync(diagnostic,
                () => cancelAt == "create" ? throw original : new object(),
                _ => { if (cancelAt == "initialize") throw original; },
                _ => Task.FromException<string?>(original))));
        Assert.Same(original, actual);
        Assert.Null(RuntimeFailureDiagnostics.Get(original));
    }

    [Fact]
    public async Task PendingSelection_ObservesShowStageBeforeTheNativeResultArrives()
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.OutputFolderPicker);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = OutputFolderPickerSteps.PickAsync(diagnostic, () => new object(), _ => { }, _ => pending.Task);
        Assert.False(task.IsCompleted);
        Assert.Equal(RuntimeFailureStage.PickerShow, diagnostic.Stage);
        Assert.Null(diagnostic.ResultCount);
        pending.SetResult(Private);
        Assert.Equal(Private, await task);
        Assert.Equal(1, diagnostic.ResultCount);
    }
}

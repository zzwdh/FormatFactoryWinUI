using System.Runtime.InteropServices;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class OpticalDiagnosticsTests
{
    private const string Private = "SyntheticPrivate C:\\private\\disc.iso media-secret token=synthetic-secret https://synthetic.invalid/private";

    [Theory]
    [InlineData((int)OpticalDiagnosticPhase.DeviceInitialize)]
    [InlineData((int)OpticalDiagnosticPhase.CapabilityCheck)]
    [InlineData((int)OpticalDiagnosticPhase.Configure)]
    [InlineData((int)OpticalDiagnosticPhase.MediaCheck)]
    [InlineData((int)OpticalDiagnosticPhase.Write)]
    [InlineData((int)OpticalDiagnosticPhase.Erase)]
    [InlineData((int)OpticalDiagnosticPhase.Cancel)]
    [InlineData((int)OpticalDiagnosticPhase.Result)]
    public void Failure_PreservesOriginalAndUsesOnlyFixedLabelsAndObservedBooleans(int phaseValue)
    {
        var lines = new List<string>();
        var operation = new OpticalDiagnosticOperation(OpticalDiagnosticAction.Burn, lines.Add)
        { RecorderSupported = true, MediaSupported = false, CloseMedia = true };
        var original = new COMException(Private, unchecked((int)0xC0AA090A));
        original.Data[Private] = Private;

        var thrown = Assert.Throws<COMException>(() => operation.Observe<int>((OpticalDiagnosticPhase)phaseValue, () => throw original));

        Assert.Same(original, thrown);
        Assert.Equal(Private, thrown.Message);
        Assert.Equal(unchecked((int)0xC0AA090A), thrown.HResult);
        var line = Assert.Single(lines);
        Assert.Contains("action=Burn phase=" + (OpticalDiagnosticPhase)phaseValue + " outcome=failed", line);
        Assert.Contains("recorder_supported=true media_supported=false close_media=true full_erase=none", line);
        Assert.Contains("exception=COMException hresult=0xC0AA090A native=none", line);
        AssertPrivateAbsent(line);
    }

    [Fact]
    public void UnknownEnumsAndLoggingFailure_CannotLeakOrReplaceOriginal()
    {
        var operation = new OpticalDiagnosticOperation((OpticalDiagnosticAction)int.MaxValue, _ => throw new IOException(Private));
        var original = new COMException(Private);
        Assert.Same(original, Assert.Throws<COMException>(() =>
            operation.Observe<int>((OpticalDiagnosticPhase)int.MaxValue, () => throw original)));
        var line = operation.Format(original);
        Assert.Contains("action=Unknown phase=Unknown", line);
        AssertPrivateAbsent(line);
    }

    [Fact]
    public void Cancellation_PreservesOriginalTokenAndUsesDistinctOutcome()
    {
        var lines = new List<string>();
        var operation = new OpticalDiagnosticOperation(OpticalDiagnosticAction.Erase, lines.Add);
        Assert.Equal(42, operation.Observe(OpticalDiagnosticPhase.Erase, () => 42));
        Assert.Empty(lines);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException(Private, cancellation.Token);

        var thrown = Assert.Throws<OperationCanceledException>(() =>
            operation.Observe<int>(OpticalDiagnosticPhase.Result, () => throw original));

        Assert.Same(original, thrown);
        Assert.Equal(cancellation.Token, thrown.CancellationToken);
        var line = Assert.Single(lines);
        Assert.Contains("phase=Cancel outcome=cancelled", line);
        AssertPrivateAbsent(line);
    }

    [Fact]
    public void CancelCallbackFailure_DoesNotOverwriteTheConcurrentWritePhase()
    {
        var lines = new List<string>();
        var operation = new OpticalDiagnosticOperation(OpticalDiagnosticAction.Burn, lines.Add)
        { Phase = OpticalDiagnosticPhase.Write };
        operation.CancelFailure(new COMException(Private));
        Assert.Equal(OpticalDiagnosticPhase.Write, operation.Phase);
        Assert.Contains("phase=Cancel outcome=failed", Assert.Single(lines));
        AssertPrivateAbsent(lines[0]);
    }

    [Fact]
    public async Task Burn_MediaRefusalReportsTheExistingCheckWithoutReadingAdditionalComProperties()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "private-disc.iso");
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        var lines = new List<string>();
        var recorder = new FakeRecorder();
        var format = new FakeFormat();
        var writer = CreateWriter(recorder, format, lines.Add);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.BurnIsoAsync(new OpticalBurnRequest(Private, input)));

        Assert.Equal(LocalizationService.T("ConversionUiMediaNoDataBurn"), error.Message);
        Assert.Equal(1, format.SupportChecks);
        Assert.Equal(1, format.Assignments);
        Assert.Equal(1, format.MediaChecks);
        Assert.Equal(0, format.EraseCalls);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(input));
        var line = Assert.Single(lines);
        Assert.Contains("action=Burn phase=MediaCheck outcome=failed", line);
        Assert.Contains("recorder_supported=true media_supported=false", line);
        AssertPrivateAbsent(line);
    }

    [Theory]
    [InlineData("initialize", "DeviceInitialize", 0, 0)]
    [InlineData("capability", "CapabilityCheck", 0, 0)]
    [InlineData("configure", "Configure", 1, 0)]
    [InlineData("erase", "Erase", 1, 1)]
    public async Task Erase_FailedComCallRetainsOriginalAndCapabilityOrder(string failure, string phase, int assignments, int eraseCalls)
    {
        var lines = new List<string>();
        var original = new COMException(Private, unchecked((int)0x80070005));
        var recorder = new FakeRecorder { Failure = failure == "initialize" ? original : null };
        var format = new FakeFormat
        {
            CapabilityFailure = failure == "capability" ? original : null,
            ConfigureFailure = failure == "configure" ? original : null,
            EraseFailure = failure == "erase" ? original : null
        };
        var writer = CreateWriter(recorder, format, lines.Add);

        var thrown = await Assert.ThrowsAsync<COMException>(() =>
            writer.EraseAsync(new OpticalEraseRequest(Private, true)));

        Assert.Same(original, thrown);
        Assert.Equal(Private, thrown.Message);
        Assert.Equal(assignments, format.Assignments);
        Assert.Equal(eraseCalls, format.EraseCalls);
        Assert.Equal(0, format.MediaChecks); // Erase never queried media support before this diagnostic change.
        var line = Assert.Single(lines);
        Assert.Contains("action=Erase phase=" + phase + " outcome=failed", line);
        Assert.Contains("hresult=0x80070005 native=5", line);
        AssertPrivateAbsent(line);
    }

    [Fact]
    public async Task PreCancelledErase_DoesNotCreateComAndRetainsCancellation()
    {
        var lines = new List<string>();
        var creates = 0;
        var writer = new WindowsOpticalMediaWriter(_ => { creates++; return new object(); }, lines.Add);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.EraseAsync(new OpticalEraseRequest(Private, false), cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, creates);
        Assert.Contains("phase=Cancel outcome=cancelled", Assert.Single(lines));
        AssertPrivateAbsent(lines[0]);
    }

    private static WindowsOpticalMediaWriter CreateWriter(FakeRecorder recorder, FakeFormat format, Action<string> write) =>
        new(programmaticId => programmaticId switch
        {
            "IMAPI2.MsftDiscRecorder2" => recorder,
            "IMAPI2.MsftDiscFormat2Erase" or "IMAPI2.MsftDiscFormat2Data" => format,
            _ => throw new InvalidOperationException("Unexpected COM class in synthetic test.")
        }, write);

    private static void AssertPrivateAbsent(string line)
    {
        Assert.DoesNotContain("SyntheticPrivate", line);
        Assert.DoesNotContain("private-disc", line);
        Assert.DoesNotContain("disc.iso", line);
        Assert.DoesNotContain("media-secret", line);
        Assert.DoesNotContain("token", line);
        Assert.DoesNotContain("synthetic.invalid", line);
    }

    public sealed class FakeRecorder
    {
        public Exception? Failure { get; init; }
        public void InitializeDiscRecorder(string _) { if (Failure is not null) throw Failure; }
    }

    public sealed class FakeFormat
    {
        public Exception? CapabilityFailure { get; init; }
        public Exception? ConfigureFailure { get; init; }
        public Exception? EraseFailure { get; init; }
        public int SupportChecks { get; private set; }
        public int Assignments { get; private set; }
        public int MediaChecks { get; private set; }
        public int EraseCalls { get; private set; }
        public object? Recorder
        {
            set { Assignments++; if (ConfigureFailure is not null) throw ConfigureFailure; }
        }
        public string? ClientName { get; set; }
        public bool ForceMediaToBeClosed { get; set; }
        public bool FullErase { get; set; }
        public bool IsRecorderSupported(object _)
        {
            SupportChecks++;
            if (CapabilityFailure is not null) throw CapabilityFailure;
            return true;
        }
        public bool IsCurrentMediaSupported(object _) { MediaChecks++; return false; }
        public void EraseMedia() { EraseCalls++; if (EraseFailure is not null) throw EraseFailure; }
    }
}

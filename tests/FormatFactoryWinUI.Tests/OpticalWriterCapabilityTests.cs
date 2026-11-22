using System.Runtime.InteropServices;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class OpticalWriterCapabilityTests
{
    [Fact]
    public async Task Erase_UnsupportedRecorderIsRejectedBeforeAssignment()
    {
        var recorder = new FakeRecorder();
        var format = new FakeFormat { Supported = false };
        var writer = CreateWriter(recorder, format);
        var progress = new CaptureProgress();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.EraseAsync(new OpticalEraseRequest("synthetic-recorder", false), progress));

        Assert.Equal(LocalizationService.T("ConversionUiDeviceNoImapi2Erase"), error.Message);
        Assert.Equal(1, format.SupportChecks);
        Assert.Equal(0, format.Assignments);
        Assert.Equal(0, format.EraseCalls);
        Assert.Empty(progress.Values);
    }

    [Fact]
    public async Task Burn_UnsupportedRecorderIsRejectedBeforeAssignmentOrImageRead()
    {
        using var directory = new TemporaryDirectory();
        var image = Path.Combine(directory.Path, "synthetic.iso");
        await File.WriteAllBytesAsync(image, [1, 2, 3]);
        var recorder = new FakeRecorder();
        var format = new FakeFormat { Supported = false };
        var writer = CreateWriter(recorder, format);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.BurnIsoAsync(new OpticalBurnRequest("synthetic-recorder", image)));

        Assert.Equal(LocalizationService.T("ConversionUiDeviceNoImapi2Burn"), error.Message);
        Assert.Equal(1, format.SupportChecks);
        Assert.Equal(0, format.Assignments);
        Assert.Equal(0, format.EraseCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Erase_SupportedRecorderRetainsRequestedModeAndProgress(bool fullErase)
    {
        var recorder = new FakeRecorder();
        var format = new FakeFormat();
        var writer = CreateWriter(recorder, format);
        var progress = new CaptureProgress();

        await writer.EraseAsync(new OpticalEraseRequest("synthetic-recorder", fullErase), progress);

        Assert.Equal("synthetic-recorder", recorder.InitializedId);
        Assert.Equal(1, format.SupportChecks);
        Assert.Equal(1, format.Assignments);
        Assert.Same(recorder, format.Recorder);
        Assert.Equal(fullErase, format.FullErase);
        Assert.Equal("FluentFormatConverter", format.ClientName);
        Assert.Equal(1, format.EraseCalls);
        Assert.Equal(2, progress.Values.Count);
    }

    [Fact]
    public async Task Erase_UnexpectedCapabilityFailureRemainsVisible()
    {
        var expected = new COMException("Synthetic capability failure.", unchecked((int)0x80004005));
        var format = new FakeFormat { SupportFailure = expected };
        var writer = CreateWriter(new FakeRecorder(), format);

        var error = await Assert.ThrowsAsync<COMException>(() =>
            writer.EraseAsync(new OpticalEraseRequest("synthetic-recorder", false)));

        Assert.Same(expected, error);
        Assert.Equal(0, format.Assignments);
        Assert.Equal(0, format.EraseCalls);
    }

    [Fact]
    public async Task Erase_PreCancelledRequestDoesNotCreateComObjects()
    {
        var creates = 0;
        var writer = new WindowsOpticalMediaWriter(_ => { creates++; return new object(); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.EraseAsync(new OpticalEraseRequest("synthetic-recorder", false),
                cancellationToken: cancellation.Token));

        Assert.Equal(0, creates);
    }

    private static WindowsOpticalMediaWriter CreateWriter(FakeRecorder recorder, FakeFormat format) =>
        new(programmaticId => programmaticId switch
        {
            "IMAPI2.MsftDiscRecorder2" => recorder,
            "IMAPI2.MsftDiscFormat2Erase" or "IMAPI2.MsftDiscFormat2Data" => format,
            _ => throw new InvalidOperationException("Unexpected COM class in synthetic test.")
        });

    public sealed class FakeRecorder
    {
        public string? InitializedId { get; private set; }
        public void InitializeDiscRecorder(string recorderId) => InitializedId = recorderId;
    }

    public sealed class FakeFormat
    {
        private object? _recorder;
        public bool Supported { get; init; } = true;
        public Exception? SupportFailure { get; init; }
        public int SupportChecks { get; private set; }
        public int Assignments { get; private set; }
        public int EraseCalls { get; private set; }
        public string? ClientName { get; set; }
        public bool FullErase { get; set; }
        public bool ForceMediaToBeClosed { get; set; }
        public object? Recorder
        {
            get => _recorder;
            set
            {
                Assignments++;
                if (!Supported)
                    throw new COMException("Synthetic unsupported recorder.", unchecked((int)0xC0AA090A));
                _recorder = value;
            }
        }

        public bool IsRecorderSupported(object recorder)
        {
            SupportChecks++;
            if (SupportFailure is not null) throw SupportFailure;
            return Supported;
        }

        public void EraseMedia() => EraseCalls++;
    }

    private sealed class CaptureProgress : IProgress<OpticalOperationProgress>
    {
        public List<OpticalOperationProgress> Values { get; } = [];
        public void Report(OpticalOperationProgress value) => Values.Add(value);
    }
}

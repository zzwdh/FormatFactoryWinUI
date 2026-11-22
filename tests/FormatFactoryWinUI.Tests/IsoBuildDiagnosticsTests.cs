using System.Runtime.InteropServices;
using FormatFactoryWinUI.Disc;

namespace FormatFactoryWinUI.Tests;

public sealed class IsoBuildDiagnosticsTests
{
    private const string Private = "SyntheticPrivate C:\\private\\input.txt volume-secret token=synthetic-secret https://synthetic.invalid/private";

    [Theory]
    [InlineData((int)IsoBuildPhase.AddTree)]
    [InlineData((int)IsoBuildPhase.AddFile)]
    [InlineData((int)IsoBuildPhase.CreateResultImage)]
    [InlineData((int)IsoBuildPhase.ReadImageStream)]
    [InlineData((int)IsoBuildPhase.WriteImageStream)]
    [InlineData((int)IsoBuildPhase.Commit)]
    public void Failure_PreservesOriginalExceptionAndReportsBoundedObservedFacts(int phaseValue)
    {
        var phase = (IsoBuildPhase)phaseValue;
        var lines = new List<string>();
        var operation = new IsoBuildDiagnosticOperation(lines.Add)
        {
            SourceCount = 3, CompletedSources = 2,
            FileSystems = IsoFileSystems.Iso9660 | IsoFileSystems.Joliet,
            BlockSize = 2048, TotalBytes = 8192, CompletedBytes = 4096
        };
        var error = new COMException(Private, unchecked((int)0xC0AAB100));
        error.Data["phase"] = Private;

        var thrown = Assert.Throws<COMException>(() => operation.Observe<int>(phase, () => throw error));

        Assert.Same(error, thrown);
        Assert.Equal(Private, thrown.Message);
        Assert.Equal(unchecked((int)0xC0AAB100), thrown.HResult);
        var line = Assert.Single(lines);
        Assert.Contains("phase=" + phase + " target=iso", line);
        Assert.Contains("source_count=3 completed_sources=2 filesystems=3 block_size=2048 total_bytes=8192 completed_bytes=4096", line);
        Assert.Contains("exception=COMException hresult=0xC0AAB100 native=none", line);
        Assert.DoesNotContain("SyntheticPrivate", line);
        Assert.DoesNotContain("input.txt", line);
        Assert.DoesNotContain("volume-secret", line);
        Assert.DoesNotContain("token", line);
        Assert.DoesNotContain("synthetic.invalid", line);
    }

    [Fact]
    public void CancellationAndSuccessfulWork_DoNotGenerateFailureObservations()
    {
        var lines = new List<string>();
        var operation = new IsoBuildDiagnosticOperation(lines.Add);
        Assert.Equal(42, operation.Observe(IsoBuildPhase.AddTree, () => 42));
        using var source = new CancellationTokenSource();
        source.Cancel();
        var error = new OperationCanceledException(Private, source.Token);
        var thrown = Assert.Throws<OperationCanceledException>(() => operation.Observe<int>(IsoBuildPhase.AddFile, () => throw error));
        Assert.Same(error, thrown);
        Assert.Equal(source.Token, thrown.CancellationToken);
        operation.Fail(error);
        Assert.Empty(lines);
    }

    [Fact]
    public void UnknownEnumsAndDiagnosticWriteFailures_DoNotLeakOrReplaceBusinessFailure()
    {
        var operation = new IsoBuildDiagnosticOperation(_ => throw new IOException(Private))
        { FileSystems = (IsoFileSystems)128 };
        var error = new IOException(Private);
        var thrown = Assert.Throws<IOException>(() => operation.Observe<int>((IsoBuildPhase)int.MaxValue, () => throw error));
        Assert.Same(error, thrown);
        var line = operation.Format(error);
        Assert.Contains("phase=Unknown target=iso", line);
        Assert.Contains("filesystems=unknown", line);
        Assert.DoesNotContain("SyntheticPrivate", line);
        Assert.DoesNotContain("synthetic-secret", line);
    }
}

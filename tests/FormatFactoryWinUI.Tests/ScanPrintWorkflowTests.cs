using System.Runtime.InteropServices;
using FormatFactoryWinUI.Documents;

namespace FormatFactoryWinUI.Tests;

public sealed class ScanPrintWorkflowTests
{
    private static WindowsScanRequest FeederRequest(int maximum = 100) =>
        new("test-device", Path.Combine(Path.GetTempPath(), "scan.png"), Source: WindowsScanSource.Feeder, MaximumPages: maximum);

    [Fact]
    public async Task Feeder_StopsAtEmptyPaperAndPreservesAcquisitionOrder()
    {
        using var session = new FakeSession();
        session.Acquisitions.Enqueue(() => ["page-0001.png"]);
        session.Acquisitions.Enqueue(() => ["page-0002.png", "page-0003.png"]);
        session.Acquisitions.Enqueue(() => throw new COMException("empty", unchecked((int)0x80210003)));
        var result = await new WindowsScannerService((_, _) => session).ScanBatchAsync(FeederRequest());
        Assert.Equal(["page-0001.png", "page-0002.png", "page-0003.png"], result.Paths);
        Assert.Equal([1, 2, 4], session.RequestedNumbers);
        Assert.Null(result.Error);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Feeder_JamPreservesCompletedPagesAndReportsFailure()
    {
        using var session = new FakeSession();
        session.Acquisitions.Enqueue(() => ["page-0001.png"]);
        session.Acquisitions.Enqueue(() => throw new COMException("jam", unchecked((int)0x80210002)));
        var result = await new WindowsScannerService((_, _) => session).ScanBatchAsync(FeederRequest());
        Assert.Equal(["page-0001.png"], result.Paths);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.False(result.Canceled);
    }

    [Fact]
    public async Task Feeder_CancelBetweenSheetsKeepsCurrentSheet()
    {
        using var cancellation = new CancellationTokenSource();
        using var session = new FakeSession();
        session.Acquisitions.Enqueue(() => { cancellation.Cancel(); return ["front.png", "back.png"]; });
        var result = await new WindowsScannerService((_, _) => session).ScanBatchAsync(FeederRequest(), cancellation.Token);
        Assert.True(result.Canceled);
        Assert.Equal(["front.png", "back.png"], result.Paths);
        Assert.Single(session.RequestedNumbers);
    }

    [Fact]
    public async Task Duplex_PartialSheetFailurePreservesTheAlreadyWrittenFront()
    {
        using var session = new FakeSession();
        session.Acquisitions.Enqueue(() => throw new ScanTransferException(["front.png"], new IOException("disk full")));
        var result = await new WindowsScannerService((_, _) => session).ScanBatchAsync(FeederRequest() with { Duplex = true });
        Assert.Equal(["front.png"], result.Paths);
        Assert.Equal("disk full", result.Error);
    }

    [Fact]
    public void Duplex_RejectsAnOddPageLimitBeforeConnecting()
    {
        var service = new WindowsScannerService((_, _) => throw new InvalidOperationException("must not connect"));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = service.ScanBatchAsync(FeederRequest(3) with { Duplex = true }); });
    }
    [Fact]
    public async Task UnsupportedDuplex_DoesNotFeedAnyPaper()
    {
        using var session = new FakeSession { Capabilities = new(true, true, false, [300]) };
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new WindowsScannerService((_, _) => session).ScanBatchAsync(FeederRequest() with { Duplex = true }));
        Assert.Empty(session.RequestedNumbers);
    }

    [Fact]
    public async Task Flatbed_AcquiresOnlyOneSheetAndReportsDriverFallback()
    {
        using var session = new FakeSession { Warnings = ["driver default"] };
        session.Acquisitions.Enqueue(() => ["page.png"]);
        var result = await new WindowsScannerService((_, _) => session).ScanBatchAsync(FeederRequest() with { Source = WindowsScanSource.Flatbed });
        Assert.Single(result.Paths);
        Assert.Equal(["driver default"], result.Warnings);
        Assert.Single(session.RequestedNumbers);
    }

    [Fact]
    public async Task Feeder_LimitDoesNotAcquireAnotherSheet()
    {
        using var session = new FakeSession();
        session.Acquisitions.Enqueue(() => ["page.png"]);
        var result = await new WindowsScannerService((_, _) => session).ScanBatchAsync(FeederRequest(1));
        Assert.Single(result.Paths);
        Assert.Single(result.Warnings);
        Assert.Single(session.RequestedNumbers);
    }

    [Fact]
    public void WiaProperties_UsePropertyIdRatherThanCollectionPosition()
    {
        var device = new FakeDevice();
        device.Properties.Values.Add(new FakeProperty { PropertyID = 100, Name = "Other", Value = 9 });
        device.Properties.Values.Add(new FakeProperty { PropertyID = 6147, Name = "Resolution", Value = 150 });
        Assert.Equal("150", WindowsScannerService.ReadString(device, 6147));
        Assert.True(WindowsScannerService.WriteProperty(device, 6147, 300));
        Assert.Equal(9, device.Properties.Values[0].Value);
        Assert.Equal(300, device.Properties.Values[1].Value);
        Assert.Null(WindowsScannerService.ReadString(device, 999));
        Assert.False(WindowsScannerService.WriteProperty(device, 999, 300));
    }

    [Fact]
    public void PageNames_PreserveNumericOrderAndDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scan-order");
        var names = Enumerable.Range(1, 12).Select(page => WindowsScannerService.PagePath(Path.Combine(directory, "scan.png"), page, true)).ToArray();
        Assert.Equal(names, names.Order(StringComparer.Ordinal).ToArray());
        Assert.All(names, path => Assert.Equal(directory, Path.GetDirectoryName(path)));
        Assert.EndsWith("scan-0012.png", names[^1]);
    }

    [Fact]
    public void PrintPageRanges_AreSortedDeduplicatedAndBounded()
    {
        Assert.Equal([1, 2, 3, 5], WindowsPrintService.SelectPages(5, [(5, 5), (2, 3), (1, 2)]));
        Assert.Equal([1, 2, 3], WindowsPrintService.SelectPages(3, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPrintService.SelectPages(5, [(0, 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPrintService.SelectPages(5, [(4, 6)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsPrintService.SelectPages(5, [(4, 2)]));
    }

    [Theory]
    [InlineData("page.PDF", true)]
    [InlineData("pages.tiff", true)]
    [InlineData("photo.png", true)]
    [InlineData("document.docx", false)]
    [InlineData("document.xps", false)]
    public void PrintRoute_UsesNativePreviewOnlyForSupportedInputs(string path, bool expected) =>
        Assert.Equal(expected, WindowsPrintService.SupportsNativePreview(path));

    public sealed class FakeDevice { public FakeProperties Properties { get; } = new(); }
    public sealed class FakeProperties
    {
        public List<FakeProperty> Values { get; } = [];
        public int Count => Values.Count;
        public FakeProperty this[object index] => index is int ordinal ? Values[ordinal - 1] : Values.Single(value => value.Name == (string)index);
    }
    public sealed class FakeProperty
    {
        public int PropertyID { get; init; }
        public string Name { get; init; } = string.Empty;
        public object Value { get; set; } = 0;
    }
    private sealed class FakeSession : IWindowsScanSession
    {
        public WindowsScannerCapabilities Capabilities { get; set; } = new(true, true, true, [150, 300, 600]);
        public Queue<Func<IReadOnlyList<string>>> Acquisitions { get; } = new();
        public List<int> RequestedNumbers { get; } = [];
        public IReadOnlyList<string> Warnings { get; set; } = [];
        public bool Disposed { get; private set; }
        public IReadOnlyList<string> Configure(WindowsScanRequest request) => Warnings;
        public IReadOnlyList<string> Acquire(WindowsScanRequest request, int firstPageNumber)
        {
            RequestedNumbers.Add(firstPageNumber);
            return Acquisitions.Dequeue()();
        }
        public void Dispose() => Disposed = true;
    }
}



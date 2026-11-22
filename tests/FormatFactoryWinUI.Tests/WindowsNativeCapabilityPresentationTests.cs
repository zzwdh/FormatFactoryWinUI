using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class WindowsNativeCapabilityPresentationTests
{
    [Theory]
    [InlineData(NativeCapabilityStatus.Available, false)]
    [InlineData(NativeCapabilityStatus.Unavailable, true)]
    [InlineData(NativeCapabilityStatus.NotSupported, true)]
    [InlineData(NativeCapabilityStatus.Failed, true)]
    public void NeedsAttention_PreservesStatusMeaning(NativeCapabilityStatus status, bool expected)
    {
        var entry = Entry("other", status, 0);
        Assert.Equal(expected, WindowsNativeCapabilityPresentation.NeedsAttention(entry));
        Assert.Equal(status, entry.Status);
    }

    [Theory]
    [InlineData(WindowsNativeCapabilityIds.Scanner)]
    [InlineData(WindowsNativeCapabilityIds.Printer)]
    [InlineData(WindowsNativeCapabilityIds.OpticalMedia)]
    public void AvailableServiceWithNoDevice_NeedsAttentionButRemainsAvailable(string id)
    {
        var entry = Entry(id, NativeCapabilityStatus.Available, 0);
        Assert.True(WindowsNativeCapabilityPresentation.NeedsAttention(entry));
        Assert.True(entry.IsAvailable);
        Assert.False(WindowsNativeCapabilityPresentation.NeedsAttention(entry with { ItemCount = 1 }));
        Assert.False(WindowsNativeCapabilityPresentation.NeedsAttention(entry with { ItemCount = null }));
    }

    [Fact]
    public void FilteringAndCycling_KeepDiagnosticsAndHandleRemovedCurrentItem()
    {
        var ready = Entry("ready", NativeCapabilityStatus.Available, 1);
        var missing = Entry(WindowsNativeCapabilityIds.Scanner, NativeCapabilityStatus.Available, 0);
        var failed = Entry("failed", NativeCapabilityStatus.Failed, null) with
        {
            Details = new Dictionary<string, string> { ["reason"] = "original diagnostic" }
        };
        var snapshot = new WindowsNativeCapabilitySnapshot(DateTimeOffset.UtcNow, [ready, missing, failed]);
        Assert.Same(snapshot.Capabilities, WindowsNativeCapabilityPresentation.GetVisibleEntries(snapshot, false));
        Assert.Equal([missing, failed], WindowsNativeCapabilityPresentation.GetVisibleEntries(snapshot, true));
        Assert.Same(missing, WindowsNativeCapabilityPresentation.FindNextAttention(snapshot, null));
        Assert.Same(failed, WindowsNativeCapabilityPresentation.FindNextAttention(snapshot, missing.Id.ToUpperInvariant()));
        Assert.Same(missing, WindowsNativeCapabilityPresentation.FindNextAttention(snapshot, failed.Id));
        Assert.Same(missing, WindowsNativeCapabilityPresentation.FindNextAttention(snapshot, "removed"));
        Assert.Equal("original diagnostic", failed.Details!["reason"]);
        Assert.Null(WindowsNativeCapabilityPresentation.FindNextAttention(snapshot with { Capabilities = [ready] }, null));
    }

    [Fact]
    public void DeviceHint_DistinguishesMissingHardwareFromFailedChecksAndIgnoresOtherBackends()
    {
        var scanner = Entry(WindowsNativeCapabilityIds.Scanner, NativeCapabilityStatus.Available, 1);
        var snapshot = new WindowsNativeCapabilitySnapshot(DateTimeOffset.UtcNow,
            [scanner, Entry(WindowsNativeCapabilityIds.MediaFoundation, NativeCapabilityStatus.Failed, null)]);
        Assert.Null(WindowsNativeCapabilityPresentation.GetDeviceHintKey(snapshot, 1));
        Assert.Equal("NativeUiWorkflowConnectDevice", WindowsNativeCapabilityPresentation.GetDeviceHintKey(snapshot, 0));
        Assert.Equal("NativeUiWorkflowConnectDevice", WindowsNativeCapabilityPresentation.GetDeviceHintKey(
            snapshot with { Capabilities = [scanner with { ItemCount = 0 }] }, 1));
        Assert.Equal("NativeUiWorkflowDeviceUnavailable", WindowsNativeCapabilityPresentation.GetDeviceHintKey(
            snapshot with { Capabilities = [scanner with { Status = NativeCapabilityStatus.Unavailable }] }, 1));
        Assert.Equal("NativeUiWorkflowDeviceCheckFailed", WindowsNativeCapabilityPresentation.GetDeviceHintKey(
            snapshot with { Capabilities = [scanner with { Status = NativeCapabilityStatus.Failed }] }, 1));
    }

    private static WindowsNativeCapabilityEntry Entry(string id, NativeCapabilityStatus status, int? count) =>
        new(id, id, status, "diagnostic", count);
}

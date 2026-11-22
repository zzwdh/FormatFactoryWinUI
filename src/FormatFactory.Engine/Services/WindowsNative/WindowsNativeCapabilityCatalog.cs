namespace FormatFactoryWinUI.Services;

/// <summary>Stable identifiers for Windows-native facilities surfaced by the application.</summary>
public static class WindowsNativeCapabilityIds
{
    public const string MediaFoundation = "media-foundation";
    public const string WindowsImaging = "windows-imaging";
    public const string AudioEndpoints = "audio-endpoints";
    public const string Direct3DVideo = "direct3d-video";
    public const string ShellProperties = "shell-properties";
    public const string ShellThumbnails = "shell-thumbnails";
    public const string WindowsFilter = "windows-ifilter";
    public const string DirectWriteFonts = "directwrite-fonts";
    public const string Scanner = "wia-scanner";
    public const string Printer = "windows-printing";
    public const string OpticalMedia = "optical-imapi";
    public const string IsoMount = "iso-mount";
    public const string DeviceNotifications = "device-notifications";
    public const string PowerState = "power-state";
    public const string Recovery = "application-recovery";
    public const string DirectoryWatcher = "directory-watcher";
    public const string Accessibility = "accessibility";
}

public sealed record WindowsNativeCapabilityEntry(
    string Id,
    string DisplayName,
    NativeCapabilityStatus Status,
    string Summary,
    int? ItemCount = null,
    IReadOnlyDictionary<string, string>? Details = null)
{
    public bool IsAvailable => Status == NativeCapabilityStatus.Available;
}

public sealed record WindowsNativeCapabilitySnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<WindowsNativeCapabilityEntry> Capabilities)
{
    public int AvailableCount => Capabilities.Count(item => item.IsAvailable);

    public int UnavailableCount => Capabilities.Count(item =>
        item.Status is NativeCapabilityStatus.Unavailable or NativeCapabilityStatus.NotSupported);

    public int FailedCount => Capabilities.Count(item => item.Status == NativeCapabilityStatus.Failed);

    public WindowsNativeCapabilityEntry? Find(string id) =>
        Capabilities.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public string ToLocalizedSummary()
    {
        if (Capabilities.Count == 0)
        {
            return LocalizationService.T("NativeCapabilitySummaryEmpty");
        }

        var parts = new List<string>
        {
            LocalizationService.T(
                "NativeCapabilitySummaryAvailable",
                AvailableCount,
                Capabilities.Count)
        };
        if (UnavailableCount > 0)
        {
            parts.Add(LocalizationService.T(
                "NativeCapabilitySummaryUnavailable",
                UnavailableCount));
        }
        if (FailedCount > 0)
        {
            parts.Add(LocalizationService.T(
                "NativeCapabilitySummaryFailed",
                FailedCount));
        }

        return LocalizationService.T(
            "NativeCapabilitySummarySentence",
            string.Join(LocalizationService.T("NativeCapabilitySummarySeparator"), parts));
    }

    /// <summary>
    /// Compatibility wrapper retained for existing callers. The returned text follows the
    /// currently selected application language.
    /// </summary>
    public string ToChineseSummary() => ToLocalizedSummary();
}

public interface IWindowsNativeCapabilityProbe
{
    string Id { get; }

    string DisplayName { get; }

    ValueTask<WindowsNativeCapabilityEntry> ProbeAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Captures one fault-isolated snapshot of all registered Windows-native facilities.
/// </summary>
public sealed class WindowsNativeCapabilityCatalog
{
    private readonly IReadOnlyList<IWindowsNativeCapabilityProbe> _probes;

    public WindowsNativeCapabilityCatalog(IEnumerable<IWindowsNativeCapabilityProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes.ToArray();
    }

    public static WindowsNativeCapabilityCatalog CreateDefault() =>
        new(WindowsNativeCapabilityProbes.CreateDefault());

    public async Task<WindowsNativeCapabilitySnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var captures = _probes.Select((probe, index) =>
            CaptureOneAsync(probe, index, cancellationToken)).ToArray();
        var results = await Task.WhenAll(captures).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new WindowsNativeCapabilitySnapshot(
            DateTimeOffset.UtcNow,
            results.OrderBy(result => result.Index).Select(result => result.Entry).ToArray());
    }

    private static Task<(int Index, WindowsNativeCapabilityEntry Entry)> CaptureOneAsync(
        IWindowsNativeCapabilityProbe probe,
        int index,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            try
            {
                var entry = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
                return (index, entry);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (index, new WindowsNativeCapabilityEntry(
                    probe.Id,
                    probe.DisplayName,
                    NativeCapabilityStatus.Failed,
                    string.IsNullOrWhiteSpace(exception.Message)
                        ? LocalizationService.T("NativeCapabilityProbeFailed")
                        : LocalizationService.T(
                            "NativeCapabilityProbeFailedWithMessage",
                            exception.Message)));
            }
        }, CancellationToken.None);
}

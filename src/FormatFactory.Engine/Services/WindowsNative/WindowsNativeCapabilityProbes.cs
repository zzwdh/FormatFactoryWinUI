using System.Runtime.InteropServices;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Documents;

namespace FormatFactoryWinUI.Services;

internal static class WindowsNativeCapabilityProbes
{
    public static IReadOnlyList<IWindowsNativeCapabilityProbe> CreateDefault() =>
    [
        new DelegateProbe(
            WindowsNativeCapabilityIds.MediaFoundation,
            "NativeCapabilityNameMediaFoundation",
            ProbeMediaFoundationAsync),
        new DelegateProbe(
            WindowsNativeCapabilityIds.WindowsImaging,
            "NativeCapabilityNameWindowsImaging",
            ProbeWindowsImagingAsync),
        new DelegateProbe(
            WindowsNativeCapabilityIds.AudioEndpoints,
            "NativeCapabilityNameAudioEndpoints",
            ProbeAudioEndpointsAsync),
        new DelegateProbe(
            WindowsNativeCapabilityIds.Direct3DVideo,
            "NativeCapabilityNameDirect3DVideo",
            ProbeDirect3DAsync),
        NativeExports(
            WindowsNativeCapabilityIds.ShellProperties,
            "NativeCapabilityNameShellProperties",
            "NativeCapabilityShellPropertiesAvailable",
            ("shell32.dll", "SHGetPropertyStoreFromParsingName"),
            ("propsys.dll", "PSGetNameFromPropertyKey")),
        NativeExports(
            WindowsNativeCapabilityIds.ShellThumbnails,
            "NativeCapabilityNameShellThumbnails",
            "NativeCapabilityShellThumbnailsAvailable",
            ("shell32.dll", "SHCreateItemFromParsingName"),
            ("ole32.dll", "CoCreateInstance")),
        NativeExports(
            WindowsNativeCapabilityIds.WindowsFilter,
            "NativeCapabilityNameWindowsFilter",
            "NativeCapabilityWindowsFilterAvailable",
            ("query.dll", "LoadIFilter")),
        new DelegateProbe(
            WindowsNativeCapabilityIds.DirectWriteFonts,
            "NativeCapabilityNameDirectWriteFonts",
            ProbeDirectWriteAsync),
        new DelegateProbe(
            WindowsNativeCapabilityIds.Scanner,
            "NativeCapabilityNameScanner",
            ProbeScannersAsync),
        new DelegateProbe(
            WindowsNativeCapabilityIds.Printer,
            "NativeCapabilityNamePrinter",
            ProbePrintersAsync),
        new DelegateProbe(
            WindowsNativeCapabilityIds.OpticalMedia,
            "NativeCapabilityNameOpticalMedia",
            ProbeOpticalMediaAsync),
        NativeExports(
            WindowsNativeCapabilityIds.IsoMount,
            "NativeCapabilityNameIsoMount",
            "NativeCapabilityIsoMountAvailable",
            ("virtdisk.dll", "OpenVirtualDisk"),
            ("virtdisk.dll", "AttachVirtualDisk"),
            ("virtdisk.dll", "DetachVirtualDisk")),
        new DelegateProbe(
            WindowsNativeCapabilityIds.DeviceNotifications,
            "NativeCapabilityNameDeviceNotifications",
            ProbeDeviceNotificationsAsync),
        new DelegateProbe(
            WindowsNativeCapabilityIds.PowerState,
            "NativeCapabilityNamePowerState",
            ProbePowerStateAsync),
        NativeExports(
            WindowsNativeCapabilityIds.Recovery,
            "NativeCapabilityNameRecovery",
            "NativeCapabilityRecoveryAvailable",
            ("kernel32.dll", "RegisterApplicationRestart"),
            ("kernel32.dll", "RegisterApplicationRecoveryCallback")),
        NativeExports(
            WindowsNativeCapabilityIds.DirectoryWatcher,
            "NativeCapabilityNameDirectoryWatcher",
            "NativeCapabilityDirectoryWatcherAvailable",
            ("kernel32.dll", "ReadDirectoryChangesW"),
            ("kernel32.dll", "CancelIoEx")),
        new DelegateProbe(
            WindowsNativeCapabilityIds.Accessibility,
            "NativeCapabilityNameAccessibility",
            ProbeAccessibilityAsync)
    ];

    private static ValueTask<WindowsNativeCapabilityEntry> ProbeMediaFoundationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new MediaFoundationNativeService().EnumerateHardwareTransforms();
        var count = result.Value?.Count;
        return ValueTask.FromResult(FromResult(
            WindowsNativeCapabilityIds.MediaFoundation,
            Name("NativeCapabilityNameMediaFoundation"),
            result,
            count is null
                ? null
                : LocalizationService.T("NativeCapabilityHardwareMftFound", count),
            count));
    }

    private static ValueTask<WindowsNativeCapabilityEntry> ProbeWindowsImagingAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new WindowsImagingNativeService().EnumerateCodecs();
        var count = result.Value?.Count;
        return ValueTask.FromResult(FromResult(
            WindowsNativeCapabilityIds.WindowsImaging,
            Name("NativeCapabilityNameWindowsImaging"),
            result,
            count is null
                ? null
                : LocalizationService.T("NativeCapabilityWicCodecsFound", count),
            count));
    }

    private static ValueTask<WindowsNativeCapabilityEntry> ProbeAudioEndpointsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new WindowsAudioEndpointService().EnumerateEndpoints();
        var count = result.Value?.Count;
        return ValueTask.FromResult(FromResult(
            WindowsNativeCapabilityIds.AudioEndpoints,
            Name("NativeCapabilityNameAudioEndpoints"),
            result,
            count is null
                ? null
                : LocalizationService.T("NativeCapabilityAudioEndpointsFound", count),
            count));
    }

    private static ValueTask<WindowsNativeCapabilityEntry> ProbeDirect3DAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Direct3DVideoCapabilityService.Probe();
        IReadOnlyDictionary<string, string>? details = result.Value is null
            ? null
            : new Dictionary<string, string>
            {
                ["FeatureLevel"] = result.Value.FeatureLevel,
                ["Adapter"] = result.Value.AdapterName,
                ["DXVA"] = YesNo(result.Value.SupportsDxva)
            };
        return ValueTask.FromResult(new WindowsNativeCapabilityEntry(
            WindowsNativeCapabilityIds.Direct3DVideo,
            Name("NativeCapabilityNameDirect3DVideo"),
            result.Status,
            result.Value is null
                ? LocalizationService.T(
                    "NativeCapabilityDetectionResult",
                    Name("NativeCapabilityNameDirect3DVideo"),
                    result.Message)
                : LocalizationService.T(
                    "NativeCapabilityDirect3DProbeSummary",
                    result.Value.FeatureLevel,
                    LocalizationService.T(
                        result.Value.SupportsDxva
                            ? "NativeCapabilityAvailable"
                            : "NativeCapabilityUnavailable")),
            result.Value?.DxvaDecoderProfiles.Count,
            details));
    }

    private static ValueTask<WindowsNativeCapabilityEntry> ProbeDirectWriteAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(NotSupported(
                WindowsNativeCapabilityIds.DirectWriteFonts,
                Name("NativeCapabilityNameDirectWriteFonts")));
        }

        var fonts = new DirectWriteFontCatalog().Discover();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new WindowsNativeCapabilityEntry(
            WindowsNativeCapabilityIds.DirectWriteFonts,
            Name("NativeCapabilityNameDirectWriteFonts"),
            fonts.Count > 0 ? NativeCapabilityStatus.Available : NativeCapabilityStatus.Unavailable,
            fonts.Count > 0
                ? LocalizationService.T("NativeCapabilityFontFamiliesFound", fonts.Count)
                : LocalizationService.T("NativeCapabilityNoDirectWriteFonts"),
            fonts.Count));
    }

    private static async ValueTask<WindowsNativeCapabilityEntry> ProbeScannersAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NotSupported(
                WindowsNativeCapabilityIds.Scanner,
                Name("NativeCapabilityNameScanner"));
        }
        if (Type.GetTypeFromProgID("WIA.DeviceManager", throwOnError: false) is null)
        {
            return new WindowsNativeCapabilityEntry(
                WindowsNativeCapabilityIds.Scanner,
                Name("NativeCapabilityNameScanner"),
                NativeCapabilityStatus.Unavailable,
                LocalizationService.T("NativeCapabilityWiaUnavailable"));
        }

        var scanners = await new WindowsScannerService()
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        return new WindowsNativeCapabilityEntry(
            WindowsNativeCapabilityIds.Scanner,
            Name("NativeCapabilityNameScanner"),
            NativeCapabilityStatus.Available,
            scanners.Count == 0
                ? LocalizationService.T("NativeCapabilityNoScanners")
                : LocalizationService.T("NativeCapabilityScannersFound", scanners.Count),
            scanners.Count);
    }

    private static ValueTask<WindowsNativeCapabilityEntry> ProbePrintersAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(NotSupported(
                WindowsNativeCapabilityIds.Printer,
                Name("NativeCapabilityNamePrinter")));
        }

        var printers = new WindowsPrintService().DiscoverPrinters();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new WindowsNativeCapabilityEntry(
            WindowsNativeCapabilityIds.Printer,
            Name("NativeCapabilityNamePrinter"),
            NativeCapabilityStatus.Available,
            printers.Count == 0
                ? LocalizationService.T("NativeCapabilityNoPrinters")
                : LocalizationService.T("NativeCapabilityPrintersFound", printers.Count),
            printers.Count));
    }

    private static async ValueTask<WindowsNativeCapabilityEntry> ProbeOpticalMediaAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NotSupported(
                WindowsNativeCapabilityIds.OpticalMedia,
                Name("NativeCapabilityNameOpticalMedia"));
        }

        var rawAccess = HasExports(("kernel32.dll", "DeviceIoControl"));
        var imapiType = Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2", throwOnError: false);
        if (imapiType is null)
        {
            return new WindowsNativeCapabilityEntry(
                WindowsNativeCapabilityIds.OpticalMedia,
                Name("NativeCapabilityNameOpticalMedia"),
                rawAccess ? NativeCapabilityStatus.Available : NativeCapabilityStatus.Unavailable,
                rawAccess
                    ? LocalizationService.T("NativeCapabilityOpticalRawReadOnly")
                    : LocalizationService.T("NativeCapabilityOpticalUnavailable"),
                0,
                new Dictionary<string, string>
                {
                    ["RawRead"] = YesNo(rawAccess),
                    ["IMAPI2"] = YesNo(false)
                });
        }

        var recorders = await new WindowsOpticalMediaWriter()
            .GetRecordersAsync(cancellationToken)
            .ConfigureAwait(false);
        return new WindowsNativeCapabilityEntry(
            WindowsNativeCapabilityIds.OpticalMedia,
            Name("NativeCapabilityNameOpticalMedia"),
            NativeCapabilityStatus.Available,
            recorders.Count == 0
                ? LocalizationService.T("NativeCapabilityNoOpticalRecorders")
                : LocalizationService.T(
                    "NativeCapabilityOpticalRecordersFound",
                    recorders.Count),
            recorders.Count,
            new Dictionary<string, string>
            {
                ["RawRead"] = YesNo(rawAccess),
                ["IMAPI2"] = YesNo(true)
            });
    }

    private static ValueTask<WindowsNativeCapabilityEntry> ProbeDeviceNotificationsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(NotSupported(
                WindowsNativeCapabilityIds.DeviceNotifications,
                Name("NativeCapabilityNameDeviceNotifications")));
        }

        using var watcher = new OpticalDeviceWatcher();
        var started = watcher.Start();
        watcher.Stop();
        return ValueTask.FromResult(new WindowsNativeCapabilityEntry(
            WindowsNativeCapabilityIds.DeviceNotifications,
            Name("NativeCapabilityNameDeviceNotifications"),
            started ? NativeCapabilityStatus.Available : NativeCapabilityStatus.Unavailable,
            LocalizationService.T(
                started
                    ? "NativeCapabilityDeviceNotificationsAvailable"
                    : "NativeCapabilityDeviceNotificationsFailed")));
    }

    private static ValueTask<WindowsNativeCapabilityEntry> ProbePowerStateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(NotSupported(
                WindowsNativeCapabilityIds.PowerState,
                Name("NativeCapabilityNamePowerState")));
        }

        using var service = new SystemPowerStateService();
        var available = service.Refresh();
        var power = service.Current;
        return ValueTask.FromResult(new WindowsNativeCapabilityEntry(
            WindowsNativeCapabilityIds.PowerState,
            Name("NativeCapabilityNamePowerState"),
            available ? NativeCapabilityStatus.Available : NativeCapabilityStatus.Unavailable,
            power is null
                ? LocalizationService.T("NativeCapabilityPowerStateUnavailable")
                : LocalizationService.T(
                    "NativeCapabilityPowerSummary",
                    DescribePowerSource(power.Source),
                    LocalizationService.T(
                        power.EnergySaverEnabled
                            ? "NativeCapabilityEnabled"
                            : "NativeCapabilityDisabled")),
            null,
            power is null
                ? null
                : new Dictionary<string, string>
                {
                    ["Source"] = power.Source.ToString(),
                    ["BatteryPercent"] = power.BatteryPercent?.ToString() ?? string.Empty,
                    ["EnergySaver"] = YesNo(power.EnergySaverEnabled)
                }));
    }

    private static ValueTask<WindowsNativeCapabilityEntry> ProbeAccessibilityAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var service = new WindowsAccessibilityService();
        var available = service.Refresh();
        return ValueTask.FromResult(new WindowsNativeCapabilityEntry(
            WindowsNativeCapabilityIds.Accessibility,
            Name("NativeCapabilityNameAccessibility"),
            available ? NativeCapabilityStatus.Available : NativeCapabilityStatus.NotSupported,
            service.Current?.ToLocalizedSummary()
                ?? LocalizationService.T("NativeCapabilityAccessibilityUnavailable"),
            null,
            service.Current is null
                ? null
                : new Dictionary<string, string>
                {
                    ["HighContrast"] = YesNo(service.Current.HighContrastEnabled),
                    ["TextScale"] = service.Current.TextScaleFactor.ToString(
                        "0.##",
                        System.Globalization.CultureInfo.InvariantCulture)
                }));
    }

    private static IWindowsNativeCapabilityProbe NativeExports(
        string id,
        string displayNameKey,
        string availableSummaryKey,
        params (string Library, string Export)[] exports) =>
        new DelegateProbe(id, displayNameKey, cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = OperatingSystem.IsWindows() && HasExports(exports);
            var displayName = Name(displayNameKey);
            return ValueTask.FromResult(new WindowsNativeCapabilityEntry(
                id,
                displayName,
                !OperatingSystem.IsWindows()
                    ? NativeCapabilityStatus.NotSupported
                    : available
                        ? NativeCapabilityStatus.Available
                        : NativeCapabilityStatus.Unavailable,
                !OperatingSystem.IsWindows()
                    ? LocalizationService.T(
                        "NativeCapabilityWindowsOnly",
                        displayName)
                    : available
                        ? LocalizationService.T(availableSummaryKey)
                        : LocalizationService.T(
                            "NativeCapabilityInterfaceUnavailable",
                            displayName)));
        });

    private static bool HasExports(params (string Library, string Export)[] exports)
    {
        foreach (var group in exports.GroupBy(item => item.Library, StringComparer.OrdinalIgnoreCase))
        {
            if (!NativeLibrary.TryLoad(group.Key, out var library))
            {
                return false;
            }

            try
            {
                if (group.Any(item => !NativeLibrary.TryGetExport(library, item.Export, out _)))
                {
                    return false;
                }
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }
        return true;
    }

    private static WindowsNativeCapabilityEntry FromResult<T>(
        string id,
        string displayName,
        NativeCapabilityResult<T> result,
        string? availableSummary,
        int? itemCount)
        where T : class =>
        new(
            id,
            displayName,
            result.Status,
            result.IsAvailable && !string.IsNullOrWhiteSpace(availableSummary)
                ? availableSummary
                : LocalizationService.T(
                    "NativeCapabilityDetectionResult",
                    displayName,
                    result.Message),
            itemCount);

    private static WindowsNativeCapabilityEntry NotSupported(string id, string displayName) =>
        new(
            id,
            displayName,
            NativeCapabilityStatus.NotSupported,
            LocalizationService.T("NativeCapabilityWindowsOnly", displayName));

    private static string DescribePowerSource(SystemPowerSource source) => source switch
    {
        SystemPowerSource.Ac => LocalizationService.T("NativeCapabilityPowerSourceAc"),
        SystemPowerSource.Battery => LocalizationService.T("NativeCapabilityPowerSourceBattery"),
        _ => LocalizationService.T("NativeCapabilityPowerSourceUnknown")
    };

    private static string Name(string key) => LocalizationService.T(key);

    private static string YesNo(bool value) =>
        LocalizationService.T(
            value
                ? "NativeCapabilityYes"
                : "NativeCapabilityNo");

    private sealed class DelegateProbe(
        string id,
        string displayNameKey,
        Func<CancellationToken, ValueTask<WindowsNativeCapabilityEntry>> capture)
        : IWindowsNativeCapabilityProbe
    {
        public string Id { get; } = id;

        public string DisplayName => Name(displayNameKey);

        public ValueTask<WindowsNativeCapabilityEntry> ProbeAsync(
            CancellationToken cancellationToken = default) => capture(cancellationToken);
    }
}

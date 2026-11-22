using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class WindowsNativeCapabilityCatalogTests
{
    [Fact]
    public async Task CaptureAsync_IsolatesProbeFailureAndPreservesRegistrationOrder()
    {
        var catalog = new WindowsNativeCapabilityCatalog(
        [
            FakeProbe.Returning("first", "第一项", NativeCapabilityStatus.Available),
            FakeProbe.Throwing("second", "第二项", new InvalidOperationException("模拟故障")),
            FakeProbe.Returning("third", "第三项", NativeCapabilityStatus.Unavailable)
        ]);

        var snapshot = await catalog.CaptureAsync();

        Assert.Equal(["first", "second", "third"], snapshot.Capabilities.Select(item => item.Id));
        Assert.Equal(NativeCapabilityStatus.Available, snapshot.Capabilities[0].Status);
        Assert.Equal(NativeCapabilityStatus.Failed, snapshot.Capabilities[1].Status);
        Assert.Contains("模拟故障", snapshot.Capabilities[1].Summary);
        Assert.Equal(NativeCapabilityStatus.Unavailable, snapshot.Capabilities[2].Status);
        Assert.Equal(1, snapshot.AvailableCount);
        Assert.Equal(1, snapshot.UnavailableCount);
        Assert.Equal(1, snapshot.FailedCount);
        Assert.Equal(snapshot.Capabilities[0], snapshot.Find("FIRST"));
    }

    [Fact]
    public async Task LocalizedSummary_IsCompactAndReportsAllStatusGroups()
    {
        var catalog = new WindowsNativeCapabilityCatalog(
        [
            FakeProbe.Returning("ready", "可用", NativeCapabilityStatus.Available),
            FakeProbe.Returning("missing", "不可用", NativeCapabilityStatus.NotSupported),
            FakeProbe.Throwing("failed", "失败", new Exception("bad"))
        ]);

        try
        {
            LocalizationService.SetLanguage("zh-Hans");
            var snapshot = await catalog.CaptureAsync();
            Assert.Equal("Windows 原生能力：1/3 项可用，1 项当前不可用，1 项检测失败。", snapshot.ToLocalizedSummary());

            LocalizationService.SetLanguage("en-US");
            Assert.Equal(
                "Windows native capabilities: 1/3 available, 1 currently unavailable, detection failures: 1.",
                snapshot.ToLocalizedSummary());
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    [Fact]
    public async Task CaptureAsync_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var catalog = new WindowsNativeCapabilityCatalog(
        [
            FakeProbe.Returning("ready", "可用", NativeCapabilityStatus.Available)
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalog.CaptureAsync(cancellation.Token));
    }

    [Fact]
    public async Task DefaultCatalog_CapturesAllProbesWithoutConnectedHardware()
    {
        var snapshot = await WindowsNativeCapabilityCatalog
            .CreateDefault()
            .CaptureAsync()
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(17, snapshot.Capabilities.Count);
        Assert.Equal(
            snapshot.Capabilities.Count,
            snapshot.Capabilities.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(snapshot.Capabilities, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
            Assert.False(string.IsNullOrWhiteSpace(item.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(item.Summary));
        });
    }

    [Fact]
    public void NativeDiagnostics_UsesStableEtwProviderIdentity()
    {
        Assert.Equal("zzwdh-FormatFactoryWinUI-Native", NativeDiagnostics.Log.Name);
        Assert.NotEqual(Guid.Empty, NativeDiagnostics.Log.Guid);

        NativeDiagnostics.Log.ServiceStarted("test");
        NativeDiagnostics.Log.ConversionStage("task", "probe", 50);
    }

    [Fact]
    public void AccessibilityService_PublishesOnlyMaterialPreferenceChanges()
    {
        var initial = Accessibility(highContrast: false, textScale: 1d, accent: 0xFF336699);
        var platform = new FakeAccessibilityPlatform(initial);
        using var service = new WindowsAccessibilityService(platform);
        var changes = new List<WindowsAccessibilityChangedEventArgs>();
        service.Changed += (_, args) => changes.Add(args);

        Assert.True(service.Refresh());
        platform.Current = initial with { CapturedAt = initial.CapturedAt.AddMinutes(1) };
        Assert.True(service.Refresh());
        platform.Current = initial with
        {
            HighContrastEnabled = true,
            TextScaleFactor = 1.5,
            AccentColorArgb = 0xFF112233,
            CapturedAt = initial.CapturedAt.AddMinutes(2)
        };
        Assert.True(service.Refresh());

        Assert.Equal(2, changes.Count);
        Assert.Null(changes[0].Previous);
        Assert.False(changes[0].Current.HighContrastEnabled);
        Assert.True(changes[1].Current.HighContrastEnabled);
        Assert.Equal(1.5, changes[1].Current.TextScaleFactor);
        try
        {
            LocalizationService.SetLanguage("zh-Hans");
            Assert.Contains("文本缩放 1.5 倍", changes[1].Current.ToLocalizedSummary());

            LocalizationService.SetLanguage("en-US");
            Assert.Contains("text scaling: 1.5×", changes[1].Current.ToLocalizedSummary());
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }

    [Fact]
    public void AccessibilityService_FailedRefreshKeepsLastSnapshot()
    {
        var initial = Accessibility(highContrast: false, textScale: 1d, accent: 0xFF336699);
        var platform = new FakeAccessibilityPlatform(initial);
        using var service = new WindowsAccessibilityService(platform);
        Assert.True(service.Refresh());

        platform.IsAvailable = false;

        Assert.False(service.Refresh());
        Assert.Equal(initial, service.Current);
    }

    private static WindowsAccessibilitySnapshot Accessibility(
        bool highContrast,
        double textScale,
        uint accent) => new(
        highContrast,
        highContrast ? "测试高对比度" : null,
        textScale,
        0xFFFFFFFF,
        0xFF000000,
        0xFF0078D4,
        accent,
        DateTimeOffset.UtcNow);

    private sealed class FakeAccessibilityPlatform(WindowsAccessibilitySnapshot current)
        : IWindowsAccessibilityPlatform
    {
        public WindowsAccessibilitySnapshot Current { get; set; } = current;

        public bool IsAvailable { get; set; } = true;

        public bool TryCapture(out WindowsAccessibilitySnapshot snapshot)
        {
            snapshot = Current;
            return IsAvailable;
        }
    }

    private sealed class FakeProbe : IWindowsNativeCapabilityProbe
    {
        private readonly Func<CancellationToken, WindowsNativeCapabilityEntry> _capture;

        private FakeProbe(
            string id,
            string displayName,
            Func<CancellationToken, WindowsNativeCapabilityEntry> capture)
        {
            Id = id;
            DisplayName = displayName;
            _capture = capture;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public ValueTask<WindowsNativeCapabilityEntry> ProbeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_capture(cancellationToken));

        public static FakeProbe Returning(
            string id,
            string displayName,
            NativeCapabilityStatus status) =>
            new(id, displayName, cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new WindowsNativeCapabilityEntry(id, displayName, status, displayName);
            });

        public static FakeProbe Throwing(
            string id,
            string displayName,
            Exception exception) =>
            new(id, displayName, _ => throw exception);
    }
}

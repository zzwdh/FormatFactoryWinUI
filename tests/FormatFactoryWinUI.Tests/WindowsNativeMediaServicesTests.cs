using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class WindowsNativeMediaServicesTests
{
    [Fact]
    public void CapabilityResultFactories_MaintainAvailabilityInvariant()
    {
        var available = NativeCapabilityResult<string>.Available("ready");
        var unavailable = NativeCapabilityResult<string>.Unavailable("missing");

        Assert.True(available.IsAvailable);
        Assert.Equal("ready", available.Value);
        Assert.False(unavailable.IsAvailable);
        Assert.Null(unavailable.Value);
        Assert.Equal(NativeCapabilityStatus.Unavailable, unavailable.Status);
    }

    [Theory]
    [InlineData("34363248-0000-0010-8000-00aa00389b71", "H.264")]
    [InlineData("43564548-0000-0010-8000-00aa00389b71", "HEVC")]
    [InlineData("31305641-0000-0010-8000-00aa00389b71", "AV1")]
    public void MediaFoundationSubtypeNames_AreStable(string value, string expected)
    {
        Assert.Equal(expected, MediaFoundationNativeService.DescribeSubtype(new Guid(value)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(8, 8)]
    [InlineData(9, 1)]
    public void ExifOrientation_IsNormalized(int value, int expected)
    {
        Assert.Equal(
            (ushort)expected,
            WindowsImagingNativeService.NormalizeExifOrientation((ushort)value));
    }

    [Fact]
    public async Task MissingInputs_ReturnExplicitFailureResults()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.bin");

        var media = await new MediaFoundationNativeService().ProbeSourceAsync(missing);
        var image = await new WindowsImagingNativeService().ReadMetadataAsync(missing);
        var properties = new WindowsShellMetadataService().ReadProperties(missing);
        var thumbnail = await new WindowsThumbnailCacheService().GetThumbnailAsync(missing, 128);

        Assert.Equal(NativeCapabilityStatus.Failed, media.Status);
        Assert.Equal(NativeCapabilityStatus.Failed, image.Status);
        Assert.Equal(NativeCapabilityStatus.Failed, properties.Status);
        Assert.Equal(NativeCapabilityStatus.Failed, thumbnail.Status);
        Assert.All(new[] { media.Message, image.Message, properties.Message, thumbnail.Message }, message =>
            Assert.False(string.IsNullOrWhiteSpace(message)));
    }

    [Fact]
    public async Task WicAndMediaFoundation_ProbeSimpleLocalArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var bitmapPath = Path.Combine(directory.Path, "one-pixel.bmp");
        var wavePath = Path.Combine(directory.Path, "silence.wav");
        await File.WriteAllBytesAsync(bitmapPath, CreateOnePixelBitmap());
        await File.WriteAllBytesAsync(wavePath, CreateWaveFile());

        var image = await new WindowsImagingNativeService().ReadMetadataAsync(bitmapPath);
        var media = await new MediaFoundationNativeService().ProbeSourceAsync(wavePath);

        Assert.True(image.IsAvailable, image.Message);
        Assert.Equal(1, image.Value!.Width);
        Assert.Equal(1, image.Value.Height);
        Assert.Equal((ushort)1, image.Value.ExifOrientation);

        Assert.True(media.IsAvailable, media.Message);
        Assert.Contains(media.Value!.Streams, stream =>
            stream.Kind == "Audio" && stream.Channels == 1 && stream.SamplesPerSecond == 8000);
    }

    [Fact]
    public void NativeEnumerators_AlwaysReturnStructuredResults()
    {
        var mediaFoundation = new MediaFoundationNativeService().EnumerateHardwareTransforms();
        var imaging = new WindowsImagingNativeService().EnumerateCodecs();
        var direct3D = Direct3DVideoCapabilityService.Probe();
        var audio = new WindowsAudioEndpointService().EnumerateEndpoints();

        AssertResultInvariant(mediaFoundation);
        AssertResultInvariant(imaging);
        AssertResultInvariant(direct3D);
        AssertResultInvariant(audio);
    }

    [Fact]
    public void AudioEndpointWatcher_ForwardsEveryChangeKindOnlyWhileRunning()
    {
        var backend = new FakeAudioEndpointNotificationBackend();
        var watcher = new WindowsAudioEndpointWatcher(backend);
        var changes = new List<WindowsAudioEndpointChangeEventArgs>();
        watcher.Changed += (_, args) => changes.Add(args);

        Assert.True(watcher.Start());
        Assert.True(watcher.Start());
        backend.Publish(new WindowsAudioEndpointChangeEventArgs(
            WindowsAudioEndpointChangeKind.DeviceAdded,
            "render-1",
            DateTimeOffset.UtcNow));
        backend.Publish(new WindowsAudioEndpointChangeEventArgs(
            WindowsAudioEndpointChangeKind.DeviceRemoved,
            "capture-1",
            DateTimeOffset.UtcNow));
        backend.Publish(new WindowsAudioEndpointChangeEventArgs(
            WindowsAudioEndpointChangeKind.DeviceStateChanged,
            "render-1",
            DateTimeOffset.UtcNow,
            state: 1));
        backend.Publish(new WindowsAudioEndpointChangeEventArgs(
            WindowsAudioEndpointChangeKind.DefaultDeviceChanged,
            "render-2",
            DateTimeOffset.UtcNow,
            WindowsAudioDataFlow.Render,
            WindowsAudioEndpointRole.Multimedia));
        var propertyFormat = Guid.NewGuid();
        backend.Publish(new WindowsAudioEndpointChangeEventArgs(
            WindowsAudioEndpointChangeKind.PropertyValueChanged,
            "render-2",
            DateTimeOffset.UtcNow,
            propertyFormatId: propertyFormat,
            propertyId: 14));

        watcher.Stop();
        watcher.Stop();
        backend.Publish(new WindowsAudioEndpointChangeEventArgs(
            WindowsAudioEndpointChangeKind.DeviceAdded,
            "late-device",
            DateTimeOffset.UtcNow));

        Assert.Equal(
            [
                WindowsAudioEndpointChangeKind.DeviceAdded,
                WindowsAudioEndpointChangeKind.DeviceRemoved,
                WindowsAudioEndpointChangeKind.DeviceStateChanged,
                WindowsAudioEndpointChangeKind.DefaultDeviceChanged,
                WindowsAudioEndpointChangeKind.PropertyValueChanged
            ],
            changes.Select(change => change.Kind));
        Assert.Equal((uint)1, changes[2].State);
        Assert.Equal(WindowsAudioDataFlow.Render, changes[3].DataFlow);
        Assert.Equal(WindowsAudioEndpointRole.Multimedia, changes[3].Role);
        Assert.Equal(propertyFormat, changes[4].PropertyFormatId);
        Assert.Equal((uint)14, changes[4].PropertyId);
        Assert.Equal(1, backend.RegisterCount);
        Assert.Equal(1, backend.UnregisterCount);

        watcher.Dispose();
        watcher.Dispose();
        Assert.Equal(1, backend.DisposeCount);
    }

    [Fact]
    public async Task AudioEndpointWatcher_MarshalsEventsAndDropsQueuedEventsAfterStop()
    {
        var backend = new FakeAudioEndpointNotificationBackend();
        var context = new QueuedSynchronizationContext();
        using var watcher = new WindowsAudioEndpointWatcher(backend);
        var changes = new List<string?>();
        watcher.Changed += (_, args) => changes.Add(args.DeviceId);

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            Assert.True(watcher.Start());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await Task.Run(() => backend.Publish(new WindowsAudioEndpointChangeEventArgs(
            WindowsAudioEndpointChangeKind.DefaultDeviceChanged,
            "stale-default",
            DateTimeOffset.UtcNow,
            WindowsAudioDataFlow.Render,
            WindowsAudioEndpointRole.Console)));
        Assert.Empty(changes);

        watcher.Stop();
        context.Drain();
        Assert.Empty(changes);

        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            Assert.True(watcher.Start());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await Task.Run(() => backend.Publish(new WindowsAudioEndpointChangeEventArgs(
            WindowsAudioEndpointChangeKind.DeviceStateChanged,
            "current-device",
            DateTimeOffset.UtcNow,
            state: 1)));
        context.Drain();

        Assert.Equal(["current-device"], changes);
    }

    [Fact]
    public void NativeAudioEndpointWatcher_StartStop_IsSafe()
    {
        using var watcher = new WindowsAudioEndpointWatcher();

        _ = watcher.Start();
        watcher.Stop();

        Assert.False(watcher.IsRunning);
    }

    private static void AssertResultInvariant<T>(NativeCapabilityResult<T> result) where T : class
    {
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        if (result.IsAvailable)
        {
            Assert.NotNull(result.Value);
        }
        else
        {
            Assert.Null(result.Value);
        }
    }

    private static byte[] CreateOnePixelBitmap() =>
    [
        0x42, 0x4D, 0x3A, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x36, 0x00, 0x00, 0x00, 0x28, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x01, 0x00, 0x20, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x13, 0x0B,
        0x00, 0x00, 0x13, 0x0B, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x40,
        0x80, 0xFF
    ];

    private static byte[] CreateWaveFile()
    {
        const int sampleRate = 8000;
        const int sampleCount = 80;
        const int dataLength = sampleCount * sizeof(short);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((ushort)sizeof(short));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        return stream.ToArray();
    }

    private sealed class FakeAudioEndpointNotificationBackend
        : IWindowsAudioEndpointNotificationBackend
    {
        public event EventHandler<WindowsAudioEndpointChangeEventArgs>? Changed;

        public int LastErrorCode => 0;
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public int DisposeCount { get; private set; }

        public bool TryRegister()
        {
            RegisterCount++;
            return true;
        }

        public void Unregister() => UnregisterCount++;

        public void Dispose() => DisposeCount++;

        public void Publish(WindowsAudioEndpointChangeEventArgs args) =>
            Changed?.Invoke(this, args);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private readonly object _gate = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                _callbacks.Enqueue((callback, state));
            }
        }

        public void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) pending;
                lock (_gate)
                {
                    if (!_callbacks.TryDequeue(out pending))
                    {
                        return;
                    }
                }

                pending.Callback(pending.State);
            }
        }
    }
}

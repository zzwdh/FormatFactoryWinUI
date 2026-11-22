namespace FormatFactoryWinUI.Services;

public enum NativeCapabilityStatus
{
    Available,
    Unavailable,
    NotSupported,
    Failed
}

public sealed record NativeCapabilityResult<T>(
    NativeCapabilityStatus Status,
    T? Value,
    string Message)
    where T : class
{
    public bool IsAvailable => Status == NativeCapabilityStatus.Available;

    public static NativeCapabilityResult<T> Available(T value, string message = "Available") =>
        new(NativeCapabilityStatus.Available, value, message);

    public static NativeCapabilityResult<T> Unavailable(string message) =>
        new(NativeCapabilityStatus.Unavailable, null, message);

    public static NativeCapabilityResult<T> NotSupported(string message) =>
        new(NativeCapabilityStatus.NotSupported, null, message);

    public static NativeCapabilityResult<T> Failed(string message) =>
        new(NativeCapabilityStatus.Failed, null, message);
}

public enum MediaFoundationTransformKind
{
    VideoDecoder,
    VideoEncoder,
    AudioDecoder,
    AudioEncoder
}

public sealed record MftTransformInfo(
    Guid ClassId,
    string Name,
    MediaFoundationTransformKind Kind,
    bool IsHardware,
    bool IsAsynchronous);

public sealed record MediaFoundationStreamInfo(
    string Kind,
    Guid MajorType,
    Guid Subtype,
    string SubtypeName,
    int Width,
    int Height,
    int Channels,
    int SamplesPerSecond,
    uint AverageBitrate);

public sealed record MediaFoundationSourceInfo(
    string Path,
    TimeSpan Duration,
    IReadOnlyList<MediaFoundationStreamInfo> Streams);

public sealed record Direct3DVideoCapabilities(
    string FeatureLevel,
    string AdapterName,
    ulong DedicatedVideoMemory,
    bool SupportsDxgi,
    bool SupportsDxva,
    IReadOnlyList<Guid> DxvaDecoderProfiles);

public enum WicCodecKind
{
    Decoder,
    Encoder
}

public sealed record WicCodecInfo(
    Guid ClassId,
    WicCodecKind Kind,
    string Name,
    string Author,
    string Version,
    string MimeTypes,
    string FileExtensions,
    bool SupportsAnimation,
    bool SupportsLossless,
    bool SupportsMultipleFrames);

public sealed record WicColorContextInfo(
    string Kind,
    int ProfileByteCount,
    uint ExifColorSpace);

public sealed record WicImageMetadata(
    int Width,
    int Height,
    ushort ExifOrientation,
    IReadOnlyList<WicColorContextInfo> ColorContexts)
{
    public bool HasEmbeddedColorProfile => ColorContexts.Any(context =>
        context.Kind.Equals("Profile", StringComparison.OrdinalIgnoreCase) && context.ProfileByteCount > 0);
}

public sealed record ShellPropertyMetadata(
    string CanonicalName,
    string Value);

public sealed record ThumbnailPixels(
    int Width,
    int Height,
    int Stride,
    byte[] Bgra32,
    bool HasAlpha);

public enum WindowsAudioDataFlow
{
    Render,
    Capture
}

public sealed record WindowsAudioEndpointInfo(
    string Id,
    string Name,
    WindowsAudioDataFlow DataFlow,
    uint State,
    int Channels,
    int SamplesPerSecond,
    int BitsPerSample);

public interface IMediaFoundationNativeService
{
    NativeCapabilityResult<IReadOnlyList<MftTransformInfo>> EnumerateHardwareTransforms();

    Task<NativeCapabilityResult<MediaFoundationSourceInfo>> ProbeSourceAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public interface IWindowsImagingNativeService
{
    NativeCapabilityResult<IReadOnlyList<WicCodecInfo>> EnumerateCodecs();

    Task<NativeCapabilityResult<WicImageMetadata>> ReadMetadataAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public interface IWindowsShellMetadataService
{
    NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>> ReadProperties(string path);
}

public interface IWindowsThumbnailCacheService
{
    Task<NativeCapabilityResult<ThumbnailPixels>> GetThumbnailAsync(
        string path,
        uint requestedSize,
        CancellationToken cancellationToken = default);
}

public interface IWindowsAudioEndpointService
{
    NativeCapabilityResult<IReadOnlyList<WindowsAudioEndpointInfo>> EnumerateEndpoints();
}

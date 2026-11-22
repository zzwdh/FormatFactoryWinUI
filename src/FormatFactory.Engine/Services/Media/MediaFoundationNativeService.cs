using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

public sealed partial class MediaFoundationNativeService : IMediaFoundationNativeService
{
    private const int MfVersion = 0x00020070;
    private const uint MftEnumFlagHardware = 0x00000004;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;
    private const uint SourceReaderMediaSource = 0xFFFFFFFF;
    private const uint SourceReaderFirstVideoStream = 0xFFFFFFFC;
    private const uint SourceReaderFirstAudioStream = 0xFFFFFFFD;

    private static readonly Guid VideoDecoderCategory = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid AudioDecoderCategory = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");
    private static readonly Guid AudioEncoderCategory = new("91c64bd0-f91e-4d8c-9276-db248279d975");
    private static readonly Guid FriendlyNameAttribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid TransformClassIdAttribute = new("6821c42b-65a4-4e82-99bc-9a88205ecd0c");
    private static readonly Guid TransformAsyncAttribute = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    private static readonly Guid PresentationDurationAttribute = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid MajorTypeAttribute = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubtypeAttribute = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSizeAttribute = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid AudioChannelsAttribute = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid AudioSamplesPerSecondAttribute = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid AverageBitrateAttribute = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");

    private static readonly IReadOnlyDictionary<uint, string> KnownMediaSubtypes = new Dictionary<uint, string>
    {
        [0x34363248] = "H.264",
        [0x43564548] = "HEVC",
        [0x31305641] = "AV1",
        [0x30395056] = "VP9",
        [0x3231564E] = "NV12",
        [0x30313050] = "P010",
        [0x00000001] = "PCM",
        [0x00000003] = "IEEE Float",
        [0x00000055] = "MP3",
        [0x00001610] = "AAC",
        [0x0000F1AC] = "FLAC"
    };

    public NativeCapabilityResult<IReadOnlyList<MftTransformInfo>> EnumerateHardwareTransforms()
    {
        if (!OperatingSystem.IsWindows())
        {
            return NativeCapabilityResult<IReadOnlyList<MftTransformInfo>>.NotSupported(
                "Media Foundation is available only on Windows.");
        }

        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null)
        {
            return FromFailure<IReadOnlyList<MftTransformInfo>, NativeComSession>(comResult);
        }

        using var com = comResult.Value;
        var startup = TryStartMediaFoundation();
        if (startup < 0)
        {
            return NativeCapabilityResult<IReadOnlyList<MftTransformInfo>>.Unavailable(
                $"Media Foundation startup failed (0x{startup:X8}).");
        }

        try
        {
            var transforms = new List<MftTransformInfo>();
            EnumerateCategory(VideoDecoderCategory, MediaFoundationTransformKind.VideoDecoder, transforms);
            EnumerateCategory(VideoEncoderCategory, MediaFoundationTransformKind.VideoEncoder, transforms);
            EnumerateCategory(AudioDecoderCategory, MediaFoundationTransformKind.AudioDecoder, transforms);
            EnumerateCategory(AudioEncoderCategory, MediaFoundationTransformKind.AudioEncoder, transforms);

            var distinct = transforms
                .DistinctBy(item => (item.ClassId, item.Kind))
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return NativeCapabilityResult<IReadOnlyList<MftTransformInfo>>.Available(
                distinct,
                distinct.Length == 0
                    ? "Media Foundation is available; no hardware transforms were reported."
                    : $"Detected {distinct.Length} Media Foundation hardware transforms.");
        }
        catch (DllNotFoundException)
        {
            return NativeCapabilityResult<IReadOnlyList<MftTransformInfo>>.Unavailable(
                "Media Foundation libraries were not found.");
        }
        catch (EntryPointNotFoundException)
        {
            return NativeCapabilityResult<IReadOnlyList<MftTransformInfo>>.Unavailable(
                "MFTEnumEx is unavailable on this Windows version.");
        }
        catch (Exception exception)
        {
            return NativeCapabilityResult<IReadOnlyList<MftTransformInfo>>.Failed(exception.Message);
        }
        finally
        {
            ShutdownMediaFoundation();
        }
    }

    public Task<NativeCapabilityResult<MediaFoundationSourceInfo>> ProbeSourceAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(NativeCapabilityResult<MediaFoundationSourceInfo>.Failed("A source path is required."));
        }

        return Task.Run(() => ProbeSource(path, cancellationToken), cancellationToken);
    }

    public static string DescribeSubtype(Guid subtype)
    {
        var bytes = subtype.ToByteArray();
        var baseTail = new byte[] { 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 };
        if (bytes.AsSpan(4, 12).SequenceEqual(baseTail) && KnownMediaSubtypes.TryGetValue((uint)subtype.ToByteArray()[0]
            | ((uint)subtype.ToByteArray()[1] << 8)
            | ((uint)subtype.ToByteArray()[2] << 16)
            | ((uint)subtype.ToByteArray()[3] << 24), out var name))
        {
            return name;
        }

        return subtype == Guid.Empty ? "Unknown" : subtype.ToString("D");
    }

    private static NativeCapabilityResult<MediaFoundationSourceInfo> ProbeSource(
        string path,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NativeCapabilityResult<MediaFoundationSourceInfo>.NotSupported(
                "Media Foundation is available only on Windows.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return NativeCapabilityResult<MediaFoundationSourceInfo>.Failed("The media source does not exist.");
        }

        var comResult = NativeComSession.TryCreate();
        if (!comResult.IsAvailable || comResult.Value is null)
        {
            return FromFailure<MediaFoundationSourceInfo, NativeComSession>(comResult);
        }

        using var com = comResult.Value;
        var startup = TryStartMediaFoundation();
        if (startup < 0)
        {
            return NativeCapabilityResult<MediaFoundationSourceInfo>.Unavailable(
                $"Media Foundation startup failed (0x{startup:X8}).");
        }

        IntPtr sourceReader = IntPtr.Zero;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var createResult = MFCreateSourceReaderFromURL(fullPath, IntPtr.Zero, out sourceReader);
            if (createResult < 0 || sourceReader == IntPtr.Zero)
            {
                return NativeCapabilityResult<MediaFoundationSourceInfo>.Unavailable(
                    $"Media Foundation could not open this source (0x{createResult:X8}).");
            }

            var streams = new List<MediaFoundationStreamInfo>(2);
            TryReadStream(sourceReader, SourceReaderFirstVideoStream, "Video", streams);
            TryReadStream(sourceReader, SourceReaderFirstAudioStream, "Audio", streams);

            var duration = ReadDuration(sourceReader);
            cancellationToken.ThrowIfCancellationRequested();
            var source = new MediaFoundationSourceInfo(fullPath, duration, streams);
            return NativeCapabilityResult<MediaFoundationSourceInfo>.Available(
                source,
                streams.Count == 0
                    ? "The source opened, but no native audio or video stream was reported."
                    : $"Detected {streams.Count} native media streams.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException)
        {
            return NativeCapabilityResult<MediaFoundationSourceInfo>.Unavailable(
                "Media Foundation source reader libraries were not found.");
        }
        catch (EntryPointNotFoundException)
        {
            return NativeCapabilityResult<MediaFoundationSourceInfo>.Unavailable(
                "Media Foundation source probing is unavailable on this Windows version.");
        }
        catch (Exception exception)
        {
            return NativeCapabilityResult<MediaFoundationSourceInfo>.Failed(exception.Message);
        }
        finally
        {
            if (sourceReader != IntPtr.Zero)
            {
                Marshal.Release(sourceReader);
            }
            ShutdownMediaFoundation();
        }
    }

    private static void EnumerateCategory(
        Guid category,
        MediaFoundationTransformKind kind,
        ICollection<MftTransformInfo> output)
    {
        IntPtr activationArray = IntPtr.Zero;
        var result = MFTEnumEx(
            ref category,
            MftEnumFlagHardware | MftEnumFlagSortAndFilter,
            IntPtr.Zero,
            IntPtr.Zero,
            out activationArray,
            out var count);
        if (result < 0 || activationArray == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (var index = 0u; index < count; index++)
            {
                var activation = Marshal.ReadIntPtr(activationArray, checked((int)index * IntPtr.Size));
                if (activation == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var name = ReadAllocatedString(activation, FriendlyNameAttribute);
                    var classId = ReadGuid(activation, TransformClassIdAttribute);
                    var isAsync = ReadUInt32(activation, TransformAsyncAttribute) != 0;
                    output.Add(new MftTransformInfo(
                        classId,
                        string.IsNullOrWhiteSpace(name) ? classId.ToString("D") : name,
                        kind,
                        true,
                        isAsync));
                }
                finally
                {
                    Marshal.Release(activation);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(activationArray);
        }
    }

    private static void TryReadStream(
        IntPtr sourceReader,
        uint streamIndex,
        string kind,
        ICollection<MediaFoundationStreamInfo> streams)
    {
        var getNativeMediaType = GetDelegate<GetNativeMediaTypeDelegate>(sourceReader, 5);
        var result = getNativeMediaType(sourceReader, streamIndex, 0, out var mediaType);
        if (result < 0 || mediaType == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var majorType = ReadGuid(mediaType, MajorTypeAttribute);
            var subtype = ReadGuid(mediaType, SubtypeAttribute);
            var packedFrameSize = ReadUInt64(mediaType, FrameSizeAttribute);
            streams.Add(new MediaFoundationStreamInfo(
                kind,
                majorType,
                subtype,
                DescribeSubtype(subtype),
                (int)(packedFrameSize >> 32),
                (int)(packedFrameSize & uint.MaxValue),
                checked((int)ReadUInt32(mediaType, AudioChannelsAttribute)),
                checked((int)ReadUInt32(mediaType, AudioSamplesPerSecondAttribute)),
                ReadUInt32(mediaType, AverageBitrateAttribute)));
        }
        finally
        {
            Marshal.Release(mediaType);
        }
    }

    private static TimeSpan ReadDuration(IntPtr sourceReader)
    {
        var getPresentationAttribute = GetDelegate<GetPresentationAttributeDelegate>(sourceReader, 12);
        var key = PresentationDurationAttribute;
        var variant = default(NativePropertyVariant);
        try
        {
            if (getPresentationAttribute(sourceReader, SourceReaderMediaSource, ref key, out variant) < 0)
            {
                return TimeSpan.Zero;
            }

            var ticks = NativePropertyValue.ToUInt64(variant);
            return ticks <= long.MaxValue ? TimeSpan.FromTicks((long)ticks) : TimeSpan.Zero;
        }
        finally
        {
            NativePropertyValue.Clear(ref variant);
        }
    }

    private static string ReadAllocatedString(IntPtr attributes, Guid key)
    {
        var getter = GetDelegate<GetAllocatedStringDelegate>(attributes, 13);
        IntPtr value = IntPtr.Zero;
        try
        {
            return getter(attributes, ref key, out value, out _) >= 0 && value != IntPtr.Zero
                ? Marshal.PtrToStringUni(value) ?? string.Empty
                : string.Empty;
        }
        finally
        {
            if (value != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(value);
            }
        }
    }

    private static Guid ReadGuid(IntPtr attributes, Guid key)
    {
        var getter = GetDelegate<GetGuidDelegate>(attributes, 10);
        return getter(attributes, ref key, out var value) >= 0 ? value : Guid.Empty;
    }

    private static uint ReadUInt32(IntPtr attributes, Guid key)
    {
        var getter = GetDelegate<GetUInt32Delegate>(attributes, 7);
        return getter(attributes, ref key, out var value) >= 0 ? value : 0;
    }

    private static ulong ReadUInt64(IntPtr attributes, Guid key)
    {
        var getter = GetDelegate<GetUInt64Delegate>(attributes, 8);
        return getter(attributes, ref key, out var value) >= 0 ? value : 0;
    }

    private static T GetDelegate<T>(IntPtr comObject, int slot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    internal static int TryStartMediaFoundation()
    {
        try
        {
            return MFStartup(MfVersion, 0);
        }
        catch (DllNotFoundException)
        {
            return unchecked((int)0x8007007E);
        }
        catch (EntryPointNotFoundException)
        {
            return unchecked((int)0x8007007F);
        }
    }

    private static NativeCapabilityResult<TOutput> FromFailure<TOutput, TInput>(NativeCapabilityResult<TInput> input)
        where TOutput : class
        where TInput : class =>
        new(input.Status, null, input.Message);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAllocatedStringDelegate(
        IntPtr self,
        ref Guid key,
        out IntPtr value,
        out uint length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGuidDelegate(IntPtr self, ref Guid key, out Guid value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUInt32Delegate(IntPtr self, ref Guid key, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetUInt64Delegate(IntPtr self, ref Guid key, out ulong value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNativeMediaTypeDelegate(
        IntPtr self,
        uint streamIndex,
        uint mediaTypeIndex,
        out IntPtr mediaType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPresentationAttributeDelegate(
        IntPtr self,
        uint streamIndex,
        ref Guid key,
        out NativePropertyVariant value);

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(int version, int flags);

    [LibraryImport("mfplat.dll", EntryPoint = "MFShutdown")]
    internal static partial int ShutdownMediaFoundation();

    [LibraryImport("mfplat.dll")]
    private static partial int MFTEnumEx(
        ref Guid category,
        uint flags,
        IntPtr inputType,
        IntPtr outputType,
        out IntPtr activations,
        out uint count);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MFCreateSourceReaderFromURL(
        string url,
        IntPtr attributes,
        out IntPtr sourceReader);
}

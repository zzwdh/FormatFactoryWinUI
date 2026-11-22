using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class ConversionInputFailureTests
{
    [Theory]
    [InlineData("en-US", "missing", "The input file could not be read.")]
    [InlineData("en-US", "io", "The input file could not be read.")]
    [InlineData("en-US", "probe", "The input file could not be read.")]
    [InlineData("zh-Hans", "missing", "无法读取输入文件。")]
    [InlineData("zh-Hans", "io", "无法读取输入文件。")]
    [InlineData("zh-Hans", "probe", "无法读取输入文件。")]
    [InlineData("pt-BR", "missing", "Não foi possível ler o arquivo de entrada.")]
    [InlineData("pt-BR", "io", "Não foi possível ler o arquivo de entrada.")]
    [InlineData("pt-BR", "probe", "Não foi possível ler o arquivo de entrada.")]
    [InlineData("pt-PT", "missing", "Não foi possível ler o ficheiro de entrada.")]
    [InlineData("pt-PT", "io", "Não foi possível ler o ficheiro de entrada.")]
    [InlineData("pt-PT", "probe", "Não foi possível ler o ficheiro de entrada.")]
    public async Task ProbeReadFailure_DoesNotClaimDecodeFailure(string language, string failure, string expectedPrefix)
    {
        var exception = failure switch
        {
            "missing" => (Exception)new FileNotFoundException("Synthetic missing input."),
            "io" => new IOException("Synthetic unavailable input."),
            _ => new InvalidDataException("Synthetic probe read failure.")
        };
        await AssertFailureAsync(language, exception, null, "UnreadableInput", expectedPrefix);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-Hans")]
    [InlineData("pt-BR")]
    [InlineData("pt-PT")]
    public async Task ActualDecodeFailure_StillRejectsInputWithItsDecodeDiagnostic(string language)
    {
        const string detail = "Synthetic corrupt media packet.";
        await AssertFailureAsync(language, null, detail, "InvalidMedia", null);
    }

    private static async Task AssertFailureAsync(string language, Exception? probeError, string? decodeError,
        string expectedKind, string? expectedPrefix)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "synthetic.mp4");
        var output = Path.Combine(directory.Path, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "existing.txt");
        await File.WriteAllTextAsync(input, "Synthetic input bytes.");
        await File.WriteAllTextAsync(sentinel, "Existing output bytes.");
        var decoded = false;
        var fallbackValidated = false;
        var reportedProgress = false;
        var probe = new MediaProbeResult { Kind = MediaKind.Video, VideoCodec = "h264", VideoStreamIndex = 0 };
        var preflight = new MediaInputPreflightService(
            (_, _, _) => probeError is null ? Task.FromResult(probe) : Task.FromException<MediaProbeResult>(probeError),
            (_, _) => Task.FromResult(new FfmpegCapabilitySnapshot(new HashSet<string> { "mp4" })
            {
                Decoders = new HashSet<string> { "h264" },
                Encoders = new HashSet<string> { "libx264" }
            }),
            (_, _, _, _) =>
            {
                decoded = true;
                return Task.FromResult(new MediaDecodeCheckResult(false, decodeError!));
            },
            (_, _) =>
            {
                fallbackValidated = true;
                return Task.FromResult(true);
            });
        var task = new MediaTask { InputPath = input, Kind = MediaKind.Video, TargetFormatId = "mp4" };
        LocalizationService.SetLanguage(language);
        try
        {
            var result = await new ConversionService(preflight,
                new OutputRecoveryService(Path.Combine(directory.Path, "recovery"))).ConvertAsync(task,
                Path.Combine(directory.Path, "synthetic-engine", "ffmpeg.exe"),
                new AppSettings { CustomOutputDirectory = output, HardwareAcceleration = false },
                _ => reportedProgress = true, CancellationToken.None);

            Assert.False(result.Success);
            Assert.False(result.Cancelled);
            Assert.Empty(result.OutputPath);
            Assert.Null(result.RecoverableOutputId);
            Assert.False(fallbackValidated);
            Assert.False(reportedProgress);
            Assert.Equal(probeError is null, decoded);
            Assert.Contains(probeError?.Message ?? decodeError!, result.Message);
            if (expectedPrefix is not null)
            {
                Assert.StartsWith(expectedPrefix, result.Message);
                Assert.DoesNotContain(LocalizationService.T("MediaCompatibilityDamaged", probeError!.Message), result.Message);
            }
            else
            {
                Assert.Equal(LocalizationService.T("MediaCompatibilityDamaged", decodeError!), result.Message);
            }
            Assert.Contains($"Input check: {expectedKind}", await File.ReadAllTextAsync(result.LogPath));
            Assert.Equal("Synthetic input bytes.", await File.ReadAllTextAsync(input));
            Assert.Equal("Existing output bytes.", await File.ReadAllTextAsync(sentinel));
            Assert.Equal(new[] { sentinel }, Directory.GetFiles(output));
        }
        finally
        {
            LocalizationService.SetLanguage("en-US");
        }
    }
}

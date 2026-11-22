using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class OpticalDiscBackendTests
{
    [Fact]
    public void Classify_RecognizesBluRayDvdDataAudioAndUnknownMedia()
    {
        Assert.Equal(
            OpticalDiscMediaKind.BluRay,
            OpticalDiscDiscovery.Classify(true, true, ["AUDIO_TS", "bdmv"]));
        Assert.Equal(
            OpticalDiscMediaKind.DvdVideo,
            OpticalDiscDiscovery.Classify(true, true, [@"E:\VIDEO_TS"]));
        Assert.Equal(
            OpticalDiscMediaKind.DataDisc,
            OpticalDiscDiscovery.Classify(true, true, ["documents"]));
        Assert.Equal(
            OpticalDiscMediaKind.AudioCd,
            OpticalDiscDiscovery.Classify(true, false, null));
        Assert.Equal(
            OpticalDiscMediaKind.Unknown,
            OpticalDiscDiscovery.Classify(false, false, null));
    }

    [Theory]
    [InlineData(OpticalDiscMediaKind.BluRay, OpticalDiscFormatIds.BluRayDisc)]
    [InlineData(OpticalDiscMediaKind.DvdVideo, OpticalDiscFormatIds.DvdDisc)]
    [InlineData(OpticalDiscMediaKind.DataDisc, OpticalDiscFormatIds.DataDisc)]
    [InlineData(OpticalDiscMediaKind.AudioCd, OpticalDiscFormatIds.AudioCd)]
    [InlineData(OpticalDiscMediaKind.Unknown, OpticalDiscFormatIds.OpticalDisc)]
    public void OpticalDiscInfo_ExposesStablePathsAndSourceFormat(
        OpticalDiscMediaKind kind,
        string sourceFormatId)
    {
        var disc = new OpticalDiscInfo(
            "e:/",
            @"\\.\E:",
            "MY DISC",
            kind,
            true,
            true,
            2);

        Assert.Equal(@"E:\", disc.RootPath);
        Assert.Equal(@"\\.\E:", disc.DevicePath);
        Assert.Equal(sourceFormatId, disc.SourceFormatId);
        Assert.Equal("MY DISC (E:)", disc.DisplayName);
        Assert.Equal(2, disc.DriveOrdinal);
    }

    [Fact]
    public async Task ToolDiscovery_PrefersSupportedExecutablesInConfiguredDirectories()
    {
        using var directory = new TemporaryDirectory();
        var makeMkv32 = Touch(directory.Path, "makemkvcon.exe");
        var makeMkv64 = Touch(directory.Path, "makemkvcon64.exe");
        var icedax = Touch(directory.Path, "icedax.exe");
        var readom = Touch(directory.Path, "readom.exe");
        var discovery = new OpticalDiscToolDiscovery(new OpticalDiscToolConfiguration(
            directory.Path,
            directory.Path,
            directory.Path));

        var tools = await discovery.DiscoverAsync();

        Assert.Equal(Path.GetFullPath(makeMkv64), Tool(tools, OpticalDiscToolKind.MakeMkv).ExecutablePath);
        Assert.Equal(Path.GetFullPath(icedax), Tool(tools, OpticalDiscToolKind.AudioCdRipper).ExecutablePath);
        Assert.Equal(Path.GetFullPath(readom), Tool(tools, OpticalDiscToolKind.Readom).ExecutablePath);
        Assert.DoesNotContain(tools, tool => !tool.IsAvailable);
        Assert.NotEqual(makeMkv32, Tool(tools, OpticalDiscToolKind.MakeMkv).ExecutablePath);
    }

    [Fact]
    public void CommandFactory_KeepsPathsAsSingleArguments()
    {
        using var directory = new TemporaryDirectory();
        var mkvOutput = Path.Combine(directory.Path, "movie output & extras");
        var wavOutput = Path.Combine(directory.Path, "wav output & extras");
        var isoOutput = Path.Combine(directory.Path, "disc output & extras.iso");

        var scan = OpticalDiscCommandFactory.BuildMakeMkvDriveScan("makemkvcon64.exe");
        var makeMkv = OpticalDiscCommandFactory.BuildBluRayToMkv("makemkvcon64.exe", 3, mkvOutput);
        var audio = OpticalDiscCommandFactory.BuildAudioCdToWavTracks("icedax.exe", "e:\\", wavOutput);
        var readom = OpticalDiscCommandFactory.BuildDiscToIso("readom.exe", "e:\\", isoOutput);

        Assert.Equal(
            new[] { "--robot", "--messages=-stdout", "info", "disc:9999" },
            scan.Arguments);
        Assert.Equal(
            new[]
            {
                "--robot",
                "--messages=-stdout",
                "--progress=-stdout",
                "mkv",
                "disc:3",
                "all",
                Path.GetFullPath(mkvOutput)
            },
            makeMkv.Arguments);
        Assert.Equal(new[] { "-D", "E:", "-B", "-O", "wav", "track" }, audio.Arguments);
        Assert.Equal(Path.GetFullPath(wavOutput), audio.WorkingDirectory);
        Assert.Equal(
            new[] { "dev=E:", "f=" + Path.GetFullPath(isoOutput) },
            readom.Arguments);
    }

    [Fact]
    public void MakeMkvDriveListParser_MapsRobotDriveByWindowsDevicePath()
    {
        const string output = """"
            MSG:1005,0,1,"started","%1 started","MakeMKV"
            DRV:0,0,999,0,"DVD Vendor","", "\\.\D:"
            DRV:7,2,999,12,"BD-ROM Vendor, Model","MY ""DISC""","E:"
            """";

        var drives = MakeMkvDriveListParser.Parse(output);

        Assert.Equal(2, drives.Count);
        Assert.Equal("BD-ROM Vendor, Model", drives[1].DriveName);
        Assert.Equal("MY \"DISC\"", drives[1].DiscName);
        Assert.Equal(7, MakeMkvDriveListParser.ResolveDriveIndex(output, @"E:\"));
        Assert.Null(MakeMkvDriveListParser.ResolveDriveIndex(output, @"F:\"));
    }

    [Fact]
    public void ProgressParser_ParsesMakeMkvRobotAndPercentOutput()
    {
        Assert.Equal(
            50d,
            OpticalDiscProgressParser.ParsePercentage(
                OpticalDiscOperation.BluRayToMkv,
                "PRGV:100,32768,65536"));
        Assert.Equal(
            42.5d,
            OpticalDiscProgressParser.ParsePercentage(
                OpticalDiscOperation.AudioCdToWavTracks,
                "track 4: 42.5%"));
        Assert.Null(OpticalDiscProgressParser.ParsePercentage(
            OpticalDiscOperation.DiscToIso,
            "addr: 12345"));
    }

    [Fact]
    public void BackendPlanner_TreatsNativeDiscReadsAsRequiredAndToolsAsFallbacks()
    {
        var wav = OpticalDiscBackendPlanner.Plan(OpticalDiscFormatIds.AudioCd, "wav");
        Assert.Equal(OpticalDiscRouteKind.AudioCdExtraction, wav.Route);
        Assert.Empty(wav.RequiredDependencies);
        Assert.Equal([OpticalDiscDependency.WindowsOpticalReader], wav.PreferredDependencies);
        Assert.Equal([OpticalDiscDependency.AudioCdRipper], wav.OptionalFallbackDependencies);

        var mp3 = OpticalDiscBackendPlanner.Plan(OpticalDiscFormatIds.AudioCd, "mp3");
        Assert.Equal([OpticalDiscDependency.Ffmpeg], mp3.RequiredDependencies);
        Assert.Equal([OpticalDiscDependency.WindowsOpticalReader], mp3.PreferredDependencies);

        var discImage = OpticalDiscBackendPlanner.Plan(OpticalDiscFormatIds.DvdDisc, "cso");
        Assert.Equal(OpticalDiscRouteKind.PhysicalDiscImage, discImage.Route);
        Assert.Empty(discImage.RequiredDependencies);
        Assert.Equal(
            [OpticalDiscDependency.WindowsOpticalReader],
            discImage.PreferredDependencies);
        Assert.Equal([OpticalDiscDependency.Readom], discImage.OptionalFallbackDependencies);

        var imageConversion = OpticalDiscBackendPlanner.Plan("iso", "cso");
        Assert.Equal(OpticalDiscRouteKind.BuiltInImageConversion, imageConversion.Route);
        Assert.Equal([OpticalDiscDependency.BuiltInImageCodec], imageConversion.RequiredDependencies);
        Assert.Empty(imageConversion.PreferredDependencies);
        Assert.Empty(imageConversion.OptionalFallbackDependencies);

        var bluRay = OpticalDiscBackendPlanner.Plan(OpticalDiscFormatIds.BluRayDisc, "mkv");
        Assert.Equal([OpticalDiscDependency.MakeMkv], bluRay.RequiredDependencies);
        Assert.False(OpticalDiscBackendPlanner.Plan(OpticalDiscFormatIds.DvdDisc, "mp3").IsSupported);
    }

    [Fact]
    public async Task ConversionService_PrefersWindowsNativeAudioRead()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "native tracks");
        var nativeReader = new StubNativeDiscReader();
        var executor = new ArtifactWritingExecutor();
        var service = new OpticalDiscConversionService(
            nativeReader,
            new AvailableToolDiscovery(),
            executor);

        var result = await service.ExtractAudioCdToWavTracksAsync(
            Disc(OpticalDiscMediaKind.AudioCd),
            output);

        Assert.Equal(OpticalDiscReadBackend.WindowsNative, result.Backend);
        Assert.Single(result.OutputArtifacts);
        Assert.True(File.Exists(result.OutputArtifacts[0]));
        Assert.Equal(1, nativeReader.AudioReadCount);
        Assert.Empty(executor.Commands);
    }

    [Fact]
    public async Task ConversionService_FallsBackToAudioToolAfterNativeReadFailure()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "fallback tracks");
        var nativeReader = new StubNativeDiscReader(audioError: new IOException("raw read failed"));
        var executor = new ArtifactWritingExecutor();
        var service = new OpticalDiscConversionService(
            nativeReader,
            new AvailableToolDiscovery(),
            executor);

        var result = await service.ExtractAudioCdToWavTracksAsync(
            Disc(OpticalDiscMediaKind.AudioCd),
            output);

        Assert.Equal(OpticalDiscReadBackend.ExternalToolFallback, result.Backend);
        Assert.Contains("raw read failed", result.StandardError);
        Assert.Single(executor.Commands);
        Assert.Equal(OpticalDiscOperation.AudioCdToWavTracks, executor.Commands[0].Operation);
        Assert.True(File.Exists(result.OutputArtifacts.Single()));
    }

    [Fact]
    public async Task ConversionService_PrefersWindowsNativeIsoRead()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "native.iso");
        var nativeReader = new StubNativeDiscReader();
        var executor = new ArtifactWritingExecutor();
        var service = new OpticalDiscConversionService(
            nativeReader,
            new AvailableToolDiscovery(),
            executor);

        var result = await service.CreateIsoAsync(
            Disc(OpticalDiscMediaKind.DvdVideo),
            output);

        Assert.Equal(OpticalDiscReadBackend.WindowsNative, result.Backend);
        Assert.Equal(Path.GetFullPath(output), result.OutputPath);
        Assert.True(File.Exists(output));
        Assert.Equal(1, nativeReader.IsoReadCount);
        Assert.Empty(executor.Commands);
    }

    [Fact]
    public async Task ConversionService_FallsBackToReadomAfterNativeReadFailure()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "fallback.iso");
        var nativeReader = new StubNativeDiscReader(isoError: new IOException("sector read failed"));
        var executor = new ArtifactWritingExecutor();
        var service = new OpticalDiscConversionService(
            nativeReader,
            new AvailableToolDiscovery(),
            executor);

        var result = await service.CreateIsoAsync(
            Disc(OpticalDiscMediaKind.DataDisc),
            output);

        Assert.Equal(OpticalDiscReadBackend.ExternalToolFallback, result.Backend);
        Assert.Contains("sector read failed", result.StandardError);
        Assert.Single(executor.Commands);
        Assert.Equal(OpticalDiscOperation.DiscToIso, executor.Commands[0].Operation);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task ConversionService_MapsMakeMkvDriveAndCommitsDirectory()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "Blu-ray output");
        var executor = new ArtifactWritingExecutor();
        var service = new OpticalDiscConversionService(
            new AvailableToolDiscovery(),
            executor);

        var result = await service.ConvertBluRayToMkvAsync(
            Disc(OpticalDiscMediaKind.BluRay, driveOrdinal: 4),
            output);

        Assert.Equal(Path.GetFullPath(output), result.OutputPath);
        Assert.Single(result.OutputArtifacts);
        Assert.True(File.Exists(result.OutputArtifacts[0]));
        var conversion = executor.Commands.Single(
            command => command.Operation == OpticalDiscOperation.BluRayToMkv);
        Assert.Equal("disc:7", conversion.Arguments[4]);
        Assert.NotEqual(output, conversion.Arguments[^1]);
        Assert.Contains(
            executor.Commands,
            command => command.Operation == OpticalDiscOperation.InspectMakeMkvDrives);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, "*.partial"));
    }

    [Fact]
    public async Task ConversionService_CommitsReadomIsoFromSiblingStagingFile()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "DVD backup.iso");
        var executor = new ArtifactWritingExecutor();
        var service = new OpticalDiscConversionService(
            new AvailableToolDiscovery(),
            executor);

        var result = await service.CreateIsoAsync(
            Disc(OpticalDiscMediaKind.DvdVideo),
            output);

        Assert.Equal(Path.GetFullPath(output), result.OutputArtifacts.Single());
        Assert.True(File.Exists(output));
        var stagedArgument = executor.Commands.Single().Arguments.Single(
            argument => argument.StartsWith("f="));
        Assert.NotEqual("f=" + Path.GetFullPath(output), stagedArgument);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.partial.iso"));
    }

    [Fact]
    public async Task ConversionService_DoesNotCommitFailedOutputAndCleansStaging()
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "tracks");
        var service = new OpticalDiscConversionService(
            new AvailableToolDiscovery(),
            new ArtifactWritingExecutor(exitCode: 9));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExtractAudioCdToWavTracksAsync(
                Disc(OpticalDiscMediaKind.AudioCd),
                output));

        Assert.Equal(
            LocalizationService.T("ConversionUiToolExecutionFailed", "AudioCdRipper", 9, "failure"),
            error.Message);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, "*.partial"));
    }

    [Fact]
    public async Task CommandExecutor_CancellationTerminatesDescendantProcess()
    {
        using var directory = new TemporaryDirectory();
        var childPidPath = Path.Combine(directory.Path, "child.pid");
        var command = new OpticalDiscToolCommand(
            OpticalDiscOperation.DiscToIso,
            RepositoryTestContext.DotNetHost,
            [
                RepositoryTestContext.TestHelperAssembly,
                "spawn-and-wait",
                childPidPath
            ]);
        using var cancellation = new CancellationTokenSource();
        var run = new OpticalDiscCommandExecutor(TimeSpan.FromSeconds(30))
            .ExecuteAsync(command, cancellationToken: cancellation.Token);

        await RepositoryTestContext.WaitForFileAsync(childPidPath, TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var childPid = int.Parse(await File.ReadAllTextAsync(childPidPath));
        await RepositoryTestContext.AssertProcessExitedAsync(childPid, TimeSpan.FromSeconds(5));
    }

    private static string Touch(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static OpticalDiscToolInfo Tool(
        IReadOnlyList<OpticalDiscToolInfo> tools,
        OpticalDiscToolKind kind) => tools.Single(tool => tool.Kind == kind);

    private static OpticalDiscInfo Disc(
        OpticalDiscMediaKind kind,
        int driveOrdinal = 0) => new(
        @"E:\",
        @"\\.\E:",
        null,
        kind,
        true,
        kind is not OpticalDiscMediaKind.AudioCd,
        driveOrdinal);

    private sealed class AvailableToolDiscovery : IOpticalDiscToolDiscovery
    {
        public Task<IReadOnlyList<OpticalDiscToolInfo>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<OpticalDiscToolInfo> tools = Enum.GetValues<OpticalDiscToolKind>()
                .Select(Available)
                .ToList();
            return Task.FromResult(tools);
        }

        public Task<OpticalDiscToolInfo> DiscoverAsync(
            OpticalDiscToolKind kind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Available(kind));
        }

        private static OpticalDiscToolInfo Available(OpticalDiscToolKind kind) => new(
            kind,
            kind.ToString(),
            true,
            kind + ".exe",
            "1",
            "可用");
    }

    private sealed class ArtifactWritingExecutor(int exitCode = 0) : IOpticalDiscCommandExecutor
    {
        public List<OpticalDiscToolCommand> Commands { get; } = [];

        public Task<OpticalDiscCommandExecutionResult> ExecuteAsync(
            OpticalDiscToolCommand command,
            IProgress<OpticalDiscProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            switch (command.Operation)
            {
                case OpticalDiscOperation.InspectMakeMkvDrives:
                    return Task.FromResult(new OpticalDiscCommandExecutionResult(
                        exitCode,
                        @"DRV:7,2,999,12,""BD-ROM Vendor"",""DISC"",""E:""",
                        exitCode == 0 ? string.Empty : "failure"));
                case OpticalDiscOperation.BluRayToMkv:
                    File.WriteAllBytes(Path.Combine(command.Arguments[^1], "title00.mkv"), [1, 2, 3]);
                    break;
                case OpticalDiscOperation.AudioCdToWavTracks:
                    File.WriteAllBytes(Path.Combine(command.WorkingDirectory!, "track_01.wav"), [1, 2, 3]);
                    break;
                case OpticalDiscOperation.DiscToIso:
                    var output = command.Arguments.Single(argument => argument.StartsWith("f="))[2..];
                    File.WriteAllBytes(output, [1, 2, 3]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command.Operation));
            }

            return Task.FromResult(new OpticalDiscCommandExecutionResult(
                exitCode,
                "stdout",
                exitCode == 0 ? string.Empty : "failure"));
        }
    }

    private sealed class StubNativeDiscReader(
        Exception? audioError = null,
        Exception? isoError = null) : IWindowsOpticalDiscReader
    {
        public int AudioReadCount { get; private set; }

        public int IsoReadCount { get; private set; }

        public Task<CdTableOfContents> ReadTableOfContentsAsync(
            string rootOrDevicePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ExtractAudioCdToWaveAsync(
            string rootOrDevicePath,
            string outputDirectory,
            IProgress<OpticalOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AudioReadCount++;
            if (audioError is not null)
            {
                return Task.FromException<IReadOnlyList<string>>(audioError);
            }

            Directory.CreateDirectory(outputDirectory);
            var output = Path.Combine(outputDirectory, "track01.wav");
            File.WriteAllBytes(output, [1, 2, 3]);
            IReadOnlyList<string> outputs = [output];
            return Task.FromResult(outputs);
        }

        public Task<string> ReadDataDiscToIsoAsync(
            string rootOrDevicePath,
            string outputPath,
            IProgress<OpticalOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsoReadCount++;
            if (isoError is not null)
            {
                return Task.FromException<string>(isoError);
            }

            File.WriteAllBytes(outputPath, [1, 2, 3]);
            return Task.FromResult(Path.GetFullPath(outputPath));
        }
    }
}

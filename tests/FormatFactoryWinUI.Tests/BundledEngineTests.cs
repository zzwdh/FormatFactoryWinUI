using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Services;
using PdfSharp.Pdf;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class BundledEngineTests
{
    [Fact]
    public void IsBundledExecutable_UsesApplicationRootAndDirectoryBoundary()
    {
        var root = AppContext.BaseDirectory;
        Assert.True(BundledEngineLocator.IsBundledExecutable(Path.Combine(root, "Engines", "calibre", "ebook-convert.exe")));
        Assert.False(BundledEngineLocator.IsBundledExecutable(null));
        Assert.False(BundledEngineLocator.IsBundledExecutable(""));
        Assert.False(BundledEngineLocator.IsBundledExecutable(Path.Combine(root, "Engines-old", "calibre", "ebook-convert.exe")));
        Assert.False(BundledEngineLocator.IsBundledExecutable(Path.Combine(root, "Engines", "..", "ebook-convert.exe")));
        Assert.False(BundledEngineLocator.IsBundledExecutable(Path.Combine(root, "other", "Engines", "ebook-convert.exe")));
    }

    [Fact]
    public void FfmpegCandidates_PreferBundledPairBeforeLegacyAndSystemVersions()
    {
        using var directory = new TemporaryDirectory();
        var candidates = FfmpegLocator.EnumerateAutomaticCandidates(directory.Path).Take(3).ToArray();
        Assert.Equal(Path.Combine(directory.Path, "Engines", "ffmpeg", "bin", "ffmpeg.exe"), candidates[0]);
        Assert.Equal(Path.Combine(directory.Path, "Engines", "ffmpeg", "ffmpeg.exe"), candidates[1]);
        Assert.Contains(Path.Combine("FFmpeg", "Current"), candidates[2], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ffmpeg", "ffmpeg.exe")]
    [InlineData("calibre", "ebook-convert.exe")]
    [InlineData("libreoffice", "program/soffice.com")]
    [InlineData("caj2pdf", "python.exe")]
    public async Task BundledDiscovery_RejectsMissingCompanionsBeforeExecuting(string engineId, string relativeExecutable)
    {
        using var directory = new TemporaryDirectory();
        Touch(directory.Path, "Engines/" + engineId + "/" + relativeExecutable);
        var result = await BundledEngineLocator.FindExecutableAsync(engineId, baseDirectory: directory.Path,
            runProcess: (_, _, _, _) => throw new InvalidOperationException("Missing companions must prevent execution."));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundledDiscovery_RejectsFailedOrUnstartableRuntime(bool throws)
    {
        using var directory = new TemporaryDirectory();
        Touch(directory.Path, "Engines/readom/readom.exe");
        var result = await BundledEngineLocator.FindExecutableAsync("readom", baseDirectory: directory.Path,
            runProcess: (_, _, _, _) => throws
                ? throw new System.ComponentModel.Win32Exception("Missing DLL")
                : Task.FromResult(new ProcessExecutionResult(1, "", "Missing DLL")));
        Assert.Null(result);
    }

    [Fact]
    public async Task BundledDiscovery_PropagatesCancellation()
    {
        using var directory = new TemporaryDirectory();
        Touch(directory.Path, "Engines/readom/readom.exe");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BundledEngineLocator.FindExecutableAsync(
            "readom", cancellation.Token, directory.Path, (_, _, _, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Success();
            }));
    }

    [Fact]
    public async Task FfmpegBundle_RequiresBothExecutablesToRun()
    {
        using var directory = new TemporaryDirectory();
        var ffmpeg = Touch(directory.Path, "Engines/ffmpeg/ffmpeg.exe");
        var ffprobe = Touch(directory.Path, "Engines/ffmpeg/ffprobe.exe");
        var probes = new List<string>();
        var result = await BundledEngineLocator.FindExecutableAsync("ffmpeg", baseDirectory: directory.Path,
            runProcess: (path, arguments, _, _) =>
            {
                probes.Add(path);
                Assert.Equal(["-version"], arguments);
                return Task.FromResult(new ProcessExecutionResult(path == ffprobe ? 1 : 0, "", ""));
            });
        Assert.Null(result);
        Assert.Equal([ffmpeg, ffprobe], probes);
    }

    [Fact]
    public async Task CalibreBundle_ProbesConverterAndMetadataReader()
    {
        using var directory = new TemporaryDirectory();
        var converter = Touch(directory.Path, "Engines/calibre/ebook-convert.exe");
        var metadata = Touch(directory.Path, "Engines/calibre/ebook-meta.exe");
        var probes = new List<string>();
        var discovery = new ExternalToolDiscovery(null, directory.Path, (path, arguments, _, _) =>
        {
            probes.Add(path);
            Assert.Equal(["--version"], arguments);
            return Success();
        });
        var tool = await discovery.DiscoverAsync(ExternalToolKind.Calibre);
        Assert.True(tool.IsAvailable);
        Assert.Equal(converter, tool.ExecutablePath);
        Assert.Equal([converter, metadata], probes);
    }

    [Fact]
    public async Task ConfiguredDocumentTool_TakesPrecedenceAndKeepsStandaloneInvocation()
    {
        using var directory = new TemporaryDirectory();
        var configured = Touch(directory.Path, "custom/caj2pdf.exe");
        TouchCajBundle(directory.Path);
        var discovery = new ExternalToolDiscovery(new ExternalToolConfiguration(Caj2PdfPath: configured), directory.Path,
            (_, _, _, _) => throw new InvalidOperationException("Configured tool must take precedence."));
        var tool = await discovery.DiscoverAsync(ExternalToolKind.Caj2Pdf);
        Assert.Equal(configured, tool.ExecutablePath);
        Assert.Empty(tool.ArgumentPrefix);
    }

    [Fact]
    public async Task CajBundle_UsesIsolatedPythonAndAbsoluteLauncherForProbeAndConversion()
    {
        using var directory = new TemporaryDirectory();
        var python = TouchCajBundle(directory.Path);
        var launcher = Path.Combine(Path.GetDirectoryName(python)!, "launcher.py");
        var discovery = new ExternalToolDiscovery(null, directory.Path, (path, arguments, _, _) =>
        {
            Assert.Equal(python, path);
            Assert.Equal(["-I", "-B", launcher, "--version"], arguments);
            return Success();
        });
        var tool = await discovery.DiscoverAsync(ExternalToolKind.Caj2Pdf);
        Assert.True(tool.IsAvailable);
        Assert.Equal(["-I", "-B", launcher], tool.ArgumentPrefix);
        var input = Touch(directory.Path, "source & 文档.caj");
        var output = Path.Combine(directory.Path, "result.pdf");
        string? stagedOutput = null;
        var converter = new ExternalDocumentConverter(new CalibreEbookArtifactValidator(), (path, arguments, _, _) =>
        {
            Assert.Equal(python, path);
            var args = arguments.ToArray();
            Assert.Equal(["-I", "-B", launcher, "convert", input, "-o"], args.Take(6));
            stagedOutput = args[6];
            using var pdf = new PdfDocument();
            pdf.AddPage();
            pdf.Save(stagedOutput);
            return Success();
        });
        await converter.ConvertWithCaj2PdfAsync(tool, input, output);
        Assert.True(File.Exists(output));
        Assert.NotNull(stagedOutput);
        Assert.False(Directory.Exists(Path.GetDirectoryName(stagedOutput)));
    }

    [Theory]
    [InlineData("launcher.py")]
    [InlineData("python312.dll")]
    [InlineData("python312.zip")]
    [InlineData("python312._pth")]
    [InlineData("mutool.exe")]
    [InlineData("msvcp140.dll")]
    [InlineData("vcruntime140.dll")]
    [InlineData("vcruntime140_1.dll")]
    public async Task CajBundle_MissingNativeDependencyNeverStartsPython(string missingFile)
    {
        using var directory = new TemporaryDirectory();
        var python = TouchCajBundle(directory.Path);
        File.Delete(Path.Combine(Path.GetDirectoryName(python)!, missingFile));
        var processCalls = 0;

        var available = await BundledEngineLocator.ValidateExecutableAsync("caj2pdf", python,
            runProcess: (_, _, _, _) =>
            {
                processCalls++;
                return Success();
            });

        Assert.False(available);
        Assert.Equal(0, processCalls);
    }

    [Fact]
    public async Task BrokenBundleOnPath_IsSkippedAndSystemToolIsUsed()
    {
        using var directory = new TemporaryDirectory();
        TouchCajBundle(directory.Path);
        var rejected = Touch(directory.Path, "Engines/caj2pdf/caj2pdf.exe");
        var installed = Touch(directory.Path, "installed/caj2pdf.exe");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator,
                Path.GetDirectoryName(rejected), Path.GetDirectoryName(installed)));
            var discovery = new ExternalToolDiscovery(null, directory.Path,
                (_, _, _, _) => Task.FromResult(new ProcessExecutionResult(1, "", "broken runtime")));
            var tool = await discovery.DiscoverAsync(ExternalToolKind.Caj2Pdf);
            Assert.True(tool.IsAvailable);
            Assert.Equal(installed, tool.ExecutablePath);
            Assert.Empty(tool.ArgumentPrefix);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    [Fact]
    public async Task OpticalDiscovery_PrefersConfiguredThenValidatedBundle()
    {
        using var directory = new TemporaryDirectory();
        var configured = Touch(directory.Path, "custom/readom.exe");
        var bundled = Touch(directory.Path, "Engines/readom/readom.exe");
        var explicitDiscovery = new OpticalDiscToolDiscovery(new OpticalDiscToolConfiguration(ReadomPath: configured), directory.Path,
            (_, _, _, _) => throw new InvalidOperationException("Configured readom must take precedence."));
        Assert.Equal(configured, (await explicitDiscovery.DiscoverAsync(OpticalDiscToolKind.Readom)).ExecutablePath);
        var automatic = new OpticalDiscToolDiscovery(null, directory.Path, (path, arguments, _, _) =>
        {
            Assert.Equal(bundled, path);
            Assert.Equal(["-version"], arguments);
            return Success();
        });
        Assert.Equal(bundled, (await automatic.DiscoverAsync(OpticalDiscToolKind.Readom)).ExecutablePath);
    }

    [Fact]
    public async Task LibreOffice_UsesDistinctWritableProfilesAndCleansThemAfterConversion()
    {
        using var directory = new TemporaryDirectory();
        var input = Touch(directory.Path, "source & 文档.doc");
        var profiles = new List<string>();
        var converter = new ExternalDocumentConverter(new CalibreEbookArtifactValidator(), (path, arguments, _, _) =>
        {
            var args = arguments.ToArray();
            var profile = new Uri(Assert.Single(args, value => value.StartsWith("-env:UserInstallation="))[22..]).LocalPath;
            var staging = args[Array.IndexOf(args, "--outdir") + 1];
            Assert.Equal(Path.Combine(staging, "libreoffice-profile") + Path.DirectorySeparatorChar, profile);
            Assert.Equal(input, args[^1]);
            Directory.CreateDirectory(profile);
            profiles.Add(profile);
            File.WriteAllText(Path.Combine(staging, Path.GetFileNameWithoutExtension(input) + ".docx"), "converted document");
            return Success();
        });
        var tool = new ExternalToolInfo(ExternalToolKind.LibreOffice, "LibreOffice", true, "soffice.com", "1", "available");
        await converter.ConvertWithLibreOfficeAsync(tool, input, Path.Combine(directory.Path, "first.docx"));
        await converter.ConvertWithLibreOfficeAsync(tool, input, Path.Combine(directory.Path, "second.docx"));
        Assert.Equal(2, profiles.Distinct().Count());
        Assert.All(profiles, profile => Assert.False(Directory.Exists(profile)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LibreOffice_CleansProfileAndDoesNotCommitOnFailureOrCancellation(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        var input = Touch(directory.Path, "source.doc");
        var output = Path.Combine(directory.Path, "result.docx");
        string? profile = null;
        using var cancellation = new CancellationTokenSource();
        var converter = new ExternalDocumentConverter(new CalibreEbookArtifactValidator(), (_, arguments, _, token) =>
        {
            profile = new Uri(arguments.First(value => value.StartsWith("-env:UserInstallation="))[22..]).LocalPath;
            Directory.CreateDirectory(profile);
            if (cancel)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(new ProcessExecutionResult(1, "", "conversion failed"));
        });
        var tool = new ExternalToolInfo(ExternalToolKind.LibreOffice, "LibreOffice", true, "soffice.com", "1", "available");
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertWithLibreOfficeAsync(tool, input, output, cancellation.Token));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => converter.ConvertWithLibreOfficeAsync(tool, input, output));
        Assert.NotNull(profile);
        Assert.False(Directory.Exists(profile));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void LibreOfficeProfile_EncodesSpacesUnicodeAndUriDelimiters()
    {
        using var directory = new TemporaryDirectory();
        var staging = Path.Combine(directory.Path, "配置 #中文 & 空格");
        var argument = ExternalDocumentConverter.BuildLibreOfficeProfileArgument(staging);
        Assert.StartsWith("-env:UserInstallation=file:///", argument);
        Assert.Equal(Path.Combine(staging, "libreoffice-profile") + Path.DirectorySeparatorChar, new Uri(argument[22..]).LocalPath);
    }

    [Fact]
    public void SettingsNormalization_ClearsAllVersionedBundlePathsWithoutResolvingEmptyPath()
    {
        static string Bundled(string relative) => Path.Combine(AppContext.BaseDirectory, "Engines", relative);
        var draft = new FormatFactoryWinUI.Models.SettingsDraft
        {
            FfmpegPath = Bundled("ffmpeg/ffmpeg.exe"),
            LibreOfficePath = Bundled("libreoffice/program/soffice.com"),
            CalibreEbookConvertPath = Bundled("calibre/ebook-convert.exe"),
            Caj2PdfPath = Bundled("caj2pdf/python.exe"),
            MakeMkvPath = Bundled("makemkv/makemkvcon64.exe"),
            AudioCdRipperPath = Bundled("cdda2wav/icedax.exe"),
            ReadomPath = Bundled("readom/readom.exe"),
            ParallelCount = 5
        };

        var saved = BundledEngineLocator.NormalizeSettingsPaths(draft);

        Assert.Equal(new FormatFactoryWinUI.Models.SettingsDraft { ParallelCount = 5 }, saved);
    }

    [Fact]
    public void SettingsNormalization_PreservesCustomPathsAndEmptyAutomaticPaths()
    {
        var external = Path.Combine(AppContext.BaseDirectory, "Engines-old", "custom.exe");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, external);
        var draft = new FormatFactoryWinUI.Models.SettingsDraft
        {
            FfmpegPath = " " + relative + " ",
            LibreOfficePath = relative,
            CalibreEbookConvertPath = " ",
            Caj2PdfPath = relative,
            MakeMkvPath = relative,
            AudioCdRipperPath = relative,
            ReadomPath = relative
        };

        var saved = BundledEngineLocator.NormalizeSettingsPaths(draft);

        Assert.Equal(draft with
        {
            FfmpegPath = external,
            LibreOfficePath = external,
            CalibreEbookConvertPath = string.Empty,
            Caj2PdfPath = external,
            MakeMkvPath = external,
            AudioCdRipperPath = external,
            ReadomPath = external
        }, saved);
        Assert.Equal(new FormatFactoryWinUI.Models.SettingsDraft(),
            BundledEngineLocator.NormalizeSettingsPaths(new FormatFactoryWinUI.Models.SettingsDraft()));
    }
    private static Task<ProcessExecutionResult> Success() => Task.FromResult(new ProcessExecutionResult(0, "version", ""));

    private static string TouchCajBundle(string root)
    {
        foreach (var name in new[] { "launcher.py", "python312.dll", "python312.zip", "python312._pth",
                                    "mutool.exe", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
            Touch(root, "Engines/caj2pdf/" + name);
        return Touch(root, "Engines/caj2pdf/python.exe");
    }

    private static string Touch(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test file");
        return path;
    }
}

using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class OutputDirectoryDefaultsTests
{
    [Fact]
    public void Resolve_MissingRegisteredVideosFolderDoesNotUseWorkingDirectoryOrCreateIt()
    {
        using var directory = new TemporaryDirectory();
        var videos = Path.Combine(directory.Path, "not-created", "Videos");

        var output = OutputDirectoryDefaults.Resolve((folder, option) =>
        {
            Assert.Equal(Environment.SpecialFolder.MyVideos, folder);
            return option == Environment.SpecialFolderOption.DoNotVerify ? videos : string.Empty;
        });

        Assert.Equal(Path.Combine(videos, OutputDirectoryDefaults.FolderName), output);
        Assert.True(Path.IsPathFullyQualified(output));
        Assert.False(Directory.Exists(videos));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Videos")]
    public void Resolve_UnavailableOrRelativeVideosFolderUsesAbsoluteUserProfile(string videos)
    {
        using var directory = new TemporaryDirectory();
        var profile = Path.Combine(directory.Path, "profile");

        var output = OutputDirectoryDefaults.Resolve((folder, _) => folder switch
        {
            Environment.SpecialFolder.MyVideos => videos,
            Environment.SpecialFolder.UserProfile => profile,
            _ => throw new InvalidOperationException("No further fallback should be queried.")
        });

        Assert.Equal(Path.Combine(profile, "Videos", OutputDirectoryDefaults.FolderName), output);
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public void Resolve_UnavailableUserProfileUsesAbsoluteLocalAppData()
    {
        using var directory = new TemporaryDirectory();
        var output = OutputDirectoryDefaults.Resolve((folder, _) =>
            folder == Environment.SpecialFolder.LocalApplicationData ? directory.Path : string.Empty);

        Assert.Equal(Path.Combine(directory.Path, OutputDirectoryDefaults.FolderName, "Output"), output);
        Assert.True(Path.IsPathFullyQualified(output));
    }

    [Fact]
    public void Resolve_UnavailableAbsoluteFoldersNeverFallsBackToWorkingDirectory()
    {
        Assert.Throws<DirectoryNotFoundException>(() => OutputDirectoryDefaults.Resolve((_, _) => string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Format Factory WinUI")]
    [InlineData("format factory winui\\")]
    [InlineData("FluentFormatConverter")]
    [InlineData("fluentformatconverter\\")]
    public void ResolveOutputDirectory_RepairsLegacyRelativeDefault(string configured)
    {
        var settings = new AppSettings { CustomOutputDirectory = configured };
        var task = new MediaTask { InputPath = Path.Combine(Path.GetTempPath(), "input", "clip.mov") };

        var output = OutputPathService.ResolveOutputDirectory(task, settings);

        Assert.Equal(new AppSettings().CustomOutputDirectory, output);
        Assert.True(Path.IsPathFullyQualified(output));
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("Format Factory WinUI")]
    public void ResolveOutputDirectory_PreservesExplicitCustomAndSourceDirectories(string folderName)
    {
        using var directory = new TemporaryDirectory();
        var task = new MediaTask { InputPath = Path.Combine(directory.Path, "source", "clip.mov") };
        var custom = Path.Combine(directory.Path, folderName);

        Assert.Equal(custom, OutputPathService.ResolveOutputDirectory(task,
            new AppSettings { OutputMode = "custom", CustomOutputDirectory = custom }));
        Assert.Equal(task.InputDirectory, OutputPathService.ResolveOutputDirectory(task,
            new AppSettings { OutputMode = "source", CustomOutputDirectory = custom }));
    }
}

using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class CalibreEbookArtifactValidatorTests
{
    [Fact]
    public async Task ValidateAsync_UsesCompanionEbookMetaAndAcceptsReadableMetadata()
    {
        using var directory = new TemporaryDirectory();
        var converter = Path.Combine(directory.Path, "ebook-convert.exe");
        var metadataReader = Path.Combine(directory.Path, "ebook-meta.exe");
        var artifact = Path.Combine(directory.Path, "book.azw3");
        await File.WriteAllTextAsync(converter, "converter");
        await File.WriteAllTextAsync(metadataReader, "metadata");
        await File.WriteAllTextAsync(artifact, "ebook-content");
        string? invokedExecutable = null;
        IReadOnlyList<string>? invokedArguments = null;
        var validator = new CalibreEbookArtifactValidator((executable, arguments, _, _) =>
        {
            invokedExecutable = executable;
            invokedArguments = arguments.ToList();
            return Task.FromResult(new ProcessExecutionResult(0, "Title : 测试电子书\nAuthor(s) : 作者", string.Empty));
        });

        var result = await validator.ValidateAsync(Tool(converter), artifact, CancellationToken.None);

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(metadataReader, invokedExecutable);
        Assert.Equal([artifact], invokedArguments);
    }

    [Fact]
    public async Task Discovery_RequiresCompanionEbookMetaBeforeAdvertisingCalibre()
    {
        using var directory = new TemporaryDirectory();
        var converter = Path.Combine(directory.Path, "ebook-convert.exe");
        await File.WriteAllTextAsync(converter, "converter");
        var discovery = new ExternalToolDiscovery(new ExternalToolConfiguration(
            CalibreEbookConvertPath: converter));

        var missingMetadata = await discovery.DiscoverAsync(ExternalToolKind.Calibre);

        Assert.False(missingMetadata.IsAvailable);
        Assert.Contains("ebook-meta", missingMetadata.StatusMessage, StringComparison.OrdinalIgnoreCase);

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "ebook-meta.exe"), "metadata");
        var available = await discovery.DiscoverAsync(ExternalToolKind.Calibre);

        Assert.True(available.IsAvailable, available.StatusMessage);
        Assert.Equal(converter, available.ExecutablePath);
    }

    [Fact]
    public async Task ValidateAsync_FailsWhenCompanionEbookMetaIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var converter = Path.Combine(directory.Path, "ebook-convert.exe");
        var artifact = Path.Combine(directory.Path, "book.mobi");
        await File.WriteAllTextAsync(converter, "converter");
        await File.WriteAllTextAsync(artifact, "ebook-content");
        var validator = new CalibreEbookArtifactValidator((_, _, _, _) =>
            throw new InvalidOperationException("runner should not be invoked"));

        var result = await validator.ValidateAsync(Tool(converter), artifact, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("ebook-meta", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, "Title : 测试", "conversion failed")]
    [InlineData(0, "metadata without a value separator", "")]
    public async Task ValidateAsync_RejectsFailedOrUnreadableMetadata(int exitCode, string output, string error)
    {
        using var directory = new TemporaryDirectory();
        var converter = Path.Combine(directory.Path, "ebook-convert.exe");
        var metadataReader = Path.Combine(directory.Path, "ebook-meta.exe");
        var artifact = Path.Combine(directory.Path, "book.mobi");
        await File.WriteAllTextAsync(converter, "converter");
        await File.WriteAllTextAsync(metadataReader, "metadata");
        await File.WriteAllTextAsync(artifact, "ebook-content");
        var validator = new CalibreEbookArtifactValidator((_, _, _, _) =>
            Task.FromResult(new ProcessExecutionResult(exitCode, output, error)));

        var result = await validator.ValidateAsync(Tool(converter), artifact, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    private static ExternalToolInfo Tool(string converter) =>
        new(ExternalToolKind.Calibre, "Calibre", true, converter, "1", "可用");
}

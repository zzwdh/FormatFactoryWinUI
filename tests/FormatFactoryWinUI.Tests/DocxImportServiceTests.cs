using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class DocxImportServiceTests
{
    private static readonly ExternalToolInfo LibreOffice =
        new(ExternalToolKind.LibreOffice, "LibreOffice", true, "soffice.com", "1", "available");

    [Fact]
    public async Task ValidDocument_DoesNotRequireOrInvokeRepairEvenWhenEnabled()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "original.docx");
        WriteDocument(input, invalid: false);
        var importer = Importer((_, _, _, _) => throw new InvalidOperationException("Repair must not run."));

        var result = await importer.ReadAsync(input, true, [], Path.Combine(directory.Path, "unused-workspace"));

        Assert.Equal("content", Assert.IsType<NormalizedParagraph>(Assert.Single(result.Blocks)).Text);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "unused-workspace")));
    }

    [Fact]
    public async Task InvalidDocument_WithoutOptInStillRejectsAndNeverInvokesRepair()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "original.docx");
        WriteDocument(input, invalid: true);
        var importer = Importer((_, _, _, _) => throw new InvalidOperationException("Repair must not run."));

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ReadAsync(input, false,
            [LibreOffice], Path.Combine(directory.Path, "unused-workspace")));

        Assert.False(Directory.Exists(Path.Combine(directory.Path, "unused-workspace")));
    }

    [Fact]
    public async Task OptedInRepair_UsesIsolatedCopyValidatesOutputAndKeepsOriginalAndTitle()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "原始文档.docx");
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace")).FullName;
        WriteDocument(input, invalid: true);
        var original = await File.ReadAllBytesAsync(input);
        var calls = 0;
        string? repairInput = null;
        var importer = Importer(async (_, arguments, _, token) =>
        {
            calls++;
            var args = arguments.ToArray();
            repairInput = args[^1];
            Assert.NotEqual(input, repairInput);
            Assert.StartsWith(workspace + Path.DirectorySeparatorChar, repairInput);
            Assert.Equal(original, await File.ReadAllBytesAsync(repairInput, token));
            Assert.Equal("docx:Office Open XML Text", args[Array.IndexOf(args, "--convert-to") + 1]);
            // Even a repair tool that rewrites its input cannot modify the original.
            await File.WriteAllTextAsync(repairInput, "changed temporary input", token);
            var staging = args[Array.IndexOf(args, "--outdir") + 1];
            WriteDocument(Path.Combine(staging, Path.GetFileName(repairInput)), invalid: false);
            return new ProcessExecutionResult(0, "", "");
        });

        var result = await importer.ReadAsync(input, true, [LibreOffice], workspace);

        Assert.Equal(1, calls);
        Assert.Equal("原始文档", result.Title);
        Assert.Equal("content", Assert.IsType<NormalizedParagraph>(Assert.Single(result.Blocks)).Text);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.NotNull(repairInput);
        Assert.False(File.Exists(repairInput));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
    }

    [Fact]
    public async Task RepairToolSuccess_WithStillInvalidPackageIsRejectedAndCleaned()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "original.docx");
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace")).FullName;
        WriteDocument(input, invalid: true);
        var original = await File.ReadAllBytesAsync(input);
        var importer = Importer((_, arguments, _, _) =>
        {
            var args = arguments.ToArray();
            var staging = args[Array.IndexOf(args, "--outdir") + 1];
            WriteDocument(Path.Combine(staging, Path.GetFileName(args[^1])), invalid: true);
            return Task.FromResult(new ProcessExecutionResult(0, "", ""));
        });

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            importer.ReadAsync(input, true, [LibreOffice], workspace));

        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("4294963199")]
    [InlineData("4294967295")]
    [InlineData("-4097")]
    [InlineData("0")]
    [InlineData(null)]
    public async Task RegeneratedGridOffset_PassesStrictValidationWithSignedNormalizationWhenNeeded(string? spacing)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "original.docx");
        WriteDocument(input, invalid: true);
        var original = await File.ReadAllBytesAsync(input);
        var importer = GridRepairImporter(spacing);

        var result = await importer.ReadAsync(input, true, [LibreOffice], Path.Combine(directory.Path, "workspace"));

        Assert.Equal("content", Assert.IsType<NormalizedParagraph>(Assert.Single(result.Blocks)).Text);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
    }

    [Theory]
    [InlineData("4294967296")]
    [InlineData("invalid")]
    public async Task RegeneratedGridOffset_OutsideUnsigned32BitDomainStillFailsStrictValidation(string spacing)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "original.docx");
        WriteDocument(input, invalid: true);
        var original = await File.ReadAllBytesAsync(input);

        await Assert.ThrowsAsync<InvalidDataException>(() => GridRepairImporter(spacing)
            .ReadAsync(input, true, [LibreOffice], Path.Combine(directory.Path, "workspace")));

        Assert.Equal(original, await File.ReadAllBytesAsync(input));
    }

    private static DocxImportService GridRepairImporter(string? spacing) => Importer((_, arguments, _, _) =>
    {
        var args = arguments.ToArray();
        var staging = args[Array.IndexOf(args, "--outdir") + 1];
        var output = Path.Combine(staging, Path.GetFileName(args[^1]));
        WriteDocument(output, invalid: false);
        using var package = WordprocessingDocument.Open(output, true);
        var grid = new DocGrid();
        if (spacing is not null)
            grid.SetAttribute(new OpenXmlAttribute("w", "charSpace", "http://schemas.openxmlformats.org/wordprocessingml/2006/main", spacing));
        var document = package.MainDocumentPart!.Document!;
        document.Body!.AppendChild(new SectionProperties(grid));
        document.Save();
        return Task.FromResult(new ProcessExecutionResult(0, "", ""));
    });

    [Fact]
    public async Task RepairFailure_KeepsOriginalAndDoesNotLeaveTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "original.docx");
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace")).FullName;
        WriteDocument(input, invalid: true);
        var original = await File.ReadAllBytesAsync(input);
        var importer = Importer((_, _, _, _) => Task.FromResult(new ProcessExecutionResult(1, "", "tool failed")));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            importer.ReadAsync(input, true, [LibreOffice], workspace));

        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
    }

    [Fact]
    public async Task RepairCancellation_RemainsCancellationAndCleansTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "original.docx");
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace")).FullName;
        WriteDocument(input, invalid: true);
        var original = await File.ReadAllBytesAsync(input);
        using var cancellation = new CancellationTokenSource();
        var importer = Importer((_, _, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable.");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            importer.ReadAsync(input, true, [LibreOffice], workspace, cancellationToken: cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
    }

    [Fact]
    public async Task InvalidDocument_OptedInWithoutLibreOfficeReportsUnavailable()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "original.docx");
        WriteDocument(input, invalid: true);
        var importer = Importer((_, _, _, _) => throw new InvalidOperationException("Repair must not run."));

        await Assert.ThrowsAsync<DocumentEngineUnavailableException>(() => importer.ReadAsync(input, true,
            [], Path.Combine(directory.Path, "unused-workspace")));

        Assert.False(Directory.Exists(Path.Combine(directory.Path, "unused-workspace")));
    }

    private static DocxImportService Importer(
        Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>> runProcess) =>
        new(new DocxDocumentReader(), new ExternalDocumentConverter(new CalibreEbookArtifactValidator(), runProcess));

    private static void WriteDocument(string path, bool invalid)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var part = package.AddMainDocumentPart();
        var paragraph = new Paragraph(new Run(new Text("content")));
        if (invalid) paragraph.SetAttribute(new OpenXmlAttribute("url", string.Empty, "https://example.invalid/"));
        part.Document = new Document(new Body(paragraph));
        part.Document.Save();
    }
}

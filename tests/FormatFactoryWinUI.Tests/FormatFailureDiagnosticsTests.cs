using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class FormatFailureDiagnosticsTests
{
    private const string Private = "SyntheticPrivate private-media.mov token=synthetic-secret https://synthetic.invalid/private";

    [Fact]
    public void Formatter_RejectsPrivateArbitraryValuesAndExceptionData_AndRetainsEarliestFact()
    {
        var error = new InvalidDataException(Private);
        error.Data["stage"] = Private;
        FormatFailureDiagnostics.Record(error, new(FormatFailureStage.RemuxPolicy, FormatFailureReason.UnsupportedStream)
        { Source = Private, Target = Private, Codec = Private, StreamType = Private, SchemaId = Private, Count = 3 });
        FormatFailureDiagnostics.Record(error, new(FormatFailureStage.Unknown, FormatFailureReason.Unknown));
        var line = FormatFailureDiagnostics.Format(error)!;
        Assert.Contains("stage=RemuxPolicy reason=UnsupportedStream", line);
        Assert.Contains("source=unknown target=unknown codec=unknown stream_type=unknown schema=other", line);
        Assert.Contains("count=3", line);
        Assert.DoesNotContain("SyntheticPrivate", line);
        Assert.DoesNotContain("token", line);
        Assert.DoesNotContain("synthetic.invalid", line);
        Assert.DoesNotContain("private-media", line);
        Assert.Equal(line, FormatFailureDiagnostics.Format(new Exception(Private, error)));
        Assert.Equal(Private, error.Message);
        Assert.Equal(Private, error.Data["stage"]);
    }

    [Theory]
    [InlineData(23, "HeaderSize")]
    [InlineData(25, "HeaderSize")]
    [InlineData(0, "Version")]
    public async Task CsoReject_KeepsObservedNumericHeaderWithoutChangingSource(int headerSize, string reason)
    {
        byte[] bytes = new byte[24];
        "CISO"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)headerSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 2048);
        bytes[20] = headerSize == 0 ? (byte)2 : (byte)1;
        var original = bytes.ToArray();
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "header.cso");
        await File.WriteAllBytesAsync(path, bytes);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new DiscImageConverter().ConvertCsoToIsoAsync(path, Path.Combine(directory.Path, "decode.iso")));
        var line = FormatFailureDiagnostics.Format(error)!;
        Assert.Contains("stage=CsoHeader reason=" + reason, line);
        Assert.Contains("header_size=" + headerSize, line);
        Assert.Contains("total_bytes=2048 block_size=2048", line);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task PdfInputAndOutputParseFailures_HaveDifferentStagesAndPreserveContent()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "private.pdf");
        var bytes = System.Text.Encoding.UTF8.GetBytes(Private);
        await File.WriteAllBytesAsync(path, bytes);
        var error = await Record.ExceptionAsync(() => new PdfContentExtractor().ExtractAsync(path));
        Assert.NotNull(error);
        var inputLine = FormatFailureDiagnostics.Format(error)!;
        Assert.Contains("stage=PdfInputParse reason=ParseFailure", inputLine);
        Assert.Contains("source=pdf", inputLine);
        var result = await new PdfArtifactValidator().ValidateAsync(new("output", "pdf", path), CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Equal(error.Message, result.Message);
        var outputLine = FormatFailureDiagnostics.Format(result)!;
        Assert.Contains("stage=PdfOutputValidate reason=ParseFailure", outputLine);
        Assert.Contains("target=pdf", outputLine);
        foreach (var line in new[] { inputLine, outputLine })
        {
            Assert.DoesNotContain("private.pdf", line);
            Assert.DoesNotContain("synthetic-secret", line);
            Assert.DoesNotContain("synthetic.invalid", line);
            Assert.DoesNotContain("SyntheticPrivate", line);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MissingPdfOutput_RemainsInvalidWithoutInventingHeaderBytes()
    {
        using var directory = new TemporaryDirectory();
        var result = await new PdfArtifactValidator().ValidateAsync(new("output", "pdf", Path.Combine(directory.Path, "missing.pdf")), default);
        Assert.False(result.IsValid);
        var line = FormatFailureDiagnostics.Format(result)!;
        Assert.Contains("stage=PdfOutputValidate reason=Missing", line);
        Assert.DoesNotContain("pdf_header_offset", line);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DocxSchemaFailure_RecordsSafeSchemaCategoryWithoutContentOrAttributes()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "private.docx");
        using (var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var part = package.AddMainDocumentPart();
            var paragraph = new Paragraph(new Run(new Text(Private)));
            paragraph.SetAttribute(new OpenXmlAttribute("url", string.Empty, "https://synthetic.invalid/private"));
            part.Document = new Document(new Body(paragraph));
            part.Document.Save();
        }
        var original = await File.ReadAllBytesAsync(path);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new DocxDocumentReader().ReadAsync(path));
        var line = FormatFailureDiagnostics.Format(error)!;
        Assert.Contains("stage=DocxSchema reason=SchemaValidation", line);
        Assert.Contains("source=docx", line);
        Assert.DoesNotContain("SyntheticPrivate", line);
        Assert.DoesNotContain("synthetic.invalid", line);
        Assert.DoesNotContain("private.docx", line);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("precision", "Precision", "pixel_bits=16")]
    [InlineData("pages", "MultiplePages", "pages=2")]
    [InlineData("animation", "AnimationFormat", "animated=true")]
    public void ImagePolicy_RecordsObservedReasonWithoutWeakeningGuard(string scenario, string reason, string fact)
    {
        var task = new MediaTask { InputPath = "synthetic-missing.png", SourceFormatId = "png", TargetFormatId = "jpg",
            Kind = MediaKind.Image, ConversionMode = "lossless",
            Probe = new() { Kind = MediaKind.Image, Width = 20, Height = 30, PixelFormat = "rgb48le", PageCount = scenario == "pages" ? 2 : 1,
                IsAnimated = scenario == "animation" } };
        var error = Assert.ThrowsAny<NotSupportedException>(() => ImagePreservationPolicy.ValidateFfmpegInput(task));
        var line = FormatFailureDiagnostics.Format(error)!;
        Assert.Contains("stage=ImagePolicy reason=" + reason, line);
        Assert.Contains(fact, line);
        Assert.Contains("source=png target=jpg", line);
        Assert.DoesNotContain(task.InputPath, line);
    }

    [Theory]
    [InlineData("aac", "aac")]
    [InlineData(Private, "unknown")]
    public void StrictCopyFailure_PreservesRefusalAndFiltersCodecMetadata(string codec, string safeCodec)
    {
        var task = new MediaTask { InputPath = "synthetic.m4a", SourceFormatId = "m4a", TargetFormatId = "mp3",
            Kind = MediaKind.Audio, ConversionMode = "lossless" };
        var error = Assert.Throws<NotSupportedException>(() => RemuxCompatibilityService.Validate(task, [new("audio", codec)]));
        var line = FormatFailureDiagnostics.Format(error)!;
        Assert.Contains("stage=RemuxPolicy reason=UnsupportedStream", line);
        Assert.Contains("codec=" + safeCodec, line);
        Assert.Contains("target=mp3", line);
        Assert.DoesNotContain("SyntheticPrivate", line);
        Assert.DoesNotContain("synthetic-secret", line);
    }

    [Theory]
    [InlineData("probe", "NativeImageProbe")]
    [InlineData("encode", "NativeImageEncode")]
    [InlineData("validate", "NativeImageValidate")]
    public async Task NativeCodecFailure_PreservesFailureAndSeparatesObservedStages(string failure, string stage)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "source.bmp");
        await File.WriteAllBytesAsync(path, [7, 8, 9]);
        var error = new COMException(Private, unchecked((int)0xC00D5212));
        var codec = new FailingCodec(failure, error);
        var task = new MediaTask { InputPath = path, SourceFormatId = "bmp", TargetFormatId = "png", Kind = MediaKind.Image, ConversionMode = "lossless" };
        var service = new NativeImageConversionService(codec, (_, _, _, _, _) => throw new InvalidOperationException("No fallback for lossless failure."));
        var result = await service.ConvertAsync(task, "", new AppSettings { OutputMode = "source" }, _ => { }, default);
        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Contains("stage=" + stage, FormatFailureDiagnostics.Format(error)!);
        Assert.Contains("hresult=0xC00D5212", FormatFailureDiagnostics.Format(error)!);
        Assert.DoesNotContain("synthetic-secret", FormatFailureDiagnostics.Format(error)!);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(false, "Original")]
    [InlineData(true, "Intermediate")]
    [InlineData(null, "Unknown")]
    public async Task CalibreFailure_SeparatesOriginalFromIntermediateWithoutLoggingNames(bool? intermediate, string origin)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "private-book.epub");
        await File.WriteAllTextAsync(input, Private);
        var converter = new ExternalDocumentConverter(new CalibreEbookArtifactValidator(),
            (_, _, _, _) => Task.FromResult(new ProcessExecutionResult(1, "", Private)));
        var tool = new ExternalToolInfo(ExternalToolKind.Calibre, Private, true, Path.Combine(directory.Path, "ebook-convert.exe"), Private, Private);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => converter.ConvertWithCalibreAsync(tool, input,
            Path.Combine(directory.Path, "out.mobi"), cancellationToken: default, diagnosticIntermediate: intermediate));
        var line = FormatFailureDiagnostics.Format(error)!;
        Assert.Contains("stage=CalibreRun", line);
        Assert.Contains("input_origin=" + origin, line);
        Assert.Contains("source=epub target=mobi", line);
        Assert.DoesNotContain("private-book", line);
        Assert.DoesNotContain("SyntheticPrivate", line);
        Assert.DoesNotContain("synthetic-secret", line);
        Assert.Equal(Private, await File.ReadAllTextAsync(input));
        Assert.Equal([input], Directory.GetFiles(directory.Path));
    }

    private sealed class FailingCodec(string failure, Exception error) : IImageCodecService
    {
        private int _probes;
        public bool CanEncode(string targetFormatId) => true;
        public Task<MediaProbeResult?> ProbeAsync(string path, CancellationToken token = default)
        {
            _probes++;
            if (failure == "probe" || failure == "validate" && _probes > 1) return Task.FromException<MediaProbeResult?>(error);
            return Task.FromResult<MediaProbeResult?>(new() { Kind = MediaKind.Image, Width = 20, Height = 30, PixelFormat = "bgra", PageCount = 1 });
        }
        public async Task TranscodeAsync(string input, string output, string target, string mode, CancellationToken token = default)
        {
            if (failure == "encode") throw error;
            await File.WriteAllBytesAsync(output, [1], token);
        }
    }
}

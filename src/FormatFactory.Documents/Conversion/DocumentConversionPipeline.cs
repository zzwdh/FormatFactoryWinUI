using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ConversionEngines;

internal delegate IAsyncEnumerable<DecodedImagePage> RenderPdfPages(
    string pdfPath, bool recognizeText, string? languageTag, string? password, string? imageDirectory,
    bool includeImageContent = true, CancellationToken cancellationToken = default);

internal delegate IAsyncEnumerable<DecodedImagePage> DecodeImagePages(
    IReadOnlyList<string> inputPaths, bool recognizeText, string? languageTag, string? imageDirectory,
    bool includeImageContent, CancellationToken cancellationToken);

/// <summary>Executes and validates document conversions with host-supplied page readers.</summary>
internal sealed class DocumentConversionPipeline : IConversionEngine
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ExternalToolInfo>>> _discoverTools;
    private readonly ExternalDocumentConverter _externalConverter = new();
    private readonly DocxImportService _docxImporter;
    private readonly OfficeFixedLayoutEpubConverter _officeFixedLayoutEpub;
    private readonly WindowsFilterTextExtractor _windowsFilterTextExtractor = new();
    private readonly PlainTextDocumentWriter _textWriter = new();
    private readonly MarkdownDocumentWriter _markdownWriter = new();
    private readonly NormalizedDocumentEpubAdapter _epubAdapter = new();
    private readonly Epub3Writer _epubWriter = new();
    private readonly DocumentConversionRoutePlanner _planner = new();
    private readonly PdfContentExtractor _pdfExtractor;
    private readonly PdfDocxWriter _pdfDocxWriter = new();
    private readonly SearchablePdfWriter _searchablePdfWriter = new();
    private readonly RenderPdfPages _renderPdfPages;
    private readonly DecodeImagePages _decodeImagePages;
    private readonly ImageDocumentBuilder _imageDocumentBuilder = new();
    private readonly ImagePdfWriter _imagePdfWriter = new();
    private readonly PdfArtifactValidator _pdfValidator = new();
    private readonly EpubStructureValidator _epubValidator = new();

    public DocumentConversionPipeline(Func<CancellationToken, Task<IReadOnlyList<ExternalToolInfo>>> discoverTools,
        PdfContentExtractor pdfExtractor,
        RenderPdfPages renderPdfPages, DecodeImagePages decodeImagePages)
    {
        _discoverTools = discoverTools;
        _pdfExtractor = pdfExtractor;
        _renderPdfPages = renderPdfPages;
        _decodeImagePages = decodeImagePages;
        _docxImporter = new DocxImportService(new DocxDocumentReader(), _externalConverter);
        _officeFixedLayoutEpub = new OfficeFixedLayoutEpubConverter(_imageDocumentBuilder, _epubAdapter, _epubWriter);
    }

    public string EngineId => DocumentEngineIds.Pipeline;

    public bool CanHandle(string sourceFormatId, string targetFormatId) =>
        _planner.CanPlan(sourceFormatId, targetFormatId);

    public async Task<ConversionExecutionResult> ConvertAsync(
        ConversionRequest request,
        IProgress<ConversionExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? uncommittedPath = null;
        var diagnosticStage = FormatFailureStage.DocumentPolicy;
        try
        {
            request.Validate();
            ApplicationServices.DocumentPreservationPolicy.Validate(request);
            diagnosticStage = FormatFailureStage.DocumentConvert;
            var tools = await _discoverTools(cancellationToken);
            var plan = _planner.Plan(request, tools);
            var inputFormats = request.Inputs.Select(input => Normalize(input.FormatId)).Distinct(StringComparer.Ordinal).ToList();
            var sourceFormat = inputFormats.Count == 1 ? inputFormats[0] : "image";
            var targetFormat = request.TargetFormatId.Trim().TrimStart('.').ToLowerInvariant();
            var outputTask = ApplicationServices.ConversionRequestFactory.CreateMediaTask(request, EngineId);
            var outputSettings = ApplicationServices.ConversionRequestFactory.OutputSettings(
                new AppSettings { OutputMode = "source" }, request);
            // Markdown owns its atomic commit together with a sibling resource directory.
            var markdown = targetFormat == "md";
            using var output = new OutputPathService().Reserve(outputTask, outputSettings,
                FormatCatalog.ExtensionFor(targetFormat), isDirectory: false,
                explicitOutputPath: Option(request, "outputPath"), writeDirectly: markdown);
            var outputPath = markdown ? output.FinalPath : output.TemporaryPath;
            if (!markdown) uncommittedPath = outputPath;
            progress?.Report(new ConversionExecutionProgress(0, "planning", LocalizationService.T("ConversionUiPlanStepsSelected", plan.Steps.Count)));

            using var workspace = new PipelineWorkspace();
            using var workspaceBudget = DiskSpaceBudgetService.Shared.Reserve(outputTask, workspace.Path, workspace.Path);
            await ExecuteAsync(sourceFormat, targetFormat, request, tools, outputPath, workspace, progress, cancellationToken);
            // The Markdown writer has already committed the document and its
            // resources together. A cancellation arriving afterwards cannot
            // turn that committed output into a cancelled result.
            diagnosticStage = FormatFailureStage.DocumentOutput;
            var strictlyValidated = await ValidateOutputAsync(targetFormat, outputPath,
                markdown ? CancellationToken.None : cancellationToken);
            if (!markdown)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (strictlyValidated)
                    output.CommitValidated(request.TaskId, cancellationToken: cancellationToken,
                        targetFormatId: targetFormat, conversionMode: request.Mode, engineId: EngineId);
                else output.Commit();
            }
            uncommittedPath = null;
            try { progress?.Report(new ConversionExecutionProgress(100, "completed", LocalizationService.T("ConversionUiDocumentConversionCompleted"))); }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Committed document progress observer failed: {exception.Message}"); }
            return new ConversionExecutionResult(
                true,
                false,
                new[] { new ConversionArtifact("output", targetFormat, output.FinalPath) },
                LocalizationService.T("ConversionUiDocumentConversionCompleted"));
        }
        catch (OperationCanceledException)
        {
            return new ConversionExecutionResult(false, true, [], LocalizationService.T("ConversionUiDocumentConversionCancelled"));
        }
        catch (OutputCommitException ex)
        {
            // The lease preserved the validated artifact, including when indexing
            // failed. The ordinary failure cleanup must not delete that private copy.
            uncommittedPath = null;
            return new ConversionExecutionResult(false, false, [], ex.Message)
            {
                RecoverableOutputId = ex.RecoveryId,
                PreservedOutputPath = ex.PreservedPath
            };
        }
        catch (Exception ex)
        {
            FormatFailureDiagnostics.Record(ex, new(diagnosticStage,
                diagnosticStage == FormatFailureStage.DocumentPolicy ? FormatFailureReason.UnsupportedRoute : FormatFailureReason.Unknown)
            { Target = request.TargetFormatId, Count = request.Inputs.Count,
                Source = request.Inputs.Count == 1 ? request.Inputs[0].FormatId : null, Lossless = request.Mode == "lossless" });
            FormatFailureDiagnostics.WriteApp(ex, request.TaskId);
            return new ConversionExecutionResult(false, false, [], ex.Message)
            { Retryable = ConversionFailurePolicy.IsTransient(ex) };
        }
        finally
        {
            if (uncommittedPath is not null)
            {
                try { if (File.Exists(uncommittedPath)) File.Delete(uncommittedPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private async Task ExecuteAsync(
        string source,
        string target,
        ConversionRequest request,
        IReadOnlyList<ExternalToolInfo> tools,
        string outputPath,
        PipelineWorkspace workspace,
        IProgress<ConversionExecutionProgress>? progress,
        CancellationToken token)
    {
        var inputs = request.Inputs.Select(item => Path.GetFullPath(item.Path)).ToList();
        var input = inputs[0];
        var language = Option(request, "ocrLanguage") ?? Option(request, "language");
        var password = Option(request, "password");
        var fixedLayout = DocumentConversionOptions.IsFixedLayout(request);

        if (source == "pdf" && target is "pdf" or "docx" or "txt" or "md" or "epub" or "mobi" or "azw3")
        {
            await ConvertPdfBuiltInAsync(input, target, outputPath, language, password, fixedLayout, tools, workspace, progress, token);
            return;
        }

        if ((source == "image" || DocumentConversionRoutePlanner.IsImageFormat(source)) &&
            target is "pdf" or "txt" or "md" or "epub" or "mobi" or "azw3")
        {
            progress?.Report(new ConversionExecutionProgress(15, "image-decode", LocalizationService.T("ConversionUiDecodingImagePages")));
            var pages = _decodeImagePages(inputs, target != "pdf", language,
                workspace.Path, includeImageContent: target != "txt", cancellationToken: token);
            if (target == "pdf")
            {
                progress?.Report(new ConversionExecutionProgress(65, "image-pdf", LocalizationService.T("ConversionUiMergingImagePages")));
                await _imagePdfWriter.WriteAsync(pages, outputPath, token);
                return;
            }

            var title = Option(request, "title") ?? Path.GetFileNameWithoutExtension(input);
            var document = await _imageDocumentBuilder.BuildAsync(pages, title, language ?? "zh-CN", fixedLayout, token);
            await WriteNormalizedAsync(document, target, outputPath, tools, workspace, progress, fixedLayout, token);
            return;
        }

        if (source == "docx" && target == "epub" && fixedLayout)
        {
            progress?.Report(new ConversionExecutionProgress(10, "office-layout",
                LocalizationService.T("DocumentCompatibilityFixedEpub")));
            await _officeFixedLayoutEpub.ConvertAsync(input, outputPath, workspace.Path, language,
                Option(request, "title"),
                (sourcePath, pdfPath, cancellation) => ConvertOfficeToPdfAsync(sourcePath, pdfPath, tools, progress, cancellation),
                (pdfPath, cancellation) => _renderPdfPages(pdfPath,
                    recognizeText: false, language, password: null, workspace.Path, cancellationToken: cancellation),
                progress, token);
            return;
        }

        if (source == "docx" && target is "txt" or "md" or "epub")
        {
            await ConvertDocxBuiltInAsync(input, target, outputPath, tools, workspace,
                DocumentConversionOptions.RepairDocx(request), progress, token);
            return;
        }

        if (source is "doc" or "docx" && target == "pdf")
        {
            await ConvertOfficeToPdfAsync(input, outputPath, tools, progress, token);
            return;
        }

        if (source == "doc" && target is "txt" or "md" or "epub" or "mobi" or "azw3")
        {
            var libreOffice = tools.FirstOrDefault(tool => tool.Kind == ExternalToolKind.LibreOffice && tool.IsAvailable);
            if (libreOffice is not null)
            {
                try
                {
                    var docx = workspace.File("source.docx");
                    progress?.Report(new ConversionExecutionProgress(10, "office", LocalizationService.T("ConversionUiNormalizingDocToDocx")));
                    await _externalConverter.ConvertWithLibreOfficeAsync(libreOffice, input, docx, token);
                    if (target is "txt" or "md" or "epub")
                    {
                        await ConvertDocxBuiltInAsync(docx, target, outputPath, tools, workspace,
                            repairDocx: false, progress, token);
                    }
                    else
                    {
                        var epub = workspace.File("book.epub");
                        await ConvertDocxBuiltInAsync(docx, "epub", epub, tools, workspace,
                            repairDocx: false, progress, token);
                        await _externalConverter.ConvertWithCalibreAsync(RequireTool(tools, ExternalToolKind.Calibre), epub, outputPath,
                            cancellationToken: token, diagnosticIntermediate: true);
                    }
                    return;
                }
                catch when (_windowsFilterTextExtractor.IsAvailable(input) && !token.IsCancellationRequested)
                {
                    progress?.Report(new ConversionExecutionProgress(30, "windows-ifilter", LocalizationService.T("ConversionUiLibreOfficeFailedUsingIFilter")));
                }
            }

            progress?.Report(new ConversionExecutionProgress(20, "windows-ifilter", LocalizationService.T("ConversionUiExtractingWithIFilter")));
            var extraction = await _windowsFilterTextExtractor.ExtractAsync(input, token);
            if (!extraction.IsAvailable || string.IsNullOrWhiteSpace(extraction.Text))
            {
                throw new DocumentEngineUnavailableException(extraction.Error ?? LocalizationService.T("ConversionUiDocNoIFilter"));
            }
            var document = WindowsFilterTextExtractor.CreateDocument(input, extraction.Text, language ?? "zh-CN");
            await WriteNormalizedAsync(document, target, outputPath, tools, workspace, progress, fixedLayout: false, token);
            return;
        }

        if (source == "epub" && target is "mobi" or "azw3")
        {
            progress?.Report(new ConversionExecutionProgress(20, "calibre", LocalizationService.T("ConversionUiGeneratingFormat", target.ToUpperInvariant())));
            await _externalConverter.ConvertWithCalibreAsync(RequireTool(tools, ExternalToolKind.Calibre), input, outputPath,
                cancellationToken: token, diagnosticIntermediate: false);
            return;
        }

        if (source == "docx" && target is "mobi" or "azw3")
        {
            var epub = workspace.File("book.epub");
            await ConvertDocxBuiltInAsync(input, "epub", epub, tools, workspace,
                DocumentConversionOptions.RepairDocx(request), progress, token);
            progress?.Report(new ConversionExecutionProgress(75, "calibre", LocalizationService.T("ConversionUiGeneratingFormat", target.ToUpperInvariant())));
            await _externalConverter.ConvertWithCalibreAsync(RequireTool(tools, ExternalToolKind.Calibre), epub, outputPath,
                cancellationToken: token, diagnosticIntermediate: true);
            return;
        }

        if (source is "caj" or "hn" or "c8" or "kdh")
        {
            var pdfPath = target == "pdf" ? outputPath : workspace.File("source.pdf");
            progress?.Report(new ConversionExecutionProgress(15, "caj2pdf", LocalizationService.T("ConversionUiConvertingCajToPdf")));
            await _externalConverter.ConvertWithCaj2PdfAsync(RequireTool(tools, ExternalToolKind.Caj2Pdf), input, pdfPath, token);
            if (target == "pdf") return;

            await ConvertPdfBuiltInAsync(pdfPath, target, outputPath, language, password: null, fixedLayout, tools, workspace, progress, token);
            return;
        }

        throw new NotSupportedException(LocalizationService.T("ConversionUiRouteNotImplemented", source, target));
    }

    private async Task ConvertPdfBuiltInAsync(
        string inputPath,
        string target,
        string outputPath,
        string? language,
        string? password,
        bool fixedLayout,
        IReadOnlyList<ExternalToolInfo> tools,
        PipelineWorkspace workspace,
        IProgress<ConversionExecutionProgress>? progress,
        CancellationToken token)
    {
        if (fixedLayout && target is "epub" or "mobi" or "azw3")
        {
            progress?.Report(new ConversionExecutionProgress(20, "pdf-render", LocalizationService.T("ConversionUiRenderingPdfPages")));
            var pages = _renderPdfPages(inputPath, recognizeText: true,
                language, password, workspace.Path, cancellationToken: token);
            var document = await _imageDocumentBuilder.BuildAsync(
                pages,
                Path.GetFileNameWithoutExtension(inputPath),
                language ?? "zh-CN",
                fixedLayout: true,
                cancellationToken: token);
            await WriteNormalizedAsync(document, target, outputPath, tools, workspace, progress, fixedLayout: true, token);
            return;
        }

        progress?.Report(new ConversionExecutionProgress(20, "pdf-extract", LocalizationService.T("ConversionUiExtractingPdfText")));
        var extraction = await _pdfExtractor.ExtractAsync(inputPath, language, password, token);
        if (target == "pdf")
        {
            progress?.Report(new ConversionExecutionProgress(70, "pdf-text-layer", LocalizationService.T("ConversionUiWritingOcrTextLayer")));
            await _searchablePdfWriter.WriteLayoutsAsync(inputPath, extraction.OcrLayouts, outputPath, password, token);
            return;
        }

        await WriteNormalizedAsync(extraction.Document, target, outputPath, tools, workspace, progress, fixedLayout: false, token);
    }

    private async Task WriteNormalizedAsync(
        NormalizedDocument document,
        string target,
        string outputPath,
        IReadOnlyList<ExternalToolInfo> tools,
        PipelineWorkspace workspace,
        IProgress<ConversionExecutionProgress>? progress,
        bool fixedLayout,
        CancellationToken token)
    {
        if (target == "docx")
        {
            progress?.Report(new ConversionExecutionProgress(70, "docx-write", LocalizationService.T("PdfDocxWriting")));
            await _pdfDocxWriter.WriteAsync(document, outputPath, token);
            return;
        }

        if (target == "txt")
        {
            progress?.Report(new ConversionExecutionProgress(70, "text", LocalizationService.T("ConversionUiWritingUtf8Text")));
            await _textWriter.WriteAsync(document, outputPath, token);
            return;
        }

        if (target == "md")
        {
            progress?.Report(new ConversionExecutionProgress(70, "markdown", LocalizationService.T("ConversionUiWritingMarkdown")));
            await _markdownWriter.WriteAsync(document, outputPath, token);
            return;
        }

        var epubPath = target == "epub" ? outputPath : workspace.File("book.epub");
        progress?.Report(new ConversionExecutionProgress(65, "epub", LocalizationService.T("ConversionUiGeneratingEpub")));
        await _epubWriter.WriteAsync(_epubAdapter.CreatePublication(document, fixedLayout: fixedLayout), epubPath, token);
        if (target is "mobi" or "azw3")
        {
            progress?.Report(new ConversionExecutionProgress(82, "calibre", LocalizationService.T("ConversionUiGeneratingFormat", target.ToUpperInvariant())));
            await _externalConverter.ConvertWithCalibreAsync(
                RequireTool(tools, ExternalToolKind.Calibre),
                epubPath,
                outputPath,
                cancellationToken: token, diagnosticIntermediate: true);
        }
    }

    private async Task<bool> ValidateOutputAsync(string format, string outputPath, CancellationToken token)
    {
        if (format == "docx")
        {
            await _pdfDocxWriter.ValidateAsync(outputPath, token);
            return true;
        }
        if (format == "pdf")
        {
            var result = await _pdfValidator.ValidateAsync(new ConversionArtifact("output", "pdf", outputPath), token);
            if (!result.IsValid) throw FormatFailureDiagnostics.Transfer(result,
                new InvalidDataException(result.Message ?? LocalizationService.T("ConversionUiGeneratedPdfInvalid")));
            return true;
        }
        if (format == "epub")
        {
            var result = await _epubValidator.ValidateAsync(outputPath, token);
            if (!result.IsValid) throw FormatFailureDiagnostics.Attach(
                new InvalidDataException(LocalizationService.T("ConversionUiGeneratedEpubInvalid", string.Join("; ", result.Errors))),
                new(FormatFailureStage.DocumentOutput, FormatFailureReason.OutputValidation) { Target = "epub", Count = result.Errors.Count });
            return true;
        }
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidDataException(LocalizationService.T("ConversionUiEngineNoOutput"));
        // Every MOBI/AZW3 execution route uses ExternalDocumentConverter, which
        // validates the Calibre output before returning. Text has no such check.
        return format is "mobi" or "azw3";
    }

    private static string? Option(ConversionRequest request, string name) =>
        request.Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string Normalize(string value) => value.Trim().TrimStart('.').ToLowerInvariant();

    private async Task ConvertDocxBuiltInAsync(
        string inputPath,
        string target,
        string outputPath,
        IReadOnlyList<ExternalToolInfo> tools,
        PipelineWorkspace workspace,
        bool repairDocx,
        IProgress<ConversionExecutionProgress>? progress,
        CancellationToken token)
    {
        progress?.Report(new ConversionExecutionProgress(20, "docx", LocalizationService.T("ConversionUiReadingDocxStructure")));
        var document = await _docxImporter.ReadAsync(inputPath, repairDocx, tools,
            workspace.Path, progress, token);
        await WriteNormalizedAsync(document, target, outputPath, tools, workspace, progress, fixedLayout: false, token);
    }

    private async Task ConvertOfficeToPdfAsync(
        string inputPath,
        string outputPath,
        IReadOnlyList<ExternalToolInfo> tools,
        IProgress<ConversionExecutionProgress>? progress,
        CancellationToken token)
    {
        var word = tools.FirstOrDefault(tool => tool.Kind == ExternalToolKind.MicrosoftWord && tool.IsAvailable);
        var libreOffice = tools.FirstOrDefault(tool => tool.Kind == ExternalToolKind.LibreOffice && tool.IsAvailable);
        if (word is not null)
        {
            progress?.Report(new ConversionExecutionProgress(20, "word", LocalizationService.T("ConversionUiExportingPdfWithWord")));
            try
            {
                await _externalConverter.ConvertWithWordAsync(word, inputPath, outputPath, token);
                return;
            }
            catch when (libreOffice is not null && !token.IsCancellationRequested)
            {
                progress?.Report(new ConversionExecutionProgress(35, "libreoffice", LocalizationService.T("ConversionUiWordFailedUsingLibreOffice")));
            }
        }
        if (libreOffice is not null)
        {
            await _externalConverter.ConvertWithLibreOfficeAsync(libreOffice, inputPath, outputPath, token);
            return;
        }
        throw new DocumentEngineUnavailableException(LocalizationService.T("ConversionUiDocToPdfEngineRequired"));
    }

    private static ExternalToolInfo RequireTool(IReadOnlyList<ExternalToolInfo> tools, ExternalToolKind kind) =>
        tools.FirstOrDefault(tool => tool.Kind == kind && tool.IsAvailable)
        ?? throw new DocumentEngineUnavailableException(kind switch
        {
            ExternalToolKind.LibreOffice => LocalizationService.T("ConversionUiRequiresLibreOffice"),
            ExternalToolKind.Calibre => LocalizationService.T("ConversionUiRequiresCalibre"),
            ExternalToolKind.Caj2Pdf => LocalizationService.T("ConversionUiRequiresCaj2Pdf"),
            _ => LocalizationService.T("ConversionUiMissingConversionEngine")
        });

    private sealed class PipelineWorkspace : IDisposable
    {
        private readonly string _root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FormatFactoryWinUI",
            "document-pipeline"));
        private int _disposed;

        public PipelineWorkspace()
        {
            Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || !Directory.Exists(Path)) return;
            var fullPath = System.IO.Path.GetFullPath(Path);
            var prefix = _root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            try { Directory.Delete(fullPath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

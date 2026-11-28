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
            var plan = await _planner.PlanAsync(request, tools, cancellationToken);
            var targetFormat = plan.Steps[^1].TargetFormatId;
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
            await ExecuteAsync(plan, tools, outputPath, workspace, progress, cancellationToken);
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

    private async Task ExecuteAsync(ConversionPlan plan, IReadOnlyList<ExternalToolInfo> tools, string outputPath,
        PipelineWorkspace workspace, IProgress<ConversionExecutionProgress>? progress, CancellationToken token)
    {
        var request = plan.Request;
        var steps = plan.Steps;
        var inputs = request.Inputs.Select(item => Path.GetFullPath(item.Path)).ToList();
        var source = inputs[0];
        var target = steps[^1].TargetFormatId;
        var first = steps[0];
        var language = Option(request, "ocrLanguage") ?? Option(request, "language");
        var password = first.SourceFormatId == "pdf" ? Option(request, "password") : null;
        var fixedLayout = DocumentConversionOptions.IsFixedLayout(request);
        if (first.EngineId is ExternalEngineIds.Word or ExternalEngineIds.LibreOffice &&
            steps.Count > 1 && steps[1].EngineId == DocumentEngineIds.PdfRenderer)
        {
            Report(10, "office-layout", "DocumentCompatibilityFixedEpub");
            await _officeFixedLayoutEpub.ConvertAsync(source, outputPath, workspace.Path, language, Option(request, "title"),
                (input, pdf, cancellation) => ConvertOfficeToPdfAsync(first.EngineId, input, pdf, tools, progress, cancellation),
                (pdf, cancellation) => _renderPdfPages(pdf, recognizeText: false, language, password: null,
                    workspace.Path, cancellationToken: cancellation), progress, token);
            return;
        }

        var currentPath = source;
        NormalizedDocument? document = null;
        IAsyncEnumerable<DecodedImagePage>? pages = null;
        var legacyImport = first.EngineId == ExternalEngineIds.LibreOffice && first.TargetFormatId == "docx";
        try { await RunStepsAsync(0); }
        catch when (legacyImport && !token.IsCancellationRequested && _windowsFilterTextExtractor.IsAvailable(source))
        {
            Report(30, "windows-ifilter", "ConversionUiLibreOfficeFailedUsingIFilter");
            document = await ReadFilterAsync();
            // Replace the failed LibreOffice -> DOCX reader prefix with the native
            // fallback. The remaining writer/Calibre steps are exactly the same plan.
            await RunStepsAsync(2);
        }

        async Task RunStepsAsync(int start)
        {
            for (var i = start; i < steps.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var step = steps[i];
                var last = i == steps.Count - 1;
                switch (step.EngineId)
                {
                    case ExternalEngineIds.Word:
                    case ExternalEngineIds.LibreOffice when step.TargetFormatId == "pdf":
                        await ConvertOfficeToPdfAsync(step.EngineId, currentPath, outputPath, tools, progress, token);
                        break;
                    case ExternalEngineIds.LibreOffice:
                        Report(10, "office", "ConversionUiNormalizingDocToDocx");
                        currentPath = workspace.File("source.docx");
                        await _externalConverter.ConvertWithLibreOfficeAsync(RequireTool(tools, ExternalToolKind.LibreOffice),
                            source, currentPath, token);
                        break;
                    case ExternalEngineIds.Caj2Pdf:
                        Report(15, "caj2pdf", "ConversionUiConvertingCajToPdf");
                        currentPath = last ? outputPath : workspace.File("source.pdf");
                        await _externalConverter.ConvertWithCaj2PdfAsync(RequireTool(tools, ExternalToolKind.Caj2Pdf),
                            source, currentPath, token);
                        break;
                    case ExternalEngineIds.Calibre:
                        progress?.Report(new(i == 0 ? 20 : 82, "calibre",
                            LocalizationService.T("ConversionUiGeneratingFormat", target.ToUpperInvariant())));
                        await _externalConverter.ConvertWithCalibreAsync(RequireTool(tools, ExternalToolKind.Calibre),
                            currentPath, outputPath, cancellationToken: token, diagnosticIntermediate: i != 0);
                        break;
                    case DocumentEngineIds.WindowsFilter:
                        document = await ReadFilterAsync();
                        break;
                    case DocumentEngineIds.DocxReader:
                        Report(20, "docx", "ConversionUiReadingDocxStructure");
                        document = await _docxImporter.ReadAsync(currentPath,
                            !legacyImport && DocumentConversionOptions.RepairDocx(request), tools, workspace.Path, progress, token);
                        break;
                    case DocumentEngineIds.PdfExtractor:
                    case DocumentEngineIds.PdfOcr:
                        Report(20, "pdf-extract", "ConversionUiExtractingPdfText");
                        var extracted = await _pdfExtractor.ExtractAsync(currentPath, language, password, token);
                        if (step.EngineId == DocumentEngineIds.PdfOcr)
                        {
                            Report(70, "pdf-text-layer", "ConversionUiWritingOcrTextLayer");
                            await _searchablePdfWriter.WriteLayoutsAsync(currentPath, extracted.OcrLayouts, outputPath, password, token);
                        }
                        else document = extracted.Document;
                        break;
                    case DocumentEngineIds.PdfRenderer:
                        Report(20, "pdf-render", "ConversionUiRenderingPdfPages");
                        pages = _renderPdfPages(currentPath, recognizeText: true, language, password, workspace.Path, cancellationToken: token);
                        document = await _imageDocumentBuilder.BuildAsync(pages, Path.GetFileNameWithoutExtension(currentPath),
                            language ?? "zh-CN", fixedLayout: true, cancellationToken: token);
                        break;
                    case DocumentEngineIds.ImageDecoder:
                    case DocumentEngineIds.ImageOcr:
                        Report(15, "image-decode", "ConversionUiDecodingImagePages");
                        var ocr = step.EngineId == DocumentEngineIds.ImageOcr;
                        pages = _decodeImagePages(inputs, ocr, language, workspace.Path, includeImageContent: target != "txt", cancellationToken: token);
                        if (ocr) document = await _imageDocumentBuilder.BuildAsync(pages,
                            Option(request, "title") ?? Path.GetFileNameWithoutExtension(source), language ?? "zh-CN", fixedLayout, token);
                        break;
                    case DocumentEngineIds.ImagePdf:
                        Report(65, "image-pdf", "ConversionUiMergingImagePages");
                        await _imagePdfWriter.WriteAsync(pages ?? throw new InvalidDataException("Missing image pages."), outputPath, token);
                        break;
                    case DocumentEngineIds.DocxWriter:
                        Report(70, "docx-write", "PdfDocxWriting");
                        await _pdfDocxWriter.WriteAsync(RequiredDocument(), outputPath, token);
                        break;
                    case DocumentEngineIds.TextWriter:
                        Report(70, "text", "ConversionUiWritingUtf8Text");
                        await _textWriter.WriteAsync(RequiredDocument(), outputPath, token);
                        break;
                    case DocumentEngineIds.MarkdownWriter:
                        Report(70, "markdown", "ConversionUiWritingMarkdown");
                        await _markdownWriter.WriteAsync(RequiredDocument(), outputPath, token);
                        break;
                    case DocumentEngineIds.EpubWriter:
                        Report(65, "epub", "ConversionUiGeneratingEpub");
                        currentPath = last ? outputPath : workspace.File("book.epub");
                        // Legacy DOC and native DOCX structure import produce flowing content.
                        var useFixedLayout = fixedLayout && first.SourceFormatId is not ("doc" or "docx");
                        await _epubWriter.WriteAsync(_epubAdapter.CreatePublication(RequiredDocument(), fixedLayout: useFixedLayout),
                            currentPath, token);
                        break;
                    default:
                        throw new InvalidDataException($"Unrecognized document conversion step: {step.EngineId}");
                }
            }
        }

        NormalizedDocument RequiredDocument() => document ?? throw new InvalidDataException("Missing normalized document.");

        async Task<NormalizedDocument> ReadFilterAsync()
        {
            Report(20, "windows-ifilter", "ConversionUiExtractingWithIFilter");
            var extraction = await _windowsFilterTextExtractor.ExtractAsync(source, token);
            if (!extraction.IsAvailable || string.IsNullOrWhiteSpace(extraction.Text))
                throw new DocumentEngineUnavailableException(extraction.Error ?? LocalizationService.T("ConversionUiDocNoIFilter"));
            return WindowsFilterTextExtractor.CreateDocument(source, extraction.Text, language ?? "zh-CN");
        }

        void Report(double fraction, string stage, string key) =>
            progress?.Report(new ConversionExecutionProgress(fraction, stage, LocalizationService.T(key)));
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

    private async Task ConvertOfficeToPdfAsync(
        string engineId,
        string inputPath,
        string outputPath,
        IReadOnlyList<ExternalToolInfo> tools,
        IProgress<ConversionExecutionProgress>? progress,
        CancellationToken token)
    {
        var word = tools.FirstOrDefault(tool => tool.Kind == ExternalToolKind.MicrosoftWord && tool.IsAvailable);
        var libreOffice = tools.FirstOrDefault(tool => tool.Kind == ExternalToolKind.LibreOffice && tool.IsAvailable);
        if (engineId == ExternalEngineIds.Word && word is not null)
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

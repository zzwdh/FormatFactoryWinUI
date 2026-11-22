using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PdfSharp.Pdf.IO;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckPdfDocxAsync()
    {
        var fixture = Path.Combine(_runRoot, "pdf-docx", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var textPdf = Path.Combine(fixture, "editable-text.pdf");
        WriteDimensionPdf(textPdf, (595, 842, "FIRST EDITABLE PAGE"), (595, 842, "SECOND EDITABLE PAGE"));
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(textPdf));
        var converter = new DocumentConversionService();

        await CheckNativeAsync("PDF to DOCX exports editable text, page breaks and a valid Word package", async () =>
        {
            var result = await ConvertPdfDocxFixtureAsync(converter, textPdf, Path.Combine(fixture, "text-output"));
            var output = RequirePdfDocxArtifact(result);
            using var package = WordprocessingDocument.Open(output, false);
            Require(!new OpenXmlValidator().Validate(package).Any(), "The exported DOCX failed Open XML validation.");
            var body = package.MainDocumentPart!.Document!.Body!;
            Require(body.InnerText.Contains("FIRST EDITABLE PAGE") && body.InnerText.Contains("SECOND EDITABLE PAGE"),
                "The exported DOCX lost extracted page text.");
            Require(body.Descendants<Text>().Any(text => text.Text.Contains("FIRST EDITABLE PAGE")),
                "PDF text was not exported as editable Word text.");
            Require(body.Descendants<Break>().Count(lineBreak => lineBreak.Type?.Value == BreakValues.Page) == 1,
                "The two-page PDF did not preserve its page boundary.");
            var reopened = await new DocxDocumentReader().ReadAsync(output);
            Require(reopened.Blocks.OfType<NormalizedParagraph>().Any(paragraph => paragraph.Text.Contains("SECOND EDITABLE PAGE")),
                "The exported DOCX cannot be read by the production document reader.");
            var sourceHashAfter = SHA256.HashData(await File.ReadAllBytesAsync(textPdf));
            Require(sourceHash.SequenceEqual(sourceHashAfter),
                "PDF to DOCX changed the original text PDF.");
        });

        var scanPdf = Path.Combine(fixture, "scanned-page.pdf");
        var scannedSource = Path.Combine(fixture, "scan-render-source.pdf");
        WriteDimensionPdf(scannedSource, (595, 842, "SCANNED DOCUMENT PAGE"));
        await CheckNativeAsync("Scanned PDF to DOCX runs the real Windows OCR and keeps editable recognized text", async () =>
        {
            var pages = await new WindowsPdfPageContentService().RenderAsync(scannedSource, false, null, null);
            await new ImagePdfWriter().WriteAsync(pages, scanPdf);
            using (var scan = UglyToad.PdfPig.PdfDocument.Open(scanPdf))
                Require(scan.GetPages().All(page => string.IsNullOrWhiteSpace(page.Text)), "The scan fixture still contains a PDF text layer.");
            var scanHash = SHA256.HashData(await File.ReadAllBytesAsync(scanPdf));
            var result = await ConvertPdfDocxFixtureAsync(converter, scanPdf, Path.Combine(fixture, "scan-output"));
            using var package = WordprocessingDocument.Open(RequirePdfDocxArtifact(result), false);
            Require(package.MainDocumentPart!.Document!.Body!.Descendants<Text>()
                .Any(text => text.Text.Contains("SCANNED DOCUMENT PAGE", StringComparison.OrdinalIgnoreCase)),
                "Real Windows OCR did not produce editable Word text from the scan.");
            var scanHashAfter = SHA256.HashData(await File.ReadAllBytesAsync(scanPdf));
            Require(scanHash.SequenceEqual(scanHashAfter),
                "OCR conversion changed the scanned PDF.");
        });

        await CheckNativeAsync("Encrypted scanned PDF to DOCX passes its password through extraction and Windows OCR", async () =>
        {
            Require(File.Exists(scanPdf), "The scanned PDF fixture was not produced.");
            var encryptedPdf = Path.Combine(fixture, "encrypted-scan.pdf");
            using (var encrypted = PdfReader.Open(scanPdf, PdfDocumentOpenMode.Modify))
            {
                encrypted.SecuritySettings.UserPassword = "pdf-docx-secret";
                encrypted.SecuritySettings.OwnerPassword = "pdf-docx-owner";
                encrypted.SecuritySettings.PermitExtractContent = true;
                encrypted.Save(encryptedPdf);
            }
            var encryptedHash = SHA256.HashData(await File.ReadAllBytesAsync(encryptedPdf));
            var rejectedDirectory = Path.Combine(fixture, "wrong-password-output");
            var rejected = await ConvertPdfDocxFixtureAsync(converter, encryptedPdf, rejectedDirectory, "wrong-password");
            Require(!rejected.Success && !rejected.Cancelled && rejected.Artifacts.Count == 0,
                "A wrong PDF password unexpectedly produced a successful DOCX.");
            Require(NoPdfDocxOutput(rejectedDirectory), "A wrong PDF password left a final DOCX behind.");
            var result = await ConvertPdfDocxFixtureAsync(converter, encryptedPdf,
                Path.Combine(fixture, "encrypted-output"), "pdf-docx-secret");
            using var package = WordprocessingDocument.Open(RequirePdfDocxArtifact(result), false);
            Require(package.MainDocumentPart!.Document!.Body!.InnerText.Contains("SCANNED DOCUMENT PAGE", StringComparison.OrdinalIgnoreCase),
                "The password-protected scan lost its recognized editable text.");
            var encryptedHashAfter = SHA256.HashData(await File.ReadAllBytesAsync(encryptedPdf));
            Require(encryptedHash.SequenceEqual(encryptedHashAfter),
                "Password-protected conversion changed its source PDF.");
        });

        await CheckNativeAsync("PDF to DOCX failure and cancellation publish no final document", async () =>
        {
            var broken = Path.Combine(fixture, "broken.pdf");
            await File.WriteAllTextAsync(broken, "This file is not a PDF.");
            var brokenOutput = Path.Combine(fixture, "broken-output");
            var failed = await ConvertPdfDocxFixtureAsync(converter, broken, brokenOutput);
            Require(!failed.Success && !failed.Cancelled && failed.Artifacts.Count == 0 && NoPdfDocxOutput(brokenOutput),
                "A malformed PDF published a DOCX output.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledOutput = Path.Combine(fixture, "cancelled-output");
            var stopped = await ConvertPdfDocxFixtureAsync(converter, textPdf, cancelledOutput, cancellation: cancelled.Token);
            Require(!stopped.Success && stopped.Cancelled && stopped.Artifacts.Count == 0 && NoPdfDocxOutput(cancelledOutput),
                "A cancelled PDF conversion published a DOCX output.");
            using var duringExtraction = new CancellationTokenSource();
            var interruptedOutput = Path.Combine(fixture, "interrupted-output");
            var request = PdfDocxFixtureRequest(textPdf, interruptedOutput);
            var progress = new PdfDocxCancellationProgress(duringExtraction);
            var interrupted = await Task.Run(() => converter.ConvertAsync(request, progress, duringExtraction.Token));
            Require(progress.CancelledExtraction && !interrupted.Success && interrupted.Cancelled &&
                interrupted.Artifacts.Count == 0 && NoPdfDocxOutput(interruptedOutput),
                "Cancellation after pipeline planning published a DOCX output.");
        });

        await CheckAsync("Restored PDF queue binds DOCX selection, localized notices and its built-in availability", async () =>
        {
            var originalWindow = _window;
            var originalLanguage = LocalizationService.CurrentLanguageCode;
            var durable = new QueueStore(Path.Combine(fixture, "queue.json"), () => []);
            await durable.SaveAsync(new[] { new MediaTask
            {
                InputPath = textPdf, SourceFormatId = "pdf", ContentKind = ContentKind.Document,
                TargetFormatId = "docx", State = ConversionState.Waiting
            } });
            var services = new MainWindowServices(new SettingsService(Path.Combine(fixture, "settings.json")),
                durable, new LocalImportStorageAccess(),
                feedbackDeliveryFactory: () => new FeedbackDeliveryService(new SmokeFeedbackHandler(this)));
            var window = new MainWindow(services);
            DetachSmokeWindowStartup(window);
            _window = window;
            var session = (QueueSession)Field("_queueSession")!;
            try
            {
                SetField("_ffmpegPath", string.Empty);
                SetField("_documentTools", new Dictionary<ExternalToolKind, ExternalToolInfo>());
                await session.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var task = window.Tasks.Single();
                Require(task.Model.TargetFormatId == "docx" && task.AvailableFormats.Contains("DOCX"),
                    "Restoring a PDF task lost its requested DOCX target.");
                Require(task.IsTargetAvailable && string.IsNullOrWhiteSpace(task.EngineRequirement) &&
                    Invoke("EnsureTaskEngineReady", task) is true,
                    "Built-in PDF to DOCX incorrectly requires an external converter or FFmpeg.");
                var row = (FrameworkElement)((ListView)Field("TaskList")!).ItemTemplate.LoadContent();
                row.DataContext = task;
                var notice = EpubRouteDescendants(row).OfType<TextBlock>().Single(text => text.Name == "PdfDocxNoticeText");
                var target = EpubRouteDescendants(row).OfType<ComboBox>()
                    .Single(combo => ReferenceEquals(combo.ItemsSource, task.AvailableFormats));
                var start = EpubRouteDescendants(row).OfType<Button>()
                    .Single(button => button.Content is FontIcon { Glyph: "\uE768" });
                foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
                {
                    LocalizationService.SetLanguage(language);
                    task.RefreshLocalization();
                    Require(task.IsPdfDocxNoticeVisible && notice.Visibility == Visibility.Visible &&
                        !string.IsNullOrWhiteSpace(notice.Text) && notice.Text == task.PdfDocxNotice && start.IsEnabled,
                        "The actual DOCX row lost its localized conversion notice or start availability.");
                    target.SelectedItem = "TXT";
                    Require(task.Model.TargetFormatId == "txt" && !task.IsPdfDocxNoticeVisible && notice.Visibility == Visibility.Collapsed,
                        "Switching away from DOCX did not hide the PDF to DOCX notice.");
                    target.SelectedItem = "DOCX";
                    Require(task.Model.TargetFormatId == "docx" && task.IsTargetAvailable &&
                        task.IsPdfDocxNoticeVisible && notice.Visibility == Visibility.Visible && start.IsEnabled,
                        "The queue's two-way format binding did not restore PDF to DOCX.");
                }
                Require(task.IsPdfPasswordVisible, "The PDF password editor disappeared for DOCX output.");
                Require((await durable.LoadResultAsync()).Items.Single().TargetFormatId == "docx",
                    "Restoring or editing the queue silently changed its durable DOCX target.");
            }
            finally
            {
                await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                window.Close();
                _window = originalWindow;
                LocalizationService.SetLanguage(originalLanguage);
            }
        });
    }

    private static ConversionRequest PdfDocxFixtureRequest(string input, string outputDirectory, string? password = null) => new()
    {
        Inputs = new ConversionInput[] { new(input, "pdf") }, TargetFormatId = "docx", OutputDirectory = outputDirectory,
        Options = password is null ? new Dictionary<string, string> { ["ocrLanguage"] = "en-US" }
            : new Dictionary<string, string> { ["ocrLanguage"] = "en-US", ["password"] = password }
    };

    private static Task<ConversionExecutionResult> ConvertPdfDocxFixtureAsync(DocumentConversionService converter,
        string input, string outputDirectory, string? password = null, CancellationToken cancellation = default) =>
        Task.Run(() => converter.ConvertAsync(PdfDocxFixtureRequest(input, outputDirectory, password), null, cancellation));

    private static string RequirePdfDocxArtifact(ConversionExecutionResult result)
    {
        Require(result.Success && !result.Cancelled && result.Artifacts.Count == 1,
            "PDF to DOCX failed: " + result.Message);
        var artifact = result.Artifacts.Single();
        Require(artifact.FormatId == "docx" && Path.GetExtension(artifact.Path).Equals(".docx", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(artifact.Path), "The converter did not return an existing DOCX artifact.");
        return artifact.Path;
    }

    private static bool NoPdfDocxOutput(string directory) => !Directory.Exists(directory) ||
        !Directory.EnumerateFiles(directory, "*.docx", SearchOption.AllDirectories).Any();

    private sealed class PdfDocxCancellationProgress(CancellationTokenSource cancellation) : IProgress<ConversionExecutionProgress>
    {
        public bool CancelledExtraction { get; private set; }
        public void Report(ConversionExecutionProgress value)
        {
            if (value.Stage != "pdf-extract") return;
            CancelledExtraction = true;
            cancellation.Cancel();
        }
    }
}

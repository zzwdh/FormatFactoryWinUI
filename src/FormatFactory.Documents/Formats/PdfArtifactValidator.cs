using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using UglyToad.PdfPig;

namespace FormatFactoryWinUI.Documents;

public sealed class PdfArtifactValidator : IArtifactValidator
{
    public string ValidatorId => "pdf-validator";
    public bool CanValidate(string formatId) => formatId.Trim().TrimStart('.').Equals("pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ArtifactValidationResult> ValidateAsync(ConversionArtifact artifact, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(artifact.Path)) return Task.FromResult(Failure(
                new(false, LocalizationService.T("ConversionUiPdfFileMissing")), FormatFailureReason.Missing));
            using var document = PdfDocument.Open(artifact.Path);
            return Task.FromResult(document.NumberOfPages > 0
                ? new ArtifactValidationResult(true)
                : Failure(new(false, LocalizationService.T("ConversionUiPdfHasNoPages")), FormatFailureReason.NoPages, document.NumberOfPages));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            FormatFailureDiagnostics.Record(ex, new(FormatFailureStage.PdfOutputValidate, FormatFailureReason.ParseFailure) { Target = "pdf" });
            FormatFailureDiagnostics.WriteApp(ex);
            return Task.FromResult(FormatFailureDiagnostics.Transfer(ex, new ArtifactValidationResult(false, ex.Message)));
        }
    }

    private static ArtifactValidationResult Failure(ArtifactValidationResult result, FormatFailureReason reason, int? pages = null)
    {
        FormatFailureDiagnostics.Record(result, new(FormatFailureStage.PdfOutputValidate, reason) { Target = "pdf", Pages = pages });
        FormatFailureDiagnostics.WriteApp(result);
        return result;
    }
}

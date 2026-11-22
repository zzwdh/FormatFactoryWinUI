using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class DocumentCompatibilityRouteTests
{
    private static readonly ExternalToolInfo Word =
        new(ExternalToolKind.MicrosoftWord, "Word", true, "winword.exe", "1", "available");
    private static readonly ExternalToolInfo LibreOffice =
        new(ExternalToolKind.LibreOffice, "LibreOffice", true, "soffice.com", "1", "available");

    [Theory]
    [InlineData("layoutMode")]
    [InlineData("layout")]
    [InlineData("mode")]
    public void FixedDocxEpub_ComposesOfficePdfRenderAndEpubWithoutCalibre(string selection)
    {
        var request = Request("epub") with
        {
            Mode = selection == "mode" ? "fixed" : "balanced",
            Options = selection == "mode" ? new Dictionary<string, string>() : new Dictionary<string, string> { [selection] = "fixed" }
        };

        var plan = new DocumentConversionRoutePlanner().Plan(request, [Word, LibreOffice]);

        Assert.Equal([ExternalEngineIds.Word, DocumentEngineIds.PdfRenderer, DocumentEngineIds.EpubWriter],
            plan.Steps.Select(step => step.EngineId));
        Assert.Equal("pdf", plan.Steps[0].TargetFormatId);
        Assert.Equal("pdf", plan.Steps[1].SourceFormatId);
        Assert.Equal("normalized-document", plan.Steps[1].TargetFormatId);
        Assert.Equal("epub", plan.Steps[^1].TargetFormatId);
    }

    [Fact]
    public void FixedDocxEpub_UsesLibreOfficeWhenWordUnavailable()
    {
        var plan = new DocumentConversionRoutePlanner().Plan(FixedRequest("epub"), [LibreOffice]);

        Assert.Equal(ExternalEngineIds.LibreOffice, plan.Steps[0].EngineId);
    }

    [Fact]
    public void FixedDocxEpub_WithoutOfficeToolRejectsBeforeExecution() =>
        Assert.Throws<DocumentEngineUnavailableException>(() =>
            new DocumentConversionRoutePlanner().Plan(FixedRequest("epub"), []));

    [Theory]
    [InlineData("mobi")]
    [InlineData("azw3")]
    public void FixedDocx_OtherEbookTargetsCannotSilentlyBecomeReflowable(string target) =>
        Assert.Throws<NotSupportedException>(() => new DocumentConversionRoutePlanner().Plan(FixedRequest(target), [Word]));

    [Fact]
    public void ReflowableDocxEpub_KeepsBuiltInRouteAndNeedsNoOfficeTool()
    {
        var plan = new DocumentConversionRoutePlanner().Plan(Request("epub"), []);

        Assert.Equal([DocumentEngineIds.DocxReader, DocumentEngineIds.EpubWriter], plan.Steps.Select(step => step.EngineId));
    }

    [Fact]
    public void RepairAndFixedOptions_CannotBypassLosslessPreservationPolicy()
    {
        var request = FixedRequest("epub") with { Mode = "lossless" };

        Assert.Throws<NotSupportedException>(() => new DocumentConversionRoutePlanner().Plan(request, [Word, LibreOffice]));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    public void DocxRepair_RequiresExplicitTrueValue(string value, bool expected) =>
        Assert.Equal(expected, DocumentConversionOptions.RepairDocx(Request("txt") with
        {
            Options = new Dictionary<string, string> { ["repairDocx"] = value }
        }));

    private static ConversionRequest Request(string target) => new()
    {
        Inputs = [new("source.docx", "docx")], TargetFormatId = target
    };

    private static ConversionRequest FixedRequest(string target) => Request(target) with
    {
        Options = new Dictionary<string, string> { ["layoutMode"] = "fixed", ["repairDocx"] = "true" }
    };
}

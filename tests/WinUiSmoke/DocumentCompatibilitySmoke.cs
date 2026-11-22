using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckDocumentCompatibilityAsync()
    {
        var repository = Path.GetFullPath(Path.Combine(_runRoot, "../../.."));
        var office = Path.Combine(repository, "work/prepared-engines/x64/Engines/libreoffice/program/soffice.com");
        if (!File.Exists(office))
        {
            _results.Add(new { name = "DOCX fixed-layout EPUB with real Office and Windows rendering", skipped = "Prepared LibreOffice is unavailable." });
            return;
        }
        await CheckAsync("DOCX fixed-layout EPUB with real Office and Windows rendering", async () =>
        {
            var directory = Path.Combine(_runRoot, "document-compatibility");
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "two-pages.docx");
            using (var package = WordprocessingDocument.Create(source, WordprocessingDocumentType.Document))
            {
                var main = package.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Text("First page · 第一页"))),
                    new Paragraph(new Run(new Break { Type = BreakValues.Page }, new Text("Second page · 第二页")))));
                main.Document.Save();
            }
            var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var request = new ConversionRequest
            {
                Inputs = new[] { new ConversionInput(source, "docx") }, TargetFormatId = "epub",
                OutputDirectory = Path.Combine(directory, "output"),
                Options = new Dictionary<string, string> { ["layoutMode"] = "fixed" }
            };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var result = await Task.Run(() => new DocumentConversionService(new ExternalToolConfiguration(LibreOfficePath: office))
                .ConvertAsync(request, null, cancellation.Token));
            Require(result.Success && result.Artifacts.Count == 1, "Fixed EPUB conversion failed: " + result.Message);
            var after = SHA256.HashData(await File.ReadAllBytesAsync(source));
            Require(before.SequenceEqual(after), "Office conversion changed the original DOCX.");
            var validation = await new EpubStructureValidator().ValidateAsync(result.Artifacts[0].Path);
            Require(validation.IsValid, "Fixed EPUB failed structural validation.");
            using var archive = ZipFile.OpenRead(result.Artifacts[0].Path);
            var viewports = new List<string>();
            var fixedLayout = false;
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".opf") || entry.FullName.EndsWith(".xhtml")))
            {
                using var stream = entry.Open();
                var xml = XDocument.Load(stream);
                foreach (var meta in xml.Descendants().Where(element => element.Name.LocalName == "meta"))
                {
                    if ((string?)meta.Attribute("property") == "rendition:layout" && meta.Value == "pre-paginated") fixedLayout = true;
                    if ((string?)meta.Attribute("name") == "viewport") viewports.Add((string?)meta.Attribute("content") ?? "");
                }
            }
            Require(fixedLayout && viewports.Count == 2 && viewports.All(value => value.Contains("width=") && value.Contains("height=")),
                "The two Office pages did not retain fixed EPUB viewports.");
            Require(archive.Entries.Count(entry => entry.FullName.EndsWith(".png")) >= 2, "Fixed EPUB is missing rendered page images.");
        });
        await CheckAsync("DOCX validation failure is repaired by real LibreOffice without changing the original", async () =>
        {
            var directory = Path.Combine(_runRoot, "docx-repair");
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "invalid-attribute.docx");
            using (var package = WordprocessingDocument.Create(source, WordprocessingDocumentType.Document))
            {
                var main = package.AddMainDocumentPart();
                var paragraph = new Paragraph(new Run(new Text("Text retained after repair · 修复后保留正文")));
                paragraph.SetAttribute(new OpenXmlAttribute("repair", "unexpected", "urn:formatfactory:invalid", "1"));
                main.Document = new Document(new Body(paragraph));
                main.Document.Save();
            }
            var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var configuration = new ExternalToolConfiguration(LibreOfficePath: office);
            var request = new ConversionRequest
            {
                Inputs = new[] { new ConversionInput(source, "docx") }, TargetFormatId = "epub",
                OutputDirectory = Path.Combine(directory, "output")
            };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var rejected = await Task.Run(() => new DocumentConversionService(configuration).ConvertAsync(request, null, cancellation.Token));
            Require(!rejected.Success && rejected.Artifacts.Count == 0, "Strict import accepted an undeclared DOCX attribute.");
            var repairedRequest = request with { Options = new Dictionary<string, string> { ["repairDocx"] = "true" } };
            var repaired = await Task.Run(() => new DocumentConversionService(configuration).ConvertAsync(repairedRequest, null, cancellation.Token));
            Require(repaired.Success && repaired.Artifacts.Count == 1, "Real LibreOffice DOCX repair failed: " + repaired.Message);
            var after = SHA256.HashData(await File.ReadAllBytesAsync(source));
            Require(before.SequenceEqual(after), "Repair changed the original DOCX.");
            var validation = await new EpubStructureValidator().ValidateAsync(repaired.Artifacts[0].Path);
            Require(validation.IsValid, "Repaired DOCX produced an invalid EPUB.");
            using var archive = ZipFile.OpenRead(repaired.Artifacts[0].Path);
            var retainedText = false;
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".xhtml")))
            {
                using var stream = entry.Open();
                retainedText |= XDocument.Load(stream).Root?.Value.Contains("Text retained after repair") == true;
            }
            Require(retainedText, "Repair lost the original document text.");
        });
    }
}

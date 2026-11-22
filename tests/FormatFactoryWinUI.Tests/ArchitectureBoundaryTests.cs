using System.Reflection;
using System.Xml.Linq;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void TestsExerciseTheSameAssembliesAsTheDesktopApplication()
    {
        Assert.Equal("FormatFactory.Core", typeof(MediaTask).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Core", typeof(ArchiveTask).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Core", typeof(IImportStorageAccess).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Engine", typeof(ConversionService).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Documents", typeof(DocxDocumentReader).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Documents", typeof(PdfContentExtractor).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Engine", typeof(MediaConversionPreparation).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Engine", typeof(MediaEncodingService).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Engine", typeof(MediaOutputValidator).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Engine", typeof(MediaOutputCommitter).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Engine", typeof(ArchiveSession).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Engine", typeof(QueueOperationsCoordinator).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Engine", typeof(ApplicationLifecycleCoordinator).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Engine", typeof(MediaImportCoordinator).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Presentation", typeof(MediaTaskViewModel).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Presentation", typeof(QueuePageViewModel).Assembly.GetName().Name);
        Assert.Equal("FormatFactory.Presentation", typeof(FormatFactoryWinUI.Views.ArchiveTaskItem).Assembly.GetName().Name);

        var testAssembly = typeof(ArchitectureBoundaryTests).Assembly;
        Assert.DoesNotContain(testAssembly.GetTypes(), type =>
            type.Namespace is { } typeNamespace &&
            (typeNamespace.StartsWith("FormatFactoryWinUI.", StringComparison.Ordinal) ||
             typeNamespace.StartsWith("FormatFactoryShellExtension", StringComparison.Ordinal)) &&
            !typeNamespace.StartsWith("FormatFactoryWinUI.Tests", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("FormatFactory.Core")]
    [InlineData("FormatFactory.Engine", "FormatFactory.Core")]
    [InlineData("FormatFactory.Documents", "FormatFactory.Core", "FormatFactory.Engine")]
    [InlineData("FormatFactory.Presentation", "FormatFactory.Core", "FormatFactory.Engine")]
    public void LibraryReferencesFollowTheDependencyDirection(string projectName, params string[] allowedReferences)
    {
        var projectPath = Path.Combine(RepositoryTestContext.Root, "src", projectName, projectName + ".csproj");
        var project = XDocument.Load(projectPath);
        var references = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!))
            .ToArray();
        Assert.Equal(allowedReferences.Order(), references.Order());

        var assembly = Assembly.Load(projectName);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "FormatFactoryWinUI" or "Microsoft.WinUI" or "WinRT.Runtime" ||
            reference.Name?.StartsWith("Microsoft.WindowsAppSDK", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DocumentFormatsAreIsolatedFromTheSharedEngineAndPresentation()
    {
        var documentLibraries = new[] { "DocumentFormat.OpenXml", "PdfSharp", "UglyToad.PdfPig", "FormatFactory.Documents" };
        foreach (var assembly in new[] { typeof(MediaTask).Assembly, typeof(QueueSession).Assembly, typeof(QueuePageViewModel).Assembly })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                documentLibraries.Any(prefix => reference.Name?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true));
        }
    }

    [Fact]
    public void ProductionAndTestProjectsDoNotRecompileEachOthersSourceFiles()
    {
        var root = RepositoryTestContext.Root;
        var projectPaths = new[] { "src", "tests" }
            .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(root, directory), "*.csproj", SearchOption.AllDirectories));
        foreach (var projectPath in projectPaths)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var project = XDocument.Load(projectPath);
            foreach (var compile in project.Descendants("Compile").Where(item => item.Attribute("Include") is not null))
            {
                var include = (string)compile.Attribute("Include")!;
                var sourcePath = Path.GetFullPath(include, projectDirectory);
                Assert.True(sourcePath.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetRelativePath(root, projectPath)} recompiles external source {include}; use a project reference instead.");
            }
        }
    }

    [Fact]
    public void ProductPersistenceUsesAnIsolatedTestDataDirectory()
    {
        Assert.Equal(TestEnvironment.AppDataRoot, LogService.AppDataRoot);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI.Tests"), LogService.AppDataRoot, StringComparison.OrdinalIgnoreCase);
    }
}

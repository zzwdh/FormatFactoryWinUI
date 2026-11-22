using System.Text;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ConversionEngines;

/// <summary>
/// Runs optional document converters through the application's bounded process
/// runner. All outputs are staged and committed only after a successful exit.
/// </summary>
public sealed class ExternalDocumentConverter
{
    private static readonly TimeSpan OfficeTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EbookTimeout = TimeSpan.FromMinutes(10);
    private readonly CalibreEbookArtifactValidator _calibreValidator;
    private readonly Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>> _runProcess;

    public ExternalDocumentConverter()
        : this(new CalibreEbookArtifactValidator())
    {
    }

    internal ExternalDocumentConverter(
        CalibreEbookArtifactValidator calibreValidator,
        Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessExecutionResult>>? runProcess = null)
    {
        _calibreValidator = calibreValidator ?? throw new ArgumentNullException(nameof(calibreValidator));
        _runProcess = runProcess ?? (static (executable, arguments, timeout, token) =>
            ProcessRunner.RunLoggedAsync(executable, arguments, timeout, token));
    }

    public async Task<ExternalConversionResult> ConvertWithLibreOfficeAsync(
        ExternalToolInfo tool,
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        EnsureTool(tool, ExternalToolKind.LibreOffice);
        var input = RequireInput(inputPath);
        var output = PrepareOutput(input, outputPath);
        var extension = Path.GetExtension(output).ToLowerInvariant();
        var conversionFilter = extension switch
        {
            ".pdf" => "pdf",
            ".docx" => "docx:Office Open XML Text",
            _ => throw new NotSupportedException(LocalizationService.T("ConversionUiLibreOfficeFormatUnsupported", extension))
        };

        using var staging = new StagingDirectory();
        var result = await RunCheckedAsync(
            tool.ExecutablePath!,
            [
                BuildLibreOfficeProfileArgument(staging.Path),
                "--headless",
                "--nologo",
                "--nodefault",
                "--nofirststartwizard",
                "--nolockcheck",
                "--convert-to",
                conversionFilter,
                "--outdir",
                staging.Path,
                input
            ],
            OfficeTimeout,
            cancellationToken);

        var generated = System.IO.Path.Combine(
            staging.Path,
            System.IO.Path.GetFileNameWithoutExtension(input) + extension);
        cancellationToken.ThrowIfCancellationRequested();
        await ValidatePdfIfRequiredAsync(generated, output, cancellationToken);
        CommitOutput(generated, output);
        return new ExternalConversionResult(tool.Kind, output, result.StandardOutput, result.StandardError);
    }

    public async Task<ExternalConversionResult> ConvertWithCalibreAsync(
        ExternalToolInfo tool,
        string inputPath,
        string outputPath,
        string? title = null,
        CancellationToken cancellationToken = default,
        bool? diagnosticIntermediate = null)
    {
        var diagnosticStage = FormatFailureStage.CalibreInput;
        try
        {
        EnsureTool(tool, ExternalToolKind.Calibre);
        var input = RequireInput(inputPath);
        var output = PrepareOutput(input, outputPath);
        var extension = Path.GetExtension(output).ToLowerInvariant();
        if (extension is not (".mobi" or ".azw3" or ".epub"))
        {
            throw new NotSupportedException(LocalizationService.T("ConversionUiCalibreFormatUnsupported", extension));
        }

        using var staging = new StagingDirectory();
        var stagedOutput = System.IO.Path.Combine(staging.Path, "book" + extension);
        var arguments = new List<string> { input, stagedOutput };
        if (!string.IsNullOrWhiteSpace(title))
        {
            arguments.Add("--title");
            arguments.Add(title.Trim());
        }

        diagnosticStage = FormatFailureStage.CalibreRun;
        var result = await RunCheckedAsync(
            tool.ExecutablePath!,
            arguments,
            EbookTimeout,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        diagnosticStage = FormatFailureStage.CalibreOutput;
        var validation = await _calibreValidator.ValidateAsync(tool, stagedOutput, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Message ?? LocalizationService.T("ConversionUiCalibreOutputUnreadable"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        CommitOutput(stagedOutput, output);
        return new ExternalConversionResult(tool.Kind, output, result.StandardOutput, result.StandardError);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            FormatFailureDiagnostics.Record(error, new(diagnosticStage, FormatFailureReason.Unknown)
            { Source = SafeInputFormat(inputPath), Target = SafeInputFormat(outputPath),
                InputOrigin = diagnosticIntermediate is true ? FormatInputOrigin.Intermediate :
                    diagnosticIntermediate is false ? FormatInputOrigin.Original : FormatInputOrigin.Unknown });
            FormatFailureDiagnostics.WriteApp(error);
            throw;
        }
    }

    public async Task<ExternalConversionResult> ConvertWithCaj2PdfAsync(
        ExternalToolInfo tool,
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var diagnosticStage = FormatFailureStage.CajInput;
        try
        {
        EnsureTool(tool, ExternalToolKind.Caj2Pdf);
        var input = RequireInput(inputPath);
        var sourceExtension = Path.GetExtension(input).ToLowerInvariant();
        if (sourceExtension is not (".caj" or ".hn" or ".c8" or ".kdh" or ".pdf"))
        {
            throw new NotSupportedException(LocalizationService.T("ConversionUiCajInputUnsupported", sourceExtension));
        }

        var output = PrepareOutput(input, outputPath);
        if (!Path.GetExtension(output).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(LocalizationService.T("ConversionUiCajPdfOnly"));
        }

        using var staging = new StagingDirectory();
        var stagedOutput = System.IO.Path.Combine(staging.Path, "document.pdf");
        diagnosticStage = FormatFailureStage.CajRun;
        var result = await RunCheckedAsync(
            tool.ExecutablePath!,
            [.. tool.ArgumentPrefix, "convert", input, "-o", stagedOutput],
            EbookTimeout,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        diagnosticStage = FormatFailureStage.CajOutput;
        await ValidatePdfIfRequiredAsync(stagedOutput, output, cancellationToken);
        CommitOutput(stagedOutput, output);
        return new ExternalConversionResult(tool.Kind, output, result.StandardOutput, result.StandardError);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            FormatFailureDiagnostics.Record(error, new(diagnosticStage, FormatFailureReason.Unknown)
            { Source = SafeInputFormat(inputPath), Target = "pdf", InputOrigin = FormatInputOrigin.Original,
                PythonLauncher = tool is not null &&
                    string.Equals(Path.GetFileName(tool.ExecutablePath), "python.exe", StringComparison.OrdinalIgnoreCase),
                Count = tool?.ArgumentPrefix.Count });
            FormatFailureDiagnostics.WriteApp(error);
            throw;
        }
    }

    private static string? SafeInputFormat(string path)
    {
        try
        {
            var extension = Path.GetExtension(path);
            // This is a format label, not an import route decision. Export-only
            // formats such as MOBI still have a known catalog identity.
            return FormatCatalog.FindByExtension(extension)?.Id ??
                FormatCatalog.All.FirstOrDefault(format => format.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))?.Id;
        }
        catch (ArgumentException) { return null; }
    }

    public async Task<ExternalConversionResult> ConvertWithWordAsync(
        ExternalToolInfo tool,
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        EnsureTool(tool, ExternalToolKind.MicrosoftWord, executableRequired: false);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(LocalizationService.T("ConversionUiWordFixedLayoutWindowsOnly"));
        }

        var input = RequireInput(inputPath);
        var output = PrepareOutput(input, outputPath);
        if (!Path.GetExtension(output).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(LocalizationService.T("ConversionUiWordFixedLayoutPdfOnly"));
        }

        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powerShell))
        {
            throw new FileNotFoundException(LocalizationService.T("ConversionUiPowerShellMissingForWord"), powerShell);
        }

        using var staging = new StagingDirectory();
        var stagedOutput = Path.Combine(staging.Path, "document.pdf");
        var script = BuildWordPdfScript(input, stagedOutput);
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await RunCheckedAsync(
            powerShell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encodedScript],
            OfficeTimeout,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await ValidatePdfIfRequiredAsync(stagedOutput, output, cancellationToken);
        CommitOutput(stagedOutput, output);
        return new ExternalConversionResult(tool.Kind, output, result.StandardOutput, result.StandardError);
    }

    internal static string BuildLibreOfficeProfileArgument(string stagingPath)
    {
        var profile = Path.GetFullPath(Path.Combine(stagingPath, "libreoffice-profile"));
        return "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri;
    }

    internal static string BuildWordPdfScript(string inputPath, string outputPath)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        return $$"""
            $ErrorActionPreference = 'Stop'
            $word = $null
            $document = $null
            try {
                $word = New-Object -ComObject Word.Application
                $word.Visible = $false
                $word.DisplayAlerts = 0
                # Disable document macros before opening any untrusted input.
                $word.AutomationSecurity = 3
                $document = $word.Documents.Open({{Quote(inputPath)}}, $false, $true)
                $document.ExportAsFixedFormat({{Quote(outputPath)}}, 17)
            }
            finally {
                if ($null -ne $document) {
                    $document.Close($false)
                    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($document)
                }
                if ($null -ne $word) {
                    $word.Quit($false)
                    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($word)
                }
                [GC]::Collect()
                [GC]::WaitForPendingFinalizers()
            }
            """;
    }

    private async Task<ProcessExecutionResult> RunCheckedAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await _runProcess(executable, arguments, timeout, cancellationToken);
        if (result.ExitCode == 0)
        {
            return result;
        }

        var details = FirstNonEmpty(result.StandardError, result.StandardOutput, LocalizationService.T("ConversionUiNoErrorDetails"));
        throw new InvalidOperationException(
            LocalizationService.T("ConversionUiExternalConversionFailed", Path.GetFileName(executable), result.ExitCode, details));
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value)).Trim();

    private static string RequireInput(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(LocalizationService.T("ConversionUiSourceDocumentNotFound"), fullPath);
        }

        return fullPath;
    }

    private static string PrepareOutput(string inputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        if (string.Equals(inputPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiOutputPathMustDiffer"), nameof(outputPath));
        }

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(LocalizationService.T("ConversionUiOutputPathNeedsDirectory"), nameof(outputPath));
        Directory.CreateDirectory(directory);
        return fullPath;
    }

    private static void CommitOutput(string stagedPath, string outputPath)
    {
        if (!File.Exists(stagedPath) || new FileInfo(stagedPath).Length == 0)
        {
            throw new InvalidDataException(LocalizationService.T("ConversionUiConverterNoValidOutput"));
        }

        File.Move(stagedPath, outputPath, overwrite: false);
    }

    private static async Task ValidatePdfIfRequiredAsync(
        string stagedPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(outputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return;
        var validation = await new PdfArtifactValidator().ValidateAsync(
            new ConversionArtifact("staged-pdf", "pdf", stagedPath, true),
            cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Message ?? LocalizationService.T("ConversionUiConverterPdfInvalid"));
        }
    }

    private static void EnsureTool(
        ExternalToolInfo tool,
        ExternalToolKind expectedKind,
        bool executableRequired = true)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool.Kind != expectedKind)
        {
            throw new ArgumentException(LocalizationService.T("ConversionUiToolKindMismatch", expectedKind, tool.Kind), nameof(tool));
        }

        if (!tool.IsAvailable || (executableRequired && string.IsNullOrWhiteSpace(tool.ExecutablePath)))
        {
            throw new InvalidOperationException(tool.StatusMessage);
        }
    }

    private sealed class StagingDirectory : IDisposable
    {
        private readonly string _root;
        private int _disposed;

        public StagingDirectory()
        {
            _root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FormatFactoryWinUI", "document-conversion"));
            Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || !Directory.Exists(Path))
            {
                return;
            }

            var fullPath = System.IO.Path.GetFullPath(Path);
            var rootPrefix = _root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(fullPath, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

using System.Collections.ObjectModel;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Models;

public enum ContentKind
{
    Video,
    Audio,
    Image,
    Document,
    Ebook,
    Disc,
    Archive
}

[Flags]
public enum FormatCapabilities
{
    None = 0,
    Import = 1,
    Export = 2,
    MultiInput = 4
}

/// <summary>
/// Stable description of a file format. <see cref="Id"/> is persisted and must
/// not be localized; <see cref="DisplayName"/> is the short label shown by the UI.
/// </summary>
public sealed record FormatDescriptor
{
    public FormatDescriptor(
        string id,
        string displayName,
        ContentKind contentKind,
        string primaryExtension,
        IEnumerable<string> extensions,
        IEnumerable<string> mimeTypes,
        FormatCapabilities capabilities,
        string? defaultEngineId = null,
        bool isVirtual = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryExtension);

        Id = NormalizeId(id);
        DisplayName = displayName.Trim();
        ContentKind = contentKind;
        PrimaryExtension = NormalizeExtension(primaryExtension);
        Extensions = Array.AsReadOnly(extensions
            .Select(NormalizeExtension)
            .Append(PrimaryExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        MimeTypes = Array.AsReadOnly(mimeTypes
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        Capabilities = capabilities;
        DefaultEngineId = string.IsNullOrWhiteSpace(defaultEngineId)
            ? null
            : NormalizeId(defaultEngineId);
        IsVirtual = isVirtual;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public ContentKind ContentKind { get; }
    public string PrimaryExtension { get; }
    public IReadOnlyList<string> Extensions { get; }
    public IReadOnlyList<string> MimeTypes { get; }
    public FormatCapabilities Capabilities { get; }
    public string? DefaultEngineId { get; }
    public bool IsVirtual { get; }
    public bool CanImport => Capabilities.HasFlag(FormatCapabilities.Import);
    public bool CanExport => Capabilities.HasFlag(FormatCapabilities.Export);
    public bool SupportsMultipleInputs => Capabilities.HasFlag(FormatCapabilities.MultiInput);

    internal static string NormalizeId(string value) => value.Trim().ToLowerInvariant();

    internal static string NormalizeExtension(string value)
    {
        var extension = value.Trim().ToLowerInvariant();
        return extension.StartsWith('.', StringComparison.Ordinal) ? extension : $".{extension}";
    }
}

public sealed record ConversionInput(
    string Path,
    string FormatId,
    ContentProbeResult? Probe = null,
    string? AccessToken = null)
{
    public string FormatId { get; init; } = FormatDescriptor.NormalizeId(FormatId);
}

public sealed record ConversionArtifact(
    string Id,
    string FormatId,
    string Path,
    bool IsTemporary = false)
{
    public string FormatId { get; init; } = FormatDescriptor.NormalizeId(FormatId);
}

/// <summary>
/// Engine-neutral conversion request. A request may contain several inputs (for
/// example an ordered image collection) and may be expanded into a multi-step plan.
/// </summary>
public sealed record ConversionRequest
{
    public Guid TaskId { get; init; } = Guid.NewGuid();
    public IReadOnlyList<ConversionInput> Inputs { get; init; } = [];
    public string TargetFormatId { get; init; } = string.Empty;
    public string? OutputDirectory { get; init; }
    public string Mode { get; init; } = FormatCatalog.DefaultConversionMode;
    public int ParametersVersion { get; init; } = 1;
    public int SelectedAudioTrack { get; init; }
    public string? SourceRoot { get; init; }
    public string? SourceRootAccessToken { get; init; }
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    public void Validate()
    {
        if (Inputs.Count == 0)
        {
            throw new InvalidOperationException(LocalizationService.T("ConversionUiRequestRequiresInput"));
        }

        if (Inputs.Any(input => string.IsNullOrWhiteSpace(input.Path) || string.IsNullOrWhiteSpace(input.FormatId)))
        {
            throw new InvalidOperationException(LocalizationService.T("ConversionUiInputRequiresPathAndFormat"));
        }

        if (string.IsNullOrWhiteSpace(TargetFormatId))
        {
            throw new InvalidOperationException(LocalizationService.T("ConversionUiRequestRequiresTargetFormat"));
        }

        if (ParametersVersion < 1)
        {
            throw new InvalidOperationException(LocalizationService.T("ConversionUiParametersVersionMinimum"));
        }
    }
}

public sealed record ConversionStep(
    int Order,
    string EngineId,
    string SourceFormatId,
    string TargetFormatId,
    IReadOnlyList<string> InputArtifactIds,
    string OutputArtifactId,
    IReadOnlyDictionary<string, string>? Options = null)
{
    public string EngineId { get; init; } = FormatDescriptor.NormalizeId(EngineId);
    public string SourceFormatId { get; init; } = FormatDescriptor.NormalizeId(SourceFormatId);
    public string TargetFormatId { get; init; } = FormatDescriptor.NormalizeId(TargetFormatId);
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        Options ?? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}

public sealed record ConversionPlan(
    ConversionRequest Request,
    IReadOnlyList<ConversionStep> Steps)
{
    public void Validate()
    {
        Request.Validate();
        if (Steps.Count == 0)
        {
            throw new InvalidOperationException(LocalizationService.T("ConversionUiPlanRequiresStep"));
        }

        var expectedOrder = 0;
        var knownArtifacts = new HashSet<string>(
            Request.Inputs.Select((_, index) => $"input:{index}"),
            StringComparer.Ordinal);
        foreach (var step in Steps.OrderBy(value => value.Order))
        {
            if (step.Order != expectedOrder++)
            {
                throw new InvalidOperationException(LocalizationService.T("ConversionUiStepsRequireContiguousOrder"));
            }

            if (step.InputArtifactIds.Count == 0 || step.InputArtifactIds.Any(id => !knownArtifacts.Contains(id)))
            {
                throw new InvalidOperationException(LocalizationService.T("ConversionUiStepUnknownInputArtifact", step.Order));
            }

            if (!knownArtifacts.Add(step.OutputArtifactId))
            {
                throw new InvalidOperationException(LocalizationService.T(
                    "ConversionUiStepReusesArtifactId",
                    step.Order,
                    step.OutputArtifactId));
            }
        }
    }
}

/// <summary>
/// Common probe information. Format-specific probes derive from this type and
/// retain their strongly typed metadata.
/// </summary>
public class ContentProbeResult
{
    public ContentKind ContentKind { get; set; }
    public string FormatId { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int PageCount { get; set; }
    public IReadOnlyDictionary<string, string> Metadata { get; set; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}

public enum EngineAvailabilityState
{
    Available,
    Missing,
    Unsupported,
    Disabled
}

public sealed record EngineAvailability(
    string EngineId,
    EngineAvailabilityState State,
    string? Version = null,
    string? ExecutablePath = null,
    string? Message = null);

public sealed record ArtifactValidationResult(bool IsValid, string? Message = null);

public sealed record ConversionExecutionProgress(
    double Percent,
    string Stage,
    string Message,
    string? Speed = null,
    string? Remaining = null);

public sealed record ConversionExecutionResult(
    bool Success,
    bool Cancelled,
    IReadOnlyList<ConversionArtifact> Artifacts,
    string Message,
    string? LogPath = null)
{
    public string? RecoverableOutputId { get; init; }
    public string? PreservedOutputPath { get; init; }
    public bool Retryable { get; init; }
}

using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

/// <summary>Writes a normalized document as UTF-8 Markdown plus a sibling asset directory.</summary>
public sealed class MarkdownDocumentWriter
{
    public async Task WriteAsync(
        NormalizedDocument document,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(LocalizationService.T("ConversionUiMarkdownOutputNeedsDirectory"), nameof(outputPath));
        Directory.CreateDirectory(directory);

        var token = Guid.NewGuid().ToString("N");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{token}.tmp");
        var finalAssetDirectory = document.Resources.Count == 0 ? null : ReserveAssetDirectory(fullPath);
        var temporaryAssetDirectory = finalAssetDirectory is null
            ? null
            : Path.Combine(directory, $".{Path.GetFileName(finalAssetDirectory)}.{token}.tmp");
        var committedAssets = false;
        var committedMarkdown = false;

        try
        {
            var resourceLinks = temporaryAssetDirectory is null || finalAssetDirectory is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : await StageResourcesAsync(
                    document.Resources,
                    temporaryAssetDirectory,
                    Path.GetFileName(finalAssetDirectory),
                    cancellationToken);

            await DocumentTextWorker.WriteAsync(document, "md", temporaryPath, resourceLinks, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            OutputCommitReceiptStore.Shared.PrepareRegisteredPath(fullPath, temporaryPath,
                temporaryAssetDirectory, finalAssetDirectory);
            if (temporaryAssetDirectory is not null && finalAssetDirectory is not null)
            {
                OutputRecoveryService.Publish(temporaryAssetDirectory, finalAssetDirectory, isDirectory: true);
                committedAssets = true;
            }
            OutputRecoveryService.Publish(temporaryPath, fullPath, isDirectory: false);
            committedMarkdown = true;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (temporaryAssetDirectory is not null && Directory.Exists(temporaryAssetDirectory))
                DeleteDirectory(temporaryAssetDirectory);
            if (!committedMarkdown && committedAssets && finalAssetDirectory is not null && Directory.Exists(finalAssetDirectory))
                DeleteDirectory(finalAssetDirectory);
        }
    }

    private static async Task<Dictionary<string, string>> StageResourcesAsync(
        IReadOnlyList<NormalizedResource> resources,
        string temporaryDirectory,
        string finalDirectoryName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(temporaryDirectory);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < resources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = resources[index];
            var fileName = UniqueFileName(ResourceFileName(resource, index), names);
            await using (var stream = new FileStream(
                Path.Combine(temporaryDirectory, fileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await using var source = resource.OpenRead();
                await source.CopyToAsync(stream, cancellationToken);
            }
            links.TryAdd(resource.Id,
                $"{Uri.EscapeDataString(finalDirectoryName)}/{Uri.EscapeDataString(fileName)}");
        }
        return links;
    }

    private static string ResourceFileName(NormalizedResource resource, int index)
    {
        var name = string.IsNullOrWhiteSpace(resource.FileName)
            ? $"resource-{index + 1}{ExtensionFor(resource.MediaType)}"
            : Path.GetFileName(resource.FileName.Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            name = $"resource-{index + 1}{ExtensionFor(resource.MediaType)}";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(value => invalid.Contains(value) ? '_' : value).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? $"resource-{index + 1}{ExtensionFor(resource.MediaType)}"
            : sanitized;
    }

    private static string UniqueFileName(string requested, HashSet<string> names)
    {
        if (names.Add(requested)) return requested;
        var stem = Path.GetFileNameWithoutExtension(requested);
        var extension = Path.GetExtension(requested);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem}-{suffix}{extension}";
            if (names.Add(candidate)) return candidate;
        }
    }

    private static string ExtensionFor(string mediaType) => mediaType.Trim().ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/tiff" => ".tiff",
        "image/svg+xml" => ".svg",
        _ => ".bin"
    };

    private static string ReserveAssetDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        var baseName = Path.GetFileNameWithoutExtension(outputPath) + ".assets";
        var candidate = Path.Combine(directory, baseName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        for (var suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(directory, $"{baseName}-{suffix}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

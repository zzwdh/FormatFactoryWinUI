using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FormatFactoryWinUI.Documents;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

internal static class ClipboardService
{
    public static Task<IReadOnlyList<string>> ReadSupportedFilePathsAsync() =>
        ReadSupportedFilePathsAsync(Clipboard.GetContent());

    internal static async Task<IReadOnlyList<string>> ReadSupportedFilePathsAsync(DataPackageView data, string? importDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            var items = await ReadAvailableFormatAsync(async () => await data.GetStorageItemsAsync());
            if (items is not null)
                return items.OfType<StorageFile>()
                    .Select(item => item.Path)
                    .Where(IsSupportedFile)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }

        string? text = null;
        if (data.Contains(StandardDataFormats.Text))
        {
            text = await ReadAvailableFormatAsync(async () => await data.GetTextAsync());
            if (text is not null)
            {
                var paths = ParsePaths(text);
                if (paths.Count > 0) return paths;
            }
        }

        CleanupExpiredImports(importDirectory);
        if (data.Contains(StandardDataFormats.Html))
        {
            var html = await ReadAvailableFormatAsync(async () => await data.GetHtmlFormatAsync());
            if (html is not null)
            {
                var fragmentText = await ConversionBackend.RequestAsync<string>("clipboard-html", HtmlFormatHelper.GetStaticFragment(html));
                return new[] { await MaterializeDocumentAsync(fragmentText, "HTML", importDirectory) };
            }
        }
        if (data.Contains(StandardDataFormats.Rtf))
        {
            var rtf = await ReadAvailableFormatAsync(async () => await data.GetRtfAsync());
            if (rtf is not null)
                return new[] { await MaterializeDocumentAsync(ConvertRtfToText(rtf), "RTF", importDirectory) };
        }
        if (data.Contains(StandardDataFormats.Bitmap))
        {
            using var input = await ReadAvailableFormatAsync(async () => await (await data.GetBitmapAsync()).OpenReadAsync());
            if (input is not null)
                return new[] { await MaterializeBitmapAsync(input, importDirectory) };
        }
        if (text is not null)
            return new[] { await MaterializeDocumentAsync(text, LocalizationService.T("GeneralServiceClipboardTextSource"), importDirectory) };

        return [];
    }

    private static async Task<T?> ReadAvailableFormatAsync<T>(Func<Task<T>> read) where T : class
    {
        try { return await read(); }
        // A clipboard owner may advertise a delayed format and then fail to supply it.
        // Only DV_E_FORMATETC means this representation is unavailable; access, decode
        // and destination write failures must still reach the caller.
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80040064))
        { return null; }
    }

    private static IReadOnlyList<string> ParsePaths(string text) => text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim().Trim('"'))
            .Where(path => File.Exists(path) && IsSupportedFile(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static async Task<string> MaterializeDocumentAsync(string text, string sourceLabel, string? importDirectory)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException(LocalizationService.T("GeneralServiceClipboardNoText"));
        }

        var outputPath = CreateImportPath("docx", importDirectory);
        await PlainTextDocxWriter.WriteAsync(outputPath, LocalizationService.T("GeneralServiceClipboardImportTitle", sourceLabel), text);
        return outputPath;
    }

    private static async Task<string> MaterializeBitmapAsync(IRandomAccessStream input, string? importDirectory)
    {
        var outputPath = CreateImportPath("png", importDirectory);
        var temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (File.Create(temporaryPath)) { }
            var file = await StorageFile.GetFileFromPathAsync(temporaryPath);
            using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var decoder = await BitmapDecoder.CreateAsync(input);
                var pixels = await decoder.GetPixelDataAsync();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetPixelData(
                    decoder.BitmapPixelFormat,
                    decoder.BitmapAlphaMode,
                    decoder.PixelWidth,
                    decoder.PixelHeight,
                    decoder.DpiX,
                    decoder.DpiY,
                    pixels.DetachPixelData());
                await encoder.FlushAsync();
                await output.FlushAsync();
            }
            // Finish and close the encoded temporary image before committing it.
            File.Move(temporaryPath, outputPath, overwrite: false);
            return outputPath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string ConvertRtfToText(string rtf)
    {
        var decoded = Regex.Replace(rtf, @"\\'(?<hex>[0-9a-fA-F]{2})", match =>
            ((char)Convert.ToByte(match.Groups["hex"].Value, 16)).ToString());
        decoded = Regex.Replace(decoded, @"\\par[d]?\s?", "\n", RegexOptions.IgnoreCase);
        decoded = Regex.Replace(decoded, @"\\[a-zA-Z]+-?\d*\s?", string.Empty);
        return decoded.Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Replace("\\", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string CreateImportPath(string extension, string? importDirectory)
    {
        var directory = importDirectory ?? Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", "ClipboardImports");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.{extension}");
    }

    private static void CleanupExpiredImports(string? importDirectory)
    {
        var directory = importDirectory ?? Path.Combine(Path.GetTempPath(), "FormatFactoryWinUI", "ClipboardImports");
        if (!Directory.Exists(directory))
        {
            return;
        }
        var expiration = DateTime.UtcNow.AddDays(-1);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < expiration)
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static bool IsSupportedFile(string path) =>
        FormatCatalog.SupportedExtensions.Contains(Path.GetExtension(path));
}

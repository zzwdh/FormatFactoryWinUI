using System.Security.Cryptography;
using System.Text;
using PdfSharp.Fonts;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Documents;

internal sealed record PdfFontRun(string Text, string FontName);

/// <summary>Chooses an installed font per Unicode scalar, checking cmap coverage before embedding.</summary>
internal sealed class UnicodePdfFontResolver : IFontResolver
{
    public static UnicodePdfFontResolver Instance { get; } = new();
    private readonly object _gate = new();
    private readonly Lazy<IReadOnlyList<SystemFontFile>> _files = new(DirectWriteFontFiles.Discover);
    private readonly Dictionary<SystemFontFile, TrueTypeFontData?> _loaded = [];
    private readonly Dictionary<SystemFontFile, string> _fontNames = [];
    private readonly Dictionary<int, string> _scalarFonts = [];
    private readonly Dictionary<string, byte[]> _embedded = new(StringComparer.Ordinal);

    public IReadOnlyList<PdfFontRun> ResolveRuns(string text)
    {
        lock (_gate)
        {
            var runs = new List<PdfFontRun>();
            foreach (var rune in text.EnumerateRunes())
            {
                var name = Resolve(rune.Value);
                if (runs.Count > 0 && runs[^1].FontName == name)
                    runs[^1] = runs[^1] with { Text = runs[^1].Text + rune.ToString() };
                else runs.Add(new PdfFontRun(rune.ToString(), name));
            }
            return runs;
        }
    }

    private string Resolve(int scalar)
    {
        if (_scalarFonts.TryGetValue(scalar, out var resolved)) return resolved;
        foreach (var file in _files.Value)
        {
            if (!_loaded.TryGetValue(file, out var font))
            {
                try { font = new TrueTypeFontData(File.ReadAllBytes(file.Path), file.FaceIndex); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                    NotSupportedException or ArgumentException or OverflowException) { font = null; }
                _loaded[file] = font;
            }
            if (font is null) continue;
            if (!font.Supports(scalar))
            {
                font.ReleaseFontBytes();
                continue;
            }
            if (!_fontNames.TryGetValue(file, out var name))
            {
                var bytes = font.Bytes.Length > 0 ? font.Bytes : TrueTypeFontData.ExtractFace(File.ReadAllBytes(file.Path), file.FaceIndex);
                name = "FormatFactoryOcr-" + Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 8));
                _embedded.TryAdd(name, bytes);
                _fontNames[file] = name;
                font.ReleaseFontBytes();
            }
            _scalarFonts[scalar] = name;
            return name;
        }
        // Fail visibly instead of silently writing missing-glyph boxes into a searchable document.
        throw new NotSupportedException(LocalizationService.T("NativeOcrMissingFont", scalar.ToString("X4")));
    }

    public byte[]? GetFont(string faceName)
    {
        lock (_gate) return _embedded.GetValueOrDefault(faceName);
    }
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        lock (_gate) return _embedded.ContainsKey(familyName) ? new FontResolverInfo(familyName) : null;
    }
}

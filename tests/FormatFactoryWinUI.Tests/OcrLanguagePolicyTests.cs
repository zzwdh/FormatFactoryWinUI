using FormatFactoryWinUI.Documents;

namespace FormatFactoryWinUI.Tests;

public sealed class OcrLanguagePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData(" AUTO ")]
    public void Resolve_AutomaticValueUsesProfileLanguages(string? languageTag)
    {
        var automatic = new object();
        var requestedCalled = false;

        var result = OcrLanguagePolicy.Resolve(
            languageTag,
            _ =>
            {
                requestedCalled = true;
                return new object();
            },
            () => automatic);

        Assert.Same(automatic, result);
        Assert.False(requestedCalled);
    }

    [Fact]
    public void Resolve_MissingExplicitLanguageDoesNotFallBackSilently()
    {
        var automaticCalled = false;

        var error = Assert.Throws<OcrLanguageUnavailableException>(() =>
            OcrLanguagePolicy.Resolve<object>(
                " zh-Hans ",
                _ => null,
                () =>
                {
                    automaticCalled = true;
                    return new object();
                }));

        Assert.Contains("zh-Hans", error.Message, StringComparison.Ordinal);
        Assert.False(automaticCalled);
    }

    [Fact]
    public void Resolve_InvalidExplicitLanguageReportsTheRequestedTag()
    {
        var error = Assert.Throws<OcrLanguageUnavailableException>(() =>
            OcrLanguagePolicy.Resolve<object>(
                "not_a_language",
                _ => throw new ArgumentException("invalid"),
                () => new object()));

        Assert.Contains("not_a_language", error.Message, StringComparison.Ordinal);
        Assert.IsType<ArgumentException>(error.InnerException);
    }
}

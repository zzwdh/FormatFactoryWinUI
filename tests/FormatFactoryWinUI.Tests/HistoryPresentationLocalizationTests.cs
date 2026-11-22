using System.Globalization;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class HistoryPresentationLocalizationTests
{
    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void HistoryDateUsesSelectedAppLanguageWhenSystemFormattingDiffers(string language)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousLanguage = LocalizationService.CurrentLanguageCode;
        try
        {
            LocalizationService.SetLanguage(language);
            // The OS date format can differ from the app's explicit language.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var item = new ConversionHistoryItem
            {
                CompletedAtUtc = new DateTimeOffset(2026, 10, 4, 15, 27, 0, TimeSpan.Zero)
            };

            var actual = HistoryPageViewModel.DescribeCompletedAt(item);

            Assert.Equal(item.CompletedAtUtc.ToLocalTime().ToString("g", LocalizationService.CurrentUICulture), actual);
            Assert.NotEqual(item.CompletedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), actual);
        }
        finally
        {
            LocalizationService.SetLanguage(previousLanguage);
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}

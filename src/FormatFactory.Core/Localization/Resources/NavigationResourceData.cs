namespace FormatFactoryWinUI.Services;

// Additional-language overlays are independent of catalog initialization.
internal static class NavigationResourceData
{
    private static readonly IReadOnlyDictionary<string, (string Expand, string Collapse)> Translations =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["da-DK"] = ("Udvid navigationen", "Fold navigationen sammen"),
            ["hu-HU"] = ("Navigáció kibontása", "Navigáció összecsukása"),
            ["ro-RO"] = ("Extinde navigarea", "Restrânge navigarea"),
            ["sv-SE"] = ("Expandera navigering", "Fäll ihop navigering"),
            ["nb-NO"] = ("Utvid navigasjonen", "Trekk sammen navigasjonen"),
            ["fi-FI"] = ("Laajenna siirtymisvalikko", "Pienennä siirtymisvalikko"),
            ["cs-CZ"] = ("Rozbalit navigaci", "Sbalit navigaci"),
            ["el-GR"] = ("Ανάπτυξη πλοήγησης", "Σύμπτυξη πλοήγησης"),
            ["uk-UA"] = ("Розгорнути навігацію", "Згорнути навігацію"),
            ["vi-VN"] = ("Mở rộng thanh điều hướng", "Thu gọn thanh điều hướng"),
            ["th-TH"] = ("ขยายแถบนำทาง", "ย่อแถบนำทาง"),
            ["id-ID"] = ("Perluas navigasi", "Ciutkan navigasi"),
            ["ms-MY"] = ("Kembangkan navigasi", "Runtuhkan navigasi"),
            ["hi-IN"] = ("नेविगेशन विस्तृत करें", "नेविगेशन संक्षिप्त करें"),
            ["fil-PH"] = ("Palawakin ang nabigasyon", "Paliitin ang nabigasyon"),
            ["sk-SK"] = ("Rozbaliť navigáciu", "Zbaliť navigáciu"),
            ["sl-SI"] = ("Razširi krmarjenje", "Strni krmarjenje"),
            ["hr-HR"] = ("Proširi navigaciju", "Sažmi navigaciju"),
            ["bg-BG"] = ("Разгъване на навигацията", "Свиване на навигацията"),
            ["lt-LT"] = ("Išskleisti naršymą", "Sutraukti naršymą"),
            ["lv-LV"] = ("Izvērst navigāciju", "Sakļaut navigāciju"),
            ["et-EE"] = ("Laienda navigeerimisriba", "Ahenda navigeerimisriba"),
            ["is-IS"] = ("Stækka yfirlitsstiku", "Fella saman yfirlitsstiku"),
            ["sr-Latn-RS"] = ("Proširi navigaciju", "Skupi navigaciju"),
            ["pt-PT"] = ("Expandir navegação", "Recolher navegação"),
            ["he-IL"] = ("הרחבת הניווט", "כיווץ הניווט"),
            ["fa-IR"] = ("گسترش پیمایش", "جمع‌کردن پیمایش"),
            ["bn-BD"] = ("নেভিগেশন প্রসারিত করুন", "নেভিগেশন সংকুচিত করুন"),
            ["ta-IN"] = ("வழிசெலுத்தலை விரிவாக்கு", "வழிசெலுத்தலைச் சுருக்கு")
        };

    internal static void AddTo(string languageCode, IDictionary<string, string> resources)
    {
        if (!Translations.TryGetValue(languageCode, out var translated))
            throw new InvalidOperationException($"Missing navigation translations for '{languageCode}'.");
        resources["DesktopNavigationExpand"] = translated.Expand;
        resources["DesktopNavigationCollapse"] = translated.Collapse;
    }
}

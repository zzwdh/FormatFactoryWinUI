namespace FormatFactoryWinUI.Services;

internal static class TrayLocalizationResources
{
    internal static readonly LocalizedResourceModule Module = new("Tray",
        TrayResourceData.Keys.Select(key => new LocalizedResourceEntry(key,
            LocalizedResourceModule.LegacyLanguageCodes.Select(code => TrayResourceData.Translate(code, key)).ToArray())).ToArray());
}

// Additional-language overlays must not depend on module initialization.
internal static class TrayResourceData
{
    internal static readonly IReadOnlyList<string> Keys = ["TrayOpenWindow", "TrayConversions", "TrayExit"];

    private static readonly IReadOnlyDictionary<string, (string OpenWindow, string Conversions, string Exit)> Translations =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["en-US"] = ("Open main window", "Conversion categories", "Exit application"),
            ["zh-Hans"] = ("打开主窗口", "转换分类", "退出软件"),
            ["zh-Hant"] = ("開啟主視窗", "轉換分類", "結束軟體"),
            ["ja-JP"] = ("メインウィンドウを開く", "変換カテゴリー", "アプリを終了"),
            ["ko-KR"] = ("메인 창 열기", "변환 분류", "앱 종료"),
            ["es-ES"] = ("Abrir ventana principal", "Categorías de conversión", "Salir de la aplicación"),
            ["fr-FR"] = ("Ouvrir la fenêtre principale", "Catégories de conversion", "Quitter l’application"),
            ["de-DE"] = ("Hauptfenster öffnen", "Konvertierungskategorien", "Anwendung beenden"),
            ["pt-BR"] = ("Abrir janela principal", "Categorias de conversão", "Sair do aplicativo"),
            ["ru-RU"] = ("Открыть главное окно", "Категории преобразования", "Выйти из приложения"),
            ["it-IT"] = ("Apri finestra principale", "Categorie di conversione", "Esci dall’applicazione"),
            ["nl-NL"] = ("Hoofdvenster openen", "Conversiecategorieën", "Toepassing afsluiten"),
            ["pl-PL"] = ("Otwórz główne okno", "Kategorie konwersji", "Zakończ aplikację"),
            ["tr-TR"] = ("Ana pencereyi aç", "Dönüştürme kategorileri", "Uygulamadan çık"),
            ["ar-SA"] = ("فتح النافذة الرئيسية", "فئات التحويل", "إنهاء التطبيق"),
            ["da-DK"] = ("Åbn hovedvindue", "Konverteringskategorier", "Afslut program"),
            ["hu-HU"] = ("Főablak megnyitása", "Konvertálási kategóriák", "Kilépés az alkalmazásból"),
            ["ro-RO"] = ("Deschide fereastra principală", "Categorii de conversie", "Închide aplicația"),
            ["sv-SE"] = ("Öppna huvudfönstret", "Konverteringskategorier", "Avsluta programmet"),
            ["nb-NO"] = ("Åpne hovedvinduet", "Konverteringskategorier", "Avslutt programmet"),
            ["fi-FI"] = ("Avaa pääikkuna", "Muunnosluokat", "Lopeta sovellus"),
            ["cs-CZ"] = ("Otevřít hlavní okno", "Kategorie převodu", "Ukončit aplikaci"),
            ["el-GR"] = ("Άνοιγμα κύριου παραθύρου", "Κατηγορίες μετατροπής", "Έξοδος από την εφαρμογή"),
            ["uk-UA"] = ("Відкрити головне вікно", "Категорії перетворення", "Вийти з програми"),
            ["vi-VN"] = ("Mở cửa sổ chính", "Danh mục chuyển đổi", "Thoát ứng dụng"),
            ["th-TH"] = ("เปิดหน้าต่างหลัก", "หมวดหมู่การแปลง", "ออกจากแอป"),
            ["id-ID"] = ("Buka jendela utama", "Kategori konversi", "Keluar dari aplikasi"),
            ["ms-MY"] = ("Buka tetingkap utama", "Kategori penukaran", "Keluar daripada aplikasi"),
            ["hi-IN"] = ("मुख्य विंडो खोलें", "रूपांतरण श्रेणियाँ", "ऐप से बाहर निकलें"),
            ["fil-PH"] = ("Buksan ang pangunahing window", "Mga kategorya ng conversion", "Isara ang application"),
            ["sk-SK"] = ("Otvoriť hlavné okno", "Kategórie konverzie", "Ukončiť aplikáciu"),
            ["sl-SI"] = ("Odpri glavno okno", "Kategorije pretvorbe", "Zapri aplikacijo"),
            ["hr-HR"] = ("Otvori glavni prozor", "Kategorije pretvorbe", "Izađi iz aplikacije"),
            ["bg-BG"] = ("Отваряне на главния прозорец", "Категории за преобразуване", "Изход от приложението"),
            ["lt-LT"] = ("Atverti pagrindinį langą", "Konvertavimo kategorijos", "Išeiti iš programos"),
            ["lv-LV"] = ("Atvērt galveno logu", "Konvertēšanas kategorijas", "Iziet no lietotnes"),
            ["et-EE"] = ("Ava põhiaken", "Teisenduskategooriad", "Välju rakendusest"),
            ["is-IS"] = ("Opna aðalglugga", "Flokkar sniðbreytinga", "Loka forriti"),
            ["sr-Latn-RS"] = ("Otvori glavni prozor", "Kategorije konverzije", "Izađi iz aplikacije"),
            ["pt-PT"] = ("Abrir janela principal", "Categorias de conversão", "Sair da aplicação"),
            ["he-IL"] = ("פתיחת החלון הראשי", "קטגוריות המרה", "יציאה מהיישום"),
            ["fa-IR"] = ("باز کردن پنجرهٔ اصلی", "دسته‌های تبدیل", "خروج از برنامه"),
            ["bn-BD"] = ("প্রধান উইন্ডো খুলুন", "রূপান্তরের বিভাগ", "অ্যাপ থেকে বের হন"),
            ["ta-IN"] = ("முதன்மைச் சாளரத்தைத் திற", "மாற்று வகைகள்", "செயலியிலிருந்து வெளியேறு")
        };

    internal static string Translate(string languageCode, string key)
    {
        if (!Translations.TryGetValue(languageCode, out var text))
            throw new InvalidOperationException($"Missing tray translations for '{languageCode}'.");

        return key switch
        {
            "TrayOpenWindow" => text.OpenWindow,
            "TrayConversions" => text.Conversions,
            "TrayExit" => text.Exit,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown tray resource key.")
        };
    }

    internal static void AddTo(string languageCode, IDictionary<string, string> resources)
    {
        foreach (var key in Keys) resources[key] = Translate(languageCode, key);
    }
}

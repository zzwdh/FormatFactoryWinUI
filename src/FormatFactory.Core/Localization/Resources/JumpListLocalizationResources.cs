namespace FormatFactoryWinUI.Services;

internal static class JumpListLocalizationResources
{
    internal static readonly LocalizedResourceModule Module = new("JumpList",
        JumpListResourceData.Keys.Select(key => new LocalizedResourceEntry(key,
            LocalizedResourceModule.LegacyLanguageCodes.Select(code => JumpListResourceData.Translate(code, key)).ToArray())).ToArray());
}

// Keep translation data independent of module initialization for additional-language overlays.
internal static class JumpListResourceData
{
    internal static readonly IReadOnlyList<string> Keys =
    [
        "JumpListConversionGroup",
        "JumpListWorkspaceGroup",
        "JumpListRecentOutputsGroup"
    ];

    private static readonly IReadOnlyDictionary<string, (string Conversion, string Workspace, string RecentOutputs)> Translations =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["en-US"] = ("Format conversion", "Tasks and management", "Recent outputs"),
            ["zh-Hans"] = ("格式转换", "任务与管理", "最近输出"),
            ["zh-Hant"] = ("格式轉換", "工作與管理", "最近輸出"),
            ["ja-JP"] = ("形式変換", "タスクと管理", "最近の出力"),
            ["ko-KR"] = ("형식 변환", "작업 및 관리", "최근 출력"),
            ["es-ES"] = ("Conversión de formatos", "Tareas y gestión", "Salidas recientes"),
            ["fr-FR"] = ("Conversion de formats", "Tâches et gestion", "Sorties récentes"),
            ["de-DE"] = ("Formatkonvertierung", "Aufgaben und Verwaltung", "Letzte Ausgaben"),
            ["pt-BR"] = ("Conversão de formatos", "Tarefas e gerenciamento", "Saídas recentes"),
            ["ru-RU"] = ("Преобразование форматов", "Задачи и управление", "Последние результаты"),
            ["it-IT"] = ("Conversione di formati", "Attività e gestione", "Output recenti"),
            ["nl-NL"] = ("Formaatconversie", "Taken en beheer", "Recente uitvoer"),
            ["pl-PL"] = ("Konwersja formatów", "Zadania i zarządzanie", "Ostatnie wyniki"),
            ["tr-TR"] = ("Biçim dönüştürme", "Görevler ve yönetim", "Son çıktılar"),
            ["ar-SA"] = ("تحويل الصيغ", "المهام والإدارة", "المخرجات الأخيرة"),
            ["da-DK"] = ("Formatkonvertering", "Opgaver og administration", "Seneste output"),
            ["hu-HU"] = ("Formátumkonvertálás", "Feladatok és kezelés", "Legutóbbi kimenetek"),
            ["ro-RO"] = ("Conversie de formate", "Sarcini și administrare", "Rezultate recente"),
            ["sv-SE"] = ("Formatkonvertering", "Uppgifter och hantering", "Senaste utdata"),
            ["nb-NO"] = ("Formatkonvertering", "Oppgaver og administrasjon", "Siste utdata"),
            ["fi-FI"] = ("Muotomuunnos", "Tehtävät ja hallinta", "Viimeisimmät tulokset"),
            ["cs-CZ"] = ("Převod formátů", "Úlohy a správa", "Nedávné výstupy"),
            ["el-GR"] = ("Μετατροπή μορφών", "Εργασίες και διαχείριση", "Πρόσφατα αποτελέσματα"),
            ["uk-UA"] = ("Перетворення форматів", "Завдання та керування", "Останні результати"),
            ["vi-VN"] = ("Chuyển đổi định dạng", "Tác vụ và quản lý", "Đầu ra gần đây"),
            ["th-TH"] = ("แปลงรูปแบบ", "งานและการจัดการ", "ผลลัพธ์ล่าสุด"),
            ["id-ID"] = ("Konversi format", "Tugas dan pengelolaan", "Hasil terbaru"),
            ["ms-MY"] = ("Penukaran format", "Tugas dan pengurusan", "Output terkini"),
            ["hi-IN"] = ("प्रारूप रूपांतरण", "कार्य और प्रबंधन", "हाल के आउटपुट"),
            ["fil-PH"] = ("Pag-convert ng format", "Mga gawain at pamamahala", "Mga kamakailang output"),
            ["sk-SK"] = ("Konverzia formátov", "Úlohy a správa", "Nedávne výstupy"),
            ["sl-SI"] = ("Pretvorba oblik", "Opravila in upravljanje", "Nedavni izhodi"),
            ["hr-HR"] = ("Pretvorba formata", "Zadaci i upravljanje", "Nedavni izlazi"),
            ["bg-BG"] = ("Преобразуване на формати", "Задачи и управление", "Последни резултати"),
            ["lt-LT"] = ("Formatų konvertavimas", "Užduotys ir valdymas", "Naujausi rezultatai"),
            ["lv-LV"] = ("Formātu konvertēšana", "Uzdevumi un pārvaldība", "Nesenie rezultāti"),
            ["et-EE"] = ("Vormingu teisendamine", "Ülesanded ja haldus", "Viimatised väljundid"),
            ["is-IS"] = ("Sniðbreyting", "Verkefni og stjórnun", "Nýlegt úttak"),
            ["sr-Latn-RS"] = ("Konverzija formata", "Zadaci i upravljanje", "Nedavni izlazi"),
            ["pt-PT"] = ("Conversão de formatos", "Tarefas e gestão", "Saídas recentes"),
            ["he-IL"] = ("המרת פורמטים", "משימות וניהול", "תוצרים אחרונים"),
            ["fa-IR"] = ("تبدیل قالب", "کارها و مدیریت", "خروجی‌های اخیر"),
            ["bn-BD"] = ("ফরম্যাট রূপান্তর", "কাজ ও ব্যবস্থাপনা", "সাম্প্রতিক আউটপুট"),
            ["ta-IN"] = ("வடிவ மாற்றம்", "பணிகளும் நிர்வாகமும்", "சமீபத்திய வெளியீடுகள்")
        };

    internal static string Translate(string languageCode, string key)
    {
        if (!Translations.TryGetValue(languageCode, out var text))
            throw new InvalidOperationException($"Missing Jump List translations for '{languageCode}'.");

        return key switch
        {
            "JumpListConversionGroup" => text.Conversion,
            "JumpListWorkspaceGroup" => text.Workspace,
            "JumpListRecentOutputsGroup" => text.RecentOutputs,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown Jump List resource key.")
        };
    }

    internal static void AddTo(string languageCode, IDictionary<string, string> resources)
    {
        foreach (var key in Keys) resources[key] = Translate(languageCode, key);
    }
}

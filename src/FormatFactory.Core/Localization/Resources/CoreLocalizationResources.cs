namespace FormatFactoryWinUI.Services;

internal static class CoreLocalizationResources
{
    internal static LocalizedResourceModule Module { get; } = new(
        "Core",
        [
            new("ConversionUiInstallMakeMkv",
            [
                "Install MakeMKV",
                "安装 MakeMKV",
                "安裝 MakeMKV",
                "MakeMKV をインストール",
                "MakeMKV 설치",
                "Instalar MakeMKV",
                "Installer MakeMKV",
                "MakeMKV installieren",
                "Instalar MakeMKV",
                "Установить MakeMKV",
                "Installa MakeMKV",
                "MakeMKV installeren",
                "Zainstaluj MakeMKV",
                "MakeMKV yükle",
                "تثبيت MakeMKV"
            ]),
            new("ConversionUiMakeMkvInstallHint",
            [
                "Blu-ray uses MakeMKV. The original installer is included; a trial or registration is required.",
                "蓝光转换使用 MakeMKV，随包附带官方安装程序，仍需试用或注册。",
                "藍光轉換使用 MakeMKV，隨附官方安裝程式，仍需試用或註冊。",
                "Blu-ray 変換には MakeMKV を使用します。公式インストーラーを同梱しています。試用または登録が必要です。",
                "Blu-ray 변환에는 MakeMKV를 사용합니다. 공식 설치 프로그램이 포함되며 평가판 또는 등록이 필요합니다.",
                "Blu-ray usa MakeMKV. Se incluye el instalador original; se requiere una prueba o registro.",
                "Le Blu-ray utilise MakeMKV. Le programme officiel est inclus ; un essai ou une inscription est nécessaire.",
                "Blu-ray verwendet MakeMKV. Der Originalinstaller ist enthalten; Testversion oder Registrierung erforderlich.",
                "Blu-ray usa MakeMKV. O instalador original está incluído; é necessário teste ou registro.",
                "Для Blu-ray используется MakeMKV. Оригинальный установщик включён; требуется пробный период или регистрация.",
                "Blu-ray usa MakeMKV. È incluso il programma di installazione originale; è richiesta una prova o registrazione.",
                "Blu-ray gebruikt MakeMKV. Het oorspronkelijke installatieprogramma is inbegrepen; een proefperiode of registratie is vereist.",
                "Blu-ray korzysta z MakeMKV. Dołączono oryginalny instalator; wymagany okres próbny lub rejestracja.",
                "Blu-ray için MakeMKV kullanılır. Özgün yükleyici dahildir; deneme veya kayıt gerekir.",
                "يستخدم Blu-ray برنامج MakeMKV. برنامج التثبيت الأصلي مضمّن، ويلزم إصدار تجريبي أو تسجيل."
            ]),
            new("ConversionUiBundledInstallerInvalid",
            [
                "The bundled installer failed integrity verification. Repair or reinstall this app.",
                "随包安装程序校验失败，请修复或重新安装本应用。",
                "隨附安裝程式驗證失敗，請修復或重新安裝本應用程式。",
                "同梱インストーラーの検証に失敗しました。アプリを修復または再インストールしてください。",
                "포함된 설치 프로그램 확인에 실패했습니다. 이 앱을 복구하거나 다시 설치하세요.",
                "Error de integridad del instalador incluido. Repara o reinstala esta aplicación.",
                "La vérification du programme inclus a échoué. Réparez ou réinstallez cette application.",
                "Integritätsprüfung des Installers fehlgeschlagen. App reparieren oder neu installieren.",
                "Falha na verificação do instalador incluído. Repare ou reinstale este aplicativo.",
                "Проверка установщика не пройдена. Восстановите или переустановите приложение.",
                "Verifica del programma di installazione non riuscita. Ripara o reinstalla questa app.",
                "Integriteitscontrole mislukt. Herstel of installeer deze app opnieuw.",
                "Weryfikacja instalatora nie powiodła się. Napraw lub zainstaluj ponownie tę aplikację.",
                "Yükleyici bütünlük doğrulaması başarısız. Bu uygulamayı onarın veya yeniden yükleyin.",
                "فشل التحقق من سلامة برنامج التثبيت. أصلح هذا التطبيق أو أعد تثبيته."
            ]),
            new("ConversionUiBundledInstallerFailed",
            [
                "The installer could not be started.",
                "无法启动安装程序。",
                "無法啟動安裝程式。",
                "インストーラーを起動できませんでした。",
                "설치 프로그램을 시작할 수 없습니다.",
                "No se pudo iniciar el instalador.",
                "Impossible de démarrer le programme d’installation.",
                "Das Installationsprogramm konnte nicht gestartet werden.",
                "Não foi possível iniciar o instalador.",
                "Не удалось запустить установщик.",
                "Impossibile avviare il programma di installazione.",
                "Het installatieprogramma kon niet worden gestart.",
                "Nie udało się uruchomić instalatora.",
                "Yükleyici başlatılamadı.",
                "تعذّر تشغيل برنامج التثبيت."
            ]),
            new("CoreAudioCdRipperLabel",
            [
                "Audio CD ripper", "音乐 CD 抓轨器", "音樂 CD 擷取工具", "オーディオ CD リッパー", "오디오 CD 추출기",
                "Extractor de CD de audio", "Extracteur de CD audio", "Audio-CD-Ripper", "Extrator de CD de áudio", "Программа извлечения аудио с CD",
                "Estrattore di CD audio", "Audio-cd-ripper", "Program do zgrywania płyt audio CD", "Ses CD'si kopyalayıcısı", "أداة نسخ الأقراص الصوتية"
            ]),
            new("CoreCajConverterLabel",
            [
                "CAJ converter", "CAJ 转换器", "CAJ 轉換器", "CAJ コンバーター", "CAJ 변환기",
                "Conversor de CAJ", "Convertisseur CAJ", "CAJ-Konverter", "Conversor de CAJ", "Конвертер CAJ",
                "Convertitore CAJ", "CAJ-converter", "Konwerter CAJ", "CAJ dönüştürücü", "محوّل CAJ"
            ]),
            new("CoreShareConvertedMediaDescription",
            [
                "Converted media files", "已转换的媒体文件", "已轉換的媒體檔案", "変換済みのメディアファイル", "변환된 미디어 파일",
                "Archivos multimedia convertidos", "Fichiers multimédias convertis", "Konvertierte Mediendateien", "Arquivos de mídia convertidos", "Преобразованные медиафайлы",
                "File multimediali convertiti", "Geconverteerde mediabestanden", "Przekonwertowane pliki multimedialne", "Dönüştürülen medya dosyaları", "ملفات الوسائط المحوّلة"
            ]),
            new("CoreMakeMkvPathPlaceholder",
            [
                "Path to makemkvcon64.exe (optional)", "makemkvcon64.exe 路径（可选）", "makemkvcon64.exe 路徑（選用）", "makemkvcon64.exe のパス（任意）", "makemkvcon64.exe 경로(선택 사항)",
                "Ruta a makemkvcon64.exe (opcional)", "Chemin vers makemkvcon64.exe (facultatif)", "Pfad zu makemkvcon64.exe (optional)", "Caminho para makemkvcon64.exe (opcional)", "Путь к makemkvcon64.exe (необязательно)",
                "Percorso di makemkvcon64.exe (facoltativo)", "Pad naar makemkvcon64.exe (optioneel)", "Ścieżka do makemkvcon64.exe (opcjonalnie)", "makemkvcon64.exe yolu (isteğe bağlı)", "مسار makemkvcon64.exe (اختياري)"
            ]),
            new("CoreAudioCdRipperPathPlaceholder",
            [
                "Path to cdda2wav.exe or icedax.exe (optional)", "cdda2wav.exe 或 icedax.exe 路径（可选）", "cdda2wav.exe 或 icedax.exe 路徑（選用）", "cdda2wav.exe または icedax.exe のパス（任意）", "cdda2wav.exe 또는 icedax.exe 경로(선택 사항)",
                "Ruta a cdda2wav.exe o icedax.exe (opcional)", "Chemin vers cdda2wav.exe ou icedax.exe (facultatif)", "Pfad zu cdda2wav.exe oder icedax.exe (optional)", "Caminho para cdda2wav.exe ou icedax.exe (opcional)", "Путь к cdda2wav.exe или icedax.exe (необязательно)",
                "Percorso di cdda2wav.exe o icedax.exe (facoltativo)", "Pad naar cdda2wav.exe of icedax.exe (optioneel)", "Ścieżka do cdda2wav.exe lub icedax.exe (opcjonalnie)", "cdda2wav.exe veya icedax.exe yolu (isteğe bağlı)", "مسار cdda2wav.exe أو icedax.exe (اختياري)"
            ]),
            new("CoreReadomPathPlaceholder",
            [
                "Path to readom.exe (optional)", "readom.exe 路径（可选）", "readom.exe 路徑（選用）", "readom.exe のパス（任意）", "readom.exe 경로(선택 사항)",
                "Ruta a readom.exe (opcional)", "Chemin vers readom.exe (facultatif)", "Pfad zu readom.exe (optional)", "Caminho para readom.exe (opcional)", "Путь к readom.exe (необязательно)",
                "Percorso di readom.exe (facoltativo)", "Pad naar readom.exe (optioneel)", "Ścieżka do readom.exe (opcjonalnie)", "readom.exe yolu (isteğe bağlı)", "مسار readom.exe (اختياري)"
            ]),
            new("CoreLibreOfficePathPlaceholder",
            [
                "Path to soffice.com (optional)", "soffice.com 路径（可选）", "soffice.com 路徑（選用）", "soffice.com のパス（任意）", "soffice.com 경로(선택 사항)",
                "Ruta a soffice.com (opcional)", "Chemin vers soffice.com (facultatif)", "Pfad zu soffice.com (optional)", "Caminho para soffice.com (opcional)", "Путь к soffice.com (необязательно)",
                "Percorso di soffice.com (facoltativo)", "Pad naar soffice.com (optioneel)", "Ścieżka do soffice.com (opcjonalnie)", "soffice.com yolu (isteğe bağlı)", "مسار soffice.com (اختياري)"
            ]),
            new("CoreCalibrePathPlaceholder",
            [
                "Path to ebook-convert.exe (optional)", "ebook-convert.exe 路径（可选）", "ebook-convert.exe 路徑（選用）", "ebook-convert.exe のパス（任意）", "ebook-convert.exe 경로(선택 사항)",
                "Ruta a ebook-convert.exe (opcional)", "Chemin vers ebook-convert.exe (facultatif)", "Pfad zu ebook-convert.exe (optional)", "Caminho para ebook-convert.exe (opcional)", "Путь к ebook-convert.exe (необязательно)",
                "Percorso di ebook-convert.exe (facoltativo)", "Pad naar ebook-convert.exe (optioneel)", "Ścieżka do ebook-convert.exe (opcjonalnie)", "ebook-convert.exe yolu (isteğe bağlı)", "مسار ebook-convert.exe (اختياري)"
            ]),
            new("CoreCajPathPlaceholder",
            [
                "Custom caj2pdf.exe path (optional)",
                "自定义 caj2pdf.exe 路径（可留空）",
                "自訂 caj2pdf.exe 路徑（可留空）",
                "caj2pdf.exe のカスタムパス（任意）",
                "사용자 지정 caj2pdf.exe 경로(선택 사항)",
                "Ruta personalizada a caj2pdf.exe (opcional)",
                "Chemin personnalisé vers caj2pdf.exe (facultatif)",
                "Eigener Pfad zu caj2pdf.exe (optional)",
                "Caminho personalizado para caj2pdf.exe (opcional)",
                "Свой путь к caj2pdf.exe (необязательно)",
                "Percorso personalizzato di caj2pdf.exe (facoltativo)",
                "Aangepast pad naar caj2pdf.exe (optioneel)",
                "Własna ścieżka do caj2pdf.exe (opcjonalnie)",
                "Özel caj2pdf.exe yolu (isteğe bağlı)",
                "مسار مخصص إلى caj2pdf.exe (اختياري)"
            ]),
            new("CoreOpticalDiscDataName",
            [
                "Data disc", "数据光盘", "資料光碟", "データ ディスク", "데이터 디스크",
                "Disco de datos", "Disque de données", "Datenträger", "Disco de dados", "Диск с данными",
                "Disco dati", "Gegevensschijf", "Dysk z danymi", "Veri diski", "قرص بيانات"
            ]),
            new("CoreOpticalDiscAudioCdName",
            [
                "Audio CD", "音乐 CD", "音樂 CD", "オーディオ CD", "오디오 CD",
                "CD de audio", "CD audio", "Audio-CD", "CD de áudio", "Аудио-CD",
                "CD audio", "Audio-cd", "Płyta Audio CD", "Ses CD'si", "قرص صوتي مضغوط"
            ]),
            new("CoreOpticalDriveName",
            [
                "Optical drive", "光驱", "光碟機", "光学ドライブ", "광학 드라이브",
                "Unidad óptica", "Lecteur optique", "Optisches Laufwerk", "Unidade óptica", "Оптический привод",
                "Unità ottica", "Optisch station", "Napęd optyczny", "Optik sürücü", "محرك أقراص ضوئية"
            ]),
            new("CoreOpticalDriveLetterRequired",
            [
                "The optical-drive path must start with a Windows drive letter.",
                "光驱路径必须以 Windows 驱动器号开头。",
                "光碟機路徑必須以 Windows 磁碟機代號開頭。",
                "光学ドライブのパスは Windows のドライブ文字で始まる必要があります。",
                "광학 드라이브 경로는 Windows 드라이브 문자로 시작해야 합니다.",
                "La ruta de la unidad óptica debe comenzar con una letra de unidad de Windows.",
                "Le chemin du lecteur optique doit commencer par une lettre de lecteur Windows.",
                "Der Pfad des optischen Laufwerks muss mit einem Windows-Laufwerkbuchstaben beginnen.",
                "O caminho da unidade óptica deve começar com uma letra de unidade do Windows.",
                "Путь к оптическому приводу должен начинаться с буквы диска Windows.",
                "Il percorso dell'unità ottica deve iniziare con una lettera di unità Windows.",
                "Het pad van het optische station moet beginnen met een Windows-stationsletter.",
                "Ścieżka napędu optycznego musi zaczynać się od litery dysku systemu Windows.",
                "Optik sürücü yolu bir Windows sürücü harfiyle başlamalıdır.",
                "يجب أن يبدأ مسار محرك الأقراص الضوئية بحرف محرك أقراص Windows."
            ]),
            new("CoreOpticalDriveRootRequired",
            [
                "The optical-drive path must be the drive root.",
                "光驱路径必须是驱动器根目录。",
                "光碟機路徑必須是磁碟機根目錄。",
                "光学ドライブのパスはドライブのルートである必要があります。",
                "광학 드라이브 경로는 드라이브 루트여야 합니다.",
                "La ruta de la unidad óptica debe ser la raíz de la unidad.",
                "Le chemin du lecteur optique doit être la racine du lecteur.",
                "Der Pfad des optischen Laufwerks muss auf das Stammverzeichnis verweisen.",
                "O caminho da unidade óptica deve ser a raiz da unidade.",
                "Путь к оптическому приводу должен указывать на корень диска.",
                "Il percorso dell'unità ottica deve essere la radice dell'unità.",
                "Het pad van het optische station moet naar de hoofdmap van het station verwijzen.",
                "Ścieżka napędu optycznego musi wskazywać katalog główny dysku.",
                "Optik sürücü yolu sürücünün kök dizini olmalıdır.",
                "يجب أن يكون مسار محرك الأقراص الضوئية هو جذر محرك الأقراص."
            ])
        ]);
}

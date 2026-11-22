namespace FormatFactoryWinUI.Services;

internal static class GeneralServiceLocalizationResources
{
    internal static LocalizedResourceModule Module { get; } = new(
        "GeneralService",
        [
            new("GeneralServiceAppPathUnavailable",
            [
                "Unable to determine the application path.", "无法确定应用程序路径。", "無法判斷應用程式路徑。", "アプリケーションのパスを特定できません。", "애플리케이션 경로를 확인할 수 없습니다.",
                "No se puede determinar la ruta de la aplicación.", "Impossible de déterminer le chemin de l’application.", "Der Anwendungspfad konnte nicht ermittelt werden.", "Não foi possível determinar o caminho do aplicativo.", "Не удалось определить путь к приложению.",
                "Impossibile determinare il percorso dell’applicazione.", "Het toepassingspad kan niet worden bepaald.", "Nie można ustalić ścieżki aplikacji.", "Uygulama yolu belirlenemedi.", "تعذر تحديد مسار التطبيق."
            ]),
            new("GeneralServiceCannotDetermineMediaType",
            [
                "Unable to determine the media type.", "无法确定媒体类型。", "無法判斷媒體類型。", "メディアの種類を特定できません。", "미디어 형식을 확인할 수 없습니다.",
                "No se puede determinar el tipo de medio.", "Impossible de déterminer le type de média.", "Der Medientyp konnte nicht ermittelt werden.", "Não foi possível determinar o tipo de mídia.", "Не удалось определить тип медиафайла.",
                "Impossibile determinare il tipo di contenuto multimediale.", "Het mediatype kan niet worden bepaald.", "Nie można określić typu multimediów.", "Medya türü belirlenemedi.", "تعذر تحديد نوع الوسائط."
            ]),
            new("GeneralServiceCannotWatchFolder",
            [
                "Unable to monitor folder: {0}", "无法监视文件夹：{0}", "無法監視資料夾：{0}", "フォルダーを監視できません：{0}", "폴더를 모니터링할 수 없습니다: {0}",
                "No se puede supervisar la carpeta: {0}", "Impossible de surveiller le dossier : {0}", "Der Ordner kann nicht überwacht werden: {0}", "Não foi possível monitorar a pasta: {0}", "Не удалось отслеживать папку: {0}",
                "Impossibile monitorare la cartella: {0}", "De map kan niet worden bewaakt: {0}", "Nie można monitorować folderu: {0}", "Klasör izlenemiyor: {0}", "تعذرت مراقبة المجلد: {0}"
            ]),
            new("GeneralServiceClipboardImportTitle",
            [
                "Clipboard import · {0}", "剪贴板导入 · {0}", "剪貼簿匯入 · {0}", "クリップボードからインポート · {0}", "클립보드 가져오기 · {0}",
                "Importación del portapapeles · {0}", "Importation du Presse-papiers · {0}", "Zwischenablageimport · {0}", "Importação da área de transferência · {0}", "Импорт из буфера обмена · {0}",
                "Importazione dagli Appunti · {0}", "Importeren vanaf klembord · {0}", "Import ze schowka · {0}", "Panodan içe aktarma · {0}", "استيراد من الحافظة · {0}"
            ]),
            new("GeneralServiceClipboardNoText",
            [
                "The clipboard document has no text to import.", "剪贴板文档没有可导入的文本内容。", "剪貼簿文件沒有可匯入的文字內容。", "クリップボードのドキュメントにインポートできるテキストがありません。", "클립보드 문서에 가져올 텍스트가 없습니다.",
                "El documento del portapapeles no contiene texto para importar.", "Le document du Presse-papiers ne contient aucun texte à importer.", "Das Zwischenablagedokument enthält keinen importierbaren Text.", "O documento da área de transferência não contém texto para importar.", "В документе из буфера обмена нет текста для импорта.",
                "Il documento negli Appunti non contiene testo da importare.", "Het klemborddocument bevat geen tekst om te importeren.", "Dokument w schowku nie zawiera tekstu do zaimportowania.", "Pano belgesinde içe aktarılacak metin yok.", "لا يحتوي مستند الحافظة على نص يمكن استيراده."
            ]),
            new("GeneralServiceClipboardTextSource",
            [
                "Text", "文本", "文字", "テキスト", "텍스트", "Texto", "Texte", "Text", "Texto", "Текст", "Testo", "Tekst", "Tekst", "Metin", "نص"
            ]),
            new("GeneralServiceDirectoryWatchInvalidOffset",
            [
                "ReadDirectoryChangesW returned an invalid record offset.", "ReadDirectoryChangesW 返回了无效的记录偏移。", "ReadDirectoryChangesW 傳回了無效的記錄位移。", "ReadDirectoryChangesW が無効なレコードオフセットを返しました。", "ReadDirectoryChangesW가 잘못된 레코드 오프셋을 반환했습니다.",
                "ReadDirectoryChangesW devolvió un desplazamiento de registro no válido.", "ReadDirectoryChangesW a renvoyé un décalage d’enregistrement non valide.", "ReadDirectoryChangesW hat einen ungültigen Datensatzoffset zurückgegeben.", "ReadDirectoryChangesW retornou um deslocamento de registro inválido.", "ReadDirectoryChangesW вернула недопустимое смещение записи.",
                "ReadDirectoryChangesW ha restituito un offset di record non valido.", "ReadDirectoryChangesW heeft een ongeldige recordoffset geretourneerd.", "Funkcja ReadDirectoryChangesW zwróciła nieprawidłowe przesunięcie rekordu.", "ReadDirectoryChangesW geçersiz bir kayıt uzaklığı döndürdü.", "أرجعت ReadDirectoryChangesW إزاحة سجل غير صالحة."
            ]),
            new("GeneralServiceDirectoryWatchInvalidRecord",
            [
                "ReadDirectoryChangesW returned an invalid record.", "ReadDirectoryChangesW 返回了无效的记录。", "ReadDirectoryChangesW 傳回了無效的記錄。", "ReadDirectoryChangesW が無効なレコードを返しました。", "ReadDirectoryChangesW가 잘못된 레코드를 반환했습니다.",
                "ReadDirectoryChangesW devolvió un registro no válido.", "ReadDirectoryChangesW a renvoyé un enregistrement non valide.", "ReadDirectoryChangesW hat einen ungültigen Datensatz zurückgegeben.", "ReadDirectoryChangesW retornou um registro inválido.", "ReadDirectoryChangesW вернула недопустимую запись.",
                "ReadDirectoryChangesW ha restituito un record non valido.", "ReadDirectoryChangesW heeft een ongeldig record geretourneerd.", "Funkcja ReadDirectoryChangesW zwróciła nieprawidłowy rekord.", "ReadDirectoryChangesW geçersiz bir kayıt döndürdü.", "أرجعت ReadDirectoryChangesW سجلًا غير صالح."
            ]),
            new("GeneralServiceDirectoryWatchReadFailed",
            [
                "ReadDirectoryChangesW could not read directory changes.", "ReadDirectoryChangesW 未能读取目录变更。", "ReadDirectoryChangesW 無法讀取目錄變更。", "ReadDirectoryChangesW はディレクトリの変更を読み取れませんでした。", "ReadDirectoryChangesW가 디렉터리 변경 내용을 읽지 못했습니다.",
                "ReadDirectoryChangesW no pudo leer los cambios del directorio.", "ReadDirectoryChangesW n’a pas pu lire les modifications du dossier.", "ReadDirectoryChangesW konnte die Verzeichnisänderungen nicht lesen.", "ReadDirectoryChangesW não conseguiu ler as alterações do diretório.", "ReadDirectoryChangesW не удалось прочитать изменения каталога.",
                "ReadDirectoryChangesW non è riuscito a leggere le modifiche della cartella.", "ReadDirectoryChangesW kon mapwijzigingen niet lezen.", "Funkcja ReadDirectoryChangesW nie mogła odczytać zmian katalogu.", "ReadDirectoryChangesW dizin değişikliklerini okuyamadı.", "تعذرت على ReadDirectoryChangesW قراءة تغييرات المجلد."
            ]),
            new("GeneralServiceDirectoryWatchStartFailed",
            [
                "ReadDirectoryChangesW could not start.", "ReadDirectoryChangesW 无法启动。", "ReadDirectoryChangesW 無法啟動。", "ReadDirectoryChangesW を開始できませんでした。", "ReadDirectoryChangesW를 시작할 수 없습니다.",
                "No se pudo iniciar ReadDirectoryChangesW.", "Impossible de démarrer ReadDirectoryChangesW.", "ReadDirectoryChangesW konnte nicht gestartet werden.", "Não foi possível iniciar ReadDirectoryChangesW.", "Не удалось запустить ReadDirectoryChangesW.",
                "Impossibile avviare ReadDirectoryChangesW.", "ReadDirectoryChangesW kan niet worden gestart.", "Nie można uruchomić funkcji ReadDirectoryChangesW.", "ReadDirectoryChangesW başlatılamadı.", "تعذر بدء ReadDirectoryChangesW."
            ]),
            new("GeneralServiceFfmpegCommandFailed",
            [
                "FFmpeg command failed: {0}", "FFmpeg 命令失败：{0}", "FFmpeg 命令失敗：{0}", "FFmpeg コマンドに失敗しました：{0}", "FFmpeg 명령이 실패했습니다: {0}",
                "Error en el comando de FFmpeg: {0}", "Échec de la commande FFmpeg : {0}", "FFmpeg-Befehl fehlgeschlagen: {0}", "Falha no comando do FFmpeg: {0}", "Команда FFmpeg завершилась ошибкой: {0}",
                "Comando FFmpeg non riuscito: {0}", "FFmpeg-opdracht mislukt: {0}", "Polecenie FFmpeg nie powiodło się: {0}", "FFmpeg komutu başarısız oldu: {0}", "فشل أمر FFmpeg: {0}"
            ]),
            new("GeneralServiceFfmpegMissingMuxer",
            [
                "This FFmpeg build does not include the required output muxer: {0}", "当前 FFmpeg 构建缺少所需的输出封装器：{0}", "目前的 FFmpeg 組建缺少必要的輸出多工器：{0}", "この FFmpeg ビルドには必要な出力マルチプレクサーがありません：{0}", "현재 FFmpeg 빌드에 필요한 출력 먹서가 없습니다: {0}",
                "Esta compilación de FFmpeg no incluye el multiplexor de salida necesario: {0}", "Cette version de FFmpeg ne contient pas le multiplexeur de sortie requis : {0}", "Dieser FFmpeg-Build enthält den erforderlichen Ausgabe-Muxer nicht: {0}", "Esta compilação do FFmpeg não inclui o multiplexador de saída necessário: {0}", "В этой сборке FFmpeg отсутствует требуемый выходной мультиплексор: {0}",
                "Questa build di FFmpeg non include il muxer di output richiesto: {0}", "Deze FFmpeg-build bevat niet de vereiste uitvoermuxer: {0}", "Ta kompilacja FFmpeg nie zawiera wymaganego multipleksera wyjściowego: {0}", "Bu FFmpeg derlemesinde gerekli çıkış çoklayıcısı yok: {0}", "لا تتضمن بنية FFmpeg هذه مُضاعِف الإخراج المطلوب: {0}"
            ]),
            new("GeneralServiceFfprobeCannotRead",
            [
                "FFprobe cannot read this file.", "FFprobe 无法读取该文件。", "FFprobe 無法讀取此檔案。", "FFprobe はこのファイルを読み取れません。", "FFprobe가 이 파일을 읽을 수 없습니다.",
                "FFprobe no puede leer este archivo.", "FFprobe ne peut pas lire ce fichier.", "FFprobe kann diese Datei nicht lesen.", "O FFprobe não consegue ler este arquivo.", "FFprobe не может прочитать этот файл.",
                "FFprobe non può leggere questo file.", "FFprobe kan dit bestand niet lezen.", "FFprobe nie może odczytać tego pliku.", "FFprobe bu dosyayı okuyamıyor.", "يتعذر على FFprobe قراءة هذا الملف."
            ]),
            new("GeneralServiceNoRecognizableMediaStreams",
            [
                "The file contains no recognizable media streams. An M4S file may be missing initialization data.", "文件中没有可识别的媒体流。M4S 文件可能缺少初始化信息。", "檔案中沒有可識別的媒體串流。M4S 檔案可能缺少初始化資料。", "ファイルに認識可能なメディアストリームがありません。M4S ファイルには初期化データがない可能性があります。", "파일에 인식 가능한 미디어 스트림이 없습니다. M4S 파일에 초기화 데이터가 없을 수 있습니다.",
                "El archivo no contiene secuencias multimedia reconocibles. Es posible que a un archivo M4S le falten datos de inicialización.", "Le fichier ne contient aucun flux multimédia reconnaissable. Il peut manquer des données d’initialisation au fichier M4S.", "Die Datei enthält keine erkennbaren Medienstreams. Einer M4S-Datei fehlen möglicherweise Initialisierungsdaten.", "O arquivo não contém fluxos de mídia reconhecíveis. Um arquivo M4S pode não ter dados de inicialização.", "Файл не содержит распознаваемых медиапотоков. В файле M4S могут отсутствовать данные инициализации.",
                "Il file non contiene flussi multimediali riconoscibili. In un file M4S potrebbero mancare i dati di inizializzazione.", "Het bestand bevat geen herkenbare mediastreams. In een M4S-bestand ontbreken mogelijk initialisatiegegevens.", "Plik nie zawiera rozpoznawalnych strumieni multimedialnych. W pliku M4S może brakować danych inicjalizacyjnych.", "Dosyada tanınabilir medya akışı yok. M4S dosyasında başlatma verileri eksik olabilir.", "لا يحتوي الملف على تدفقات وسائط يمكن التعرف عليها. قد يفتقد ملف M4S إلى بيانات التهيئة."
            ]),
            new("GeneralServiceOutputNoMediaStream",
            [
                "Output validation failed because no valid media stream was found.", "输出文件校验失败，未发现有效媒体流。", "輸出檔案驗證失敗，找不到有效的媒體串流。", "有効なメディアストリームが見つからなかったため、出力の検証に失敗しました。", "유효한 미디어 스트림을 찾지 못해 출력 검증에 실패했습니다.",
                "La validación de la salida falló porque no se encontró ninguna secuencia multimedia válida.", "La validation de la sortie a échoué, car aucun flux multimédia valide n’a été trouvé.", "Die Ausgabeprüfung ist fehlgeschlagen, da kein gültiger Medienstream gefunden wurde.", "A validação da saída falhou porque nenhum fluxo de mídia válido foi encontrado.", "Проверка выходного файла не пройдена: допустимые медиапотоки не найдены.",
                "La convalida dell’output non è riuscita perché non è stato trovato alcun flusso multimediale valido.", "De uitvoervalidatie is mislukt omdat geen geldige mediastream is gevonden.", "Weryfikacja pliku wyjściowego nie powiodła się, ponieważ nie znaleziono prawidłowego strumienia multimedialnego.", "Geçerli bir medya akışı bulunamadığından çıkış doğrulaması başarısız oldu.", "فشل التحقق من الإخراج لعدم العثور على تدفق وسائط صالح."
            ]),
            new("GeneralServicePowerConversionTask",
            [
                "FluentFormatConverter is running conversion tasks", "FluentFormatConverter 正在执行转换任务", "FluentFormatConverter 正在執行轉換工作", "FluentFormatConverter が変換タスクを実行しています", "FluentFormatConverter에서 변환 작업을 실행 중입니다",
                "FluentFormatConverter está ejecutando tareas de conversión", "FluentFormatConverter exécute des tâches de conversion", "FluentFormatConverter führt Konvertierungsaufgaben aus", "O FluentFormatConverter está executando tarefas de conversão", "FluentFormatConverter выполняет задачи преобразования",
                "FluentFormatConverter sta eseguendo attività di conversione", "FluentFormatConverter voert conversietaken uit", "FluentFormatConverter wykonuje zadania konwersji", "FluentFormatConverter dönüştürme görevlerini çalıştırıyor", "ينفذ FluentFormatConverter مهام التحويل"
            ]),
            new("GeneralServicePowerMediaConversion",
            [
                "FluentFormatConverter is converting media", "FluentFormatConverter 正在转换媒体", "FluentFormatConverter 正在轉換媒體", "FluentFormatConverter がメディアを変換しています", "FluentFormatConverter에서 미디어를 변환 중입니다",
                "FluentFormatConverter está convirtiendo contenido multimedia", "FluentFormatConverter convertit des médias", "FluentFormatConverter konvertiert Medien", "O FluentFormatConverter está convertendo mídia", "FluentFormatConverter преобразует медиафайл",
                "FluentFormatConverter sta convertendo contenuti multimediali", "FluentFormatConverter converteert media", "FluentFormatConverter konwertuje multimedia", "FluentFormatConverter medyayı dönüştürüyor", "يحوّل FluentFormatConverter الوسائط"
            ]),
            new("GeneralServiceProbeTimedOut",
            [
                "Probe timed out", "探测超时", "偵測逾時", "検出がタイムアウトしました", "검색 시간이 초과되었습니다",
                "Se agotó el tiempo de detección", "Le délai de détection a expiré", "Zeitüberschreitung bei der Erkennung", "A detecção atingiu o tempo limite", "Время ожидания проверки истекло",
                "Tempo scaduto per il rilevamento", "Time-out bij detectie", "Upłynął limit czasu wykrywania", "Algılama zaman aşımına uğradı", "انتهت مهلة الفحص"
            ]),
            new("GeneralServiceProcessPipesTimeout",
            [
                "The external process exited, but its output pipes did not close within 5 seconds: {0}", "外部进程已经退出，但其输出管道在 5 秒内未关闭：{0}", "外部處理程序已結束，但其輸出管道未在 5 秒內關閉：{0}", "外部プロセスは終了しましたが、出力パイプが 5 秒以内に閉じませんでした：{0}", "외부 프로세스가 종료되었지만 출력 파이프가 5초 안에 닫히지 않았습니다: {0}",
                "El proceso externo terminó, pero sus canalizaciones de salida no se cerraron en 5 segundos: {0}", "Le processus externe s’est arrêté, mais ses canaux de sortie ne se sont pas fermés dans les 5 secondes : {0}", "Der externe Prozess wurde beendet, seine Ausgabepipes wurden jedoch nicht innerhalb von 5 Sekunden geschlossen: {0}", "O processo externo foi encerrado, mas seus pipes de saída não fecharam em 5 segundos: {0}", "Внешний процесс завершился, но его каналы вывода не закрылись за 5 секунд: {0}",
                "Il processo esterno è terminato, ma le relative pipe di output non si sono chiuse entro 5 secondi: {0}", "Het externe proces is afgesloten, maar de uitvoerpijpen zijn niet binnen 5 seconden gesloten: {0}", "Proces zewnętrzny zakończył działanie, ale jego potoki wyjściowe nie zostały zamknięte w ciągu 5 sekund: {0}", "Dış işlem sonlandı ancak çıkış kanalları 5 saniye içinde kapanmadı: {0}", "انتهت العملية الخارجية، لكن قنوات إخراجها لم تُغلق خلال 5 ثوانٍ: {0}"
            ]),
            new("GeneralServiceProcessTimeout",
            [
                "The external process exceeded {0:0.#} seconds and was terminated: {1}", "外部进程执行超过 {0:0.#} 秒，已终止：{1}", "外部處理程序執行超過 {0:0.#} 秒，已終止：{1}", "外部プロセスが {0:0.#} 秒を超えたため終了しました：{1}", "외부 프로세스가 {0:0.#}초를 초과하여 종료되었습니다: {1}",
                "El proceso externo superó {0:0.#} segundos y se terminó: {1}", "Le processus externe a dépassé {0:0.#} secondes et a été arrêté : {1}", "Der externe Prozess überschritt {0:0.#} Sekunden und wurde beendet: {1}", "O processo externo excedeu {0:0.#} segundos e foi encerrado: {1}", "Внешний процесс работал более {0:0.#} с и был завершён: {1}",
                "Il processo esterno ha superato {0:0.#} secondi ed è stato terminato: {1}", "Het externe proces duurde langer dan {0:0.#} seconden en is beëindigd: {1}", "Proces zewnętrzny przekroczył {0:0.#} s i został zakończony: {1}", "Dış işlem {0:0.#} saniyeyi aştı ve sonlandırıldı: {1}", "تجاوزت العملية الخارجية {0:0.#} ثانية وتم إنهاؤها: {1}"
            ]),
            new("GeneralServiceQueueInvalidRoot",
            [
                "The queue file must contain a legacy array or a v2 object.", "队列文件必须包含旧版数组或 v2 对象。", "佇列檔案必須包含舊版陣列或 v2 物件。", "キューファイルには従来の配列または v2 オブジェクトが必要です。", "대기열 파일에는 레거시 배열 또는 v2 개체가 있어야 합니다.",
                "El archivo de cola debe contener una matriz heredada o un objeto v2.", "Le fichier de file d’attente doit contenir un tableau hérité ou un objet v2.", "Die Warteschlangendatei muss ein Legacy-Array oder ein v2-Objekt enthalten.", "O arquivo da fila deve conter uma matriz legada ou um objeto v2.", "Файл очереди должен содержать устаревший массив или объект v2.",
                "Il file della coda deve contenere un array legacy o un oggetto v2.", "Het wachtrijbestand moet een verouderde matrix of een v2-object bevatten.", "Plik kolejki musi zawierać starszą tablicę albo obiekt v2.", "Kuyruk dosyası eski bir dizi veya v2 nesnesi içermelidir.", "يجب أن يحتوي ملف قائمة الانتظار على مصفوفة قديمة أو كائن v2."
            ]),
            new("GeneralServiceQueueInvalidStructure",
            [
                "The queue file structure is invalid.", "队列文件结构无效。", "佇列檔案結構無效。", "キューファイルの構造が無効です。", "대기열 파일 구조가 잘못되었습니다.",
                "La estructura del archivo de cola no es válida.", "La structure du fichier de file d’attente n’est pas valide.", "Die Struktur der Warteschlangendatei ist ungültig.", "A estrutura do arquivo da fila é inválida.", "Недопустимая структура файла очереди.",
                "La struttura del file della coda non è valida.", "De structuur van het wachtrijbestand is ongeldig.", "Struktura pliku kolejki jest nieprawidłowa.", "Kuyruk dosyasının yapısı geçersiz.", "بنية ملف قائمة الانتظار غير صالحة."
            ]),
            new("GeneralServiceQueueLockTimeout",
            [
                "Timed out waiting for the queue file lock: {0}", "等待队列文件锁超时：{0}", "等候佇列檔案鎖定逾時：{0}", "キューファイルのロック待機がタイムアウトしました：{0}", "대기열 파일 잠금을 기다리는 동안 시간이 초과되었습니다: {0}",
                "Se agotó el tiempo de espera del bloqueo del archivo de cola: {0}", "Le délai d’attente du verrou du fichier de file d’attente a expiré : {0}", "Zeitüberschreitung beim Warten auf die Sperre der Warteschlangendatei: {0}", "Tempo esgotado ao aguardar o bloqueio do arquivo da fila: {0}", "Истекло время ожидания блокировки файла очереди: {0}",
                "Tempo scaduto in attesa del blocco del file della coda: {0}", "Time-out bij het wachten op de vergrendeling van het wachtrijbestand: {0}", "Upłynął limit oczekiwania na blokadę pliku kolejki: {0}", "Kuyruk dosyası kilidi beklenirken zaman aşımı oluştu: {0}", "انتهت مهلة انتظار قفل ملف قائمة الانتظار: {0}"
            ]),
            new("GeneralServiceQueueUnsupportedVersion",
            [
                "Queue schema version {0} is not supported.", "不支持队列架构版本 {0}。", "不支援佇列結構描述版本 {0}。", "キュースキーマのバージョン {0} はサポートされていません。", "대기열 스키마 버전 {0}은 지원되지 않습니다.",
                "La versión {0} del esquema de cola no es compatible.", "La version {0} du schéma de file d’attente n’est pas prise en charge.", "Die Warteschlangenschemaversion {0} wird nicht unterstützt.", "A versão {0} do esquema da fila não é compatível.", "Версия схемы очереди {0} не поддерживается.",
                "La versione {0} dello schema della coda non è supportata.", "Wachtrijschemaversie {0} wordt niet ondersteund.", "Wersja {0} schematu kolejki nie jest obsługiwana.", "Kuyruk şeması sürüm {0} desteklenmiyor.", "إصدار مخطط قائمة الانتظار {0} غير مدعوم."
            ]),
            new("GeneralServiceSettingsLockTimeout",
            [
                "Timed out waiting for the settings file lock: {0}", "等待设置文件锁超时：{0}", "等候設定檔案鎖定逾時：{0}", "設定ファイルのロック待機がタイムアウトしました：{0}", "설정 파일 잠금을 기다리는 동안 시간이 초과되었습니다: {0}",
                "Se agotó el tiempo de espera del bloqueo del archivo de configuración: {0}", "Le délai d’attente du verrou du fichier de paramètres a expiré : {0}", "Zeitüberschreitung beim Warten auf die Sperre der Einstellungsdatei: {0}", "Tempo esgotado ao aguardar o bloqueio do arquivo de configurações: {0}", "Истекло время ожидания блокировки файла настроек: {0}",
                "Tempo scaduto in attesa del blocco del file delle impostazioni: {0}", "Time-out bij het wachten op de vergrendeling van het instellingenbestand: {0}", "Upłynął limit oczekiwania na blokadę pliku ustawień: {0}", "Ayarlar dosyası kilidi beklenirken zaman aşımı oluştu: {0}", "انتهت مهلة انتظار قفل ملف الإعدادات: {0}"
            ]),
            new("GeneralServiceSingleInputOnly",
            [
                "The FFmpeg media engine currently accepts only one input at a time.", "FFmpeg 媒体引擎当前一次只接受一个输入。", "FFmpeg 媒體引擎目前一次只接受一個輸入。", "FFmpeg メディアエンジンは現在、一度に 1 つの入力のみ受け付けます。", "FFmpeg 미디어 엔진은 현재 한 번에 하나의 입력만 허용합니다.",
                "El motor multimedia FFmpeg solo acepta una entrada a la vez actualmente.", "Le moteur multimédia FFmpeg n’accepte actuellement qu’une seule entrée à la fois.", "Die FFmpeg-Medienengine akzeptiert derzeit nur eine Eingabe gleichzeitig.", "O mecanismo de mídia FFmpeg aceita apenas uma entrada por vez no momento.", "Медиадвижок FFmpeg сейчас принимает только один входной файл за раз.",
                "Il motore multimediale FFmpeg accetta attualmente un solo input alla volta.", "De FFmpeg-media-engine accepteert momenteel slechts één invoer tegelijk.", "Mechanizm multimedialny FFmpeg przyjmuje obecnie tylko jedno wejście naraz.", "FFmpeg medya motoru şu anda aynı anda yalnızca bir giriş kabul ediyor.", "يقبل محرك وسائط FFmpeg حاليًا إدخالًا واحدًا فقط في كل مرة."
            ]),
            new("GeneralServiceStartupRegistryUnavailable",
            [
                "Unable to access the Windows startup entry.", "无法访问 Windows 开机启动项。", "無法存取 Windows 開機啟動項目。", "Windows のスタートアップ項目にアクセスできません。", "Windows 시작 항목에 액세스할 수 없습니다.",
                "No se puede acceder a la entrada de inicio de Windows.", "Impossible d’accéder à l’entrée de démarrage de Windows.", "Auf den Windows-Autostarteintrag kann nicht zugegriffen werden.", "Não foi possível acessar a entrada de inicialização do Windows.", "Не удалось получить доступ к элементу автозапуска Windows.",
                "Impossibile accedere alla voce di avvio di Windows.", "Het Windows-opstartitem is niet toegankelijk.", "Nie można uzyskać dostępu do wpisu autostartu systemu Windows.", "Windows başlangıç girdisine erişilemiyor.", "تعذر الوصول إلى عنصر بدء تشغيل Windows."
            ]),
            new("GeneralServiceStartupTaskNotEnabled",
            [
                "The Windows startup task was not enabled: {0}", "Windows 启动任务未启用：{0}", "Windows 啟動工作未啟用：{0}", "Windows スタートアップタスクが有効になりませんでした：{0}", "Windows 시작 작업이 사용 설정되지 않았습니다: {0}",
                "La tarea de inicio de Windows no se habilitó: {0}", "La tâche de démarrage Windows n’a pas été activée : {0}", "Die Windows-Autostartaufgabe wurde nicht aktiviert: {0}", "A tarefa de inicialização do Windows não foi habilitada: {0}", "Задача автозапуска Windows не включена: {0}",
                "L’attività di avvio di Windows non è stata abilitata: {0}", "De Windows-opstarttaak is niet ingeschakeld: {0}", "Zadanie startowe systemu Windows nie zostało włączone: {0}", "Windows başlangıç görevi etkinleştirilmedi: {0}", "لم يتم تمكين مهمة بدء تشغيل Windows: {0}"
            ]),
            new("GeneralServiceTimeoutPositive",
            [
                "The process timeout must be greater than zero.", "进程超时时间必须大于零。", "處理程序逾時時間必須大於零。", "プロセスのタイムアウトは 0 より大きい必要があります。", "프로세스 시간 제한은 0보다 커야 합니다.",
                "El tiempo de espera del proceso debe ser mayor que cero.", "Le délai d’expiration du processus doit être supérieur à zéro.", "Das Prozesstimeout muss größer als null sein.", "O tempo limite do processo deve ser maior que zero.", "Время ожидания процесса должно быть больше нуля.",
                "Il timeout del processo deve essere maggiore di zero.", "De procestime-out moet groter zijn dan nul.", "Limit czasu procesu musi być większy od zera.", "İşlem zaman aşımı sıfırdan büyük olmalıdır.", "يجب أن تكون مهلة العملية أكبر من صفر."
            ]),
            new("GeneralServiceUnsupportedAutoDiscoveryArchitecture",
            [
                "The current process architecture {0} is not supported by FFmpeg automatic discovery.", "FFmpeg 自动发现不支持当前进程架构 {0}。", "FFmpeg 自動探索不支援目前的處理程序架構 {0}。", "現在のプロセスアーキテクチャ {0} は FFmpeg の自動検出でサポートされていません。", "현재 프로세스 아키텍처 {0}은 FFmpeg 자동 검색에서 지원되지 않습니다.",
                "La arquitectura de proceso actual {0} no es compatible con la detección automática de FFmpeg.", "L’architecture de processus actuelle {0} n’est pas prise en charge par la détection automatique de FFmpeg.", "Die aktuelle Prozessarchitektur {0} wird von der automatischen FFmpeg-Erkennung nicht unterstützt.", "A arquitetura de processo atual {0} não é compatível com a descoberta automática do FFmpeg.", "Текущая архитектура процесса {0} не поддерживается автоматическим поиском FFmpeg.",
                "L’architettura di processo corrente {0} non è supportata dal rilevamento automatico di FFmpeg.", "De huidige procesarchitectuur {0} wordt niet ondersteund door automatische FFmpeg-detectie.", "Bieżąca architektura procesu {0} nie jest obsługiwana przez automatyczne wykrywanie FFmpeg.", "Geçerli işlem mimarisi {0}, FFmpeg otomatik bulma tarafından desteklenmiyor.", "لا يدعم الاكتشاف التلقائي لـ FFmpeg بنية العملية الحالية {0}."
            ]),
            new("GeneralServiceUnsupportedRoute",
            [
                "FFmpeg does not support the route {0} → {1}.", "FFmpeg 不支持转换路线 {0} → {1}。", "FFmpeg 不支援轉換路徑 {0} → {1}。", "FFmpeg は変換ルート {0} → {1} をサポートしていません。", "FFmpeg는 {0} → {1} 변환 경로를 지원하지 않습니다.",
                "FFmpeg no admite la ruta {0} → {1}.", "FFmpeg ne prend pas en charge le chemin {0} → {1}.", "FFmpeg unterstützt die Route {0} → {1} nicht.", "O FFmpeg não aceita a rota {0} → {1}.", "FFmpeg не поддерживает маршрут {0} → {1}.",
                "FFmpeg non supporta il percorso {0} → {1}.", "FFmpeg ondersteunt de route {0} → {1} niet.", "FFmpeg nie obsługuje trasy {0} → {1}.", "FFmpeg {0} → {1} yolunu desteklemiyor.", "لا يدعم FFmpeg المسار {0} ← {1}."
            ]),
            new("GeneralServiceWatcherNotOpen",
            [
                "The directory monitor is not open.", "目录监视器尚未打开。", "目錄監視器尚未開啟。", "ディレクトリモニターが開かれていません。", "디렉터리 모니터가 열려 있지 않습니다.",
                "El monitor de directorio no está abierto.", "Le moniteur de dossier n’est pas ouvert.", "Die Verzeichnisüberwachung ist nicht geöffnet.", "O monitor de diretório não está aberto.", "Мониторинг каталога не открыт.",
                "Il monitor della cartella non è aperto.", "De mapmonitor is niet geopend.", "Monitor katalogu nie jest otwarty.", "Dizin izleyicisi açık değil.", "مراقب المجلد غير مفتوح."
            ])
        ]);
}

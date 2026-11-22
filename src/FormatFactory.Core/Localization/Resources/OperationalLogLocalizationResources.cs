namespace FormatFactoryWinUI.Services;

internal static class OperationalLogLocalizationResources
{
    internal static LocalizedResourceModule Module { get; } = new(
        "OperationalLog",
        [
            new("OperationalLogWatchFolderStarted",
            [
                "Folder monitoring started: {0}", "已启动监视文件夹：{0}", "已開始監看資料夾：{0}", "フォルダーの監視を開始しました：{0}", "폴더 모니터링을 시작했습니다: {0}",
                "Se inició la supervisión de la carpeta: {0}", "La surveillance du dossier a démarré : {0}", "Die Ordnerüberwachung wurde gestartet: {0}", "O monitoramento da pasta foi iniciado: {0}", "Наблюдение за папкой запущено: {0}",
                "Il monitoraggio della cartella è stato avviato: {0}", "Mapbewaking is gestart: {0}", "Rozpoczęto monitorowanie folderu: {0}", "Klasör izleme başlatıldı: {0}", "بدأت مراقبة المجلد: {0}"
            ]),
            new("OperationalLogWatchFolderImportFailed",
            [
                "Failed to import from monitored folder ({0}): {1}", "监视文件夹导入失败（{0}）：{1}", "監看資料夾匯入失敗（{0}）：{1}", "監視フォルダーからのインポートに失敗しました（{0}）：{1}", "모니터링 폴더에서 가져오지 못했습니다({0}): {1}",
                "No se pudo importar desde la carpeta supervisada ({0}): {1}", "Échec de l’importation depuis le dossier surveillé ({0}) : {1}", "Import aus dem überwachten Ordner fehlgeschlagen ({0}): {1}", "Falha ao importar da pasta monitorada ({0}): {1}", "Не удалось импортировать из отслеживаемой папки ({0}): {1}",
                "Importazione dalla cartella monitorata non riuscita ({0}): {1}", "Importeren uit de bewaakte map is mislukt ({0}): {1}", "Nie udało się zaimportować z monitorowanego folderu ({0}): {1}", "İzlenen klasörden içe aktarma başarısız oldu ({0}): {1}", "فشل الاستيراد من المجلد المراقَب ({0}): {1}"
            ]),
            new("OperationalLogExplorerBatchReadFailed",
            [
                "Failed to read the Explorer file batch: {0}", "读取资源管理器文件批次失败：{0}", "讀取檔案總管檔案批次失敗：{0}", "エクスプローラーのファイル バッチを読み取れませんでした：{0}", "파일 탐색기 파일 일괄 항목을 읽지 못했습니다: {0}",
                "No se pudo leer el lote de archivos del Explorador: {0}", "Échec de la lecture du lot de fichiers de l’Explorateur : {0}", "Explorer-Dateistapel konnte nicht gelesen werden: {0}", "Falha ao ler o lote de arquivos do Explorador: {0}", "Не удалось прочитать пакет файлов Проводника: {0}",
                "Impossibile leggere il gruppo di file di Esplora file: {0}", "De bestandsbatch van Verkenner kan niet worden gelezen: {0}", "Nie udało się odczytać partii plików Eksploratora: {0}", "Dosya Gezgini dosya toplu işi okunamadı: {0}", "تعذرت قراءة دفعة ملفات مستكشف الملفات: {0}"
            ]),
            new("OperationalLogExplorerBatchCleanupFailed",
            [
                "Failed to clean up the Explorer file batch: {0}", "清理资源管理器文件批次失败：{0}", "清理檔案總管檔案批次失敗：{0}", "エクスプローラーのファイル バッチをクリーンアップできませんでした：{0}", "파일 탐색기 파일 일괄 항목을 정리하지 못했습니다: {0}",
                "No se pudo limpiar el lote de archivos del Explorador: {0}", "Échec du nettoyage du lot de fichiers de l’Explorateur : {0}", "Explorer-Dateistapel konnte nicht bereinigt werden: {0}", "Falha ao limpar o lote de arquivos do Explorador: {0}", "Не удалось очистить пакет файлов Проводника: {0}",
                "Impossibile eliminare il gruppo di file di Esplora file: {0}", "De bestandsbatch van Verkenner kan niet worden opgeschoond: {0}", "Nie udało się wyczyścić partii plików Eksploratora: {0}", "Dosya Gezgini dosya toplu işi temizlenemedi: {0}", "تعذر تنظيف دفعة ملفات مستكشف الملفات: {0}"
            ]),
            new("OperationalLogWindowsActivationArgumentsReadFailed",
            [
                "Failed to read the Windows activation arguments: {0}", "读取 Windows 激活参数失败：{0}", "讀取 Windows 啟用引數失敗：{0}", "Windows のアクティブ化引数を読み取れませんでした：{0}", "Windows 활성화 인수를 읽지 못했습니다: {0}",
                "No se pudieron leer los argumentos de activación de Windows: {0}", "Échec de la lecture des arguments d’activation Windows : {0}", "Windows-Aktivierungsargumente konnten nicht gelesen werden: {0}", "Falha ao ler os argumentos de ativação do Windows: {0}", "Не удалось прочитать параметры активации Windows: {0}",
                "Impossibile leggere gli argomenti di attivazione di Windows: {0}", "De Windows-activeringsargumenten kunnen niet worden gelezen: {0}", "Nie udało się odczytać argumentów aktywacji systemu Windows: {0}", "Windows etkinleştirme bağımsız değişkenleri okunamadı: {0}", "تعذرت قراءة وسيطات تنشيط Windows: {0}"
            ]),
            new("OperationalLogNotificationInitializationFailed",
            [
                "Failed to initialize Windows notifications: {0}", "初始化 Windows 通知失败：{0}", "初始化 Windows 通知失敗：{0}", "Windows 通知を初期化できませんでした：{0}", "Windows 알림을 초기화하지 못했습니다: {0}",
                "No se pudieron inicializar las notificaciones de Windows: {0}", "Échec de l’initialisation des notifications Windows : {0}", "Windows-Benachrichtigungen konnten nicht initialisiert werden: {0}", "Falha ao inicializar as notificações do Windows: {0}", "Не удалось инициализировать уведомления Windows: {0}",
                "Impossibile inizializzare le notifiche di Windows: {0}", "Windows-meldingen kunnen niet worden geïnitialiseerd: {0}", "Nie udało się zainicjować powiadomień systemu Windows: {0}", "Windows bildirimleri başlatılamadı: {0}", "تعذرت تهيئة إشعارات Windows: {0}"
            ]),
            new("OperationalLogNotificationShutdownFailed",
            [
                "Failed to shut down Windows notifications: {0}", "关闭 Windows 通知失败：{0}", "關閉 Windows 通知失敗：{0}", "Windows 通知を終了できませんでした：{0}", "Windows 알림을 종료하지 못했습니다: {0}",
                "No se pudieron cerrar las notificaciones de Windows: {0}", "Échec de l’arrêt des notifications Windows : {0}", "Windows-Benachrichtigungen konnten nicht beendet werden: {0}", "Falha ao encerrar as notificações do Windows: {0}", "Не удалось завершить работу уведомлений Windows: {0}",
                "Impossibile arrestare le notifiche di Windows: {0}", "Windows-meldingen kunnen niet worden afgesloten: {0}", "Nie udało się zamknąć powiadomień systemu Windows: {0}", "Windows bildirimleri kapatılamadı: {0}", "تعذر إيقاف إشعارات Windows: {0}"
            ]),
            new("OperationalLogNotificationSendFailed",
            [
                "Failed to send a Windows notification: {0}", "发送 Windows 通知失败：{0}", "傳送 Windows 通知失敗：{0}", "Windows 通知を送信できませんでした：{0}", "Windows 알림을 보내지 못했습니다: {0}",
                "No se pudo enviar una notificación de Windows: {0}", "Échec de l’envoi d’une notification Windows : {0}", "Windows-Benachrichtigung konnte nicht gesendet werden: {0}", "Falha ao enviar uma notificação do Windows: {0}", "Не удалось отправить уведомление Windows: {0}",
                "Impossibile inviare una notifica di Windows: {0}", "De Windows-melding kan niet worden verzonden: {0}", "Nie udało się wysłać powiadomienia systemu Windows: {0}", "Windows bildirimi gönderilemedi: {0}", "تعذر إرسال إشعار Windows: {0}"
            ]),
            new("OperationalLogNotificationActivationFailed",
            [
                "Failed to process Windows notification activation: {0}", "处理 Windows 通知激活失败：{0}", "處理 Windows 通知啟用失敗：{0}", "Windows 通知のアクティブ化を処理できませんでした：{0}", "Windows 알림 활성화를 처리하지 못했습니다: {0}",
                "No se pudo procesar la activación de la notificación de Windows: {0}", "Échec du traitement de l’activation de la notification Windows : {0}", "Aktivierung der Windows-Benachrichtigung konnte nicht verarbeitet werden: {0}", "Falha ao processar a ativação da notificação do Windows: {0}", "Не удалось обработать активацию уведомления Windows: {0}",
                "Impossibile elaborare l’attivazione della notifica di Windows: {0}", "Activering van de Windows-melding kan niet worden verwerkt: {0}", "Nie udało się przetworzyć aktywacji powiadomienia systemu Windows: {0}", "Windows bildirimi etkinleştirmesi işlenemedi: {0}", "تعذرت معالجة تنشيط إشعار Windows: {0}"
            ]),
            new("OperationalLogUnhandledException",
            [
                "Unhandled exception: {0}", "未处理的异常：{0}", "未處理的例外狀況：{0}", "未処理の例外：{0}", "처리되지 않은 예외: {0}",
                "Excepción no controlada: {0}", "Exception non gérée : {0}", "Unbehandelte Ausnahme: {0}", "Exceção não tratada: {0}", "Необработанное исключение: {0}",
                "Eccezione non gestita: {0}", "Onverwerkte uitzondering: {0}", "Nieobsłużony wyjątek: {0}", "İşlenmeyen özel durum: {0}", "استثناء غير معالج: {0}"
            ]),
            new("OperationalLogMsixBackgroundWatchRegistered",
            [
                "The MSIX background folder-monitoring task was registered.", "已注册 MSIX 后台文件夹监视任务。", "已註冊 MSIX 背景資料夾監看工作。", "MSIX バックグラウンド フォルダー監視タスクを登録しました。", "MSIX 백그라운드 폴더 모니터링 작업을 등록했습니다.",
                "Se registró la tarea de supervisión de carpetas en segundo plano de MSIX.", "La tâche MSIX de surveillance des dossiers en arrière-plan a été inscrite.", "Die MSIX-Hintergrundaufgabe zur Ordnerüberwachung wurde registriert.", "A tarefa MSIX de monitoramento de pasta em segundo plano foi registrada.", "Фоновая задача MSIX для наблюдения за папкой зарегистрирована.",
                "L’attività MSIX di monitoraggio cartelle in background è stata registrata.", "De MSIX-achtergrondtaak voor mapbewaking is geregistreerd.", "Zarejestrowano zadanie MSIX monitorujące folder w tle.", "MSIX arka plan klasör izleme görevi kaydedildi.", "تم تسجيل مهمة MSIX لمراقبة المجلد في الخلفية."
            ]),
            new("OperationalLogMsixBackgroundTaskRegistrationFailed",
            [
                "Failed to register the MSIX background task: {0}", "注册 MSIX 后台任务失败：{0}", "註冊 MSIX 背景工作失敗：{0}", "MSIX バックグラウンド タスクを登録できませんでした：{0}", "MSIX 백그라운드 작업을 등록하지 못했습니다: {0}",
                "No se pudo registrar la tarea en segundo plano de MSIX: {0}", "Échec de l’inscription de la tâche MSIX en arrière-plan : {0}", "MSIX-Hintergrundaufgabe konnte nicht registriert werden: {0}", "Falha ao registrar a tarefa MSIX em segundo plano: {0}", "Не удалось зарегистрировать фоновую задачу MSIX: {0}",
                "Impossibile registrare l’attività MSIX in background: {0}", "De MSIX-achtergrondtaak kan niet worden geregistreerd: {0}", "Nie udało się zarejestrować zadania MSIX w tle: {0}", "MSIX arka plan görevi kaydedilemedi: {0}", "تعذر تسجيل مهمة MSIX في الخلفية: {0}"
            ]),
            new("OperationalLogMsixBackgroundWatchFailed",
            [
                "The MSIX background folder-monitoring task failed: {0}", "MSIX 后台文件夹监视任务失败：{0}", "MSIX 背景資料夾監看工作失敗：{0}", "MSIX バックグラウンド フォルダー監視タスクに失敗しました：{0}", "MSIX 백그라운드 폴더 모니터링 작업이 실패했습니다: {0}",
                "Error en la tarea de supervisión de carpetas en segundo plano de MSIX: {0}", "Échec de la tâche MSIX de surveillance des dossiers en arrière-plan : {0}", "MSIX-Hintergrundaufgabe zur Ordnerüberwachung ist fehlgeschlagen: {0}", "Falha na tarefa MSIX de monitoramento de pasta em segundo plano: {0}", "Сбой фоновой задачи MSIX для наблюдения за папкой: {0}",
                "Attività MSIX di monitoraggio cartelle in background non riuscita: {0}", "De MSIX-achtergrondtaak voor mapbewaking is mislukt: {0}", "Zadanie MSIX monitorujące folder w tle nie powiodło się: {0}", "MSIX arka plan klasör izleme görevi başarısız oldu: {0}", "فشلت مهمة MSIX لمراقبة المجلد في الخلفية: {0}"
            ]),
            new("OperationalLogBackgroundWatchAccessTokenMissing",
            [
                "The background monitoring task has no folder access token; waiting for the user to select the folder again.", "后台监视任务缺少文件夹访问令牌，正在等待用户重新选择文件夹。", "背景監看工作缺少資料夾存取權杖，正在等待使用者重新選取資料夾。", "バックグラウンド監視タスクにフォルダー アクセス トークンがありません。ユーザーがフォルダーを再選択するのを待機します。", "백그라운드 모니터링 작업에 폴더 액세스 토큰이 없습니다. 사용자가 폴더를 다시 선택할 때까지 기다립니다.",
                "La tarea de supervisión en segundo plano no tiene un token de acceso a la carpeta; se espera a que el usuario vuelva a seleccionarla.", "La tâche de surveillance en arrière-plan ne dispose d’aucun jeton d’accès au dossier ; elle attend que l’utilisateur le sélectionne à nouveau.", "Der Hintergrundüberwachung fehlt ein Ordnerzugriffstoken; sie wartet darauf, dass der Benutzer den Ordner erneut auswählt.", "A tarefa de monitoramento em segundo plano não tem um token de acesso à pasta; aguardando o usuário selecionar a pasta novamente.", "У фоновой задачи наблюдения нет маркера доступа к папке; ожидается повторный выбор папки пользователем.",
                "L’attività di monitoraggio in background non dispone di un token di accesso alla cartella; è in attesa che l’utente la selezioni di nuovo.", "De achtergrondbewaking heeft geen toegangstoken voor de map en wacht tot de gebruiker de map opnieuw selecteert.", "Zadanie monitorowania w tle nie ma tokenu dostępu do folderu; oczekuje na ponowny wybór folderu przez użytkownika.", "Arka plan izleme görevinde klasör erişim belirteci yok; kullanıcının klasörü yeniden seçmesi bekleniyor.", "لا تحتوي مهمة المراقبة في الخلفية على رمز وصول إلى المجلد؛ وهي تنتظر أن يعيد المستخدم تحديد المجلد."
            ]),
            new("OperationalLogMsixBackgroundFilesDiscovered",
            [
                "The MSIX background task found {0} new file(s).", "MSIX 后台任务发现 {0} 个新文件。", "MSIX 背景工作找到 {0} 個新檔案。", "MSIX バックグラウンド タスクが {0} 個の新しいファイルを検出しました。", "MSIX 백그라운드 작업에서 새 파일 {0}개를 찾았습니다.",
                "La tarea en segundo plano de MSIX encontró {0} archivo(s) nuevo(s).", "La tâche MSIX en arrière-plan a trouvé {0} nouveau(x) fichier(s).", "Die MSIX-Hintergrundaufgabe hat {0} neue Datei(en) gefunden.", "A tarefa MSIX em segundo plano encontrou {0} novo(s) arquivo(s).", "Фоновая задача MSIX обнаружила новых файлов: {0}.",
                "L’attività MSIX in background ha trovato {0} nuovi file.", "De MSIX-achtergrondtaak heeft {0} nieuw(e) bestand(en) gevonden.", "Zadanie MSIX w tle znalazło nowe pliki: {0}.", "MSIX arka plan görevi {0} yeni dosya buldu.", "عثرت مهمة MSIX في الخلفية على {0} من الملفات الجديدة."
            ]),
            new("OperationalLogTaskException",
            [
                "Exception: {0}", "异常：{0}", "例外狀況：{0}", "例外：{0}", "예외: {0}",
                "Excepción: {0}", "Exception : {0}", "Ausnahme: {0}", "Exceção: {0}", "Исключение: {0}",
                "Eccezione: {0}", "Uitzondering: {0}", "Wyjątek: {0}", "Özel durum: {0}", "استثناء: {0}"
            ]),
            new("OperationalLogAppServiceCancelResponseFailed",
            [
                "Failed to send the App Service cancellation response: {0}", "发送 App Service 取消响应失败：{0}", "傳送 App Service 取消回應失敗：{0}", "App Service のキャンセル応答を送信できませんでした：{0}", "App Service 취소 응답을 보내지 못했습니다: {0}",
                "No se pudo enviar la respuesta de cancelación de App Service: {0}", "Échec de l’envoi de la réponse d’annulation d’App Service : {0}", "App Service-Abbruchantwort konnte nicht gesendet werden: {0}", "Falha ao enviar a resposta de cancelamento do App Service: {0}", "Не удалось отправить ответ App Service об отмене: {0}",
                "Impossibile inviare la risposta di annullamento di App Service: {0}", "Het annuleringsantwoord van App Service kan niet worden verzonden: {0}", "Nie udało się wysłać odpowiedzi App Service o anulowaniu: {0}", "App Service iptal yanıtı gönderilemedi: {0}", "تعذر إرسال استجابة إلغاء App Service: {0}"
            ]),
            new("OperationalLogAppServiceRequestFailed",
            [
                "The App Service request failed: {0}", "App Service 请求失败：{0}", "App Service 要求失敗：{0}", "App Service の要求に失敗しました：{0}", "App Service 요청이 실패했습니다: {0}",
                "Error en la solicitud de App Service: {0}", "Échec de la requête App Service : {0}", "App Service-Anforderung ist fehlgeschlagen: {0}", "Falha na solicitação do App Service: {0}", "Сбой запроса App Service: {0}",
                "Richiesta App Service non riuscita: {0}", "De App Service-aanvraag is mislukt: {0}", "Żądanie App Service nie powiodło się: {0}", "App Service isteği başarısız oldu: {0}", "فشل طلب App Service: {0}"
            ]),
            new("OperationalLogAppServiceErrorResponseFailed",
            [
                "Failed to send the App Service error response: {0}", "发送 App Service 错误响应失败：{0}", "傳送 App Service 錯誤回應失敗：{0}", "App Service のエラー応答を送信できませんでした：{0}", "App Service 오류 응답을 보내지 못했습니다: {0}",
                "No se pudo enviar la respuesta de error de App Service: {0}", "Échec de l’envoi de la réponse d’erreur d’App Service : {0}", "App Service-Fehlerantwort konnte nicht gesendet werden: {0}", "Falha ao enviar a resposta de erro do App Service: {0}", "Не удалось отправить ответ App Service с ошибкой: {0}",
                "Impossibile inviare la risposta di errore di App Service: {0}", "Het foutantwoord van App Service kan niet worden verzonden: {0}", "Nie udało się wysłać odpowiedzi App Service z błędem: {0}", "App Service hata yanıtı gönderilemedi: {0}", "تعذر إرسال استجابة خطأ App Service: {0}"
            ]),
            new("OperationalLogAppServiceBackgroundTaskCancelled",
            [
                "The App Service background task was cancelled: {0}", "App Service 后台任务已取消：{0}", "App Service 背景工作已取消：{0}", "App Service のバックグラウンド タスクがキャンセルされました：{0}", "App Service 백그라운드 작업이 취소되었습니다: {0}",
                "Se canceló la tarea en segundo plano de App Service: {0}", "La tâche App Service en arrière-plan a été annulée : {0}", "Die App Service-Hintergrundaufgabe wurde abgebrochen: {0}", "A tarefa do App Service em segundo plano foi cancelada: {0}", "Фоновая задача App Service отменена: {0}",
                "L’attività App Service in background è stata annullata: {0}", "De App Service-achtergrondtaak is geannuleerd: {0}", "Zadanie App Service w tle zostało anulowane: {0}", "App Service arka plan görevi iptal edildi: {0}", "تم إلغاء مهمة App Service في الخلفية: {0}"
            ]),
            new("OperationalLogAppServiceConnectionClosed",
            [
                "The App Service connection closed: {0}", "App Service 连接已关闭：{0}", "App Service 連線已關閉：{0}", "App Service の接続が閉じられました：{0}", "App Service 연결이 닫혔습니다: {0}",
                "Se cerró la conexión de App Service: {0}", "La connexion App Service a été fermée : {0}", "Die App Service-Verbindung wurde geschlossen: {0}", "A conexão do App Service foi fechada: {0}", "Подключение App Service закрыто: {0}",
                "La connessione App Service è stata chiusa: {0}", "De App Service-verbinding is gesloten: {0}", "Połączenie App Service zostało zamknięte: {0}", "App Service bağlantısı kapatıldı: {0}", "تم إغلاق اتصال App Service: {0}"
            ]),
            new("OperationalLogIncompatibleFfmpegIgnored",
            [
                "Ignoring incompatible FFmpeg: {0} ({1}; current process: {2}).", "已忽略不兼容的 FFmpeg：{0}（{1}；当前进程：{2}）。", "已忽略不相容的 FFmpeg：{0}（{1}；目前處理程序：{2}）。", "互換性のない FFmpeg を無視します：{0}（{1}、現在のプロセス：{2}）。", "호환되지 않는 FFmpeg를 무시합니다: {0}({1}, 현재 프로세스: {2}).",
                "Se omite un FFmpeg incompatible: {0} ({1}; proceso actual: {2}).", "FFmpeg incompatible ignoré : {0} ({1} ; processus actuel : {2}).", "Inkompatibles FFmpeg wird ignoriert: {0} ({1}; aktueller Prozess: {2}).", "Ignorando FFmpeg incompatível: {0} ({1}; processo atual: {2}).", "Несовместимый FFmpeg пропущен: {0} ({1}; текущий процесс: {2}).",
                "FFmpeg incompatibile ignorato: {0} ({1}; processo corrente: {2}).", "Incompatibele FFmpeg wordt genegeerd: {0} ({1}; huidig proces: {2}).", "Pominięto niezgodny program FFmpeg: {0} ({1}; bieżący proces: {2}).", "Uyumsuz FFmpeg yok sayılıyor: {0} ({1}; geçerli işlem: {2}).", "تم تجاهل FFmpeg غير المتوافق: {0} ({1}؛ العملية الحالية: {2})."
            ]),
            new("OperationalLogExplicitX64FfmpegOnArm64",
            [
                "Using explicitly configured x64 FFmpeg through ARM64 emulation: {0}", "正在通过 ARM64 仿真使用用户明确配置的 x64 FFmpeg：{0}", "正在透過 ARM64 模擬使用使用者明確設定的 x64 FFmpeg：{0}", "ARM64 エミュレーションで明示的に設定された x64 FFmpeg を使用します：{0}", "ARM64 에뮬레이션을 통해 명시적으로 구성된 x64 FFmpeg를 사용합니다: {0}",
                "Se usa el FFmpeg x64 configurado explícitamente mediante emulación ARM64: {0}", "Utilisation de FFmpeg x64 configuré explicitement via l’émulation ARM64 : {0}", "Explizit konfiguriertes x64-FFmpeg wird über ARM64-Emulation verwendet: {0}", "Usando o FFmpeg x64 configurado explicitamente por meio da emulação ARM64: {0}", "Явно настроенный FFmpeg x64 используется через эмуляцию ARM64: {0}",
                "Uso di FFmpeg x64 configurato esplicitamente tramite emulazione ARM64: {0}", "Expliciet geconfigureerde x64-FFmpeg wordt gebruikt via ARM64-emulatie: {0}", "Jawnie skonfigurowany program FFmpeg x64 jest używany przez emulację ARM64: {0}", "Açıkça yapılandırılmış x64 FFmpeg, ARM64 öykünmesiyle kullanılıyor: {0}", "يُستخدم FFmpeg ‏x64 الذي تم إعداده صراحة عبر محاكاة ARM64: {0}"
            ]),
            new("OperationalLogHardwareDecodeCacheIncompatible",
            [
                "Ignored an incompatible version of the cached hardware-decoding report.", "已忽略版本不兼容的硬件解码报告缓存。", "已忽略版本不相容的硬體解碼報告快取。", "互換性のないバージョンのハードウェア デコード レポート キャッシュを無視しました。", "호환되지 않는 버전의 하드웨어 디코딩 보고서 캐시를 무시했습니다.",
                "Se ignoró una versión incompatible del informe de descodificación por hardware almacenado en caché.", "Une version incompatible du rapport de décodage matériel en cache a été ignorée.", "Eine inkompatible Version des zwischengespeicherten Hardwaredecodierungsberichts wurde ignoriert.", "Uma versão incompatível do relatório de decodificação por hardware em cache foi ignorada.", "Несовместимая версия кэшированного отчета об аппаратном декодировании пропущена.",
                "È stata ignorata una versione incompatibile del rapporto di decodifica hardware memorizzato nella cache.", "Een incompatibele versie van het gecachte hardwaredecoderingsrapport is genegeerd.", "Pominięto niezgodną wersję buforowanego raportu dekodowania sprzętowego.", "Önbelleğe alınmış donanım kod çözme raporunun uyumsuz bir sürümü yok sayıldı.", "تم تجاهل إصدار غير متوافق من تقرير فك الترميز بالأجهزة المخزّن مؤقتًا."
            ]),
            new("OperationalLogHardwareDecodeCacheContextChanged",
            [
                "Ignored the cached hardware-decoding report because the hardware or FFmpeg installation changed.", "由于硬件或 FFmpeg 安装已变化，已忽略缓存的硬件解码报告。", "由於硬體或 FFmpeg 安裝已變更，已忽略快取的硬體解碼報告。", "ハードウェアまたは FFmpeg のインストールが変更されたため、キャッシュされたハードウェア デコード レポートを無視しました。", "하드웨어 또는 FFmpeg 설치가 변경되어 캐시된 하드웨어 디코딩 보고서를 무시했습니다.",
                "Se ignoró el informe de descodificación por hardware almacenado en caché porque cambió el hardware o la instalación de FFmpeg.", "Le rapport de décodage matériel en cache a été ignoré, car le matériel ou l’installation de FFmpeg a changé.", "Der zwischengespeicherte Hardwaredecodierungsbericht wurde ignoriert, weil sich die Hardware oder die FFmpeg-Installation geändert hat.", "O relatório de decodificação por hardware em cache foi ignorado porque o hardware ou a instalação do FFmpeg mudou.", "Кэшированный отчет об аппаратном декодировании пропущен, поскольку оборудование или установка FFmpeg изменились.",
                "Il rapporto di decodifica hardware memorizzato nella cache è stato ignorato perché l’hardware o l’installazione di FFmpeg sono cambiati.", "Het gecachte hardwaredecoderingsrapport is genegeerd omdat de hardware of FFmpeg-installatie is gewijzigd.", "Buforowany raport dekodowania sprzętowego został pominięty, ponieważ zmienił się sprzęt lub instalacja FFmpeg.", "Donanım veya FFmpeg kurulumu değiştiği için önbelleğe alınmış donanım kod çözme raporu yok sayıldı.", "تم تجاهل تقرير فك الترميز بالأجهزة المخزّن مؤقتًا لأن الأجهزة أو تثبيت FFmpeg قد تغيّر."
            ]),
            new("OperationalLogHardwareDecodeReportReadFailed",
            [
                "Failed to read the hardware-decoding report: {0}", "读取硬件解码报告失败：{0}", "讀取硬體解碼報告失敗：{0}", "ハードウェア デコード レポートを読み取れませんでした：{0}", "하드웨어 디코딩 보고서를 읽지 못했습니다: {0}",
                "No se pudo leer el informe de descodificación por hardware: {0}", "Échec de la lecture du rapport de décodage matériel : {0}", "Hardwaredecodierungsbericht konnte nicht gelesen werden: {0}", "Falha ao ler o relatório de decodificação por hardware: {0}", "Не удалось прочитать отчет об аппаратном декодировании: {0}",
                "Impossibile leggere il rapporto di decodifica hardware: {0}", "Het hardwaredecoderingsrapport kan niet worden gelezen: {0}", "Nie udało się odczytać raportu dekodowania sprzętowego: {0}", "Donanım kod çözme raporu okunamadı: {0}", "تعذرت قراءة تقرير فك الترميز بالأجهزة: {0}"
            ]),
            new("OperationalLogJumpListUpdateFailed",
            [
                "Failed to update the Windows Jump List: {0}", "更新 Windows 跳转列表失败：{0}", "更新 Windows 捷徑清單失敗：{0}", "Windows ジャンプ リストを更新できませんでした：{0}", "Windows 점프 목록을 업데이트하지 못했습니다: {0}",
                "No se pudo actualizar la Lista de accesos directos de Windows: {0}", "Échec de la mise à jour de la liste de raccourcis Windows : {0}", "Windows-Sprungliste konnte nicht aktualisiert werden: {0}", "Falha ao atualizar a Lista de Atalhos do Windows: {0}", "Не удалось обновить список переходов Windows: {0}",
                "Impossibile aggiornare la Jump List di Windows: {0}", "De Windows-jumplist kan niet worden bijgewerkt: {0}", "Nie udało się zaktualizować listy szybkiego dostępu systemu Windows: {0}", "Windows Atlama Listesi güncellenemedi: {0}", "تعذر تحديث قائمة الانتقال في Windows: {0}"
            ]),
            new("OperationalLogMediaFoundationVideoPropertiesReadFailed",
            [
                "Failed to read Media Foundation video properties: {0}", "读取 Media Foundation 视频属性失败：{0}", "讀取 Media Foundation 視訊屬性失敗：{0}", "Media Foundation のビデオ プロパティを読み取れませんでした：{0}", "Media Foundation 비디오 속성을 읽지 못했습니다: {0}",
                "No se pudieron leer las propiedades de vídeo de Media Foundation: {0}", "Échec de la lecture des propriétés vidéo de Media Foundation : {0}", "Media Foundation-Videoeigenschaften konnten nicht gelesen werden: {0}", "Falha ao ler as propriedades de vídeo do Media Foundation: {0}", "Не удалось прочитать свойства видео Media Foundation: {0}",
                "Impossibile leggere le proprietà video di Media Foundation: {0}", "De Media Foundation-video-eigenschappen kunnen niet worden gelezen: {0}", "Nie udało się odczytać właściwości wideo Media Foundation: {0}", "Media Foundation video özellikleri okunamadı: {0}", "تعذرت قراءة خصائص فيديو Media Foundation: {0}"
            ]),
            new("OperationalLogMediaFoundationAudioPropertiesReadFailed",
            [
                "Failed to read Media Foundation audio properties: {0}", "读取 Media Foundation 音频属性失败：{0}", "讀取 Media Foundation 音訊屬性失敗：{0}", "Media Foundation のオーディオ プロパティを読み取れませんでした：{0}", "Media Foundation 오디오 속성을 읽지 못했습니다: {0}",
                "No se pudieron leer las propiedades de audio de Media Foundation: {0}", "Échec de la lecture des propriétés audio de Media Foundation : {0}", "Media Foundation-Audioeigenschaften konnten nicht gelesen werden: {0}", "Falha ao ler as propriedades de áudio do Media Foundation: {0}", "Не удалось прочитать свойства аудио Media Foundation: {0}",
                "Impossibile leggere le proprietà audio di Media Foundation: {0}", "De Media Foundation-audio-eigenschappen kunnen niet worden gelezen: {0}", "Nie udało się odczytać właściwości audio Media Foundation: {0}", "Media Foundation ses özellikleri okunamadı: {0}", "تعذرت قراءة خصائص صوت Media Foundation: {0}"
            ]),
            new("OperationalLogMediaFoundationMetadataReadFailed",
            [
                "Failed to read Media Foundation metadata: {0}", "读取 Media Foundation 元数据失败：{0}", "讀取 Media Foundation 中繼資料失敗：{0}", "Media Foundation のメタデータを読み取れませんでした：{0}", "Media Foundation 메타데이터를 읽지 못했습니다: {0}",
                "No se pudieron leer los metadatos de Media Foundation: {0}", "Échec de la lecture des métadonnées de Media Foundation : {0}", "Media Foundation-Metadaten konnten nicht gelesen werden: {0}", "Falha ao ler os metadados do Media Foundation: {0}", "Не удалось прочитать метаданные Media Foundation: {0}",
                "Impossibile leggere i metadati di Media Foundation: {0}", "De Media Foundation-metagegevens kunnen niet worden gelezen: {0}", "Nie udało się odczytać metadanych Media Foundation: {0}", "Media Foundation meta verileri okunamadı: {0}", "تعذرت قراءة بيانات تعريف Media Foundation: {0}"
            ]),
            new("OperationalLogQueueRestoreFailed",
            [
                "Failed to restore the conversion queue: {0}", "恢复转换队列失败：{0}", "還原轉換佇列失敗：{0}", "変換キューを復元できませんでした：{0}", "변환 대기열을 복원하지 못했습니다: {0}",
                "No se pudo restaurar la cola de conversión: {0}", "Échec de la restauration de la file de conversion : {0}", "Konvertierungswarteschlange konnte nicht wiederhergestellt werden: {0}", "Falha ao restaurar a fila de conversão: {0}", "Не удалось восстановить очередь преобразования: {0}",
                "Impossibile ripristinare la coda di conversione: {0}", "De conversiewachtrij kan niet worden hersteld: {0}", "Nie udało się przywrócić kolejki konwersji: {0}", "Dönüştürme kuyruğu geri yüklenemedi: {0}", "تعذرت استعادة قائمة انتظار التحويل: {0}"
            ]),
            new("OperationalLogAppRecoveryRegistrationHresultFailed",
            [
                "Failed to register application restart: HRESULT 0x{0:X8}", "注册应用重启失败：HRESULT 0x{0:X8}", "註冊應用程式重新啟動失敗：HRESULT 0x{0:X8}", "アプリケーションの再起動を登録できませんでした：HRESULT 0x{0:X8}", "애플리케이션 다시 시작을 등록하지 못했습니다: HRESULT 0x{0:X8}",
                "No se pudo registrar el reinicio de la aplicación: HRESULT 0x{0:X8}", "Échec de l’inscription du redémarrage de l’application : HRESULT 0x{0:X8}", "Anwendungsneustart konnte nicht registriert werden: HRESULT 0x{0:X8}", "Falha ao registrar a reinicialização do aplicativo: HRESULT 0x{0:X8}", "Не удалось зарегистрировать перезапуск приложения: HRESULT 0x{0:X8}",
                "Impossibile registrare il riavvio dell’applicazione: HRESULT 0x{0:X8}", "Het opnieuw starten van de toepassing kan niet worden geregistreerd: HRESULT 0x{0:X8}", "Nie udało się zarejestrować ponownego uruchomienia aplikacji: HRESULT 0x{0:X8}", "Uygulamanın yeniden başlatılması kaydedilemedi: HRESULT 0x{0:X8}", "تعذر تسجيل إعادة تشغيل التطبيق: HRESULT 0x{0:X8}"
            ]),
            new("OperationalLogCrashCheckpointRegistrationHresultFailed",
            [
                "Failed to register the crash recovery checkpoint: HRESULT 0x{0:X8}", "注册崩溃恢复检查点失败：HRESULT 0x{0:X8}", "註冊當機復原檢查點失敗：HRESULT 0x{0:X8}", "クラッシュ回復チェックポイントを登録できませんでした：HRESULT 0x{0:X8}", "충돌 복구 검사점을 등록하지 못했습니다: HRESULT 0x{0:X8}",
                "No se pudo registrar el punto de control de recuperación ante bloqueos: HRESULT 0x{0:X8}", "Échec de l’inscription du point de contrôle de récupération après incident : HRESULT 0x{0:X8}", "Absturzwiederherstellungspunkt konnte nicht registriert werden: HRESULT 0x{0:X8}", "Falha ao registrar o ponto de verificação de recuperação de falha: HRESULT 0x{0:X8}", "Не удалось зарегистрировать контрольную точку восстановления после сбоя: HRESULT 0x{0:X8}",
                "Impossibile registrare il punto di controllo di ripristino dopo un arresto anomalo: HRESULT 0x{0:X8}", "Het herstelcontrolepunt voor crashes kan niet worden geregistreerd: HRESULT 0x{0:X8}", "Nie udało się zarejestrować punktu kontrolnego odzyskiwania po awarii: HRESULT 0x{0:X8}", "Kilitlenme kurtarma denetim noktası kaydedilemedi: HRESULT 0x{0:X8}", "تعذر تسجيل نقطة تحقق الاسترداد من التعطل: HRESULT 0x{0:X8}"
            ]),
            new("OperationalLogAppRecoveryRegistrationFailed",
            [
                "Failed to register application recovery: {0}", "注册应用恢复失败：{0}", "註冊應用程式復原失敗：{0}", "アプリケーションの回復を登録できませんでした：{0}", "애플리케이션 복구를 등록하지 못했습니다: {0}",
                "No se pudo registrar la recuperación de la aplicación: {0}", "Échec de l’inscription de la récupération de l’application : {0}", "Anwendungswiederherstellung konnte nicht registriert werden: {0}", "Falha ao registrar a recuperação do aplicativo: {0}", "Не удалось зарегистрировать восстановление приложения: {0}",
                "Impossibile registrare il ripristino dell’applicazione: {0}", "Toepassingsherstel kan niet worden geregistreerd: {0}", "Nie udało się zarejestrować odzyskiwania aplikacji: {0}", "Uygulama kurtarma kaydedilemedi: {0}", "تعذر تسجيل استرداد التطبيق: {0}"
            ]),
            new("OperationalLogCrashCheckpointReadFailed",
            [
                "Failed to read the crash recovery checkpoint: {0}", "读取崩溃恢复检查点失败：{0}", "讀取當機復原檢查點失敗：{0}", "クラッシュ回復チェックポイントを読み取れませんでした：{0}", "충돌 복구 검사점을 읽지 못했습니다: {0}",
                "No se pudo leer el punto de control de recuperación ante bloqueos: {0}", "Échec de la lecture du point de contrôle de récupération après incident : {0}", "Absturzwiederherstellungspunkt konnte nicht gelesen werden: {0}", "Falha ao ler o ponto de verificação de recuperação de falha: {0}", "Не удалось прочитать контрольную точку восстановления после сбоя: {0}",
                "Impossibile leggere il punto di controllo di ripristino dopo un arresto anomalo: {0}", "Het herstelcontrolepunt voor crashes kan niet worden gelezen: {0}", "Nie udało się odczytać punktu kontrolnego odzyskiwania po awarii: {0}", "Kilitlenme kurtarma denetim noktası okunamadı: {0}", "تعذرت قراءة نقطة تحقق الاسترداد من التعطل: {0}"
            ]),
            new("OperationalLogCrashCheckpointCleanupFailed",
            [
                "Failed to clean up the crash recovery checkpoint: {0}", "清理崩溃恢复检查点失败：{0}", "清理當機復原檢查點失敗：{0}", "クラッシュ回復チェックポイントをクリーンアップできませんでした：{0}", "충돌 복구 검사점을 정리하지 못했습니다: {0}",
                "No se pudo limpiar el punto de control de recuperación ante bloqueos: {0}", "Échec du nettoyage du point de contrôle de récupération après incident : {0}", "Absturzwiederherstellungspunkt konnte nicht bereinigt werden: {0}", "Falha ao limpar o ponto de verificação de recuperação de falha: {0}", "Не удалось удалить контрольную точку восстановления после сбоя: {0}",
                "Impossibile eliminare il punto di controllo di ripristino dopo un arresto anomalo: {0}", "Het herstelcontrolepunt voor crashes kan niet worden opgeschoond: {0}", "Nie udało się wyczyścić punktu kontrolnego odzyskiwania po awarii: {0}", "Kilitlenme kurtarma denetim noktası temizlenemedi: {0}", "تعذر تنظيف نقطة تحقق الاسترداد من التعطل: {0}"
            ]),
            new("OperationalLogAppRecoveryUnregisterFailed",
            [
                "Failed to unregister application recovery: {0}", "取消注册应用恢复失败：{0}", "取消註冊應用程式復原失敗：{0}", "アプリケーションの回復登録を解除できませんでした：{0}", "애플리케이션 복구 등록을 취소하지 못했습니다: {0}",
                "No se pudo anular el registro de recuperación de la aplicación: {0}", "Échec de la désinscription de la récupération de l’application : {0}", "Registrierung der Anwendungswiederherstellung konnte nicht aufgehoben werden: {0}", "Falha ao cancelar o registro de recuperação do aplicativo: {0}", "Не удалось отменить регистрацию восстановления приложения: {0}",
                "Impossibile annullare la registrazione del ripristino dell’applicazione: {0}", "De registratie van toepassingsherstel kan niet ongedaan worden gemaakt: {0}", "Nie udało się wyrejestrować odzyskiwania aplikacji: {0}", "Uygulama kurtarma kaydı kaldırılamadı: {0}", "تعذر إلغاء تسجيل استرداد التطبيق: {0}"
            ]),
            new("OperationalLogSettingsReadFailed",
            [
                "Failed to read settings: {0}", "读取设置失败：{0}", "讀取設定失敗：{0}", "設定を読み取れませんでした：{0}", "설정을 읽지 못했습니다: {0}",
                "No se pudo leer la configuración: {0}", "Échec de la lecture des paramètres : {0}", "Einstellungen konnten nicht gelesen werden: {0}", "Falha ao ler as configurações: {0}", "Не удалось прочитать настройки: {0}",
                "Impossibile leggere le impostazioni: {0}", "De instellingen kunnen niet worden gelezen: {0}", "Nie udało się odczytać ustawień: {0}", "Ayarlar okunamadı: {0}", "تعذرت قراءة الإعدادات: {0}"
            ]),
            new("OperationalLogFileAccessPermissionSaveFailed",
            [
                "Failed to save file access permission: {0}", "保存文件访问权限失败：{0}", "儲存檔案存取權限失敗：{0}", "ファイルのアクセス許可を保存できませんでした：{0}", "파일 액세스 권한을 저장하지 못했습니다: {0}",
                "No se pudo guardar el permiso de acceso al archivo: {0}", "Échec de l’enregistrement de l’autorisation d’accès au fichier : {0}", "Dateizugriffsberechtigung konnte nicht gespeichert werden: {0}", "Falha ao salvar a permissão de acesso ao arquivo: {0}", "Не удалось сохранить разрешение на доступ к файлу: {0}",
                "Impossibile salvare l’autorizzazione di accesso al file: {0}", "De bestandstoegangsmachtiging kan niet worden opgeslagen: {0}", "Nie udało się zapisać uprawnienia dostępu do pliku: {0}", "Dosya erişim izni kaydedilemedi: {0}", "تعذر حفظ إذن الوصول إلى الملف: {0}"
            ]),
            new("OperationalLogFolderAccessPermissionSaveFailed",
            [
                "Failed to save folder access permission: {0}", "保存文件夹访问权限失败：{0}", "儲存資料夾存取權限失敗：{0}", "フォルダーのアクセス許可を保存できませんでした：{0}", "폴더 액세스 권한을 저장하지 못했습니다: {0}",
                "No se pudo guardar el permiso de acceso a la carpeta: {0}", "Échec de l’enregistrement de l’autorisation d’accès au dossier : {0}", "Ordnerzugriffsberechtigung konnte nicht gespeichert werden: {0}", "Falha ao salvar a permissão de acesso à pasta: {0}", "Не удалось сохранить разрешение на доступ к папке: {0}",
                "Impossibile salvare l’autorizzazione di accesso alla cartella: {0}", "De maptoegangsmachtiging kan niet worden opgeslagen: {0}", "Nie udało się zapisać uprawnienia dostępu do folderu: {0}", "Klasör erişim izni kaydedilemedi: {0}", "تعذر حفظ إذن الوصول إلى المجلد: {0}"
            ]),
            new("OperationalLogFileAccessPermissionRestoreFailed",
            [
                "Failed to restore file access permission: {0}", "恢复文件访问权限失败：{0}", "還原檔案存取權限失敗：{0}", "ファイルのアクセス許可を復元できませんでした：{0}", "파일 액세스 권한을 복원하지 못했습니다: {0}",
                "No se pudo restaurar el permiso de acceso al archivo: {0}", "Échec de la restauration de l’autorisation d’accès au fichier : {0}", "Dateizugriffsberechtigung konnte nicht wiederhergestellt werden: {0}", "Falha ao restaurar a permissão de acesso ao arquivo: {0}", "Не удалось восстановить разрешение на доступ к файлу: {0}",
                "Impossibile ripristinare l’autorizzazione di accesso al file: {0}", "De bestandstoegangsmachtiging kan niet worden hersteld: {0}", "Nie udało się przywrócić uprawnienia dostępu do pliku: {0}", "Dosya erişim izni geri yüklenemedi: {0}", "تعذرت استعادة إذن الوصول إلى الملف: {0}"
            ]),
            new("OperationalLogFolderAccessPermissionRestoreFailed",
            [
                "Failed to restore folder access permission: {0}", "恢复文件夹访问权限失败：{0}", "還原資料夾存取權限失敗：{0}", "フォルダーのアクセス許可を復元できませんでした：{0}", "폴더 액세스 권한을 복원하지 못했습니다: {0}",
                "No se pudo restaurar el permiso de acceso a la carpeta: {0}", "Échec de la restauration de l’autorisation d’accès au dossier : {0}", "Ordnerzugriffsberechtigung konnte nicht wiederhergestellt werden: {0}", "Falha ao restaurar a permissão de acesso à pasta: {0}", "Не удалось восстановить разрешение на доступ к папке: {0}",
                "Impossibile ripristinare l’autorizzazione di accesso alla cartella: {0}", "De maptoegangsmachtiging kan niet worden hersteld: {0}", "Nie udało się przywrócić uprawnienia dostępu do folderu: {0}", "Klasör erişim izni geri yüklenemedi: {0}", "تعذرت استعادة إذن الوصول إلى المجلد: {0}"
            ]),
            new("OperationalLogAccessTokenRegistryRestoreFailed",
            [
                "Failed to restore the access-token registry: {0}", "恢复访问令牌注册表失败：{0}", "還原存取權杖登錄失敗：{0}", "アクセス トークン レジストリを復元できませんでした：{0}", "액세스 토큰 레지스트리를 복원하지 못했습니다: {0}",
                "No se pudo restaurar el registro de tokens de acceso: {0}", "Échec de la restauration du registre des jetons d’accès : {0}", "Zugriffstoken-Registrierung konnte nicht wiederhergestellt werden: {0}", "Falha ao restaurar o registro de tokens de acesso: {0}", "Не удалось восстановить реестр маркеров доступа: {0}",
                "Impossibile ripristinare il registro dei token di accesso: {0}", "Het toegangstokenregister kan niet worden hersteld: {0}", "Nie udało się przywrócić rejestru tokenów dostępu: {0}", "Erişim belirteci kayıt defteri geri yüklenemedi: {0}", "تعذرت استعادة سجل رموز الوصول: {0}"
            ]),
            new("OperationalLogAccessTokenRegistryInvalid",
            [
                "The access-token registry has an invalid format: {0}", "访问令牌注册表格式无效：{0}", "存取權杖登錄格式無效：{0}", "アクセス トークン レジストリの形式が無効です：{0}", "액세스 토큰 레지스트리 형식이 잘못되었습니다: {0}",
                "El registro de tokens de acceso tiene un formato no válido: {0}", "Le format du registre des jetons d’accès n’est pas valide : {0}", "Die Zugriffstoken-Registrierung hat ein ungültiges Format: {0}", "O registro de tokens de acesso tem um formato inválido: {0}", "Недопустимый формат реестра маркеров доступа: {0}",
                "Il registro dei token di accesso ha un formato non valido: {0}", "Het toegangstokenregister heeft een ongeldige indeling: {0}", "Rejestr tokenów dostępu ma nieprawidłowy format: {0}", "Erişim belirteci kayıt defterinin biçimi geçersiz: {0}", "تنسيق سجل رموز الوصول غير صالح: {0}"
            ]),
            new("OperationalLogTaskbarProgressInitializationFailed",
            [
                "Failed to initialize taskbar progress: {0}", "初始化任务栏进度失败：{0}", "初始化工作列進度失敗：{0}", "タスクバーの進行状況を初期化できませんでした：{0}", "작업 표시줄 진행률을 초기화하지 못했습니다: {0}",
                "No se pudo inicializar el progreso de la barra de tareas: {0}", "Échec de l’initialisation de la progression dans la barre des tâches : {0}", "Taskleistenfortschritt konnte nicht initialisiert werden: {0}", "Falha ao inicializar o progresso da barra de tarefas: {0}", "Не удалось инициализировать индикатор выполнения на панели задач: {0}",
                "Impossibile inizializzare l’avanzamento sulla barra delle applicazioni: {0}", "De voortgang op de taakbalk kan niet worden geïnitialiseerd: {0}", "Nie udało się zainicjować postępu na pasku zadań: {0}", "Görev çubuğu ilerleme durumu başlatılamadı: {0}", "تعذرت تهيئة التقدم على شريط المهام: {0}"
            ]),
            new("OperationalLogTaskbarThumbnailButtonsInitializationFailed",
            [
                "Failed to initialize taskbar thumbnail buttons: {0}", "初始化任务栏缩略图按钮失败：{0}", "初始化工作列縮圖按鈕失敗：{0}", "タスクバーのサムネイル ボタンを初期化できませんでした：{0}", "작업 표시줄 미리 보기 단추를 초기화하지 못했습니다: {0}",
                "No se pudieron inicializar los botones de miniatura de la barra de tareas: {0}", "Échec de l’initialisation des boutons de miniature de la barre des tâches : {0}", "Taskleisten-Miniaturschaltflächen konnten nicht initialisiert werden: {0}", "Falha ao inicializar os botões de miniatura da barra de tarefas: {0}", "Не удалось инициализировать кнопки эскиза на панели задач: {0}",
                "Impossibile inizializzare i pulsanti di anteprima della barra delle applicazioni: {0}", "De miniatuurknoppen op de taakbalk kunnen niet worden geïnitialiseerd: {0}", "Nie udało się zainicjować przycisków miniatury na pasku zadań: {0}", "Görev çubuğu küçük resim düğmeleri başlatılamadı: {0}", "تعذرت تهيئة أزرار الصور المصغرة على شريط المهام: {0}"
            ]),
            new("OperationalLogTaskbarProgressUpdateFailed",
            [
                "Failed to update taskbar progress: {0}", "更新任务栏进度失败：{0}", "更新工作列進度失敗：{0}", "タスクバーの進行状況を更新できませんでした：{0}", "작업 표시줄 진행률을 업데이트하지 못했습니다: {0}",
                "No se pudo actualizar el progreso de la barra de tareas: {0}", "Échec de la mise à jour de la progression dans la barre des tâches : {0}", "Taskleistenfortschritt konnte nicht aktualisiert werden: {0}", "Falha ao atualizar o progresso da barra de tarefas: {0}", "Не удалось обновить индикатор выполнения на панели задач: {0}",
                "Impossibile aggiornare l’avanzamento sulla barra delle applicazioni: {0}", "De voortgang op de taakbalk kan niet worden bijgewerkt: {0}", "Nie udało się zaktualizować postępu na pasku zadań: {0}", "Görev çubuğu ilerleme durumu güncellenemedi: {0}", "تعذر تحديث التقدم على شريط المهام: {0}"
            ]),
            new("OperationalLogTaskbarThumbnailButtonsUpdateFailed",
            [
                "Failed to update taskbar thumbnail buttons: {0}", "更新任务栏缩略图按钮失败：{0}", "更新工作列縮圖按鈕失敗：{0}", "タスクバーのサムネイル ボタンを更新できませんでした：{0}", "작업 표시줄 미리 보기 단추를 업데이트하지 못했습니다: {0}",
                "No se pudieron actualizar los botones de miniatura de la barra de tareas: {0}", "Échec de la mise à jour des boutons de miniature de la barre des tâches : {0}", "Taskleisten-Miniaturschaltflächen konnten nicht aktualisiert werden: {0}", "Falha ao atualizar os botões de miniatura da barra de tarefas: {0}", "Не удалось обновить кнопки эскиза на панели задач: {0}",
                "Impossibile aggiornare i pulsanti di anteprima della barra delle applicazioni: {0}", "De miniatuurknoppen op de taakbalk kunnen niet worden bijgewerkt: {0}", "Nie udało się zaktualizować przycisków miniatury na pasku zadań: {0}", "Görev çubuğu küçük resim düğmeleri güncellenemedi: {0}", "تعذر تحديث أزرار الصور المصغرة على شريط المهام: {0}"
            ]),
            new("OperationalLogTaskbarProgressClearFailed",
            [
                "Failed to clear taskbar progress: {0}", "清除任务栏进度失败：{0}", "清除工作列進度失敗：{0}", "タスクバーの進行状況をクリアできませんでした：{0}", "작업 표시줄 진행률을 지우지 못했습니다: {0}",
                "No se pudo borrar el progreso de la barra de tareas: {0}", "Échec de l’effacement de la progression dans la barre des tâches : {0}", "Taskleistenfortschritt konnte nicht gelöscht werden: {0}", "Falha ao limpar o progresso da barra de tarefas: {0}", "Не удалось очистить индикатор выполнения на панели задач: {0}",
                "Impossibile cancellare l’avanzamento sulla barra delle applicazioni: {0}", "De voortgang op de taakbalk kan niet worden gewist: {0}", "Nie udało się wyczyścić postępu na pasku zadań: {0}", "Görev çubuğu ilerleme durumu temizlenemedi: {0}", "تعذر مسح التقدم على شريط المهام: {0}"
            ]),
            new("OperationalLogWatchFolderFileReadFailed",
            [
                "The monitored folder could not read file {0}: {1}", "监视文件夹无法读取文件 {0}：{1}", "監看資料夾無法讀取檔案 {0}：{1}", "監視フォルダーでファイル {0} を読み取れませんでした：{1}", "모니터링 폴더에서 파일 {0}을(를) 읽지 못했습니다: {1}",
                "La carpeta supervisada no pudo leer el archivo {0}: {1}", "Le dossier surveillé n’a pas pu lire le fichier {0} : {1}", "Der überwachte Ordner konnte die Datei {0} nicht lesen: {1}", "A pasta monitorada não conseguiu ler o arquivo {0}: {1}", "Не удалось прочитать файл {0} в отслеживаемой папке: {1}",
                "La cartella monitorata non è riuscita a leggere il file {0}: {1}", "De bewaakte map kan bestand {0} niet lezen: {1}", "Monitorowany folder nie mógł odczytać pliku {0}: {1}", "İzlenen klasör {0} dosyasını okuyamadı: {1}", "تعذر على المجلد المراقَب قراءة الملف {0}: {1}"
            ]),
            new("OperationalLogWatchFolderError",
            [
                "Folder monitoring reported an error: {0}", "文件夹监视发生错误：{0}", "資料夾監看發生錯誤：{0}", "フォルダー監視でエラーが報告されました：{0}", "폴더 모니터링에서 오류가 보고되었습니다: {0}",
                "La supervisión de carpetas notificó un error: {0}", "La surveillance des dossiers a signalé une erreur : {0}", "Die Ordnerüberwachung hat einen Fehler gemeldet: {0}", "O monitoramento de pasta relatou um erro: {0}", "Система наблюдения за папкой сообщила об ошибке: {0}",
                "Il monitoraggio della cartella ha segnalato un errore: {0}", "Mapbewaking heeft een fout gemeld: {0}", "Monitorowanie folderu zgłosiło błąd: {0}", "Klasör izleme bir hata bildirdi: {0}", "أبلغت مراقبة المجلد عن خطأ: {0}"
            ]),
            new("OperationalLogWindowsSearchIndexWriteFailed",
            [
                "Failed to write to the Windows Search index: {0}", "写入 Windows Search 索引失败：{0}", "寫入 Windows Search 索引失敗：{0}", "Windows Search インデックスに書き込めませんでした：{0}", "Windows Search 인덱스에 쓰지 못했습니다: {0}",
                "No se pudo escribir en el índice de Windows Search: {0}", "Échec de l’écriture dans l’index Windows Search : {0}", "In den Windows Search-Index konnte nicht geschrieben werden: {0}", "Falha ao gravar no índice do Windows Search: {0}", "Не удалось записать данные в индекс Windows Search: {0}",
                "Impossibile scrivere nell’indice di Windows Search: {0}", "Er kan niet naar de Windows Search-index worden geschreven: {0}", "Nie udało się zapisać danych w indeksie Windows Search: {0}", "Windows Search dizinine yazılamadı: {0}", "تعذرت الكتابة إلى فهرس Windows Search: {0}"
            ]),
            new("OperationalLogShareFilePrepareFailed",
            [
                "Failed to prepare file {0} for sharing: {1}", "准备分享文件 {0} 失败：{1}", "準備分享檔案 {0} 失敗：{1}", "ファイル {0} を共有用に準備できませんでした：{1}", "공유할 파일 {0}을(를) 준비하지 못했습니다: {1}",
                "No se pudo preparar el archivo {0} para compartirlo: {1}", "Échec de la préparation du fichier {0} pour le partage : {1}", "Datei {0} konnte nicht zum Teilen vorbereitet werden: {1}", "Falha ao preparar o arquivo {0} para compartilhamento: {1}", "Не удалось подготовить файл {0} к отправке: {1}",
                "Impossibile preparare il file {0} per la condivisione: {1}", "Bestand {0} kan niet worden voorbereid voor delen: {1}", "Nie udało się przygotować pliku {0} do udostępnienia: {1}", "{0} dosyası paylaşım için hazırlanamadı: {1}", "تعذر تجهيز الملف {0} للمشاركة: {1}"
            ])
        ]);
}

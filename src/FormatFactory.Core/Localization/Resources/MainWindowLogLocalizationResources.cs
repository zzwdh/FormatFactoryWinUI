namespace FormatFactoryWinUI.Services;

internal static class MainWindowLogLocalizationResources
{
    internal static LocalizedResourceModule Module { get; } = new(
        "MainWindowLog",
        [
            new("OperationalLogWindowLoadingStarted",
            [
                "Main window loading started.", "主窗口开始加载。", "主視窗開始載入。", "メイン ウィンドウの読み込みを開始しました。", "기본 창 로드를 시작했습니다.",
                "Se inició la carga de la ventana principal.", "Le chargement de la fenêtre principale a commencé.", "Das Laden des Hauptfensters wurde gestartet.", "O carregamento da janela principal foi iniciado.", "Начата загрузка главного окна.",
                "È iniziato il caricamento della finestra principale.", "Het laden van het hoofdvenster is gestart.", "Rozpoczęto ładowanie okna głównego.", "Ana pencere yüklenmeye başladı.", "بدأ تحميل النافذة الرئيسية."
            ]),
            new("OperationalLogShareSourceInitializationFailed",
            [
                "Failed to initialize the Windows share source: {0}", "初始化 Windows 共享源失败：{0}", "初始化 Windows 共用來源失敗：{0}", "Windows 共有ソースを初期化できませんでした: {0}", "Windows 공유 원본을 초기화하지 못했습니다: {0}",
                "No se pudo inicializar el origen de uso compartido de Windows: {0}", "Échec de l’initialisation de la source de partage Windows : {0}", "Die Windows-Freigabequelle konnte nicht initialisiert werden: {0}", "Falha ao inicializar a origem de compartilhamento do Windows: {0}", "Не удалось инициализировать источник общего доступа Windows: {0}",
                "Impossibile inizializzare l’origine di condivisione di Windows: {0}", "De Windows-deelbron kan niet worden geïnitialiseerd: {0}", "Nie udało się zainicjować źródła udostępniania systemu Windows: {0}", "Windows paylaşım kaynağı başlatılamadı: {0}", "تعذرت تهيئة مصدر مشاركة Windows: {0}"
            ]),
            new("OperationalLogTrayIconInitialized",
            [
                "The tray icon was initialized.", "托盘图标已初始化。", "系統匣圖示已初始化。", "トレイ アイコンを初期化しました。", "트레이 아이콘을 초기화했습니다.",
                "Se inicializó el icono de la bandeja.", "L’icône de la zone de notification a été initialisée.", "Das Infobereichssymbol wurde initialisiert.", "O ícone da bandeja foi inicializado.", "Значок в области уведомлений инициализирован.",
                "L’icona dell’area di notifica è stata inizializzata.", "Het systeemvakpictogram is geïnitialiseerd.", "Zainicjowano ikonę zasobnika.", "Tepsi simgesi başlatıldı.", "تمت تهيئة أيقونة علبة النظام."
            ]),
            new("OperationalLogSettingsLoaded",
            [
                "Settings were loaded.", "设置已加载。", "設定已載入。", "設定を読み込みました。", "설정을 불러왔습니다.",
                "Se cargó la configuración.", "Les paramètres ont été chargés.", "Die Einstellungen wurden geladen.", "As configurações foram carregadas.", "Настройки загружены.",
                "Le impostazioni sono state caricate.", "De instellingen zijn geladen.", "Wczytano ustawienia.", "Ayarlar yüklendi.", "تم تحميل الإعدادات."
            ]),
            new("OperationalLogFfmpegDetectionCompleted",
            [
                "FFmpeg detection completed: {0}", "FFmpeg 检测完成：{0}", "FFmpeg 偵測完成：{0}", "FFmpeg の検出が完了しました: {0}", "FFmpeg 검색이 완료되었습니다: {0}",
                "Finalizó la detección de FFmpeg: {0}", "La détection de FFmpeg est terminée : {0}", "Die FFmpeg-Erkennung wurde abgeschlossen: {0}", "A detecção do FFmpeg foi concluída: {0}", "Обнаружение FFmpeg завершено: {0}",
                "Il rilevamento di FFmpeg è terminato: {0}", "De FFmpeg-detectie is voltooid: {0}", "Zakończono wykrywanie FFmpeg: {0}", "FFmpeg algılama tamamlandı: {0}", "اكتمل اكتشاف FFmpeg: {0}"
            ]),
            new("OperationalLogWindowLoadingCompleted",
            [
                "Main window loading completed.", "主窗口加载完成。", "主視窗載入完成。", "メイン ウィンドウの読み込みが完了しました。", "기본 창 로드가 완료되었습니다.",
                "Finalizó la carga de la ventana principal.", "Le chargement de la fenêtre principale est terminé.", "Das Laden des Hauptfensters wurde abgeschlossen.", "O carregamento da janela principal foi concluído.", "Загрузка главного окна завершена.",
                "Il caricamento della finestra principale è terminato.", "Het laden van het hoofdvenster is voltooid.", "Zakończono ładowanie okna głównego.", "Ana pencere yüklemesi tamamlandı.", "اكتمل تحميل النافذة الرئيسية."
            ]),
            new("OperationalLogTaskRestoreFailed",
            [
                "Failed to restore task {0}: {1}", "恢复任务 {0} 失败：{1}", "還原工作 {0} 失敗：{1}", "タスク {0} を復元できませんでした: {1}", "작업 {0}을(를) 복원하지 못했습니다: {1}",
                "No se pudo restaurar la tarea {0}: {1}", "Échec de la restauration de la tâche {0} : {1}", "Aufgabe {0} konnte nicht wiederhergestellt werden: {1}", "Falha ao restaurar a tarefa {0}: {1}", "Не удалось восстановить задачу {0}: {1}",
                "Impossibile ripristinare l’attività {0}: {1}", "Taak {0} kan niet worden hersteld: {1}", "Nie udało się przywrócić zadania {0}: {1}", "{0} görevi geri yüklenemedi: {1}", "تعذرت استعادة المهمة {0}: {1}"
            ]),
            new("OperationalLogMediaAnalysisFailed",
            [
                "Failed to analyze {0}: {1}", "分析 {0} 失败：{1}", "分析 {0} 失敗：{1}", "{0} を解析できませんでした: {1}", "{0} 분석에 실패했습니다: {1}",
                "No se pudo analizar {0}: {1}", "Échec de l’analyse de {0} : {1}", "{0} konnte nicht analysiert werden: {1}", "Falha ao analisar {0}: {1}", "Не удалось проанализировать {0}: {1}",
                "Impossibile analizzare {0}: {1}", "{0} kan niet worden geanalyseerd: {1}", "Nie udało się przeanalizować {0}: {1}", "{0} analiz edilemedi: {1}", "تعذر تحليل {0}: {1}"
            ]),
            new("OperationalLogWindowCloseRequested",
            [
                "Window close requested: allowClose={0}, saving={1}, active tasks={2}.", "收到窗口关闭请求：允许关闭={0}，正在保存={1}，活动任务={2}。", "收到視窗關閉要求：允許關閉={0}，正在儲存={1}，作用中工作={2}。", "ウィンドウを閉じる要求を受信しました: 終了許可={0}、保存中={1}、実行中のタスク={2}。", "창 닫기 요청을 받았습니다: 닫기 허용={0}, 저장 중={1}, 활성 작업={2}.",
                "Se solicitó cerrar la ventana: permitir cierre={0}, guardando={1}, tareas activas={2}.", "Fermeture de la fenêtre demandée : fermeture autorisée={0}, enregistrement={1}, tâches actives={2}.", "Das Schließen des Fensters wurde angefordert: Schließen erlaubt={0}, Speichern={1}, aktive Aufgaben={2}.", "Foi solicitado o fechamento da janela: permitir fechamento={0}, salvando={1}, tarefas ativas={2}.", "Запрошено закрытие окна: закрытие разрешено={0}, сохранение={1}, активных задач={2}.",
                "Richiesta la chiusura della finestra: chiusura consentita={0}, salvataggio={1}, attività attive={2}.", "Sluiten van venster aangevraagd: sluiten toegestaan={0}, opslaan={1}, actieve taken={2}.", "Zażądano zamknięcia okna: zamknięcie dozwolone={0}, zapisywanie={1}, aktywne zadania={2}.", "Pencere kapatma istendi: kapatmaya izin ver={0}, kaydediliyor={1}, etkin görevler={2}.", "طُلب إغلاق النافذة: السماح بالإغلاق={0}، جارٍ الحفظ={1}، المهام النشطة={2}."
            ]),
            new("OperationalLogWindowClosed",
            [
                "The main window was closed.", "主窗口已关闭。", "主視窗已關閉。", "メイン ウィンドウを閉じました。", "기본 창이 닫혔습니다.",
                "Se cerró la ventana principal.", "La fenêtre principale a été fermée.", "Das Hauptfenster wurde geschlossen.", "A janela principal foi fechada.", "Главное окно закрыто.",
                "La finestra principale è stata chiusa.", "Het hoofdvenster is gesloten.", "Okno główne zostało zamknięte.", "Ana pencere kapatıldı.", "أُغلقت النافذة الرئيسية."
            ]),
            new("OperationalLogSystemSuspendingCheckpointSaved",
            [
                "The system is suspending; the conversion queue checkpoint was saved.", "系统即将挂起；转换队列检查点已保存。", "系統即將暫停；轉換佇列檢查點已儲存。", "システムが中断状態になります。変換キューのチェックポイントを保存しました。", "시스템이 절전 모드로 전환됩니다. 변환 대기열 검사점을 저장했습니다.",
                "El sistema se suspenderá; se guardó el punto de control de la cola de conversión.", "Le système va se mettre en veille ; le point de contrôle de la file de conversion a été enregistré.", "Das System wird angehalten; der Prüfpunkt der Konvertierungswarteschlange wurde gespeichert.", "O sistema será suspenso; o ponto de verificação da fila de conversão foi salvo.", "Система переходит в спящий режим; контрольная точка очереди преобразования сохранена.",
                "Il sistema sta per essere sospeso; il punto di controllo della coda di conversione è stato salvato.", "Het systeem wordt onderbroken; het controlepunt van de conversiewachtrij is opgeslagen.", "System jest wstrzymywany; zapisano punkt kontrolny kolejki konwersji.", "Sistem askıya alınıyor; dönüştürme kuyruğu denetim noktası kaydedildi.", "النظام على وشك التعليق؛ تم حفظ نقطة تحقق قائمة انتظار التحويل."
            ]),
            new("OperationalLogSystemResumedCapabilitiesRefreshed",
            [
                "The system resumed; devices and conversion capabilities were detected again.", "系统已恢复；设备与转换能力已重新检测。", "系統已恢復；裝置與轉換功能已重新偵測。", "システムが再開しました。デバイスと変換機能を再検出しました。", "시스템이 다시 시작되었습니다. 장치와 변환 기능을 다시 검색했습니다.",
                "El sistema se reanudó; se volvieron a detectar los dispositivos y las capacidades de conversión.", "Le système a repris ; les appareils et les capacités de conversion ont été détectés de nouveau.", "Das System wurde fortgesetzt; Geräte und Konvertierungsfunktionen wurden erneut erkannt.", "O sistema foi retomado; os dispositivos e recursos de conversão foram detectados novamente.", "Работа системы возобновлена; устройства и возможности преобразования обнаружены повторно.",
                "Il sistema è stato ripristinato; i dispositivi e le funzionalità di conversione sono stati rilevati di nuovo.", "Het systeem is hervat; apparaten en conversiemogelijkheden zijn opnieuw gedetecteerd.", "System wznowił działanie; ponownie wykryto urządzenia i możliwości konwersji.", "Sistem devam ettirildi; aygıtlar ve dönüştürme özellikleri yeniden algılandı.", "استؤنف النظام؛ أُعيد اكتشاف الأجهزة وإمكانات التحويل."
            ]),
            new("OperationalLogOpticalDeviceChanged",
            [
                "Optical device changed: {0} · {1}", "光驱设备发生变化：{0} · {1}", "光碟機裝置發生變更：{0} · {1}", "光学デバイスが変更されました: {0} · {1}", "광학 장치가 변경되었습니다: {0} · {1}",
                "Cambió el dispositivo óptico: {0} · {1}", "Le périphérique optique a changé : {0} · {1}", "Das optische Gerät wurde geändert: {0} · {1}", "O dispositivo óptico foi alterado: {0} · {1}", "Оптическое устройство изменено: {0} · {1}",
                "Il dispositivo ottico è cambiato: {0} · {1}", "Het optische apparaat is gewijzigd: {0} · {1}", "Urządzenie optyczne uległo zmianie: {0} · {1}", "Optik aygıt değişti: {0} · {1}", "تغيّر الجهاز البصري: {0} · {1}"
            ]),
        ]);
}

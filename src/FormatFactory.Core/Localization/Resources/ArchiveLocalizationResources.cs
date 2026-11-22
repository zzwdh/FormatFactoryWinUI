namespace FormatFactoryWinUI.Services;

internal static class ArchiveLocalizationResources
{
    internal static readonly LocalizedResourceModule Module = new("Archive", ArchiveResourceData.Entries,
        allowsEnglishFallback: true);
}

// Kept independent of the catalog so additional-language loading never initializes a module recursively.
internal static class ArchiveResourceData
{
    internal static readonly IReadOnlyList<LocalizedResourceEntry> Entries =
    [
        E("ArchiveTitle", "Archive conversion", "压缩包互转", "壓縮檔互轉"),
        E("ArchiveDescription", "Convert archives in batches. Original archives are kept and existing outputs receive a numbered name.", "批量转换压缩包格式，保留原包，同名输出自动编号。", "批次轉換壓縮檔格式，保留原始壓縮檔，同名輸出自動編號。"),
        E("ArchiveAdd", "Add archives", "添加压缩包", "新增壓縮檔"),
        E("ArchiveOutputFolder", "Output folder", "输出文件夹", "輸出資料夾"),
        E("ArchiveBrowse", "Choose folder", "选择文件夹", "選擇資料夾"),
        E("ArchiveBatchSettings", "Batch output settings", "整批输出设置", "整批輸出設定"),
        E("ArchiveTaskSettings", "Task output settings", "此任务输出设置", "此工作輸出設定"),
        E("ArchiveStart", "Start conversion", "开始转换", "開始轉換"),
        E("ArchiveCancel", "Cancel conversion", "取消转换", "取消轉換"),
        E("ArchiveClear", "Clear finished tasks", "清除已结束任务", "清除已結束工作"),
        E("ArchiveRemove", "Remove task", "移除任务", "移除工作"),
        E("ArchiveOpenOutput", "Open output location", "打开输出位置", "開啟輸出位置"),
        E("ArchiveRetry", "Retry task", "重试此任务", "重試此工作"),
        E("ArchiveEmpty", "No archives added", "尚未添加压缩包", "尚未新增壓縮檔"),
        E("ArchiveVolumeHint", "ZIP, 7Z, TAR, TAR.GZ and TAR.XZ can be converted; RAR is input only. Keep every volume in the same folder. Volumes from one archive become one output.", "支持 ZIP、7Z、TAR、TAR.GZ、TAR.XZ 互转，RAR 仅作输入。请将完整分卷放在同一文件夹，同组分卷合成一个输出。", "支援 ZIP、7Z、TAR、TAR.GZ、TAR.XZ 互轉，RAR 僅作輸入。請將完整分卷放在同一資料夾，同組分卷合成一個輸出。"),
        E("ArchiveTargetFormat", "Output format", "输出格式", "輸出格式"),
        E("ArchiveStrictVerification", "Strict content verification", "严格内容校验", "嚴格內容驗證"),
        E("ArchiveStrictDescription", "Standard verification checks that the output can be read completely. Strict verification also compares each file's content and takes longer.", "标准校验确认输出包完整可读；严格校验还会逐个核对文件内容，耗时更长。", "標準驗證確認輸出壓縮檔完整可讀；嚴格驗證還會逐一核對檔案內容，需要更長時間。"),
        E("ArchiveEncryptOutput", "Encrypt output archive", "加密输出压缩包", "加密輸出壓縮檔"),
        E("ArchiveOutputPassword", "Output password", "输出密码", "輸出密碼"),
        E("ArchiveConfirmPassword", "Confirm output password", "确认输出密码", "確認輸出密碼"),
        E("ArchiveEncryptionUnavailable", "Only ZIP and 7Z outputs support encryption. Choose one of these formats or turn off output encryption.", "只有 ZIP 和 7Z 输出支持加密，请改用这两种格式或关闭输出加密。", "只有 ZIP 和 7Z 輸出支援加密，請改用這兩種格式或關閉輸出加密。"),
        E("ArchivePasswordEmpty", "Enter an output password.", "请输入输出密码。", "請輸入輸出密碼。"),
        E("ArchivePasswordMismatch", "The output passwords do not match.", "两次输入的输出密码不一致。", "兩次輸入的輸出密碼不一致。"),
        E("ArchiveZipPasswordRules", "ZIP passwords must use 1–99 visible ASCII characters (letters, digits or symbols). Choose 7Z to use a Chinese password.", "ZIP 密码需使用 1–99 个可见英文字母、数字或符号；需要中文密码时请选择 7Z。", "ZIP 密碼須使用 1–99 個可見英文字母、數字或符號；需要中文密碼時請選擇 7Z。"),
        E("ArchiveSave", "Apply", "应用", "套用"),
        E("ArchiveClose", "Close", "关闭", "關閉"),
        E("ArchiveSourcePasswordTitle", "Archive password required", "需要压缩包密码", "需要壓縮檔密碼"),
        E("ArchiveSourcePassword", "Source archive password", "源压缩包密码", "來源壓縮檔密碼"),
        E("ArchiveWrongPassword", "The password was not accepted. Enter it again or skip this archive.", "密码不正确，请重新输入，或跳过此压缩包。", "密碼不正確，請重新輸入，或略過此壓縮檔。"),
        E("ArchiveSkip", "Skip this archive", "跳过此压缩包", "略過此壓縮檔"),
        E("ArchiveWaiting", "Waiting", "等待中", "等候中"),
        E("ArchiveSucceeded", "Completed", "已完成", "已完成"),
        E("ArchiveFailed", "Failed", "失败", "失敗"),
        E("ArchiveCancelled", "Cancelled", "已取消", "已取消"),
        E("ArchiveRunning", "Converting", "正在转换", "正在轉換"),
        E("ArchiveAwaitingPassword", "Waiting for password", "等待输入密码", "等候輸入密碼"),
        E("ArchiveTaskSummary", "{0} archives · {1} completed · {2} failed", "{0} 个压缩包 · {1} 个完成 · {2} 个失败", "{0} 個壓縮檔 · {1} 個完成 · {2} 個失敗"),
        E("ArchiveTargetSummary", "{0} · {1} · {2}", "{0} · {1} · {2}", "{0} · {1} · {2}"),
        E("ArchiveVerificationStandard", "Standard verification", "标准校验", "標準驗證"),
        E("ArchiveVerificationStrict", "Strict verification", "严格校验", "嚴格驗證"),
        E("ArchiveEncrypted", "Encrypted output", "输出加密", "輸出加密"),
        E("ArchiveUnencrypted", "Unencrypted output", "输出不加密", "輸出不加密"),
        E("ArchiveOutputFolderRequired", "Choose an output folder before starting.", "请先选择输出文件夹。", "請先選擇輸出資料夾。"),
        E("ArchiveBatchApplied", "Batch settings applied to waiting tasks.", "整批设置已应用到等待中的任务。", "整批設定已套用至等候中的工作。"),
        E("ArchiveUnsupportedFiles", "Some files could not be added. Choose a supported archive or its complete volume set.", "部分文件无法添加，请选择支持的压缩包或完整分卷。", "部分檔案無法新增，請選擇支援的壓縮檔或完整分卷。"),
        E("ArchiveErrorEngineUnavailable", "The bundled archive engine is unavailable. Repair or reinstall the app.", "内置压缩引擎不可用，请修复或重新安装应用。", "內建壓縮引擎無法使用，請修復或重新安裝應用程式。"),
        E("ArchiveErrorUnsupportedInput", "This archive format is not supported.", "不支持此压缩包格式。", "不支援此壓縮檔格式。"),
        E("ArchiveErrorMissingVolume", "One or more archive volumes are missing. Put the complete set in the same folder and try again.", "缺少压缩包分卷，请将完整分卷放在同一文件夹后重试。", "缺少壓縮檔分卷，請將完整分卷放在同一資料夾後重試。"),
        E("ArchiveErrorPasswordRequired", "This archive requires a password.", "此压缩包需要密码。", "此壓縮檔需要密碼。"),
        E("ArchiveErrorPasswordSkipped", "The archive was skipped because no password was provided.", "未提供密码，已跳过此压缩包。", "未提供密碼，已略過此壓縮檔。"),
        E("ArchiveErrorCorruptArchive", "The archive is damaged or incomplete. The original archive has been kept.", "压缩包已损坏或不完整，原包已保留。", "壓縮檔已損毀或不完整，原始壓縮檔已保留。"),
        E("ArchiveErrorUnsafeEntry", "The archive contains an unsafe path or link and cannot be converted.", "压缩包包含不安全的路径或链接，无法转换。", "壓縮檔包含不安全的路徑或連結，無法轉換。"),
        E("ArchiveErrorInsufficientSpace", "There is not enough disk space to convert and verify this archive.", "磁盘空间不足，无法转换并校验此压缩包。", "磁碟空間不足，無法轉換並驗證此壓縮檔。"),
        E("ArchiveErrorValidationFailed", "Output verification failed. No incomplete result was saved; the original archive has been kept.", "输出校验失败，未保存不完整结果，原包已保留。", "輸出驗證失敗，未儲存不完整結果，原始壓縮檔已保留。"),
        E("ArchiveErrorEngineFailed", "Archive conversion failed. Check the source archive and available disk space, then try again.", "压缩包转换失败，请检查原包和可用磁盘空间后重试。", "壓縮檔轉換失敗，請檢查原始壓縮檔及可用磁碟空間後重試。"),
        E("ArchiveErrorInvalidOptions", "The output settings are invalid. Check the format and encryption settings.", "输出设置无效，请检查格式和加密设置。", "輸出設定無效，請檢查格式與加密設定。"),
        E("ArchivePhaseInspecting", "Inspecting archive", "正在检查压缩包", "正在檢查壓縮檔"),
        E("ArchivePhaseExtracting", "Reading archive contents", "正在读取包内文件", "正在讀取壓縮檔內容"),
        E("ArchivePhaseCompressing", "Creating output archive", "正在生成输出压缩包", "正在建立輸出壓縮檔"),
        E("ArchivePhaseVerifying", "Verifying output archive", "正在校验输出压缩包", "正在驗證輸出壓縮檔"),
        E("ArchivePhaseComparing", "Comparing file contents", "正在比对文件内容", "正在比對檔案內容"),
        E("ArchivePhaseCompleted", "Completed", "已完成", "已完成"),
        E("ArchiveTasksRestored", "Archive tasks restored. Choose Start conversion when ready; encrypted outputs require their password again.", "压缩包任务已恢复。确认后点击开始转换；加密输出需重新输入密码。", "壓縮檔工作已還原。確認後按開始轉換；加密輸出須重新輸入密碼。"),
        E("ArchiveTaskRestoreFailed", "Saved archive tasks could not be read. The original task file has been kept. Restart after correcting its access or data problem.", "无法读取已保存的压缩包任务，原任务文件已保留。请处理文件访问或数据问题后重启。", "無法讀取已儲存的壓縮檔工作，原工作檔案已保留。請處理檔案存取或資料問題後重新啟動。"),
        E("ArchiveTaskSaveFailed", "Archive tasks could not be saved. Keep the app open and correct the data folder access or disk-space problem before exiting.", "压缩包任务保存失败，请保持应用打开，处理数据文件夹权限或磁盘空间问题后再退出。", "壓縮檔工作儲存失敗，請保持應用程式開啟，處理資料夾權限或磁碟空間問題後再結束。"),
        E("ArchiveHistorySaveFailed", "The output is complete, but its history could not be saved. It will be retried when archive tasks are restored.", "输出已完成，但历史记录保存失败；下次恢复压缩包任务时会再次保存。", "輸出已完成，但歷程記錄儲存失敗；下次還原壓縮檔工作時會再次儲存。"),
        E("ArchiveLastBatch", "Last batch:", "上次批次：", "上次批次："),
        E("ArchiveErrorOutputUnavailable", "The output folder cannot be used. Choose an accessible folder with enough free space.", "输出文件夹不可用，请选择有访问权限且空间充足的文件夹。", "輸出資料夾無法使用，請選擇有存取權限且空間充足的資料夾。"),
        E("ArchiveOutputChangedReady", "Output folder changed. Choose Start conversion to retry this task. Other failed tasks remain unchanged.", "输出文件夹已更改，点击开始转换可重试此任务；其他失败任务仍保持原状态。", "輸出資料夾已變更，按開始轉換可重試此工作；其他失敗工作仍維持原狀態。"),
        E("ArchivePreviousFailure", "Previous attempt: {0}", "上次失败：{0}", "上次失敗：{0}")
    ];

    internal static void AddTo(IDictionary<string, string> resources)
    {
        foreach (var entry in Entries)
        {
            if (!resources.ContainsKey(entry.Key))
                resources.Add(entry.Key, entry.Values[0]);
        }
    }

    private static LocalizedResourceEntry E(string key, string english, string simplifiedChinese, string traditionalChinese)
    {
        var values = Enumerable.Repeat(english, LocalizedResourceModule.LegacyLanguageCodes.Count).ToArray();
        values[1] = simplifiedChinese;
        values[2] = traditionalChinese;
        return new LocalizedResourceEntry(key, values);
    }
}

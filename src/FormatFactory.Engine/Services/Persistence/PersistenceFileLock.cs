namespace FormatFactoryWinUI.Services;

/// <summary>An existing lock needs exclusive sharing, not permission to modify its contents.</summary>
internal static class PersistenceFileLock
{
    internal static FileStream Open(string lockPath, string store)
    {
        var stage = "open-existing";
        try
        {
            try
            {
                // FileShare.None also excludes older processes that request read/write.
                // Never delete/recreate an existing lock: holders must share the same file.
                return new FileStream(lockPath, FileMode.Open, FileAccess.Read,
                    FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (FileNotFoundException)
            {
                stage = "create-new";
                // Another process winning creation yields IOException, retried by the caller
                // with its existing timeout/cancellation policy. Never truncate that file.
                return new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.Asynchronous);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var kind = store switch { "settings" => DiagnosticStore.Settings, "queue" => DiagnosticStore.Queue,
                "review" => DiagnosticStore.Review, _ => DiagnosticStore.Unknown };
            PersistenceFailureDiagnostics.Record(exception, kind, DiagnosticPersistenceStage.AcquireLock);
            if (exception is UnauthorizedAccessException)
                SafeDiagnostics.Write(DescribeAccessFailure(lockPath, store, stage, exception));
            throw;
        }
    }

    internal static string DescribeAccessFailure(string lockPath, string store, string stage, Exception exception)
    {
        var dataPath = lockPath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ? lockPath[..^5] : lockPath;
        return $"Persistence access error: store={store} stage={stage} hresult=0x{exception.HResult:X8} " +
            $"lock={Attributes(lockPath)} data={Attributes(dataPath)} parent={Attributes(Path.GetDirectoryName(lockPath))}";
    }

    private static string Attributes(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "unknown";
        try { return File.GetAttributes(path).ToString().Replace(", ", "|"); }
        catch (FileNotFoundException) { return "missing"; }
        catch (DirectoryNotFoundException) { return "missing"; }
        catch (UnauthorizedAccessException) { return "denied"; }
        catch { return "unavailable"; }
    }
}

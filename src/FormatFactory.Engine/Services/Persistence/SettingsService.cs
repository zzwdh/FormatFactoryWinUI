using System.Diagnostics;
using System.Text.Json;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Services;

public sealed class SettingsService
{
    private static readonly TimeSpan FileLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly string _lockPath;

    public SettingsService()
        : this(Path.Combine(LogService.AppDataRoot, "settings.json"))
    {
    }

    internal SettingsService(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
        _lockPath = _settingsPath + ".lock";
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await ProcessGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken) ?? new AppSettings();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PersistenceFailureDiagnostics.Record(ex, DiagnosticStore.Settings, DiagnosticPersistenceStage.Read);
            SafeDiagnostics.Write(PersistenceFailureDiagnostics.Format(ex)!);
            LogService.WriteApp(LocalizationService.T("OperationalLogSettingsReadFailed", ex.Message));
            return new AppSettings();
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Serialize before awaiting the shared lock so later caller mutations cannot change this save.
        // Serializing the settings object itself also keeps newly added properties from being omitted
        // by a manually maintained snapshot type.
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        var started = Stopwatch.GetTimestamp();
        var saved = false;

        await ProcessGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            await WriteSnapshotAsync(snapshot, cancellationToken);
            saved = true;
        }
        finally
        {
            ProcessGate.Release();
            NativeDiagnostics.Log.PersistenceCompleted("settings", saved, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task WriteSnapshotAsync(byte[] snapshot, CancellationToken cancellationToken)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            PersistenceFailureDiagnostics.Record(error, DiagnosticStore.Settings, DiagnosticPersistenceStage.CreateDirectory);
            throw;
        }
        var temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        var diagnosticStage = DiagnosticPersistenceStage.WriteSnapshot;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(snapshot, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            diagnosticStage = DiagnosticPersistenceStage.CommitSnapshot;
            File.Move(temporaryPath, _settingsPath, true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            PersistenceFailureDiagnostics.Record(error, DiagnosticStore.Settings, diagnosticStage, snapshot.Length);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // 已成功替换设置文件时，临时文件清理失败不应覆盖保存结果。
            }
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var stopwatch = Stopwatch.StartNew();
        IOException? lastError = null;
        while (stopwatch.Elapsed < FileLockTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return PersistenceFileLock.Open(_lockPath, "settings");
            }
            catch (IOException ex)
            {
                lastError = ex;
                await Task.Delay(50, cancellationToken);
            }
        }

        var failure = new TimeoutException(LocalizationService.T("GeneralServiceSettingsLockTimeout", _lockPath), lastError);
        PersistenceFailureDiagnostics.Record(failure, DiagnosticStore.Settings, DiagnosticPersistenceStage.AcquireLock,
            elapsedMs: (long)stopwatch.Elapsed.TotalMilliseconds);
        throw failure;
    }

}

using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private int _diagnosticPickerCount;

    // A snapshot is explicitly a last observation. Background errors do not read XAML objects.
    internal void CaptureDiagnosticUiSnapshot()
    {
        try
        {
            if (!DispatcherQueue.HasThreadAccess) return;
            var page = _navigationTag switch
            {
                "all" => DiagnosticPage.Queue, "video" => DiagnosticPage.Video,
                "audio" => DiagnosticPage.Audio, "image" => DiagnosticPage.Image,
                "document" => DiagnosticPage.Document, "ebook" => DiagnosticPage.Ebook,
                "disc" => DiagnosticPage.Disc, "archive" => DiagnosticPage.Archive,
                "automation" => DiagnosticPage.Automation, "history" => DiagnosticPage.History,
                "settings" => DiagnosticPage.Settings, "advanced" => DiagnosticPage.Advanced,
                _ => DiagnosticPage.Unknown
            };
            RuntimeFailureDiagnostics.ObserveUi(new(page, RootGrid.ActualWidth, RootGrid.ActualHeight,
                RootGrid.XamlRoot?.RasterizationScale ?? double.NaN, _queueCoordinator.ActiveOperationCount,
                !_windowClosed, DateTimeOffset.UtcNow));
        }
        catch (Exception diagnosticError) { System.Diagnostics.Debug.WriteLine(diagnosticError); }
    }

    private PlatformDiagnosticOperation BeginPickerDiagnostic(PlatformDiagnosticKind kind)
    {
        CaptureDiagnosticUiSnapshot();
        return new(kind)
        {
            Stage = RuntimeFailureStage.PickerCreate, WindowOpen = !_windowClosed,
            HandlePresent = _windowHandle != IntPtr.Zero,
            Concurrent = Interlocked.Increment(ref _diagnosticPickerCount)
        };
    }

    private async Task<string?> PickOutputDirectoryWithDiagnosticsAsync()
    {
        var diagnostic = BeginPickerDiagnostic(PlatformDiagnosticKind.OutputFolderPicker);
        try
        {
            var path = await _services.Shell.ChooseOutputDirectoryAsync(_windowHandle, diagnostic);
            diagnostic.WindowOpen = !_windowClosed;
            diagnostic.ResultCount = path is null ? 0 : 1;
            diagnostic.Complete(cancelled: path is null);
            return path;
        }
        catch (OperationCanceledException) { diagnostic.Complete(cancelled: true); throw; }
        catch (Exception exception)
        {
            diagnostic.WindowOpen = !_windowClosed;
            CaptureDiagnosticUiSnapshot();
            diagnostic.Fail(exception);
            throw;
        }
        finally { Interlocked.Decrement(ref _diagnosticPickerCount); }
    }
}

internal static class PlatformPackageDiagnostics
{
    internal static void Observe()
    {
        try
        {
            var id = Windows.ApplicationModel.Package.Current.Id;
            var version = id.Version;
            SafeDiagnostics.Write(FormattableString.Invariant(
                $"Package observation: session={RuntimeFailureDiagnostics.SessionId:N} observed_utc={DateTimeOffset.UtcNow:O} packaged=True version={version.Major}.{version.Minor}.{version.Build}.{version.Revision} architecture={id.Architecture} expected_name={string.Equals(id.Name, "zzwdh.WinUI", StringComparison.Ordinal)}\n"));
        }
        catch (InvalidOperationException)
        {
            SafeDiagnostics.Write($"Package observation: session={RuntimeFailureDiagnostics.SessionId:N} observed_utc={DateTimeOffset.UtcNow:O} packaged=False version=none architecture=none expected_name=none\n");
        }
        catch (Exception diagnosticError)
        {
            System.Diagnostics.Debug.WriteLine(diagnosticError);
            SafeDiagnostics.Write($"Package observation: session={RuntimeFailureDiagnostics.SessionId:N} observed_utc={DateTimeOffset.UtcNow:O} packaged=unknown version=none architecture=none expected_name=none\n");
        }
    }
}

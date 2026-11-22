using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private async Task<FrameworkElement> BuildNativeMediaInfoContentAsync(MediaTaskViewModel task)
    {
        var baseContent = BuildMediaInfoContent(task);
        if (baseContent.Content is not TextBlock detailsText)
        {
            return baseContent;
        }

        var shellTask = Task.Run(() => new WindowsShellMetadataService().ReadProperties(task.InputPath));
        var thumbnailTask = new WindowsThumbnailCacheService().GetThumbnailAsync(task.InputPath, 256);
        Task<NativeCapabilityResult<WicImageMetadata>>? imagingTask = task.Kind == MediaKind.Image
            ? new WindowsImagingNativeService().ReadMetadataAsync(task.InputPath)
            : null;
        Task<NativeCapabilityResult<MediaFoundationSourceInfo>>? mediaFoundationTask =
            task.Kind is MediaKind.Video or MediaKind.Audio
                ? new MediaFoundationNativeService().ProbeSourceAsync(task.InputPath)
                : null;

        NativeCapabilityResult<IReadOnlyList<ShellPropertyMetadata>>? shell = null;
        NativeCapabilityResult<ThumbnailPixels>? thumbnail = null;
        NativeCapabilityResult<WicImageMetadata>? imaging = null;
        NativeCapabilityResult<MediaFoundationSourceInfo>? mediaFoundation = null;
        try { shell = await shellTask; } catch { }
        try { thumbnail = await thumbnailTask; } catch { }
        if (imagingTask is not null)
        {
            try { imaging = await imagingTask; } catch { }
        }
        if (mediaFoundationTask is not null)
        {
            try { mediaFoundation = await mediaFoundationTask; } catch { }
        }

        var nativeLines = new List<string>();
        if (imaging?.Value is { } imageMetadata)
        {
            nativeLines.Add(LocalizationService.T(
                "NativeUiWicMetadata",
                imageMetadata.Width,
                imageMetadata.Height,
                imageMetadata.ExifOrientation,
                LocalizationService.T(imageMetadata.HasEmbeddedColorProfile ? "NativeUiYes" : "NativeUiNo")));
        }
        if (mediaFoundation?.Value is { } sourceInfo)
        {
            nativeLines.Add(LocalizationService.T(
                "NativeUiMediaFoundationSource",
                sourceInfo.Duration,
                sourceInfo.Streams.Count));
            nativeLines.AddRange(sourceInfo.Streams.Take(8).Select(stream =>
                LocalizationService.T("NativeUiMediaStreamBase", stream.Kind, stream.SubtypeName) +
                (stream.Width > 0
                    ? LocalizationService.T("NativeUiMediaStreamResolution", stream.Width, stream.Height)
                    : string.Empty) +
                (stream.Channels > 0
                    ? LocalizationService.T("NativeUiMediaStreamAudio", stream.Channels, stream.SamplesPerSecond)
                    : string.Empty)));
        }
        if (shell?.Value is { Count: > 0 } properties)
        {
            var preferredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "System.Title", "System.Subject", "System.Author", "System.Comment",
                "System.Keywords", "System.Rating", "System.Photo.DateTaken",
                "System.Media.Year", "System.Copyright", "System.FileOwner"
            };
            var selectedProperties = properties
                .Where(property => preferredNames.Contains(property.CanonicalName))
                .Take(10)
                .ToArray();
            if (selectedProperties.Length > 0)
            {
                nativeLines.Add(LocalizationService.T("NativeUiShellPropertiesHeading"));
                nativeLines.AddRange(selectedProperties.Select(property =>
                    LocalizationService.T("NativeUiShellProperty", property.CanonicalName, property.Value)));
            }
        }
        if (thumbnail?.Value is { } thumbnailPixels)
        {
            nativeLines.Add(LocalizationService.T(
                "NativeUiThumbnailCache",
                thumbnailPixels.Width,
                thumbnailPixels.Height));
        }

        if (nativeLines.Count > 0)
        {
            detailsText.Text += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, nativeLines);
        }
        if (thumbnail?.Value is not { } pixels)
        {
            return baseContent;
        }

        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(pixels.Bgra32);
        }
        bitmap.Invalidate();
        var preview = new Image
        {
            Source = bitmap,
            Width = Math.Min(240, pixels.Width),
            Height = Math.Min(240, pixels.Height),
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(
            preview,
            LocalizationService.T("NativeUiThumbnailAutomationName", task.FileName));
        var layout = new Grid { MinWidth = 700, ColumnSpacing = 18 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(baseContent, 1);
        layout.Children.Add(preview);
        layout.Children.Add(baseContent);
        return layout;
    }

    private void WindowsAccessibilityService_Changed(object? sender, WindowsAccessibilityChangedEventArgs e)
    {
        if (_windowClosed) return;
        if (e.Current.HighContrastEnabled)
        {
            RootGrid.RequestedTheme = ElementTheme.Default;
        }
        else
        {
            ApplyTheme();
        }
    }

    private void ApplyExecutionResourcePolicy()
    {
        if (!_windowClosed)
            _queueCoordinator.ConfigureParallelism(PerformanceTuning.ResolveParallelCount(_settings, _systemPowerStateService.Current));
    }

    private void SystemPowerStateService_StateChanged(object? sender, SystemPowerStateChangedEventArgs e)
    {
        if (_windowClosed) return;
        ApplyExecutionResourcePolicy();
    }

    private async void SystemPowerStateService_Transitioned(object? sender, SystemPowerTransitionEventArgs e)
    {
        if (_windowClosed) return;
        try
        {
            if (e.Transition == SystemPowerTransition.Suspending)
            {
                await SaveQueueAsync();
                LogService.WriteApp(LocalizationService.T("OperationalLogSystemSuspendingCheckpointSaved"));
                return;
            }

            ApplyExecutionResourcePolicy();
            await RefreshOpticalEngineStatusAsync(autoDetect: false);
            LogService.WriteApp(LocalizationService.T("OperationalLogSystemResumedCapabilitiesRefreshed"));
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
    }

    private async void OpticalDeviceWatcher_Changed(object? sender, OpticalDeviceChangeEventArgs e)
    {
        if (_windowClosed) return;
        try
        {
            SafeDiagnostics.Write(LocalizationService.T("OperationalLogOpticalDeviceChanged", e.Kind, e.DevicePath));
            await Task.Delay(350);
            if (_windowClosed) return;
            await RefreshOpticalEngineStatusAsync(autoDetect: false);
            RefreshTaskEngineAvailability();
        }
        catch (Exception exception) { SafeDiagnostics.Report(exception); }
    }

}

using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace FormatFactoryWinUI;

public partial class App
{
    private int _feedbackHttpRequests;

    private sealed class SmokeFeedbackHandler(App app) : System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref app._feedbackHttpRequests);
            var body = await request.Content!.ReadAsByteArrayAsync(token);
            var newline = Array.IndexOf(body, (byte)'\n');
            using var header = System.Text.Json.JsonDocument.Parse(body.AsMemory(0, newline));
            var eventId = header.RootElement.GetProperty("event_id").GetString();
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { id = eventId }))
            };
        }
    }

    private async Task CheckFeedbackActionsAsync()
    {
        await CheckAsync("Feedback controls forward actions, preserve error state across languages and export reviewed diagnostics", async () =>
        {
            Require(Path.GetFullPath(LogService.AppDataRoot).StartsWith(
                Path.TrimEndingDirectorySeparator(_runRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Feedback checks must use the isolated smoke data root.");
            var view = new SettingsView();
            var sends = 0;
            var reports = 0;
            var exports = 0;
            view.SendFeedbackButton_ClickRequested += (_, _) => sends++;
            view.ReportErrorButton_ClickRequested += (_, _) => reports++;
            view.ExportErrorReportButton_ClickRequested += (_, _) => exports++;
            foreach (var action in new[] { "SendFeedbackButton_Click", "ReportErrorButton_Click", "ExportErrorReportButton_Click" })
                typeof(SettingsView).GetMethod(action, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(view, [view, new RoutedEventArgs()]);
            Require(sends == 1 && reports == 1 && exports == 1, "A feedback button lost its action event.");
            var originalLanguage = LocalizationService.CurrentUICulture.Name;
            try
            {
                view.SetFeedbackState(true, busy: true);
                foreach (var language in new[] { "en-US", "zh-Hans", "ar-SA" })
                {
                    LocalizationService.SetLanguage(language);
                    view.ApplyLocalization(new AppSettings { Language = language });
                    Require(view.FeedbackTitleText.Text == LocalizationService.T("FeedbackTitle")
                        && view.FeedbackStatusText.Text == LocalizationService.T("FeedbackPendingError"),
                        "Feedback localization lost the pending error status.");
                    Require(!view.SendFeedbackButton.IsEnabled && !view.ReportErrorButton.IsEnabled
                        && !view.ExportErrorReportButton.IsEnabled, "Localization enabled a busy feedback action.");
                }
                view.SetFeedbackState(true);
                Require(view.SendFeedbackButton.IsEnabled && view.ReportErrorButton.IsEnabled
                    && view.ExportErrorReportButton.IsEnabled, "Feedback controls did not re-enable after completion.");
                RequireEngineOptionOwnership(view);
            }
            finally { LocalizationService.SetLanguage(originalLanguage); }

            Invoke("RecordFeedbackError", "Synthetic conversion error C:\\Users\\smoke-user\\private.mov", null!, null!);
            var settings = (SettingsView)Field("SettingsView")!;
            Require(settings.HasPendingFeedbackError, "A conversion error did not appear in settings.");
            var service = (FeedbackReportService)Field("_feedbackReports")!;
            using var report = await service.CreateAsync("Smoke report", includeLogs: true, "smoke");
            Require(report.Preview.Contains("Synthetic conversion error", StringComparison.Ordinal)
                && !report.Preview.Contains("private.mov", StringComparison.Ordinal),
                "The exported error report did not preserve the failure while masking its file path.");
            var oldFailure = new MediaTask { State = ConversionState.Failed, LastError = "previous-session failure" };
            Invoke("QueueSession_TaskAdded", oldFailure);
            Invoke("QueueSession_TaskStateChanged", oldFailure);
            using (var unchanged = await service.CreateAsync("", true, "smoke"))
                Require(unchanged.Preview.Contains("Synthetic conversion error", StringComparison.Ordinal)
                    && !unchanged.Preview.Contains("previous-session failure", StringComparison.Ordinal),
                    "Restoring a failed queue entry replaced the latest diagnostic snapshot.");
            service.ClearPendingError(report);
            Invoke("RefreshFeedbackState");
            Require(!settings.HasPendingFeedbackError, "A successfully acknowledged snapshot remained pending.");
            Invoke("RemoveTask", _window!.Tasks.Single(task => task.Id == oldFailure.Id));
        });
    }

    private async Task CheckLoadedFeedbackAsync()
    {
        await CheckAsync("Loaded feedback sends in the background without a report preview or delivery notification", async () =>
        {
            Task? operation = null;
            try
            {
                operation = (Task)Invoke("OpenFeedbackAsync", true, false)!;
                var compose = await WaitForFeedbackDialogAsync(null);
                var form = (StackPanel)compose.Content;
                form.Children.OfType<TextBox>().Single().Text = "Conversion failed. Please investigate.";
                Require(compose.PrimaryButtonText == LocalizationService.T("FeedbackSend")
                    && form.Children.OfType<TextBox>().All(box => !box.IsReadOnly),
                    "Feedback still required reviewing a diagnostic report.");
                await SaveFeedbackDialogCaptureAsync(compose);
                var beforeRequests = Volatile.Read(ref _feedbackHttpRequests);
                var settings = (SettingsView)Field("SettingsView")!;
                var previousInfo = settings.SettingsInfoBar.Message;
                var button = FindFeedbackPrimaryButton(compose);
                var provider = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
                provider.Invoke();
                await operation.WaitAsync(TimeSpan.FromSeconds(5));
                for (var attempt = 0; attempt < 100 && (int)Field("_manualFeedbackSending")! != 0; attempt++)
                    await Task.Delay(50);
                Require(Volatile.Read(ref _feedbackHttpRequests) == beforeRequests + 1,
                    "The single feedback form did not enqueue its report for the isolated receiver.");
                Require(settings.SettingsInfoBar.Message == previousInfo,
                    "Background feedback displayed a delivery success/failure notification.");
                Require(!(bool)Field("_feedbackInProgress")! && Field("_feedbackDialog") is null,
                    "Submitting feedback left a modal report preview.");
            }
            finally
            {
                await (Task)Invoke("DismissFeedbackAsync")!;
                if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    private async Task<ContentDialog> WaitForFeedbackDialogAsync(ContentDialog? previous)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(50);
            if (Field("_feedbackDialog") is ContentDialog dialog && !ReferenceEquals(dialog, previous) && dialog.IsLoaded)
                return dialog;
        }
        throw new TimeoutException("The feedback dialog did not open.");
    }

    private static Button FindFeedbackPrimaryButton(DependencyObject element)
    {
        if (element is Button button && button.Name == "PrimaryButton") return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            try { return FindFeedbackPrimaryButton(VisualTreeHelper.GetChild(element, index)); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("The feedback primary button is not loaded.");
    }

    private async Task SaveFeedbackDialogCaptureAsync(ContentDialog dialog)
    {
        await Task.Delay(180);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(dialog);
        Require(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "The feedback preview render is empty.");
        var folder = await StorageFolder.GetFolderFromPathAsync(Directory.CreateDirectory(Path.Combine(_runRoot, "captures")).FullName);
        var file = await folder.CreateFileAsync("feedback-form.png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
    }
}

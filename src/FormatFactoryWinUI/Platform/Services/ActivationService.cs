using FormatFactoryWinUI.Models;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

namespace FormatFactoryWinUI.Services;

internal static class ActivationService
{
    public static async Task<ActivationRequest> FromAppActivationAsync(AppActivationArguments? activation, string? launchArguments = null,
        ActivationDeliveryObservation? observation = null)
    {
        ActivationRequest Track(ActivationRequest request, ActivationArgumentSource source) => observation is null ? request :
            request with { Delivery = observation with { Source = source,
                OriginalHasWork = source == ActivationArgumentSource.Broker && (request.HasWork || observation.Kind == ActivationDiagnosticKind.Launch)
                    ? request.HasWork : null } };
        ActivationRequest Fallback() => Track(FromLaunchArguments(launchArguments), ActivationArgumentSource.OrdinaryFallback);
        if (activation is null)
        {
            return Fallback();
        }

        try
        {
            return activation.Kind switch
            {
                ExtendedActivationKind.File when activation.Data is IFileActivatedEventArgs fileArgs =>
                    Track(new ActivationRequest(fileArgs.Files.OfType<StorageFile>().Select(file => file.Path).ToArray()), ActivationArgumentSource.Broker),
                ExtendedActivationKind.Protocol when activation.Data is IProtocolActivatedEventArgs protocolArgs =>
                    Track(FromProtocol(protocolArgs.Uri), ActivationArgumentSource.Broker),
                ExtendedActivationKind.AppNotification when activation.Data is AppNotificationActivatedEventArgs notificationArgs =>
                    Track(FromNotification(notificationArgs), ActivationArgumentSource.Broker),
                ExtendedActivationKind.ShareTarget when activation.Data is ShareTargetActivatedEventArgs shareArgs =>
                    Track(await FromShareTargetAsync(shareArgs), ActivationArgumentSource.Broker),
                ExtendedActivationKind.Launch when activation.Data is ILaunchActivatedEventArgs launchArgs =>
                    Track(FromLaunchArguments(launchArgs.Arguments), ActivationArgumentSource.Broker),
                _ => Fallback()
            };
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogWindowsActivationArgumentsReadFailed", ex.Message));
            return Fallback();
        }
    }

    public static ActivationRequest FromNotification(AppNotificationActivatedEventArgs args)
        => ActivationArgumentParser.FromNotificationArguments(args.Arguments);

    public static ActivationRequest FromLaunchArguments(string? arguments)
        => ActivationArgumentParser.FromLaunchArguments(
            arguments,
            FormatCatalog.SupportedExtensions,
            batchResolver: ActivationBatchStore.ReadAndDelete);

    public static string BuildJumpListOutputArgument(string path) =>
        ActivationArgumentParser.BuildJumpListOutputArgument(path);

    private static async Task<ActivationRequest> FromShareTargetAsync(ShareTargetActivatedEventArgs args)
    {
        var operation = args.ShareOperation;
        try
        {
            operation.ReportStarted();
            var items = await operation.Data.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>()
                .Select(file => file.Path)
                .Where(path => FormatCatalog.SupportedExtensions.Contains(Path.GetExtension(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                operation.ReportError("No supported files were shared.");
                return new ActivationRequest([]);
            }

            return new ActivationRequest(paths, Lifetime: new ShareActivationLifetime(operation));
        }
        catch (Exception ex)
        {
            TryReportShareError(operation, "The shared files could not be read.");
            LogService.WriteApp($"Share Target failed: {ex.Message}");
            return new ActivationRequest([]);
        }
    }

    private static ActivationRequest FromProtocol(Uri uri)
        => ActivationArgumentParser.FromProtocol(
            uri,
            FormatCatalog.SupportedExtensions,
            batchResolver: ActivationBatchStore.ReadAndDelete);

    private sealed class ShareActivationLifetime(ShareOperation operation) : IActivationLifetime
    {
        private int _completed;

        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                try
                {
                    operation.ReportCompleted();
                }
                catch (Exception ex)
                {
                    LogService.WriteApp($"Share Target completion failed: {ex.Message}");
                }
            }
        }

        public void Fail(string message)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                TryReportShareError(operation, message);
            }
        }
    }

    private static void TryReportShareError(ShareOperation operation, string message)
    {
        try
        {
            operation.ReportError(message);
        }
        catch (Exception ex)
        {
            LogService.WriteApp($"Share Target error reporting failed: {ex.Message}");
        }
    }
}

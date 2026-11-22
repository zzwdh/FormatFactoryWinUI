using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;
using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("D4C8B1A2-3E5F-4D77-9A10-6B2C4E8F0D31")]
public sealed class FormatFactoryAppService : IBackgroundTask
{
    private readonly object _lifetimeGate = new();
    private IBackgroundTaskInstance? _taskInstance;
    private AppServiceConnection? _connection;
    private BackgroundTaskDeferral? _lifetimeDeferral;
    private CancellationTokenSource? _serviceCancellation;
    private bool _stopped;

    public void Run(IBackgroundTaskInstance taskInstance)
    {
        WindowsAppDataStorage.Initialize();
        if (taskInstance.TriggerDetails is not AppServiceTriggerDetails details)
        {
            return;
        }

        var deferral = taskInstance.GetDeferral();
        lock (_lifetimeGate)
        {
            _taskInstance = taskInstance;
            _connection = details.AppServiceConnection;
            _lifetimeDeferral = deferral;
            _serviceCancellation = new CancellationTokenSource();
        }

        taskInstance.Canceled += TaskInstance_Canceled;
        details.AppServiceConnection.RequestReceived += Connection_RequestReceived;
        details.AppServiceConnection.ServiceClosed += Connection_ServiceClosed;
    }

    private async void Connection_RequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = AppServiceProtocol.Parse(
                args.Request.Message.Select(pair =>
                    new KeyValuePair<string, object?>(pair.Key, pair.Value)));
            if (!request.IsValid)
            {
                await args.Request.SendResponseAsync(CreateResponse(request, false, request.ErrorCode));
                return;
            }

            var cancellationToken = _serviceCancellation?.Token ?? CancellationToken.None;
            var discoveredCount = 0;
            if (request.Command.Equals("scan-watch-folder", StringComparison.OrdinalIgnoreCase))
            {
                discoveredCount = await BackgroundTaskWorker.ScanWatchFolderAsync(cancellationToken);
            }
            else if (!AppServiceProtocol.IsStateQuery(request.Command))
            {
                await args.Request.SendResponseAsync(CreateResponse(
                    request,
                    false,
                    AppServiceErrorCodes.UnknownCommand));
                return;
            }

            var queue = await new QueueStore().LoadAsync(cancellationToken);
            var response = CreateResponse(request, true, AppServiceErrorCodes.None);
            response["pendingCount"] = queue.Count;
            response["discoveredCount"] = discoveredCount;
            await args.Request.SendResponseAsync(response);
        }
        catch (OperationCanceledException)
        {
            try
            {
                var request = AppServiceProtocol.Parse(
                    args.Request.Message.Select(pair =>
                        new KeyValuePair<string, object?>(pair.Key, pair.Value)));
                await args.Request.SendResponseAsync(CreateResponse(
                    request,
                    false,
                    AppServiceErrorCodes.Canceled));
            }
            catch (Exception responseException)
            {
                LogService.WriteApp(LocalizationService.T("OperationalLogAppServiceCancelResponseFailed", responseException.Message));
            }
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogAppServiceRequestFailed", ex.Message));
            try
            {
                var request = AppServiceProtocol.Parse(
                    args.Request.Message.Select(pair =>
                        new KeyValuePair<string, object?>(pair.Key, pair.Value)));
                await args.Request.SendResponseAsync(CreateResponse(
                    request,
                    false,
                    AppServiceErrorCodes.InternalError));
            }
            catch (Exception responseException)
            {
                LogService.WriteApp(LocalizationService.T("OperationalLogAppServiceErrorResponseFailed", responseException.Message));
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void TaskInstance_Canceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
    {
        LogService.WriteApp(LocalizationService.T("OperationalLogAppServiceBackgroundTaskCancelled", reason));
        Stop();
    }

    private void Connection_ServiceClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
    {
        LogService.WriteApp(LocalizationService.T("OperationalLogAppServiceConnectionClosed", args.Status));
        Stop();
    }

    private void Stop()
    {
        IBackgroundTaskInstance? taskInstance;
        AppServiceConnection? connection;
        BackgroundTaskDeferral? deferral;
        CancellationTokenSource? cancellation;
        lock (_lifetimeGate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            taskInstance = _taskInstance;
            connection = _connection;
            deferral = _lifetimeDeferral;
            cancellation = _serviceCancellation;
            _taskInstance = null;
            _connection = null;
            _lifetimeDeferral = null;
            _serviceCancellation = null;
        }

        cancellation?.Cancel();
        if (taskInstance is not null)
        {
            taskInstance.Canceled -= TaskInstance_Canceled;
        }
        if (connection is not null)
        {
            connection.RequestReceived -= Connection_RequestReceived;
            connection.ServiceClosed -= Connection_ServiceClosed;
        }
        deferral?.Complete();
    }

    private static ValueSet CreateResponse(
        AppServiceCommandRequest request,
        bool ok,
        string errorCode) =>
        new()
        {
            ["schemaVersion"] = AppServiceProtocol.CurrentSchemaVersion,
            ["requestId"] = request.RequestId,
            ["command"] = request.Command,
            ["ok"] = ok,
            ["errorCode"] = errorCode
        };
}

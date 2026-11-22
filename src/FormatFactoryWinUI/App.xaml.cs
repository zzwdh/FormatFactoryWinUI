using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Composition;

namespace FormatFactoryWinUI;

public partial class App : Application
{
    public static MainWindow? MainWindowInstance { get; private set; }
    private readonly ActivationRouter _activationRouter = new();
    private readonly DesktopApplicationServices _services;
    private readonly StartupReadinessMetrics _startupMetrics = new(measureFromProcessStart: true);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception, object> RecordedUnhandledErrors = new();

    public App()
    {
        ConfigureStartupMeasurement();
        _startupMetrics.Mark("app-constructor");
        using var applicationInitialization = _startupMetrics.MeasureStage("application-initialization");
        _services = DesktopApplicationServices.Create();
        InitializeComponent();
        PlatformPackageDiagnostics.Observe();
        _activationRouter.RoutingFailed += (_, exception) => Services.SafeDiagnostics.Report(exception);
        Services.AppNotificationService.ActivationRequested += AppNotificationService_ActivationRequested;
        Services.AppNotificationService.Initialize();
        Services.RecoveryService.Register();
        UnhandledException += (_, e) =>
        {
            RecordUnhandledError(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception) RecordUnhandledError(exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => RecordUnhandledError(e.Exception);
    }

    // Test-only partial implementations isolate data and observe the real launch path.
    // Calls are erased from the production build.
    partial void ConfigureStartupMeasurement();
    partial void ObserveStartupMeasurement(MainWindow window);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.Activation)
        { Stage = RuntimeFailureStage.ActivationRead };
        using var launchSetup = _startupMetrics.MeasureStage("activation-and-storage-recovery");
        var activationArguments = await ActivationArgumentsReader.ReadAsync(
            () => AppInstance.GetCurrent().GetActivatedEventArgs(), RecordUnhandledError, diagnosticOperation: diagnostic.Id);
        diagnostic.ActivationKind = DiagnosticActivationKind(activationArguments);
        var delivery = new ActivationDeliveryObservation(diagnostic.Id, diagnostic.ActivationKind,
            activationArguments is null ? ActivationArgumentSource.Unknown : ActivationArgumentSource.Broker);
        delivery.Write(ActivationDeliveryStage.ArgumentsRead);
        diagnostic.Stage = RuntimeFailureStage.ActivationRegister;
        AppInstance instance;
        try { instance = AppInstance.FindOrRegisterForKey("zzwdh.WinUI"); }
        catch (Exception exception) { diagnostic.Fail(exception); throw; }
        var isCurrent = instance.IsCurrent;
        diagnostic.PrimaryInstance = isCurrent;
        delivery.Write(ActivationDeliveryStage.Registered, primary: isCurrent);
        if (!isCurrent)
        {
            if (activationArguments is not null)
            {
                diagnostic.Stage = RuntimeFailureStage.ActivationRedirect;
                delivery.Write(ActivationDeliveryStage.RedirectRequested, primary: false);
                try { await instance.RedirectActivationToAsync(activationArguments); }
                catch (Exception exception) { diagnostic.Fail(exception); throw; }
                delivery.Write(ActivationDeliveryStage.RedirectReturned, primary: false);
            }
            else delivery.Write(ActivationDeliveryStage.RedirectSkipped, primary: false);
            diagnostic.Complete();
            return;
        }

        instance.Activated -= Instance_Activated;
        instance.Activated += Instance_Activated;

        diagnostic.Stage = RuntimeFailureStage.ActivationDispatch;
        var initialActivation = ResolveActivationWithDiagnosticsAsync(activationArguments, args.Arguments, diagnostic, delivery);
        var queued = _activationRouter.Enqueue(async () => await initialActivation);
        var activation = await initialActivation;
        activation.Delivery?.Write(queued ? ActivationDeliveryStage.Queued : ActivationDeliveryStage.QueueRejected,
            primary: true, routedWork: activation.HasWork);
        await StorageTokenRegistry.RecoverAsync();

        var launchArguments = activationArguments?.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs
            ? launchArgs.Arguments : args.Arguments;
        var launchOptions = launchArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var startupLaunch = launchOptions
            .Any(argument => argument.Equals("--startup", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("--startup-tray", StringComparison.OrdinalIgnoreCase));
        startupLaunch = startupLaunch || activationArguments?.Kind == ExtendedActivationKind.StartupTask;
        var startInTray = false;
        if (startupLaunch)
        {
            var settings = await _services.Settings.LoadAsync();
            startInTray = settings.StartWithWindows && settings.StartInTray;
        }

        // Redirected activations have already returned above. Only a user-initiated cold
        // start counts; startup tasks and background activations never register a launch.
        var userLaunch = !startupLaunch
            && !launchOptions.Contains("--restore", StringComparer.OrdinalIgnoreCase)
            && (activationArguments is null || activationArguments.Kind is
            ExtendedActivationKind.Launch or ExtendedActivationKind.File or ExtendedActivationKind.Protocol
            or ExtendedActivationKind.ShareTarget or ExtendedActivationKind.AppNotification);
        var reviewReminderRegistration = userLaunch && StoreReviewService.IsStoreInstalled
            ? RegisterReviewLaunchAsync()
            : null;
        launchSetup.Dispose();
        MainWindowInstance = _services.CreateMainWindow(startInTray, reviewReminderRegistration, _startupMetrics);
        ObserveStartupMeasurement(MainWindowInstance);
        _activationRouter.Attach(MainWindowInstance.DispatchActivationAsync);
        if (startInTray && !activation.HasWork)
        {
            MainWindowInstance.HideBeforeActivation();
        }
        MainWindowInstance.Activate();
        MainWindowInstance.CaptureDiagnosticUiSnapshot();
        if (!activation.HasWork && activation.Delivery is { } startupDelivery)
            _ = ObserveWindowReadinessAsync(MainWindowInstance.ActivationReadiness, startupDelivery);
    }

    private static void RecordUnhandledError(Exception exception)
    {
        try
        {
            // WinUI and AppDomain can surface the same exception. Keep one report without retaining it.
            if (!RecordedUnhandledErrors.TryAdd(exception, new object())) return;
            MainWindowInstance?.CaptureDiagnosticUiSnapshot();
            PlatformPackageDiagnostics.Observe();
            Services.SafeDiagnostics.Report(exception);
            // A live window records the diagnostic; startup failures still need local persistence.
            if (MainWindowInstance is not { IsOpenForDiagnostics: true })
                new FeedbackReportService().RecordException(exception);
        }
        catch (Exception diagnosticError) { System.Diagnostics.Debug.WriteLine(diagnosticError); }
    }

    private async Task<bool> RegisterReviewLaunchAsync()
    {
        try { return await _services.ReviewReminders.RegisterLaunchAsync(DateTimeOffset.Now); }
        catch (Exception exception)
        {
            Services.SafeDiagnostics.Report(exception);
            return false;
        }
    }

    private void Instance_Activated(object? sender, AppActivationArguments args)
    {
        var diagnostic = new PlatformDiagnosticOperation(PlatformDiagnosticKind.Activation)
        { Stage = RuntimeFailureStage.ActivationDispatch, ActivationKind = DiagnosticActivationKind(args), PrimaryInstance = true };
        var delivery = new ActivationDeliveryObservation(diagnostic.Id, diagnostic.ActivationKind, ActivationArgumentSource.Broker);
        delivery.Write(ActivationDeliveryStage.ReceiverArrived, primary: true);
        var queued = _activationRouter.Enqueue(() => ResolveActivationWithDiagnosticsAsync(args, null, diagnostic, delivery));
        delivery.Write(queued ? ActivationDeliveryStage.Queued : ActivationDeliveryStage.QueueRejected, primary: true);
    }

    private static async Task<ActivationRequest> ResolveActivationWithDiagnosticsAsync(AppActivationArguments? args,
        string? ordinaryArguments, PlatformDiagnosticOperation diagnostic, ActivationDeliveryObservation delivery)
    {
        try
        {
            var request = await ActivationService.FromAppActivationAsync(args, ordinaryArguments, delivery);
            diagnostic.HasWork = request.HasWork;
            request.Delivery?.Write(ActivationDeliveryStage.Resolved, primary: true, routedWork: request.HasWork);
            diagnostic.Complete();
            return request;
        }
        catch (Exception exception) { diagnostic.Fail(exception); throw; }
    }

    private static async Task ObserveWindowReadinessAsync(Task readiness, ActivationDeliveryObservation delivery)
    {
        try
        {
            await readiness.ConfigureAwait(false);
            delivery.Write(ActivationDeliveryStage.WindowReady, primary: true, routedWork: false);
        }
        catch (OperationCanceledException)
        {
            delivery.Write(ActivationDeliveryStage.WindowClosed, primary: true, routedWork: false);
        }
        catch (Exception error)
        {
            delivery.Write(ActivationDeliveryStage.DispatchFailed, primary: true, routedWork: false, error: error);
        }
    }

    private static ActivationDiagnosticKind DiagnosticActivationKind(AppActivationArguments? args)
    {
        try
        {
            return args?.Kind switch
            {
                ExtendedActivationKind.Launch => ActivationDiagnosticKind.Launch,
                ExtendedActivationKind.File => ActivationDiagnosticKind.File,
                ExtendedActivationKind.Protocol => ActivationDiagnosticKind.Protocol,
                ExtendedActivationKind.ShareTarget => ActivationDiagnosticKind.ShareTarget,
                ExtendedActivationKind.StartupTask => ActivationDiagnosticKind.StartupTask,
                ExtendedActivationKind.AppNotification => ActivationDiagnosticKind.AppNotification,
                null => ActivationDiagnosticKind.Unknown,
                _ => ActivationDiagnosticKind.Other
            };
        }
        catch (Exception diagnosticError)
        {
            System.Diagnostics.Debug.WriteLine(diagnosticError);
            return ActivationDiagnosticKind.Unknown;
        }
    }

    private void AppNotificationService_ActivationRequested(object? sender, ActivationRequest request)
    {
        if (!request.HasWork)
        {
            return;
        }

        _activationRouter.Enqueue(request);
    }
}

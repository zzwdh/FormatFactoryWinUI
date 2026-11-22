using System.Reflection;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    internal bool IsOpenForDiagnostics => !_windowClosed;
    private readonly object _telemetryGate = new();
    private readonly CancellationTokenSource _telemetryLifetime = new();
    private TelemetryService? _telemetry;
    private bool _telemetrySettingsLoaded;
    private bool _usageTelemetryEnabled;
    private bool _automaticErrorsEnabled;
    private bool _startupReadyTelemetryRecorded;
    private int _usagePreferenceVersion;
    private int _errorPreferenceVersion;

    private void InitializeTelemetry()
    {
        _telemetrySettingsLoaded = true;
        ApplyTelemetryPreferencesFromSettings();
        if (!_services.EnableRemoteTelemetry) return;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        // Configuration, report preparation and network work never delay startup or navigation.
        _ = Task.Run(async () =>
        {
            try
            {
                var configuration = await FeedbackDeliveryService.LoadConfigurationAsync(
                    Path.Combine(AppContext.BaseDirectory, "feedback-config.json"), _telemetryLifetime.Token);
                var service = new TelemetryService(_feedbackReports, configuration, version);
                lock (_telemetryGate)
                {
                    if (_telemetryLifetime.IsCancellationRequested)
                    {
                        service.Dispose();
                        return;
                    }
                    _telemetry = service;
                    service.Configure(_usageTelemetryEnabled, _automaticErrorsEnabled);
                    service.TrackAppLaunch();
                    PublishStartupReadyTelemetryLocked();
                }
            }
            catch (Exception exception)
            {
                // Telemetry failures must not create error-report loops or user notifications.
                System.Diagnostics.Debug.WriteLine($"Telemetry startup: {exception.GetType().Name}");
            }
        });
    }

    private void PublishStartupReadyTelemetry()
    {
        lock (_telemetryGate)
            PublishStartupReadyTelemetryLocked();
    }

    private void PublishStartupReadyTelemetryLocked()
    {
        if (_telemetry is not { } telemetry || _startupReadyTelemetryRecorded ||
            !_startupMetrics.Snapshot().TryGetValue("add-files-ready", out var elapsedMilliseconds)) return;

        _startupReadyTelemetryRecorded = true;
        telemetry.TrackStartupReady(elapsedMilliseconds);
    }

    private static TelemetryConversionResult ToTelemetryConversionResult(ConversionOutcome outcome) => outcome switch
    {
        ConversionOutcome.Completed => TelemetryConversionResult.Succeeded,
        ConversionOutcome.Cancelled => TelemetryConversionResult.Cancelled,
        _ => TelemetryConversionResult.Failed
    };

    private void ApplyTelemetryPreferencesFromSettings()
    {
        if (!_telemetrySettingsLoaded) return;
        ++_usagePreferenceVersion;
        ++_errorPreferenceVersion;
        SetTelemetryPreferences(_settings.EnableUsageTelemetry, _settings.EnableAutomaticErrorReporting);
    }

    private void SetTelemetryPreferences(bool usage, bool errors)
    {
        lock (_telemetryGate)
        {
            _usageTelemetryEnabled = usage;
            _automaticErrorsEnabled = errors;
            _telemetry?.Configure(usage, errors);
        }
    }

    private async void UsageTelemetryToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_updatingLocalization || _initializing || _windowClosed) return;
        var enabled = SettingsView.UsageTelemetryToggle.IsOn;
        var revision = ++_usagePreferenceVersion;
        // Opt-out takes effect before disk persistence, including cancellation of in-flight work.
        if (!enabled) SetTelemetryPreferences(false, _automaticErrorsEnabled);
        var saved = await CommitImmediateSettingAsync(settings => settings.EnableUsageTelemetry = enabled);
        if (_windowClosed || revision != _usagePreferenceVersion) return;
        if (saved) SetTelemetryPreferences(enabled, _automaticErrorsEnabled);
        else if (!enabled) _settings.EnableUsageTelemetry = false;
        else
        {
            _updatingLocalization = true;
            try { SettingsView.UsageTelemetryToggle.IsOn = _usageTelemetryEnabled; }
            finally { _updatingLocalization = false; }
        }
    }

    private async void AutomaticErrorReportingToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_updatingLocalization || _initializing || _windowClosed) return;
        var enabled = SettingsView.AutomaticErrorReportingToggle.IsOn;
        var revision = ++_errorPreferenceVersion;
        if (!enabled) SetTelemetryPreferences(_usageTelemetryEnabled, false);
        var saved = await CommitImmediateSettingAsync(settings => settings.EnableAutomaticErrorReporting = enabled);
        if (_windowClosed || revision != _errorPreferenceVersion) return;
        if (saved) SetTelemetryPreferences(_usageTelemetryEnabled, enabled);
        else if (!enabled) _settings.EnableAutomaticErrorReporting = false;
        else
        {
            _updatingLocalization = true;
            try { SettingsView.AutomaticErrorReportingToggle.IsOn = _automaticErrorsEnabled; }
            finally { _updatingLocalization = false; }
        }
    }

    private void TrackTelemetryPage(string tag)
    {
        var page = tag switch
        {
            "all" => TelemetryPage.Queue,
            "video" => TelemetryPage.Video,
            "audio" => TelemetryPage.Audio,
            "image" => TelemetryPage.Image,
            "document" => TelemetryPage.Document,
            "ebook" => TelemetryPage.Ebook,
            "disc" => TelemetryPage.Disc,
            "automation" => TelemetryPage.Automation,
            "settings" => TelemetryPage.Settings,
            "history" => TelemetryPage.History,
            "advanced" => TelemetryPage.Advanced,
            _ => TelemetryPage.Home
        };
        _telemetry?.TrackPageVisit(page);
    }

    private void OnDiagnosticError(Exception exception)
    {
        if (!_windowClosed) RecordFeedbackError(exception.ToString(), exception: exception);
    }

    private void DisposeTelemetry()
    {
        _telemetryLifetime.Cancel();
        lock (_telemetryGate)
        {
            _telemetry?.Dispose();
            _telemetry = null;
        }
    }
}

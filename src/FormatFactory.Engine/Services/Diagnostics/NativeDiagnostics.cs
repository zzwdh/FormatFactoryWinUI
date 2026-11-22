using System.Diagnostics.Tracing;

namespace FormatFactoryWinUI.Services;

/// <summary>ETW provider for native integration and conversion infrastructure.</summary>
[EventSource(Name = "zzwdh-FormatFactoryWinUI-Native")]
internal sealed class NativeDiagnostics : EventSource
{
    public static NativeDiagnostics Log { get; } = new();

    private NativeDiagnostics()
    {
    }

    [Event(1, Level = EventLevel.Informational, Keywords = Keywords.Lifecycle)]
    public void ServiceStarted(string serviceName) => WriteEvent(1, serviceName ?? string.Empty);

    [Event(2, Level = EventLevel.Informational, Keywords = Keywords.Lifecycle)]
    public void ServiceStopped(string serviceName) => WriteEvent(2, serviceName ?? string.Empty);

    [Event(3, Level = EventLevel.Warning, Keywords = Keywords.Native)]
    public void NativeOperationFailed(string component, int errorCode, string operation) =>
        WriteEvent(3, component ?? string.Empty, errorCode, operation ?? string.Empty);

    [Event(4, Level = EventLevel.Informational, Keywords = Keywords.Device)]
    public void OpticalDeviceChanged(string changeKind, string devicePath) =>
        WriteEvent(4, changeKind ?? string.Empty, devicePath ?? string.Empty);

    [Event(5, Level = EventLevel.Informational, Keywords = Keywords.Power)]
    public void PowerStateChanged(
        string source,
        int batteryPercent,
        bool energySaverEnabled) =>
        WriteEvent(5, source ?? string.Empty, batteryPercent, energySaverEnabled);

    [Event(6, Level = EventLevel.Informational, Keywords = Keywords.Power)]
    public void PowerTransition(string transition) => WriteEvent(6, transition ?? string.Empty);

    [Event(7, Level = EventLevel.Verbose, Keywords = Keywords.FileSystem)]
    public void DirectoryChanged(string changeKind, string path) =>
        WriteEvent(7, changeKind ?? string.Empty, path ?? string.Empty);

    [Event(8, Level = EventLevel.Informational, Keywords = Keywords.Process)]
    public void ProcessPolicyApplied(int processId, bool ecoQos, bool belowNormalPriority) =>
        WriteEvent(8, processId, ecoQos, belowNormalPriority);

    [Event(9, Level = EventLevel.Informational, Keywords = Keywords.Recovery)]
    public void RecoveryRegistrationChanged(bool registered, int restartResult, int recoveryResult) =>
        WriteEvent(9, registered, restartResult, recoveryResult);

    [Event(10, Level = EventLevel.Verbose, Keywords = Keywords.Conversion)]
    public void ConversionStage(string taskId, string stage, double percent) =>
        WriteEvent(10, taskId ?? string.Empty, stage ?? string.Empty, percent);

    [Event(11, Level = EventLevel.Informational, Keywords = Keywords.Conversion)]
    public void QueueWait(string taskId, long milliseconds) => WriteEvent(11, taskId, milliseconds);

    [Event(12, Level = EventLevel.Informational, Keywords = Keywords.Conversion)]
    public void ConversionFinished(string taskId, string engineId, string outcome, long milliseconds) =>
        WriteEvent(12, taskId, engineId, outcome, milliseconds);

    [Event(13, Level = EventLevel.Informational, Keywords = Keywords.Conversion)]
    public void EncoderAttempt(string taskId, string backend, bool hardwareDecode, int attemptNumber, bool fallback) =>
        WriteEvent(13, taskId, backend, hardwareDecode, attemptNumber, fallback);

    [Event(14, Level = EventLevel.Informational, Keywords = Keywords.Recovery)]
    public void PersistenceCompleted(string store, bool success, long milliseconds) =>
        WriteEvent(14, store, success, milliseconds);

    public static class Keywords
    {
        public const EventKeywords Lifecycle = (EventKeywords)0x1;
        public const EventKeywords Native = (EventKeywords)0x2;
        public const EventKeywords Device = (EventKeywords)0x4;
        public const EventKeywords Power = (EventKeywords)0x8;
        public const EventKeywords FileSystem = (EventKeywords)0x10;
        public const EventKeywords Process = (EventKeywords)0x20;
        public const EventKeywords Recovery = (EventKeywords)0x40;
        public const EventKeywords Conversion = (EventKeywords)0x80;
    }
}

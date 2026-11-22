using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace FormatFactoryWinUI.Services;

/// <summary>One startup session. Elapsed times use a monotonic clock after construction.</summary>
public sealed class StartupReadinessMetrics
{
    private readonly long _started;
    private readonly double _processOffsetMilliseconds;
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _milestones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StartupStageTiming> _stages = new(StringComparer.Ordinal);

    public StartupReadinessMetrics(bool measureFromProcessStart = false)
    {
        if (measureFromProcessStart)
        {
            using var process = Process.GetCurrentProcess();
            var processStart = process.StartTime.ToUniversalTime();
            var now = DateTime.UtcNow;
            _started = Stopwatch.GetTimestamp();
            _processOffsetMilliseconds = Math.Max(0,
                (now - processStart).TotalMilliseconds);
        }
        else _started = Stopwatch.GetTimestamp();
        StartupTrace.Log.WindowStart();
        StartupTrace.Log.AddFilesStart();
        StartupTrace.Log.RecoveryStart();
    }

    private double ElapsedMilliseconds => _processOffsetMilliseconds + Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

    public void Mark(string milestone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(milestone);
        double elapsed;
        lock (_gate)
        {
            elapsed = ElapsedMilliseconds;
            if (!_milestones.TryAdd(milestone, elapsed)) return;
        }
        StartupTrace.Log.Milestone(milestone, elapsed);
        switch (milestone)
        {
            case "window-visible": StartupTrace.Log.WindowStop(elapsed); break;
            case "add-files-ready": StartupTrace.Log.AddFilesStop(elapsed); break;
            case "recovery-complete": StartupTrace.Log.RecoveryStop(elapsed); break;
        }
    }

    public IDisposable MeasureStage(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_gate)
        {
            // A settings refresh later in the session must not replace startup evidence.
            if (!_stages.TryAdd(stage, new(ElapsedMilliseconds, null))) return EmptyScope.Instance;
        }
        StartupTrace.Log.StageStart(stage);
        return new StageScope(this, stage);
    }

    private void CompleteStage(string stage, long started)
    {
        var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (_gate) _stages[stage] = _stages[stage] with { DurationMilliseconds = duration };
        StartupTrace.Log.StageStop(stage, duration);
    }

    public IReadOnlyDictionary<string, double> StagesSnapshot()
    {
        lock (_gate) return _stages.Where(pair => pair.Value.DurationMilliseconds.HasValue)
            .ToDictionary(pair => pair.Key, pair => pair.Value.DurationMilliseconds!.Value, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, StartupStageTiming> StageTimingsSnapshot()
    {
        lock (_gate) return new Dictionary<string, StartupStageTiming>(_stages, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, double> Snapshot()
    {
        lock (_gate) return new Dictionary<string, double>(_milestones, StringComparer.Ordinal);
    }

    private sealed class StageScope(StartupReadinessMetrics owner, string stage) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.CompleteStage(stage, _started);
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}

public sealed record StartupStageTiming(double StartMilliseconds, double? DurationMilliseconds);

// Self-describing ETW events can be correlated with UI-thread CPU/waits in WPA.
[EventSource(Name = "FormatFactory-Startup")]
internal sealed class StartupTrace : EventSource
{
    public static readonly StartupTrace Log = new();
    private StartupTrace() : base(EventSourceSettings.EtwSelfDescribingEventFormat) { }
    [Event(1, Task = (EventTask)1, Opcode = EventOpcode.Start, ActivityOptions = EventActivityOptions.Detachable)]
    public void WindowStart() => WriteEvent(1);
    [Event(2, Task = (EventTask)1, Opcode = EventOpcode.Stop)]
    public void WindowStop(double elapsedMilliseconds) => WriteEvent(2, elapsedMilliseconds);
    [Event(3, Task = (EventTask)2, Opcode = EventOpcode.Start, ActivityOptions = EventActivityOptions.Detachable)]
    public void AddFilesStart() => WriteEvent(3);
    [Event(4, Task = (EventTask)2, Opcode = EventOpcode.Stop)]
    public void AddFilesStop(double elapsedMilliseconds) => WriteEvent(4, elapsedMilliseconds);
    [Event(5, Task = (EventTask)3, Opcode = EventOpcode.Start, ActivityOptions = EventActivityOptions.Detachable)]
    public void RecoveryStart() => WriteEvent(5);
    [Event(6, Task = (EventTask)3, Opcode = EventOpcode.Stop)]
    public void RecoveryStop(double elapsedMilliseconds) => WriteEvent(6, elapsedMilliseconds);
    [Event(7)]
    public void Milestone(string name, double elapsedMilliseconds) => WriteEvent(7, name, elapsedMilliseconds);
    [Event(8, Opcode = EventOpcode.Info)]
    public void StageStart(string name) => WriteEvent(8, name);
    [Event(9, Opcode = EventOpcode.Info)]
    public void StageStop(string name, double durationMilliseconds) => WriteEvent(9, name, durationMilliseconds);
}

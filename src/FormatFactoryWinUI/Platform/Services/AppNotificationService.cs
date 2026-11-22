using System.Collections.Concurrent;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FormatFactoryWinUI.Services;

internal static class AppNotificationService
{
    private const string TaskNotificationGroup = "conversion-task";
    // Acquire inside callers' error handling; an unavailable notification broker
    // must not poison the static class or make an unregistered window fail to close.
    private static AppNotificationManager? _manager;
    private static AppNotificationManager Manager => _manager ??= AppNotificationManager.Default;
    private static readonly ConcurrentDictionary<Guid, uint> TaskSequences = new();
    private static readonly ConcurrentDictionary<Guid, TaskNotificationState> TaskStates = new();
    private static bool _registered;

    public static event EventHandler<ActivationRequest>? ActivationRequested;

    public static void Initialize()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            Manager.NotificationInvoked += Manager_NotificationInvoked;
            Manager.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogNotificationInitializationFailed", ex.Message));
        }
    }

    public static void Shutdown()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            Manager.NotificationInvoked -= Manager_NotificationInvoked;
            Manager.Unregister();
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogNotificationShutdownFailed", ex.Message));
        }
        finally
        {
            _registered = false;
        }
    }

    public static bool Show(string title, string message, string? outputPath = null, bool retryFailedAvailable = false)
    {
        try
        {
            if (!_registered)
            {
                Initialize();
            }

            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .AddArgument("action", "open");

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                builder.AddButton(new AppNotificationButton(LocalizationService.T("OpenOutput"))
                    .AddArgument("action", "open")
                    .AddArgument("path", outputPath));
            }

            if (retryFailedAvailable)
            {
                builder.AddButton(new AppNotificationButton(LocalizationService.T("RetryFailed"))
                    .AddArgument("action", "retry-failed"));
            }

            Manager.Show(builder.BuildNotification());
            return true;
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogNotificationSendFailed", ex.Message));
            return false;
        }
    }

    public static bool ShowTask(
        Guid taskId,
        string title,
        string status,
        double progress,
        TaskNotificationState state = TaskNotificationState.Running,
        string? outputPath = null,
        string? valueStringOverride = null)
    {
        try
        {
            if (!_registered)
            {
                Initialize();
            }

            var progressData = CreateProgressData(
                taskId,
                title,
                status,
                progress,
                valueStringOverride);
            Manager.Show(BuildTaskNotification(taskId, title, progressData, state, outputPath));
            TaskStates[taskId] = state;
            return true;
        }
        catch (Exception ex)
        {
            LogService.WriteApp($"Windows task notification failed: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> UpdateTaskAsync(
        Guid taskId,
        string title,
        string status,
        double progress,
        TaskNotificationState state = TaskNotificationState.Running,
        string? outputPath = null,
        string? valueStringOverride = null)
    {
        try
        {
            if (!_registered)
            {
                Initialize();
            }

            var progressData = CreateProgressData(
                taskId,
                title,
                status,
                progress,
                valueStringOverride);
            if (!TaskStates.TryGetValue(taskId, out var previousState) || previousState != state)
            {
                Manager.Show(BuildTaskNotification(taskId, title, progressData, state, outputPath));
                TaskStates[taskId] = state;
                return true;
            }

            var result = await Manager.UpdateAsync(progressData, GetTaskTag(taskId), TaskNotificationGroup);
            if (result == AppNotificationProgressResult.Succeeded)
            {
                return true;
            }

            Manager.Show(BuildTaskNotification(taskId, title, progressData, state, outputPath));
            return true;
        }
        catch (Exception ex)
        {
            LogService.WriteApp($"Windows task notification update failed: {ex.Message}");
            return false;
        }
    }

    public static async Task<bool> RemoveTaskAsync(Guid taskId)
    {
        try
        {
            await Manager.RemoveByTagAndGroupAsync(GetTaskTag(taskId), TaskNotificationGroup);
            TaskSequences.TryRemove(taskId, out _);
            TaskStates.TryRemove(taskId, out _);
            return true;
        }
        catch (Exception ex)
        {
            LogService.WriteApp($"Windows task notification removal failed: {ex.Message}");
            return false;
        }
    }

    private static AppNotification BuildTaskNotification(
        Guid taskId,
        string title,
        AppNotificationProgressData progressData,
        TaskNotificationState state,
        string? outputPath)
    {
        var taskIdValue = taskId.ToString("D");
        var builder = new AppNotificationBuilder()
            .AddText(title)
            .AddArgument("action", "open")
            .AddArgument("taskId", taskIdValue)
            .SetTag(GetTaskTag(taskId))
            .SetGroup(TaskNotificationGroup)
            .AddProgressBar(new AppNotificationProgressBar()
                .BindTitle()
                .BindStatus()
                .BindValue()
                .BindValueStringOverride());

        switch (state)
        {
            case TaskNotificationState.Running:
                AddTaskButton(builder, LocalizationService.T("PauseResume"), "pause", taskIdValue);
                AddTaskButton(builder, LocalizationService.T("CancelRemove"), "cancel", taskIdValue);
                break;
            case TaskNotificationState.Paused:
                AddTaskButton(builder, LocalizationService.T("PauseResume"), "resume", taskIdValue);
                AddTaskButton(builder, LocalizationService.T("CancelRemove"), "cancel", taskIdValue);
                break;
            case TaskNotificationState.Failed:
            case TaskNotificationState.Cancelled:
                AddTaskButton(builder, LocalizationService.T("StartRetry"), "retry", taskIdValue);
                break;
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            AddTaskButton(
                builder,
                LocalizationService.T("OpenOutput"),
                "open-output",
                taskIdValue,
                outputPath);
        }

        var notification = builder.BuildNotification();
        notification.Progress = progressData;
        return notification;
    }

    private static void AddTaskButton(
        AppNotificationBuilder builder,
        string label,
        string action,
        string taskId,
        string? outputPath = null)
    {
        var button = new AppNotificationButton(label)
            .AddArgument("action", action)
            .AddArgument("taskId", taskId);
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            button.AddArgument("path", outputPath);
        }

        builder.AddButton(button);
    }

    private static AppNotificationProgressData CreateProgressData(
        Guid taskId,
        string title,
        string status,
        double progress,
        string? valueStringOverride)
    {
        var value = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        return new AppNotificationProgressData(NextSequence(taskId))
        {
            Title = title,
            Status = status,
            Value = value,
            ValueStringOverride = string.IsNullOrWhiteSpace(valueStringOverride)
                ? $"{Math.Round(value * 100)}%"
                : valueStringOverride
        };
    }

    private static uint NextSequence(Guid taskId)
        => TaskSequences.AddOrUpdate(taskId, 1, static (_, current) => current == uint.MaxValue ? 1 : current + 1);

    private static string GetTaskTag(Guid taskId) => taskId.ToString("N")[..16];

    private static void Manager_NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            ActivationRequested?.Invoke(null, ActivationService.FromNotification(args));
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogNotificationActivationFailed", ex.Message));
        }
    }
}

internal enum TaskNotificationState
{
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

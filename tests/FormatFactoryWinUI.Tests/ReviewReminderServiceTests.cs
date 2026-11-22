using System.Text.Json;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ReviewReminderServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task RegisterLaunchAsync_PromptsAtTenFiftyAndOneHundred()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);

        Assert.False(await service.IsPromptDueAsync(Start));
        for (var launch = 1; launch <= 100; launch++)
        {
            var expected = launch is 10 or 50 or 100;
            Assert.Equal(expected, await service.RegisterLaunchAsync(Start));
            Assert.Equal(expected, await service.IsPromptDueAsync(Start));
            if (expected)
            {
                await service.RecordPromptShownAsync(Start);
                Assert.False(await service.IsPromptDueAsync(Start));
            }
        }

        using var state = await ReadStateAsync(directory);
        Assert.Equal(100, state.RootElement.GetProperty("LaunchCount").GetInt64());
        Assert.Equal(100, state.RootElement.GetProperty("LastPromptLaunchCount").GetInt64());
    }

    [Fact]
    public async Task DeferredPrompt_RemainsDueAcrossLaunchesAndReloads()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);
        await RegisterLaunchesAsync(service, 10, Start);

        Assert.True(await service.IsPromptDueAsync(Start));
        Assert.True(await service.IsPromptDueAsync(Start));
        service = CreateService(directory);
        Assert.True(await service.RegisterLaunchAsync(Start));
        await service.RecordPromptShownAsync(Start);

        Assert.False(await CreateService(directory).IsPromptDueAsync(Start));
        using var state = await ReadStateAsync(directory);
        Assert.Equal(11, state.RootElement.GetProperty("LastPromptLaunchCount").GetInt64());
    }

    [Fact]
    public async Task MissedMilestones_AreCoalescedIntoOnePrompt()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);
        await RegisterLaunchesAsync(service, 130, Start);

        Assert.True(await service.IsPromptDueAsync(Start));
        await service.RecordPromptShownAsync(Start);
        Assert.False(await service.IsPromptDueAsync(Start));
        Assert.False(await service.RegisterLaunchAsync(Start));
        Assert.True(await service.IsPromptDueAsync(Start.AddMonths(1)));
    }

    [Fact]
    public async Task MonthlyPrompt_DoesNotBeginBeforeOneHundredLaunches()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);
        await RegisterLaunchesAsync(service, 10, Start);
        await service.RecordPromptShownAsync(Start);
        Assert.False(await service.IsPromptDueAsync(Start.AddYears(1)));

        await RegisterLaunchesAsync(service, 40, Start);
        await service.RecordPromptShownAsync(Start);
        Assert.False(await service.IsPromptDueAsync(Start.AddYears(1)));

        await RegisterLaunchesAsync(service, 50, Start);
        var firstDisplayedAt = Start.AddYears(2);
        Assert.True(await service.IsPromptDueAsync(firstDisplayedAt));
        await service.RecordPromptShownAsync(firstDisplayedAt);
        Assert.False(await service.IsPromptDueAsync(firstDisplayedAt.AddMonths(1).AddTicks(-1)));
        Assert.True(await service.IsPromptDueAsync(firstDisplayedAt.AddMonths(1)));
    }

    [Theory]
    [InlineData(2026, 1, 31, 2026, 2, 28)]
    [InlineData(2028, 1, 31, 2028, 2, 29)]
    [InlineData(2028, 2, 29, 2028, 3, 29)]
    [InlineData(2026, 3, 31, 2026, 4, 30)]
    [InlineData(2026, 12, 31, 2027, 1, 31)]
    public async Task MonthlyPrompt_UsesCalendarMonthsIncludingLeapYears(
        int year, int month, int day, int dueYear, int dueMonth, int dueDay)
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);
        var shownAt = new DateTimeOffset(year, month, day, 12, 30, 0, TimeSpan.FromHours(8));
        var dueAt = new DateTimeOffset(dueYear, dueMonth, dueDay, 12, 30, 0, TimeSpan.FromHours(8));
        await RegisterLaunchesAsync(service, 100, shownAt);
        await service.RecordPromptShownAsync(shownAt);

        Assert.False(await service.IsPromptDueAsync(dueAt.AddTicks(-1)));
        Assert.True(await CreateService(directory).IsPromptDueAsync(dueAt));
    }

    [Fact]
    public async Task MonthlyPrompt_AfterLongAbsenceIsSingleAndSchedulesFromActualDisplay()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);
        await RegisterLaunchesAsync(service, 100, Start);
        await service.RecordPromptShownAsync(Start);
        var returnAt = Start.AddMonths(8).AddDays(5);

        Assert.True(await service.RegisterLaunchAsync(returnAt));
        await service.RecordPromptShownAsync(returnAt);

        Assert.False(await service.IsPromptDueAsync(returnAt));
        Assert.False(await service.IsPromptDueAsync(returnAt.AddMonths(1).AddTicks(-1)));
        Assert.True(await service.IsPromptDueAsync(returnAt.AddMonths(1)));
        Assert.False(await service.IsPromptDueAsync(Start.AddDays(-1)));
    }

    [Fact]
    public async Task OldSettingsWithoutReminderData_StartFromFirstEligibleLaunch()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        await new SettingsService(settingsPath).SaveAsync(new AppSettings { CpuThreadsPerTask = 7 });
        var service = CreateService(directory);

        Assert.False(await service.RegisterLaunchAsync(Start));
        using var state = await ReadStateAsync(directory);
        Assert.Equal(1, state.RootElement.GetProperty("LaunchCount").GetInt64());
    }

    [Fact]
    public async Task StaleSettingsSave_CannotEraseReminderHistoryOrPermanentStop()
    {
        using var directory = new TemporaryDirectory();
        var settings = new SettingsService(Path.Combine(directory.Path, "settings.json"));
        var staleSettings = await settings.LoadAsync();
        var service = CreateService(directory);
        await RegisterLaunchesAsync(service, 100, Start);
        await service.RecordPromptShownAsync(Start);
        await service.StopRemindersAsync();
        var originalState = await File.ReadAllTextAsync(StatePath(directory));

        staleSettings.CpuThreadsPerTask = 4;
        await settings.SaveAsync(staleSettings);

        Assert.Equal(originalState, await File.ReadAllTextAsync(StatePath(directory)));
        Assert.False(await CreateService(directory).IsPromptDueAsync(Start.AddYears(20)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task StopRemindersAsync_PersistsAcrossReloadAndStopsCounting(int launchCount)
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);
        await RegisterLaunchesAsync(service, launchCount, Start);
        await service.StopRemindersAsync();

        service = CreateService(directory);
        Assert.False(await service.IsPromptDueAsync(Start.AddYears(10)));
        Assert.False(await service.RegisterLaunchAsync(Start.AddYears(10)));
        await service.RecordPromptShownAsync(Start.AddYears(10));
        using var state = await ReadStateAsync(directory);
        Assert.True(state.RootElement.GetProperty("RemindersStopped").GetBoolean());
        Assert.Equal(launchCount, state.RootElement.GetProperty("LaunchCount").GetInt64());
        Assert.Equal(0, state.RootElement.GetProperty("LastPromptLaunchCount").GetInt64());
    }

    [Fact]
    public async Task RatingCanceledOrFailed_WithoutStopKeepsFutureRemindersEnabled()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);
        await RegisterLaunchesAsync(service, 10, Start);
        await service.RecordPromptShownAsync(Start);
        // A dismissed/failed Store flow calls no permanent-stop operation.
        await RegisterLaunchesAsync(CreateService(directory), 40, Start);

        Assert.True(await service.IsPromptDueAsync(Start));
        using var state = await ReadStateAsync(directory);
        Assert.False(state.RootElement.GetProperty("RemindersStopped").GetBoolean());
    }

    [Fact]
    public async Task RecordPromptShownAsync_WhenNotDueCannotConsumeFutureMilestones()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);
        await RegisterLaunchesAsync(service, 9, Start);
        await service.RecordPromptShownAsync(Start);

        Assert.True(await service.RegisterLaunchAsync(Start));
        using var state = await ReadStateAsync(directory);
        Assert.Equal(0, state.RootElement.GetProperty("LastPromptLaunchCount").GetInt64());
    }

    [Fact]
    public async Task ConcurrentInstances_DoNotLoseLaunchCountsOrLeaveTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();

        await Task.WhenAll(Enumerable.Range(0, 96).Select(_ =>
            CreateService(directory).RegisterLaunchAsync(Start)));

        using var state = await ReadStateAsync(directory);
        Assert.Equal(96, state.RootElement.GetProperty("LaunchCount").GetInt64());
        Assert.Equal(0, state.RootElement.GetProperty("LastPromptLaunchCount").GetInt64());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "review-reminder.json.*.tmp"));
        Assert.True(await CreateService(directory).IsPromptDueAsync(Start));
    }

    [Fact]
    public async Task ExclusiveFileLock_CancellationDoesNotOverwriteStateOrLoseNextLaunch()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);
        await service.RegisterLaunchAsync(Start);
        var originalState = await File.ReadAllTextAsync(StatePath(directory));
        using var cancellation = new CancellationTokenSource();
        await using (var heldLock = new FileStream(StatePath(directory) + ".lock",
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var registration = CreateService(directory).RegisterLaunchAsync(Start, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration);
        }

        Assert.Equal(originalState, await File.ReadAllTextAsync(StatePath(directory)));
        Assert.False(await service.RegisterLaunchAsync(Start));
        using var state = await ReadStateAsync(directory);
        Assert.Equal(2, state.RootElement.GetProperty("LaunchCount").GetInt64());
        Assert.False(state.RootElement.GetProperty("RemindersStopped").GetBoolean());
    }

    [Theory]
    [InlineData("{unfinished")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1,\"LaunchCount\":-1,\"LastPromptLaunchCount\":0,\"LastPromptAt\":null,\"RemindersStopped\":false}")]
    [InlineData("{\"Version\":2,\"LaunchCount\":10,\"LastPromptLaunchCount\":0,\"LastPromptAt\":null,\"RemindersStopped\":false}")]
    [InlineData("{\"Version\":1,\"LaunchCount\":10,\"LastPromptLaunchCount\":50,\"LastPromptAt\":\"2026-01-01T12:00:00+08:00\",\"RemindersStopped\":false}")]
    [InlineData("{\"Version\":1,\"LaunchCount\":100,\"LastPromptLaunchCount\":100,\"LastPromptAt\":null,\"RemindersStopped\":false}")]
    public async Task CorruptState_ThrowsWithoutResettingOrOverwriting(string corruptJson)
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(StatePath(directory), corruptJson);
        var service = CreateService(directory);

        await Assert.ThrowsAsync<JsonException>(() => service.IsPromptDueAsync(Start));
        await Assert.ThrowsAsync<JsonException>(() => service.RegisterLaunchAsync(Start));
        await Assert.ThrowsAsync<JsonException>(() => service.RecordPromptShownAsync(Start));
        await Assert.ThrowsAsync<JsonException>(() => service.StopRemindersAsync());

        Assert.Equal(corruptJson, await File.ReadAllTextAsync(StatePath(directory)));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "review-reminder.json.*.tmp"));
    }

    [Fact]
    public async Task WriteFailure_IsReportedAndCleansTemporarySnapshot()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(StatePath(directory));
        var service = CreateService(directory);

        var failure = await Record.ExceptionAsync(() => service.RegisterLaunchAsync(Start));
        Assert.True(failure is IOException or UnauthorizedAccessException,
            $"Expected a filesystem persistence failure, received: {failure}");

        Assert.Empty(Directory.EnumerateFiles(directory.Path, "review-reminder.json.*.tmp"));
        Assert.True(Directory.Exists(StatePath(directory)));
        Directory.Delete(StatePath(directory));
        Assert.False(await service.RegisterLaunchAsync(Start));
        using var state = await ReadStateAsync(directory);
        Assert.Equal(1, state.RootElement.GetProperty("LaunchCount").GetInt64());
        Assert.False(state.RootElement.GetProperty("RemindersStopped").GetBoolean());
    }

    [Fact]
    public async Task AlreadyCanceledOperations_DoNotRecordPromptOrPermanentStop()
    {
        using var directory = new TemporaryDirectory();
        var service = CreateService(directory);
        await RegisterLaunchesAsync(service, 10, Start);
        var originalState = await File.ReadAllTextAsync(StatePath(directory));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RegisterLaunchAsync(Start, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecordPromptShownAsync(Start, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StopRemindersAsync(cancellation.Token));

        Assert.Equal(originalState, await File.ReadAllTextAsync(StatePath(directory)));
        Assert.True(await service.IsPromptDueAsync(Start));
    }

    private static string StatePath(TemporaryDirectory directory) =>
        Path.Combine(directory.Path, "review-reminder.json");

    private static ReviewReminderService CreateService(TemporaryDirectory directory) => new(StatePath(directory));

    private static async Task<JsonDocument> ReadStateAsync(TemporaryDirectory directory) =>
        JsonDocument.Parse(await File.ReadAllTextAsync(StatePath(directory)));

    private static async Task RegisterLaunchesAsync(ReviewReminderService service, int count, DateTimeOffset now)
    {
        for (var launch = 0; launch < count; launch++)
        {
            await service.RegisterLaunchAsync(now);
        }
    }
}

using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;

namespace FormatFactoryWinUI.Tests;

public sealed class PageStateTests
{
    [Fact]
    public void ResetDraftReplacesUnsavedSessionAndSynchronizesPreservedWatchFolder()
    {
        var state = new SettingsPageViewModel();
        state.InitializeDraft(new AppSettings { WatchFolderPath = "original", WatchFolderAccessToken = "original-token" });
        var oldSession = state.DraftSession;
        state.Edit(oldSession!.Current with { ParallelCount = 4, FfmpegPath = "custom-engine", WatchFolderEnabled = true });
        state.WatchFolderPath = "unsaved-folder";
        state.WatchFolderAccessToken = "unsaved-token";
        state.SaveMessage = "earlier error";
        var settings = new AppSettings
        {
            WatchFolderPath = "preserved-folder",
            WatchFolderAccessToken = "preserved-token",
            PerformanceDefaultsVersion = 2
        };

        state.ResetDraft(settings);

        Assert.NotSame(oldSession, state.DraftSession);
        Assert.Equal(SettingsDraft.From(settings), state.DraftSession!.Current);
        Assert.False(state.DraftSession.IsDirty);
        Assert.Equal("preserved-folder", state.WatchFolderPath);
        Assert.Equal("preserved-token", state.WatchFolderAccessToken);
        Assert.Equal("ConversionUiSettingsSaved", state.SaveStateKey);
        Assert.Null(state.SaveMessage);
    }

    [Fact]
    public void RevisitingSettingsRetainsUnsavedDraftAndWatchFolderChoice()
    {
        var settings = new AppSettings { ParallelCount = 2, WatchFolderPath = "original" };
        var state = new SettingsPageViewModel();
        state.InitializeDraft(settings);
        var session = state.DraftSession;
        state.WatchFolderPath = "edited";
        state.Edit(SettingsDraft.From(settings) with { ParallelCount = 4, WatchFolderPath = "edited" });
        state.InitializeDraft(settings);
        Assert.Same(session, state.DraftSession);
        Assert.Equal("edited", state.WatchFolderPath);
        Assert.Equal(4, state.DraftSession!.Current.ParallelCount);
        Assert.Equal("ConversionUiSettingsUnsaved", state.SaveStateKey);
    }

    [Fact]
    public void SettingsEditsDuringSaveRemainDirtyAfterEarlierSnapshotIsAccepted()
    {
        var state = new SettingsPageViewModel();
        state.InitializeDraft(new AppSettings());
        var captured = state.DraftSession!.Current with { ParallelCount = 3 };
        state.Edit(captured);
        state.IsSaving = true;
        state.Edit(captured with { ParallelCount = 4 });
        state.DraftSession.AcceptSaved(captured);
        Assert.True(state.DraftSession.IsDirty);
        Assert.Equal(4, state.DraftSession.Current.ParallelCount);
    }

    [Fact]
    public async Task SlowerHistorySearchCannotReplaceNewerResults()
    {
        using var state = new HistoryPageViewModel();
        var oldStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResult = new TaskCompletionSource<IReadOnlyList<ConversionHistoryItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newest = new ConversionHistoryItem { TaskId = Guid.NewGuid(), DisplayName = "new" };
        Task Dispatch(Func<Task> action) => action();
        var first = state.RefreshAsync("old", false, (_, _) => { oldStarted.SetResult(true); return oldResult.Task; }, Dispatch);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RefreshAsync("new", false, (_, _) => Task.FromResult<IReadOnlyList<ConversionHistoryItem>>([newest]), Dispatch);
        oldResult.SetResult([new ConversionHistoryItem { TaskId = Guid.NewGuid(), DisplayName = "old" }]);
        await first;
        Assert.Same(newest, Assert.Single(state.Items));
    }

    [Fact]
    public async Task ClosingHistoryPreventsLateSearchFromPublishing()
    {
        using var state = new HistoryPageViewModel();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<IReadOnlyList<ConversionHistoryItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = state.RefreshAsync("pending", false, (_, _) => { started.SetResult(true); return result.Task; }, action => action());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.Dispose();
        result.SetResult([new ConversionHistoryItem { TaskId = Guid.NewGuid() }]);
        await query;
        Assert.Empty(state.Items);
    }
}

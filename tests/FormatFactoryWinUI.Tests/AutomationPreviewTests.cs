using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AutomationPreviewTests
{
    [Fact]
    public async Task PreviewUnsavedDraft_ExplainsPriorityFiltersAndOutput_WithoutWritingAnything()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        var earlier = rule.Snapshot();
        earlier.Id = Guid.NewGuid();
        earlier.Name = "Earlier manual rule";
        earlier.FileNamePattern = "owned*";
        earlier.WatchEnabled = false;
        var own = Write(rule, "owned.png");
        var ready = Write(rule, "ready.png");
        var ignored = Write(rule, "ignored.txt");
        rule.Extensions = "png";
        var before = Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories);
        var result = await AutomationPreviewService.PreviewAsync(rule, [earlier], new(), new());

        Assert.Equal(AutomationPreviewState.OwnedByOtherRule, result.Items.Single(item => item.InputPath == own).State);
        Assert.Equal(AutomationPreviewState.FilterMismatch, result.Items.Single(item => item.InputPath == ignored).State);
        var item = result.Items.Single(item => item.InputPath == ready);
        Assert.Equal(AutomationPreviewState.Ready, item.State);
        Assert.Equal(Path.Combine(rule.OutputDirectory, "ready.png"), item.OutputPath);
        Assert.Equal(before.Order(), Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Order());
        Assert.False(Directory.Exists(rule.OutputDirectory));
        Assert.False(Directory.Exists(rule.ArchiveDirectory));
        Assert.Equal("input bytes", File.ReadAllText(ready));
    }

    [Fact]
    public async Task DraftReplacement_RetainsSavedPriorityAndDoesNotMutateSavedRule()
    {
        using var directory = new TemporaryDirectory();
        var saved = Rule(directory.Path);
        saved.FileNamePattern = "not-this*";
        var later = saved.Snapshot();
        later.Id = Guid.NewGuid();
        later.FileNamePattern = string.Empty;
        var draft = saved.Snapshot();
        draft.FileNamePattern = string.Empty;
        Write(saved, "ready.png");
        var result = await AutomationPreviewService.PreviewAsync(draft, [saved, later], new(), new());
        Assert.Equal(AutomationPreviewState.Ready, Assert.Single(result.Items).State);
        Assert.Equal("not-this*", saved.FileNamePattern);
    }

    [Fact]
    public async Task PreviewExplainsSuccessCancellationAndFailedVersionAcrossRules()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        var success = Write(rule, "success.png");
        var cancelled = Write(rule, "cancelled.png");
        var failed = Write(rule, "failed.png");
        var snapshot = new AutomationSnapshot
        {
            Records = [new(Guid.NewGuid(), success, FileFingerprintService.Read(success)!, null, null, DateTimeOffset.UtcNow)],
            Suppressions =
            [
                new(Guid.NewGuid(), cancelled, FileFingerprintService.Read(cancelled)!, true, DateTimeOffset.UtcNow),
                new(Guid.NewGuid(), failed, FileFingerprintService.Read(failed)!, false, DateTimeOffset.UtcNow)
            ]
        };
        var result = await AutomationPreviewService.PreviewAsync(rule, [rule], snapshot, new());
        Assert.Equal(AutomationPreviewState.AlreadyProcessed, result.Items.Single(item => item.InputPath == success).State);
        Assert.Equal(AutomationPreviewState.Cancelled, result.Items.Single(item => item.InputPath == cancelled).State);
        Assert.Equal(AutomationPreviewState.FailedPreviously, result.Items.Single(item => item.InputPath == failed).State);
        Assert.Equal(0, result.Ready);
        Assert.Equal(3, result.Skipped);
    }

    [Fact]
    public async Task PreviewWaitsForWriterAndQueuedInput()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        var writing = Write(rule, "writing.png");
        var queued = Write(rule, "queued.png");
        using var writer = new FileStream(writing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var result = await AutomationPreviewService.PreviewAsync(rule, [rule], new(), new(), [queued]);
        Assert.All(result.Items, item => Assert.Equal(AutomationPreviewState.Waiting, item.State));
        Assert.Equal(2, result.Waiting);
    }

    [Fact]
    public async Task PreviewUsesRuleNamingFolderPolicyAndAvoidsCollisionsWithinBatch()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        rule.IncludeSubfolders = true;
        rule.Options["outputFileNameTemplate"] = "converted-{name}";
        rule.Options["preserveFolderStructure"] = "false";
        Write(rule, "same.png");
        Directory.CreateDirectory(Path.Combine(rule.FolderPath, "nested"));
        File.WriteAllText(Path.Combine(rule.FolderPath, "nested", "same.png"), "nested bytes");
        Directory.CreateDirectory(rule.OutputDirectory);
        var existing = Path.Combine(rule.OutputDirectory, "converted-same.png");
        File.WriteAllText(existing, "existing output");
        var result = await AutomationPreviewService.PreviewAsync(rule, [rule], new(), new());
        Assert.Equal(2, result.Ready);
        Assert.Equal(2, result.Items.Select(item => item.OutputPath).Distinct().Count());
        Assert.All(result.Items, item => Assert.Equal(rule.OutputDirectory, Path.GetDirectoryName(item.OutputPath)));
        Assert.DoesNotContain(result.Items, item => item.OutputPath == existing);
        Assert.Equal("existing output", File.ReadAllText(existing));
        Assert.Single(Directory.GetFiles(rule.OutputDirectory));
    }

    [Fact]
    public async Task PreviewExcludesOtherRulesOutputAndArchiveFolders()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        rule.IncludeSubfolders = true;
        var other = rule.Snapshot();
        other.Id = Guid.NewGuid();
        other.FolderPath = Path.Combine(directory.Path, "other-input");
        other.OutputDirectory = Path.Combine(rule.FolderPath, "other-output");
        other.ArchiveDirectory = Path.Combine(rule.FolderPath, "other-archive");
        other.ArchiveSource = true;
        Directory.CreateDirectory(other.OutputDirectory);
        Directory.CreateDirectory(other.ArchiveDirectory);
        File.WriteAllText(Path.Combine(other.OutputDirectory, "out.png"), "output");
        File.WriteAllText(Path.Combine(other.ArchiveDirectory, "archived.png"), "archive");
        var result = await AutomationPreviewService.PreviewAsync(rule, [rule, other], new(), new());
        Assert.All(result.Items, item => Assert.Equal(AutomationPreviewState.Excluded, item.State));
    }

    [Fact]
    public async Task PreviewExplainsUnsupportedTargetBeforeConversion()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        rule.TargetFormatId = "mp3";
        Write(rule, "image.png");
        var result = await AutomationPreviewService.PreviewAsync(rule, [rule], new(), new());
        Assert.Equal(AutomationPreviewState.UnsupportedTarget, Assert.Single(result.Items).State);
        Assert.Null(result.Items[0].OutputPath);
    }

    [Fact]
    public async Task PreviewReportsUnavailableFolderAndLimit()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        Write(rule, "one.png");
        Write(rule, "two.png");
        var limited = await AutomationPreviewService.PreviewAsync(rule, [rule], new(), new(), limit: 1);
        Assert.True(limited.Truncated);
        Assert.Single(limited.Items);
        rule.FolderPath = Path.Combine(directory.Path, "missing");
        var missing = await AutomationPreviewService.PreviewAsync(rule, [rule], new(), new());
        Assert.NotNull(missing.Error);
        Assert.Empty(missing.Items);
    }

    [Fact]
    public async Task PreviewCancellationDoesNotWriteAnyOutput()
    {
        using var directory = new TemporaryDirectory();
        var rule = Rule(directory.Path);
        Write(rule, "one.png");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AutomationPreviewService.PreviewAsync(rule, [rule], new(), new(), cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(rule.OutputDirectory));
    }

    private static AutomationRule Rule(string root)
    {
        var input = Path.Combine(root, "input");
        Directory.CreateDirectory(input);
        return new() { Name = "Preview rule", FolderPath = input, TargetFormatId = "png",
            OutputDirectory = Path.Combine(root, "output"), ArchiveDirectory = Path.Combine(root, "archive") };
    }

    private static string Write(AutomationRule rule, string name)
    {
        var path = Path.Combine(rule.FolderPath, name);
        File.WriteAllText(path, "input bytes");
        return path;
    }
}

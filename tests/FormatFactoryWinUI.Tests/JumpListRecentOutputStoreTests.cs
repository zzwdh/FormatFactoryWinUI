using System.Text.Json;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class JumpListRecentOutputStoreTests
{
    [Fact]
    public async Task MissingStore_StartsEmptyWithoutCreatingFiles()
    {
        using var directory = new TemporaryDirectory();
        var storePath = Path.Combine(directory.Path, "missing", "dismissed.json");
        var store = new JumpListRecentOutputStore(storePath);

        Assert.Empty(await store.LoadAsync());
        Assert.False(Directory.Exists(Path.GetDirectoryName(storePath)));
    }

    [Fact]
    public async Task SavedDismissals_SurviveReopeningWithCaseInsensitiveArgumentsAndExactTimestamps()
    {
        using var directory = new TemporaryDirectory();
        var storePath = Path.Combine(directory.Path, "state", "dismissed.json");
        var arguments = ActivationArgumentParser.BuildJumpListOutputArgument(@"C:\Output Folder\结果%20.mp4");
        var dismissedAt = new DateTimeOffset(2026, 11, 14, 20, 30, 45, TimeSpan.FromHours(8)).AddTicks(1234);

        await new JumpListRecentOutputStore(storePath).SaveAsync(
            new Dictionary<string, DateTimeOffset> { [arguments] = dismissedAt });
        var reopened = await new JumpListRecentOutputStore(storePath).LoadAsync();

        Assert.Single(reopened);
        Assert.Equal(dismissedAt, reopened[arguments.ToUpperInvariant()]);
        Assert.Equal(dismissedAt.Offset, reopened[arguments].Offset);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(storePath)!));
    }

    [Fact]
    public async Task SavingReplacesRemovedEntriesAndSupportsAnEmptyStore()
    {
        using var directory = new TemporaryDirectory();
        var store = new JumpListRecentOutputStore(Path.Combine(directory.Path, "dismissed.json"));
        var dismissedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(new Dictionary<string, DateTimeOffset>
            { ["--open-output:first"] = dismissedAt, ["--open-output:second"] = dismissedAt });

        await store.SaveAsync(new Dictionary<string, DateTimeOffset>
            { ["--open-output:second"] = dismissedAt.AddMinutes(1) });
        var replaced = await store.LoadAsync();

        Assert.Single(replaced);
        Assert.False(replaced.ContainsKey("--open-output:first"));
        Assert.Equal(dismissedAt.AddMinutes(1), replaced["--open-output:second"]);
        await store.SaveAsync(new Dictionary<string, DateTimeOffset>());
        Assert.Empty(await store.LoadAsync());
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"--open-output:first\":\"not a timestamp\"}")]
    public async Task InvalidStore_ReportsCorruptionWithoutReplacingTheFile(string json)
    {
        using var directory = new TemporaryDirectory();
        var storePath = Path.Combine(directory.Path, "dismissed.json");
        await File.WriteAllTextAsync(storePath, json);

        await Assert.ThrowsAsync<JsonException>(() => new JumpListRecentOutputStore(storePath).LoadAsync());

        Assert.Equal(json, await File.ReadAllTextAsync(storePath));
    }

    [Fact]
    public async Task FailedReplacement_PreservesPreviousDismissalsAndCleansTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        var storePath = Path.Combine(directory.Path, "dismissed.json");
        var store = new JumpListRecentOutputStore(storePath);
        var dismissedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(new Dictionary<string, DateTimeOffset> { ["--open-output:first"] = dismissedAt });
        var originalJson = await File.ReadAllTextAsync(storePath);

        // Deny replacement of the destination while still allowing it to be read.
        using (File.Open(storePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var exception = await Record.ExceptionAsync(() => store.SaveAsync(
                new Dictionary<string, DateTimeOffset> { ["--open-output:second"] = dismissedAt }));
            Assert.True(exception is IOException or UnauthorizedAccessException,
                $"Expected a sharing/access denial, got {exception?.GetType().Name ?? "no exception"}.");
        }

        Assert.Equal(originalJson, await File.ReadAllTextAsync(storePath));
        Assert.Single(Directory.GetFiles(directory.Path));
    }
}

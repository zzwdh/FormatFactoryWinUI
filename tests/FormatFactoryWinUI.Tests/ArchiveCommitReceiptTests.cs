using System.IO.Compression;
using System.Text.Json.Nodes;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class ArchiveCommitReceiptTests
{
    [Fact]
    public async Task PreparedReceiptDoesNotRecoverOrMoveAnUnpublishedPrivateFile()
    {
        using var directory = new TemporaryDirectory();
        var (task, staged, final) = await FixtureAsync(directory.Path);
        var store = Store(directory.Path);

        await store.PrepareAsync(task, staged, final);

        Assert.Null(await store.TryRecoverAsync(task));
        Assert.True(File.Exists(staged));
        Assert.False(File.Exists(final));
        var json = await File.ReadAllTextAsync(ReceiptPath(directory.Path, task.Id));
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "receipts"), "*.tmp"));
    }

    [Fact]
    public async Task PublishedArchiveRecoversIdempotentlyWithoutRequiringSourceOrStagingFile()
    {
        using var directory = new TemporaryDirectory();
        var (task, staged, final) = await FixtureAsync(directory.Path);
        var store = Store(directory.Path);
        await store.PrepareAsync(task, staged, final);
        File.Move(staged, final);
        File.Delete(task.SourcePath);
        Directory.Delete(Path.GetDirectoryName(staged)!);

        var recovered = await store.TryRecoverAsync(task);
        var repeated = await store.TryRecoverAsync(task);

        Assert.NotNull(recovered);
        Assert.Equal(recovered, repeated);
        Assert.Equal(task.Id, recovered.Id);
        Assert.Equal(ArchiveTaskState.Succeeded, recovered.State);
        Assert.Equal(final, recovered.OutputPath);
        Assert.True(recovered.EncryptOutput);
        Assert.True(recovered.StrictVerification);
        Assert.Null(recovered.ResultStatistics);
        Assert.False(recovered.HistoryRecorded);
        Assert.NotNull(recovered.CompletedAtUtc);
        Assert.Single(Directory.GetFiles(task.OutputDirectory));
        await store.ForgetAsync(task.Id);
        await store.ForgetAsync(task.Id);
        Assert.Null(await store.TryRecoverAsync(task));
        Assert.True(File.Exists(final));
    }

    [Fact]
    public async Task ChangedSourcePathOptionsDestinationOrIdentityCannotRecoverAnotherConversion()
    {
        using var directory = new TemporaryDirectory();
        var (task, staged, final) = await FixtureAsync(directory.Path);
        var store = Store(directory.Path);
        await store.PrepareAsync(task, staged, final);
        File.Move(staged, final);
        var alternatives = new[]
        {
            task with { Id = Guid.NewGuid() },
            task with { SourcePath = Path.Combine(directory.Path, "another.zip") },
            task with { OutputDirectory = directory.Path },
            task with { TargetFormat = ArchiveFormat.SevenZip },
            task with { StrictVerification = false },
            task with { EncryptOutput = false }
        };
        foreach (var alternative in alternatives) Assert.Null(await store.TryRecoverAsync(alternative));
        Assert.NotNull(await store.TryRecoverAsync(task));
    }

    [Fact]
    public async Task EqualLengthChangedContentsOrChangedCreationTimeAreRejected()
    {
        using var directory = new TemporaryDirectory();
        var (task, staged, final) = await FixtureAsync(directory.Path);
        var store = Store(directory.Path);
        await store.PrepareAsync(task, staged, final);
        File.Move(staged, final);
        var original = await File.ReadAllBytesAsync(final);
        var changed = (byte[])original.Clone();
        changed[0] ^= 1;
        await File.WriteAllBytesAsync(final, changed);
        Assert.Null(await store.TryRecoverAsync(task));
        await File.WriteAllBytesAsync(final, original);
        Assert.NotNull(await store.TryRecoverAsync(task));
        File.SetCreationTimeUtc(final, File.GetCreationTimeUtc(final).AddMinutes(-1));
        Assert.Null(await store.TryRecoverAsync(task));
    }

    [Fact]
    public async Task ExistingUnrelatedFinalFileIsNeverChangedAndPrivateFileIsNeverPublishedByRecovery()
    {
        using var directory = new TemporaryDirectory();
        var (task, staged, final) = await FixtureAsync(directory.Path);
        var store = Store(directory.Path);
        await store.PrepareAsync(task, staged, final);
        await File.WriteAllTextAsync(final, "an unrelated existing result");

        Assert.Null(await store.TryRecoverAsync(task));
        Assert.Equal("an unrelated existing result", await File.ReadAllTextAsync(final));
        Assert.True(File.Exists(staged));
        Assert.True(File.Exists(task.SourcePath));
    }

    [Fact]
    public async Task InvalidOrFutureReceiptCannotRecoverAndCancelledPreparationPreservesPriorReceipt()
    {
        using var directory = new TemporaryDirectory();
        var (task, staged, final) = await FixtureAsync(directory.Path);
        var store = Store(directory.Path);
        await store.PrepareAsync(task, staged, final);
        var receiptPath = ReceiptPath(directory.Path, task.Id);
        var original = await File.ReadAllTextAsync(receiptPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PrepareAsync(task, staged, final, cancellation.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(receiptPath));
        File.Move(staged, final);
        var future = JsonNode.Parse(original)!;
        future["Version"] = 99;
        await File.WriteAllTextAsync(receiptPath, future.ToJsonString());
        Assert.Null(await store.TryRecoverAsync(task));
        await File.WriteAllTextAsync(receiptPath, "{invalid");
        Assert.Null(await store.TryRecoverAsync(task));
        Assert.Equal("{invalid", await File.ReadAllTextAsync(receiptPath));
        Assert.True(File.Exists(final));
    }

    [Fact]
    public async Task PublicationReservationCannotTargetTheSourceOrEscapeItsOutputDirectory()
    {
        using var directory = new TemporaryDirectory();
        var (task, staged, _) = await FixtureAsync(directory.Path);
        var store = Store(directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareAsync(task, staged, task.SourcePath));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareAsync(task, staged, Path.Combine(directory.Path, "outside.zip")));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PrepareAsync(task, staged, Path.Combine(task.OutputDirectory, "wrong.7z")));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "receipts")));
    }

    [Fact]
    public async Task ServicePreparesReceiptBeforePublishingAndTriesNewNameAfterACommitRace()
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateZipAsync(directory.Path);
        var output = Path.Combine(directory.Path, "outputs");
        Directory.CreateDirectory(output);
        var task = new ArchiveTaskCheckpoint
        {
            SourcePath = source, OutputDirectory = output, TargetFormat = ArchiveFormat.SevenZip,
            StrictVerification = true, State = ArchiveTaskState.Running
        };
        var store = Store(directory.Path);
        var candidates = new List<string>();
        var converted = await new ArchiveConversionService(Engine()).ConvertAsync(source, output,
            new(ArchiveFormat.SevenZip, true), (_, _) => Task.FromResult<string?>(null),
            prepareCommit: async (staged, final, token) =>
            {
                Assert.True(File.Exists(staged));
                Assert.False(File.Exists(final));
                await store.PrepareAsync(task, staged, final, token);
                candidates.Add(final);
                if (candidates.Count == 1) await File.WriteAllTextAsync(final, "another publisher won this name", token);
            });

        Assert.Equal(2, candidates.Count);
        Assert.Equal("another publisher won this name", await File.ReadAllTextAsync(candidates[0]));
        Assert.Equal(candidates[1], converted);
        Assert.Equal(converted, (await store.TryRecoverAsync(task))?.OutputPath);
        Assert.Empty(Directory.GetDirectories(output, ".ffarchive-*"));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task FailedOrCancelledCommitPreparationCannotPublishAnOutput()
    {
        using var directory = new TemporaryDirectory();
        var source = await CreateZipAsync(directory.Path);
        var output = Path.Combine(directory.Path, "outputs");
        var service = new ArchiveConversionService(Engine());
        await Assert.ThrowsAsync<IOException>(() => service.ConvertAsync(source, output,
            new(ArchiveFormat.SevenZip), (_, _) => Task.FromResult<string?>(null),
            prepareCommit: (_, _, _) => throw new IOException("synthetic receipt write failed")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConvertAsync(source, output,
            new(ArchiveFormat.SevenZip), (_, _) => Task.FromResult<string?>(null), cancellationToken: cancellation.Token,
            prepareCommit: (_, _, _) => { cancellation.Cancel(); return Task.CompletedTask; }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.True(File.Exists(source));
    }

    private static ArchiveCommitReceiptStore Store(string root) => new(Path.Combine(root, "receipts"));
    private static string ReceiptPath(string root, Guid id) => Path.Combine(root, "receipts", id.ToString("N") + ".json");

    private static async Task<(ArchiveTaskCheckpoint Task, string Staged, string Final)> FixtureAsync(string root)
    {
        var source = Path.Combine(root, "source.zip");
        await File.WriteAllTextAsync(source, "source archive bytes are never modified");
        var output = Path.Combine(root, "outputs");
        var scratch = Path.Combine(output, ".ffarchive-synthetic");
        Directory.CreateDirectory(scratch);
        var staged = Path.Combine(scratch, "converted.zip");
        await File.WriteAllTextAsync(staged, "synthetic validated archive bytes");
        return (new ArchiveTaskCheckpoint
        {
            SourcePath = source, OutputDirectory = output, TargetFormat = ArchiveFormat.Zip,
            StrictVerification = true, EncryptOutput = true, State = ArchiveTaskState.Running
        }, staged, Path.Combine(output, "source.zip"));
    }

    private static string Engine()
    {
        var path = Path.Combine(RepositoryTestContext.Root, "work", "prepared-archive", "x64", "Engines", "7zip", "7z.exe");
        if (!File.Exists(path)) throw SkipException.ForSkip("The prepared official 7-Zip runtime is unavailable.");
        return path;
    }

    private static async Task<string> CreateZipAsync(string root)
    {
        var path = Path.Combine(root, "input.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            await using var output = archive.CreateEntry("content.txt").Open();
            await output.WriteAsync("archive content"u8.ToArray());
        }
        return path;
    }
}

using System.Security.Cryptography;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

[Collection(nameof(LocalizationServiceCollection))]
public sealed class MediaProbeSafeFactsTests
{
    [Fact]
    public async Task LocalReadAndEngineHash_ObserveOneByteAndLeaveSourceUntouched()
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "synthetic-private.mov");
        var engine = Path.Combine(directory.Path, "synthetic-probe.exe");
        byte[] bytes = [7, 19, 31, 42];
        await File.WriteAllBytesAsync(input, bytes);
        await File.WriteAllBytesAsync(engine, [1, 2, 3]);
        var started = DateTimeOffset.UtcNow;

        var result = await MediaProbeSafeFacts.CollectAsync(engine, input, CancellationToken.None);

        Assert.Equal(MediaProbeReadStatus.Readable, result.ReadStatus);
        Assert.Equal(bytes.Length, result.SourceLength);
        Assert.Equal(1, result.SourceBytesRead);
        Assert.Null(result.SourceNativeError);
        Assert.Equal(MediaProbeEngineState.Hashed, result.EngineState);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })), result.EngineSha256);
        Assert.Equal(3, result.EngineLength);
        Assert.InRange(result.ObservedAtUtc, started, DateTimeOffset.UtcNow);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(input));
        Assert.DoesNotContain("synthetic-private", result.ToString());
        Assert.DoesNotContain(directory.Path, result.ToString());
    }

    [Fact]
    public async Task EmptySource_IsReadableButHasNoBytes()
    {
        using var directory = new TemporaryDirectory();
        var empty = Path.Combine(directory.Path, "empty.bin");
        await File.WriteAllBytesAsync(empty, []);

        var result = await MediaProbeSafeFacts.CollectAsync(empty, empty, CancellationToken.None);

        Assert.Equal(MediaProbeReadStatus.Empty, result.ReadStatus);
        Assert.Equal(0, result.SourceLength);
        Assert.Equal(0, result.SourceBytesRead);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), result.EngineSha256);
    }

    [Fact]
    public async Task MissingSource_UsesObservedExceptionAndDoesNotInventLength()
    {
        using var directory = new TemporaryDirectory();
        var engine = Path.Combine(directory.Path, "engine.bin");
        await File.WriteAllTextAsync(engine, "synthetic engine");

        var result = await MediaProbeSafeFacts.CollectAsync(engine, Path.Combine(directory.Path, "missing.bin"), CancellationToken.None);

        Assert.Equal(MediaProbeReadStatus.Missing, result.ReadStatus);
        Assert.Equal(2, result.SourceNativeError);
        Assert.Null(result.SourceLength);
        Assert.Null(result.SourceBytesRead);
        Assert.Equal(MediaProbeEngineState.Hashed, result.EngineState);
    }

    [Fact]
    public async Task LockedSource_RecordsSharingViolationWithoutChangingTheLock()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "locked.bin");
        var engine = Path.Combine(directory.Path, "engine.bin");
        await File.WriteAllTextAsync(source, "synthetic input");
        await File.WriteAllTextAsync(engine, "synthetic engine");
        await using var held = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await MediaProbeSafeFacts.CollectAsync(engine, source, CancellationToken.None);

        Assert.Equal(MediaProbeReadStatus.SharingViolation, result.ReadStatus);
        Assert.Equal(32, result.SourceNativeError);
        Assert.Equal(MediaProbeEngineState.Hashed, result.EngineState);
        Assert.Equal(15, held.Length);
    }

    [Theory]
    [InlineData("\\\\synthetic.invalid\\private\\source.mov")]
    [InlineData("\\\\?\\C:\\synthetic-private\\source.mov")]
    [InlineData("relative-private.mov")]
    [InlineData("C:\\synthetic-private.mov:secret-stream")]
    public async Task UnsafeLocations_AreSkippedWithoutPathOrMessageDisclosure(string path)
    {
        var result = await MediaProbeSafeFacts.CollectAsync(path, path, CancellationToken.None);

        Assert.Equal(MediaProbeReadStatus.SkippedUnsafePath, result.ReadStatus);
        Assert.Equal(MediaProbeEngineState.SkippedUnsafePath, result.EngineState);
        Assert.Null(result.SourceLength);
        Assert.Null(result.SourceNativeError);
        Assert.Null(result.EngineSha256);
        Assert.DoesNotContain("private", result.ToString());
        Assert.DoesNotContain("synthetic.invalid", result.ToString());
    }

    [Fact]
    public async Task CacheReusesIdentityAndInvalidatesWhenBinarySizeChanges()
    {
        using var directory = new TemporaryDirectory();
        var engine = Path.Combine(directory.Path, "engine.bin");
        await File.WriteAllBytesAsync(engine, [1, 2, 3]);

        var first = await MediaProbeSafeFacts.CollectAsync(engine, engine, CancellationToken.None);
        var cached = await MediaProbeSafeFacts.CollectAsync(engine, engine, CancellationToken.None);
        await File.WriteAllBytesAsync(engine, [1, 2, 3, 4]);
        var changed = await MediaProbeSafeFacts.CollectAsync(engine, engine, CancellationToken.None);

        Assert.Equal(MediaProbeEngineState.Hashed, first.EngineState);
        Assert.Equal(MediaProbeEngineState.Cached, cached.EngineState);
        Assert.Equal(first.EngineSha256, cached.EngineSha256);
        Assert.Equal(MediaProbeEngineState.Hashed, changed.EngineState);
        Assert.NotEqual(first.EngineSha256, changed.EngineSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })), changed.EngineSha256);
    }

    [Fact]
    public async Task CacheInvalidatesWhenModificationTimeChangesAtSameSize()
    {
        using var directory = new TemporaryDirectory();
        var engine = Path.Combine(directory.Path, "engine.bin");
        await File.WriteAllBytesAsync(engine, [1, 2, 3]);
        var originalTime = File.GetLastWriteTimeUtc(engine);
        var first = await MediaProbeSafeFacts.CollectAsync(engine, engine, CancellationToken.None);
        await File.WriteAllBytesAsync(engine, [3, 2, 1]);
        File.SetLastWriteTimeUtc(engine, originalTime.AddSeconds(4));

        var changed = await MediaProbeSafeFacts.CollectAsync(engine, engine, CancellationToken.None);

        Assert.Equal(MediaProbeEngineState.Hashed, changed.EngineState);
        Assert.NotEqual(first.EngineSha256, changed.EngineSha256);
    }

    [Fact]
    public async Task CacheEvictsOlderEngineIdentitiesAfterEightEntries()
    {
        using var directory = new TemporaryDirectory();
        var firstPath = Path.Combine(directory.Path, "engine-0.bin");
        for (var i = 0; i < 9; i++)
        {
            var path = Path.Combine(directory.Path, $"engine-{i}.bin");
            await File.WriteAllBytesAsync(path, [(byte)i]);
            var result = await MediaProbeSafeFacts.CollectAsync(path, path, CancellationToken.None);
            Assert.Equal(MediaProbeEngineState.Hashed, result.EngineState);
        }
        var evicted = await MediaProbeSafeFacts.CollectAsync(firstPath, firstPath, CancellationToken.None);
        Assert.Equal(MediaProbeEngineState.Hashed, evicted.EngineState);
    }

    [Theory]
    [InlineData((int)FileAttributes.ReparsePoint)]
    [InlineData((int)FileAttributes.Offline)]
    [InlineData(0x00040000)]
    [InlineData(0x00400000)]
    public void UnsafeAncestor_IsRejectedBeforeInspectingOrOpeningItsTarget(int unsafeAttribute)
    {
        var examined = new List<string>();
        var accepted = MediaProbeSafeFacts.IsEligibleLocalFile("C:\\synthetic\\linked\\private.mov", CancellationToken.None,
            _ => DriveType.Fixed, path =>
            {
                examined.Add(path);
                Assert.DoesNotContain("private.mov", path);
                return path.EndsWith("linked", StringComparison.Ordinal) ? FileAttributes.Directory | (FileAttributes)unsafeAttribute
                    : FileAttributes.Directory;
            });
        Assert.False(accepted);
        Assert.Equal(new[] { "C:\\", "C:\\synthetic", "C:\\synthetic\\linked" }, examined);
    }

    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Unknown)]
    public void NonFixedDrive_IsRejectedBeforeMetadataQueries(DriveType drive)
    {
        Assert.False(MediaProbeSafeFacts.IsEligibleLocalFile("Z:\\private.mov", CancellationToken.None,
            _ => drive, _ => throw new InvalidOperationException("No metadata query expected.")));
    }

    [Fact]
    public async Task OversizedEngine_IsNotHashed()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "input.bin");
        var engine = Path.Combine(directory.Path, "large-engine.bin");
        await File.WriteAllTextAsync(source, "synthetic source");
        await using (var file = new FileStream(engine, FileMode.CreateNew, FileAccess.Write))
            file.SetLength(256L * 1024 * 1024 + 1);

        var result = await MediaProbeSafeFacts.CollectAsync(engine, source, CancellationToken.None);

        Assert.Equal(MediaProbeReadStatus.Readable, result.ReadStatus);
        Assert.Equal(MediaProbeEngineState.TooLarge, result.EngineState);
        Assert.Null(result.EngineSha256);
    }

    [Fact]
    public async Task CancelledCaller_DoesNotStartObservation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaProbeSafeFacts.CollectWithBudgetAsync(
            (_, _) => { calls++; throw new InvalidOperationException(); }, TimeSpan.FromSeconds(1), cancelled.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DeadlineKeepsCompletedSourceAndSingleSlotUntilBlockedWorkerActuallyExits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = Facts with { ReadStatus = MediaProbeReadStatus.Readable, SourceLength = 17, SourceBytesRead = 1 };
        var first = MediaProbeSafeFacts.CollectWithBudgetAsync(async (publish, _) =>
        {
            publish(observed);
            entered.SetResult();
            await release.Task;
            return observed;
        }, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var expired = await first;
            Assert.Equal(MediaProbeReadStatus.Readable, expired.ReadStatus);
            Assert.Equal(17, expired.SourceLength);
            Assert.Equal(MediaProbeEngineState.TimedOut, expired.EngineState);
            var busy = await MediaProbeSafeFacts.CollectWithBudgetAsync((_, _) => throw new InvalidOperationException("No second worker."),
                TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal(MediaProbeReadStatus.Busy, busy.ReadStatus);
            Assert.Equal(MediaProbeEngineState.Busy, busy.EngineState);
        }
        finally { release.TrySetResult(); }
        await WaitForSlotAsync();
    }

    [Fact]
    public async Task CallerCancellation_PropagatesButCannotReleaseAnUnfinishedOsWorker()
    {
        using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = MediaProbeSafeFacts.CollectWithBudgetAsync(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return Facts;
        }, TimeSpan.FromSeconds(3), cancelled.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            var busy = await MediaProbeSafeFacts.CollectWithBudgetAsync((_, _) => throw new InvalidOperationException("No second worker."),
                TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal(MediaProbeReadStatus.Busy, busy.ReadStatus);
        }
        finally { release.TrySetResult(); }
        await WaitForSlotAsync();
    }

    [Fact]
    public async Task CollectorFailure_DoesNotExposeExceptionOrKeepSlotOccupied()
    {
        var result = await MediaProbeSafeFacts.CollectWithBudgetAsync((_, _) =>
            throw new IOException("C:\\private\\secret.mov token=synthetic-secret"), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(MediaProbeReadStatus.NotChecked, result.ReadStatus);
        Assert.DoesNotContain("secret", result.ToString());
        await WaitForSlotAsync();
    }

    private static MediaProbeSafeFacts Facts => new(MediaProbeReadStatus.NotChecked, null, null, null,
        MediaProbeEngineState.NotChecked, null, null, DateTimeOffset.UtcNow);

    private static async Task WaitForSlotAsync()
    {
        for (var i = 0; i < 500; i++)
        {
            var result = await MediaProbeSafeFacts.CollectWithBudgetAsync((_, _) => Task.FromResult(Facts),
                TimeSpan.FromSeconds(1), CancellationToken.None);
            if (result.ReadStatus != MediaProbeReadStatus.Busy) return;
            await Task.Delay(10);
        }
        Assert.Fail("Observation slot was not released when its worker completed.");
    }
}

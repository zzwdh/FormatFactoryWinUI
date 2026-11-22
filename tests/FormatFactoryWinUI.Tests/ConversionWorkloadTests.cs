using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class ConversionWorkloadTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    [Fact]
    public void Estimate_SourcePixelsHdrAndCpuHeavyCodecIncreaseAdmissionCost()
    {
        var hd = Video(1920, 1080);
        var uhd = Video(3840, 2160);
        var hdr = Video(3840, 2160);
        hdr.Probe.IsHdr = true;
        var av1 = Video(1920, 1080);
        av1.TargetFormat = "AV1";

        Assert.Equal(1, ConversionWorkload.FromTask(hd).Weight);
        Assert.Equal(2, ConversionWorkload.FromTask(uhd).Weight);
        Assert.Equal(4, ConversionWorkload.FromTask(Video(7680, 4320)).Weight);
        Assert.True(ConversionWorkload.FromTask(hdr).EstimatedPeakBytes > ConversionWorkload.FromTask(uhd).EstimatedPeakBytes);
        Assert.Equal(2, ConversionWorkload.FromTask(av1).Weight);
    }

    [Fact]
    public void Estimate_StreamCopyAndAudioExtractionDoNotReserveVideoFrameBuffers()
    {
        var remux = Video(7680, 4320);
        remux.ConversionMode = "lossless";
        var audio = Video(7680, 4320);
        audio.TargetFormat = "MP3";

        Assert.Equal(new ConversionWorkload(1, 96 * MiB), ConversionWorkload.FromTask(remux));
        Assert.Equal(new ConversionWorkload(1, 128 * MiB), ConversionWorkload.FromTask(audio));
    }

    [Fact]
    public void Estimate_LargeDocumentsAndImagesHaveLargerReservations()
    {
        var document = new MediaTask { ContentProbe = new ContentProbeResult { ContentKind = ContentKind.Document, PageCount = 200 } };
        var image = new MediaTask { Probe = new MediaProbeResult { Kind = MediaKind.Image, Width = 10000, Height = 10000 } };

        Assert.Equal(new ConversionWorkload(2, 584 * MiB), ConversionWorkload.FromTask(document));
        Assert.Equal(3, ConversionWorkload.FromTask(image).Weight);
        Assert.True(ConversionWorkload.FromTask(image).EstimatedPeakBytes > 3 * GiB);
    }

    [Fact]
    public void Admission_WeightsRetainConfiguredUpperBound()
    {
        var large = new ConversionWorkload(2, GiB);
        var small = new ConversionWorkload(1, 128 * MiB);
        var resources = new ConversionResourceSnapshot(32 * GiB, 24 * GiB);

        Assert.False(ConversionAdmissionPolicy.CanAdmit(large, [large], 2, resources));
        Assert.True(ConversionAdmissionPolicy.CanAdmit(large, [large], 4, resources));
        Assert.True(ConversionAdmissionPolicy.CanAdmit(small, [large], 4, resources));
        Assert.False(ConversionAdmissionPolicy.CanAdmit(small, [large, small, small], 4, resources));
    }

    [Fact]
    public void Admission_MemoryReserveAndCombinedPeakBudgetConstrainNewJobs()
    {
        var small = new ConversionWorkload(1, 256 * MiB);
        Assert.False(ConversionAdmissionPolicy.CanAdmit(small, [small], 4, new(8 * GiB, 900 * MiB)));
        Assert.True(ConversionAdmissionPolicy.CanAdmit(small, [small], 4, new(8 * GiB, 4 * GiB)));
        Assert.False(ConversionAdmissionPolicy.CanAdmit(new(1, 3 * GiB), [new(1, 3 * GiB)], 4, new(8 * GiB, 7 * GiB)));
    }

    [Fact]
    public void Admission_IdleQueueMakesProgressEvenWhenTaskExceedsBudget()
    {
        Assert.True(ConversionAdmissionPolicy.CanAdmit(new(4, 8 * GiB), [], 1, new(4 * GiB, 100 * MiB)));
        Assert.False(ConversionAdmissionPolicy.CanAdmit(new(1, 128 * MiB), [new(4, 8 * GiB)], 4, new(4 * GiB, 100 * MiB)));
    }

    [Fact]
    public void Admission_InvalidOrUnavailableSamplesFallBackToWorkloadWeights()
    {
        var small = new ConversionWorkload(1, GiB);
        Assert.True(ConversionAdmissionPolicy.CanAdmit(small, [small], 2, ConversionResourceSnapshot.Unknown));
        Assert.True(ConversionAdmissionPolicy.CanAdmit(small, [small], 2, new(2 * GiB, 3 * GiB)));
        Assert.False(ConversionAdmissionPolicy.CanAdmit(new(2, GiB), [small], 2, ConversionResourceSnapshot.Unknown));
    }

    [Fact]
    public async Task Coordinator_RechecksPressureWithoutInterruptingRunningOperation()
    {
        var resources = new ConversionResourceSnapshot(8 * GiB, 900 * MiB);
        using var queue = new ConversionQueueCoordinator(() => resources);
        queue.ConfigureParallelism(2);
        var first = Signal();
        var second = Signal();
        var release = Signal();
        try
        {
            Start(queue, Guid.NewGuid(), first, release, new(1, 256 * MiB));
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Start(queue, Guid.NewGuid(), second, release, new(1, 256 * MiB));
            Assert.False(second.Task.IsCompleted);

            resources = new(8 * GiB, 4 * GiB);
            queue.RecheckAdmission();
            await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, queue.ActiveOperationCount);
        }
        finally
        {
            release.TrySetResult();
            await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Coordinator_HeavyHeadKeepsOrderAndStartsAfterPreviousTaskCompletes()
    {
        using var queue = new ConversionQueueCoordinator(() => new(32 * GiB, 24 * GiB));
        queue.ConfigureParallelism(2);
        var first = Signal();
        var heavy = Signal();
        var third = Signal();
        var releaseFirst = Signal();
        var releaseHeavy = Signal();
        var releaseThird = Signal();
        try
        {
            Start(queue, Guid.NewGuid(), first, releaseFirst, new(1, 128 * MiB));
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Start(queue, Guid.NewGuid(), heavy, releaseHeavy, new(4, 8 * GiB));
            Start(queue, Guid.NewGuid(), third, releaseThird, new(1, 128 * MiB));
            Assert.False(heavy.Task.IsCompleted);
            Assert.False(third.Task.IsCompleted);
            releaseFirst.TrySetResult();
            await heavy.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(third.Task.IsCompleted);
            releaseHeavy.TrySetResult();
            await third.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseHeavy.TrySetResult();
            releaseThird.TrySetResult();
            await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Coordinator_SamplingFailureDoesNotStrandPendingOperations()
    {
        using var queue = new ConversionQueueCoordinator(() => throw new IOException("sample unavailable"));
        queue.ConfigureParallelism(2);
        var first = Signal();
        var second = Signal();
        var release = Signal();
        try
        {
            Start(queue, Guid.NewGuid(), first, release, new(1, 256 * MiB));
            Start(queue, Guid.NewGuid(), second, release, new(1, 256 * MiB));
            await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Coordinator_CancellingPressureBlockedHeadReleasesNextEligibleTask()
    {
        using var queue = new ConversionQueueCoordinator(() => new(32 * GiB, 24 * GiB));
        queue.ConfigureParallelism(2);
        var first = Signal();
        var blocked = Signal();
        var next = Signal();
        var release = Signal();
        var blockedId = Guid.NewGuid();
        try
        {
            Start(queue, Guid.NewGuid(), first, release, new(1, 128 * MiB));
            Start(queue, blockedId, blocked, release, new(4, 8 * GiB));
            Start(queue, Guid.NewGuid(), next, release, new(1, 128 * MiB));
            Assert.False(blocked.Task.IsCompleted);
            Assert.True(await queue.TryCancelWaitingAsync(blockedId));
            await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blocked.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await queue.WaitForAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static MediaTask Video(int width, int height) => new()
    {
        TargetFormat = "MP4",
        Probe = new MediaProbeResult { Kind = MediaKind.Video, Width = width, Height = height }
    };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Start(ConversionQueueCoordinator queue, Guid id, TaskCompletionSource started,
        TaskCompletionSource release, ConversionWorkload workload) => Assert.True(queue.TryStart(id,
        async token => { started.TrySetResult(); await release.Task.WaitAsync(token); },
        () => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask, workload));
}

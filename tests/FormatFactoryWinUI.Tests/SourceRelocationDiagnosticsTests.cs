using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class SourceRelocationDiagnosticsTests
{
    [Theory]
    [InlineData("size")]
    [InlineData("type")]
    [InlineData("missing")]
    [InlineData("unknown")]
    public async Task InvalidChoice_IsExpectedAndLeavesQueueAndFilesUntouched(string kind)
    {
        using var directory = new TemporaryDirectory();
        var oldPath = Path.Combine(directory.Path, "old.mp4");
        var candidate = Path.Combine(directory.Path, kind == "type" ? "new.mkv" : "new.mp4");
        var candidateBytes = new byte[kind == "size" ? 4 : 3];
        if (kind != "missing") await File.WriteAllBytesAsync(candidate, candidateBytes);
        var model = Model(oldPath, kind == "unknown" ? 0 : 3);
        var before = QueueStore.ToPersistedItem(model);
        var persistence = new CountingPersistence();
        var imported = 0;
        var released = 0;
        var diagnostics = new List<Exception>();
        using var session = Session(persistence, diagnostics.Add);
        session.AddTask(model);

        var error = await Assert.ThrowsAsync<SourceRelocationValidationException>(() =>
            session.RelocateSourcesAsync(model, new Dictionary<string, string> { [oldPath] = candidate },
                (_, _) => { imported++; throw new InvalidOperationException("Must not import invalid choice."); },
                _ => released++));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(0, imported);
        Assert.Equal(0, released);
        Assert.Equal(0, persistence.Saves);
        Assert.Empty(diagnostics);
        Assert.Same(model, Assert.Single(session.Tasks));
        Assert.Equal(before.InputPath, model.InputPath);
        Assert.Equal(before.State, model.State);
        Assert.False(model.IsRecoveringOutput);
        if (kind != "missing") Assert.Equal(candidateBytes, await File.ReadAllBytesAsync(candidate));
    }

    [Fact]
    public void SourceReturnsBeforePlanning_IsExpectedAndDoesNotReplaceIt()
    {
        using var directory = new TemporaryDirectory();
        var oldPath = Path.Combine(directory.Path, "old.mp4");
        var candidate = Path.Combine(directory.Path, "new.mp4");
        File.WriteAllBytes(oldPath, [1, 2, 3]);
        File.WriteAllBytes(candidate, [4, 5, 6]);
        var model = Model(oldPath, 3);

        Assert.Throws<SourceRelocationValidationException>(() => SourceRelocationService.CreateRequest(model,
            new Dictionary<string, string> { [oldPath] = candidate }));

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(oldPath));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(candidate));
        Assert.Equal(oldPath, model.InputPath);
    }

    [Fact]
    public void FolderChoiceMissingFile_IsExpectedBeforeImport()
    {
        using var directory = new TemporaryDirectory();
        var oldFolder = Path.Combine(directory.Path, "old");
        var newFolder = Path.Combine(directory.Path, "new");
        Directory.CreateDirectory(newFolder);
        var model = Model(Path.Combine(oldFolder, "source.mp4"), 3);

        Assert.Throws<SourceRelocationValidationException>(() =>
            SourceRelocationService.MapFolder(model, oldFolder, newFolder));
        Assert.Equal(Path.Combine(oldFolder, "source.mp4"), model.InputPath);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("size")]
    public void ImportedIdentityFailure_RemainsUnexpected(string kind)
    {
        using var directory = new TemporaryDirectory();
        var original = Model(Path.Combine(directory.Path, "old.mp4"), 3);
        var candidate = Path.Combine(directory.Path, "new.mp4");
        File.WriteAllBytes(candidate, new byte[3]);
        var request = SourceRelocationService.CreateRequest(original, new Dictionary<string, string> { [original.InputPath] = candidate });
        var imported = QueueSession.FromPersisted(request);
        if (kind == "type") imported.ContentKind = ContentKind.Audio;
        else File.WriteAllBytes(candidate, new byte[4]);

        Assert.Throws<InvalidOperationException>(() => SourceRelocationService.ValidateImported(original, request, imported));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("missing")]
    [InlineData("source-returned")]
    public async Task CandidateChangesAfterProbe_RemainsUnexpectedAndReleasesTemporaryImport(string kind)
    {
        using var directory = new TemporaryDirectory();
        var oldPath = Path.Combine(directory.Path, "old.mp4");
        var candidate = Path.Combine(directory.Path, "new.mp4");
        await File.WriteAllBytesAsync(candidate, new byte[3]);
        var original = Model(oldPath, 3);
        var persistence = new CountingPersistence();
        var dispatched = 0;
        var imported = new List<MediaTask>();
        var released = new List<MediaTask>();
        using var session = new QueueSession(persistence, (_, _) => throw new NotSupportedException(),
            (_, _, _) => throw new NotSupportedException(), (_, _) => Task.CompletedTask,
            dispatch: action =>
            {
                // The probe and its first identity validation have finished. Change the file
                // before publication on the UI dispatcher, as an external writer can do.
                if (++dispatched == 2)
                {
                    if (kind == "size") File.WriteAllBytes(candidate, new byte[4]);
                    else if (kind == "missing") File.Delete(candidate);
                    else File.WriteAllBytes(oldPath, new byte[3]);
                }
                return action();
            });
        session.AddTask(original);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RelocateSourcesAsync(original,
            new Dictionary<string, string> { [oldPath] = candidate },
            (item, _) =>
            {
                var temporary = QueueSession.FromPersisted(item);
                imported.Add(temporary);
                return Task.FromResult(new MediaImportItemResult(item.InputPath, temporary, null));
            }, released.Add));

        Assert.IsType<SourceRelocationValidationException>(error.InnerException);
        Assert.Equal(error.Message, error.InnerException!.Message);
        Assert.Same(Assert.Single(imported), Assert.Single(released));
        Assert.Same(original, Assert.Single(session.Tasks));
        Assert.Equal(oldPath, original.InputPath);
        Assert.Equal(ConversionState.Failed, original.State);
        Assert.False(original.IsRecoveringOutput);
        Assert.Equal(0, persistence.Saves);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("invalid-operation")]
    [InlineData("permission")]
    [InlineData("com")]
    public async Task ImportFailure_KeepsOriginalExceptionForHostDiagnostics(string kind)
    {
        using var directory = new TemporaryDirectory();
        var original = Model(Path.Combine(directory.Path, "old.mp4"), 3);
        var candidate = Path.Combine(directory.Path, "new.mp4");
        await File.WriteAllBytesAsync(candidate, new byte[3]);
        Exception expected = kind switch
        {
            "io" => new IOException("synthetic probe failure"),
            "permission" => new UnauthorizedAccessException("synthetic access failure"),
            "com" => new COMException("synthetic picker failure", unchecked((int)0x80004005)),
            _ => new InvalidOperationException("synthetic import failure")
        };
        var persistence = new CountingPersistence();
        using var session = Session(persistence, _ => { });
        session.AddTask(original);

        var actual = await Record.ExceptionAsync(() => session.RelocateSourcesAsync(original,
            new Dictionary<string, string> { [original.InputPath] = candidate },
            (item, _) => Task.FromResult(new MediaImportItemResult(item.InputPath, null, expected)), _ => { }));

        Assert.Same(expected, actual);
        Assert.IsNotType<SourceRelocationValidationException>(actual);
        Assert.Equal(Path.Combine(directory.Path, "old.mp4"), original.InputPath);
        Assert.False(original.IsRecoveringOutput);
        Assert.Equal(0, persistence.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedSaveOrCleanupFailure_IsStillReported(bool cleanup)
    {
        using var directory = new TemporaryDirectory();
        var original = Model(Path.Combine(directory.Path, "old.mp4"), 3);
        var candidate = Path.Combine(directory.Path, "new.mp4");
        await File.WriteAllBytesAsync(candidate, new byte[3]);
        var expected = new IOException(cleanup ? "synthetic cleanup failure" : "synthetic save failure");
        var persistence = new CountingPersistence { SaveError = cleanup ? null : expected };
        var diagnostics = new List<Exception>();
        using var session = Session(persistence, diagnostics.Add);
        session.AddTask(original);

        var result = await session.RelocateSourcesAsync(original,
            new Dictionary<string, string> { [original.InputPath] = candidate },
            (item, _) => Task.FromResult(new MediaImportItemResult(item.InputPath, QueueSession.FromPersisted(item), null)),
            _ => { if (cleanup) throw expected; });

        Assert.Same(expected, Assert.Single(diagnostics));
        if (cleanup) Assert.Null(result.SaveError);
        else Assert.Same(expected, result.SaveError);
        Assert.Equal(1, result.UpdatedTasks);
        Assert.Equal(candidate, original.InputPath);
        Assert.False(original.IsRecoveringOutput);
    }

    [Fact]
    public void SourceRelocationUi_OnlySuppressesExplicitPreImportRejections()
    {
        var code = File.ReadAllText(Path.Combine(RepositoryTestContext.Root, "src", "FormatFactoryWinUI", "Windowing", "MainWindow", "Import", "MainWindow.SourceRelocation.cs"));
        var catchBlocks = Regex.Matches(code,
            @"catch \(Exception exception\)\s*\{(?<body>.*?)\}", RegexOptions.Singleline)
            .Select(match => match.Groups["body"].Value).ToArray();
        Assert.Equal(4, catchBlocks.Length);
        foreach (var body in catchBlocks.Take(3))
            Assert.Contains("if (exception is not SourceRelocationValidationException) SafeDiagnostics.Report(exception);", body);
        Assert.Contains("ShowInfo(exception.Message, InfoBarSeverity.Warning)", code);
        Assert.Contains("skipped++;", catchBlocks[1]);
        Assert.Contains("errors.Add", catchBlocks[1]);
        Assert.Contains("catch (Exception exception) { SafeDiagnostics.Report(exception); }", code);
    }

    private static MediaTask Model(string path, long size) => new()
    {
        InputPath = path, ContentKind = ContentKind.Video, SourceFormatId = "mp4",
        TargetFormat = "MP4", TargetFormatId = "mp4", State = ConversionState.Failed, FileSize = size
    };

    private static QueueSession Session(IQueuePersistence persistence, Action<Exception> diagnostics) =>
        new(persistence, (_, _) => throw new NotSupportedException(), (_, _, _) => throw new NotSupportedException(),
            (_, _) => Task.CompletedTask, diagnostics: diagnostics);

    private sealed class CountingPersistence : IQueuePersistence
    {
        public int Saves { get; private set; }
        public Exception? SaveError { get; init; }
        public Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueLoadResult(QueueLoadStatus.Success, []));
        public Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (SaveError is not null) throw SaveError;
            return Task.CompletedTask;
        }
    }
}

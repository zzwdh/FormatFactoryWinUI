using System.Reflection;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Composition;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckQueueSessionClosingImportAsync()
    {
        await CheckAsync("Late import during failed shutdown releases its path and can be accepted after keeping the window open", async () =>
        {
            var fixture = Path.Combine(_runRoot, "queue-session-late-import", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            var queuePath = Path.Combine(fixture, "queue.json");
            var sourcePath = Path.Combine(fixture, "late.docx");
            await File.WriteAllTextAsync(sourcePath, "Import fixture; no conversion is started.");
            var persistence = new QueueSmokePersistence(new QueueStore(queuePath, () => []));
            var services = new MainWindowServices(new SettingsService(), persistence, new LocalImportStorageAccess());
            var window = new MainWindow(services);
            var session = (QueueSession)(typeof(MainWindow).GetField("_queueSession", InstanceMembers)?.GetValue(window)
                ?? throw new InvalidOperationException("The production queue session is missing."));
            var apply = typeof(MainWindow).GetMethod("ApplyImportResult", InstanceMembers)
                ?? throw new InvalidOperationException("The production import acceptance method is missing.");
            try
            {
                await session.RestoreAsync().WaitAsync(TimeSpan.FromSeconds(5));
                // Complete a real import before shutdown, then deliver its result late.
                // Local access and this queue path keep the fixture fully isolated.
                var late = await services.Conversions.MediaImport.ImportPathsAsync(string.Empty, new[] { sourcePath });
                Require(late.AddedTasks.Count == 1, "The real import did not reserve its source path.");
                persistence.FailSaves = true;
                var stopped = await session.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Require(!stopped.CanClose && session.IsStopping, "The injected save failure did not retain the open, stopped session.");
                apply.Invoke(window, [late, false]);
                Require(window.Tasks.Count == 0 && session.Tasks.Count == 0,
                    "Late import appeared in the window or durable session during shutdown.");

                persistence.FailSaves = false;
                session.ContinueAfterFailedShutdown();
                var retry = await services.Conversions.MediaImport.ImportPathsAsync(string.Empty, new[] { sourcePath });
                Require(retry.AddedTasks.Count == 1, "The rejected late import leaked its path reservation.");
                apply.Invoke(window, [retry, false]);
                var accepted = retry.AddedTasks[0];
                Require(window.Tasks.Count == 1 && session.Tasks.Count == 1 &&
                        ReferenceEquals(window.Tasks[0].Model, accepted) && ReferenceEquals(session.Tasks[0], accepted),
                    "Keeping the window open did not restore atomic UI and session admission.");
                await session.SaveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var durable = await new QueueStore(queuePath, () => []).LoadResultAsync();
                Require(durable.Items.Count == 1 && durable.Items[0].TaskId == accepted.Id,
                    "The accepted task did not reach the isolated durable queue.");
            }
            finally
            {
                persistence.FailSaves = false;
                await session.ShutdownAsync(save: false).WaitAsync(TimeSpan.FromSeconds(5));
                window.Close();
            }
        });
    }

    private sealed class QueueSmokePersistence(QueueStore durable) : IQueuePersistence
    {
        public bool FailSaves { get; set; }
        public Task<QueueLoadResult> LoadResultAsync(CancellationToken cancellationToken = default) =>
            durable.LoadResultAsync(cancellationToken);
        public Task SaveAsync(IEnumerable<MediaTask> tasks, CancellationToken cancellationToken = default) =>
            FailSaves ? Task.FromException(new IOException("Isolated queue fixture is temporarily read only."))
                : durable.SaveAsync(tasks, cancellationToken);
    }
}

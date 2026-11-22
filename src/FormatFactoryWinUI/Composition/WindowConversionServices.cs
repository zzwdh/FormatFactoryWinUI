using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Disc;
using FormatFactoryWinUI.Documents;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Composition;

/// <summary>Builds queue sessions and engine routes around one window's import and process ownership.</summary>
internal sealed class WindowConversionServices
{
    private readonly IQueuePersistence _queuePersistence;
    private readonly IImportStorageAccess _storageAccess;
    private readonly MediaProbeService _mediaProbe;

    public WindowConversionServices(IQueuePersistence? persistence, IImportStorageAccess? storageAccess)
    {
        _queuePersistence = persistence ?? new QueueStore();
        _storageAccess = storageAccess ?? new WindowsImportStorageAccess();
        var imageCodecs = new WindowsImageCodecService();
        _mediaProbe = new MediaProbeService(new WindowsMediaMetadataReader(), imageCodecs);
        NativeImageConversion = new NativeImageConversionService(imageCodecs, Conversion);
        MediaImport = CreateImporter();
        AutomationImport = CreateImporter();
        WatchFolderImport = new WatchFolderImportCoordinator(MediaImport);
    }

    public ConversionHistoryStore HistoryStore { get; } = new();
    public FfmpegLocator FfmpegLocator { get; } = new();
    public ConversionService Conversion { get; } = new();
    public NativeImageConversionService NativeImageConversion { get; }
    public OpticalDiscDiscovery OpticalDiscDiscovery { get; } = new();
    public ConversionQueueCoordinator QueueCoordinator { get; } = new();
    public MediaImportCoordinator MediaImport { get; }
    public MediaImportCoordinator AutomationImport { get; }
    public WatchFolderImportCoordinator WatchFolderImport { get; }

    // Each importer owns independent duplicate reservations. Watched folders share the main queue's importer.
    public MediaImportCoordinator CreateImporter(IImportStorageAccess? storageAccess = null) =>
        new(_mediaProbe, OpticalDiscDiscovery, storageAccess ?? _storageAccess);

    // Conversion settings are read when each operation starts, preserving edits made in the UI.
    private static DocumentConversionService CreateDocumentConversion(AppSettings settings) =>
        new(new ExternalToolConfiguration(
            settings.LibreOfficePath,
            settings.CalibreEbookConvertPath,
            settings.Caj2PdfPath));

    public ConversionWorkflow CreateConversionWorkflow(AppSettings settings, string ffmpegPath)
    {
        // Priority preserves document routes for image collections, then physical/image discs,
        // then native image conversion (with its established FFmpeg fallback), then media.
        var engines = new List<IConversionEngine>
        {
            CreateDocumentConversion(settings),
            new DiscConversionEngine(new DiscConversionService(OpticalDiscDiscovery), ffmpegPath, settings),
            new NativeImageConversionEngine(NativeImageConversion, ffmpegPath, settings)
        };
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
            engines.Add(new FfmpegConversionEngine(ffmpegPath, settings, Conversion));
        return new ConversionWorkflow(engines);
    }

    public QueueSession CreateQueueSession(Func<string> ffprobePath,
        Func<MediaTask, IProgress<ConversionExecutionProgress>?, CancellationToken, Task<ConversionWorkflowResult>> execute,
        Func<ConversionHistoryItem, CancellationToken, Task> history, Func<Func<Task>, Task> dispatch) =>
        new(_queuePersistence, (item, token) => MediaImport.ImportPersistedAsync(ffprobePath(), item, token),
            execute, history, dispatch, coordinator: QueueCoordinator, releaseImport: MediaImport.Forget);
}

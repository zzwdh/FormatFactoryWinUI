using System.Diagnostics;
using FormatFactoryWinUI.Services;
using Windows.Storage.Pickers;

namespace FormatFactoryWinUI.Composition;

/// <summary>Windows shell actions and factories for services that require a live window handle.</summary>
internal sealed class WindowShellServices
{
    private readonly Func<IntPtr, Task<string?>>? _chooseOutputDirectory;

    public WindowShellServices(Action<string>? openLogsDirectory = null, Action<ProcessStartInfo>? startExplorer = null,
        Func<IntPtr, Task<string?>>? chooseOutputDirectory = null, Func<Task>? installMakeMkv = null)
    {
        OpenLogsDirectory = openLogsDirectory ?? OpenLogsDirectoryWithExplorer;
        StartExplorer = startExplorer ?? (info => { Process.Start(info); });
        _chooseOutputDirectory = chooseOutputDirectory;
        InstallMakeMkvAsync = installMakeMkv ?? BundledEngineInstaller.InstallMakeMkvAsync;
    }

    public Action<string> OpenLogsDirectory { get; }
    public Action<ProcessStartInfo> StartExplorer { get; }
    public Func<Task> InstallMakeMkvAsync { get; }

    private static void OpenLogsDirectoryWithExplorer(string directory)
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }

    public Task<string?> ChooseOutputDirectoryAsync(IntPtr handle, PlatformDiagnosticOperation diagnostic)
    {
        if (_chooseOutputDirectory is null) return PickOutputDirectoryAsync(handle, diagnostic);
        // An injected selection does not expose native stages; do not invent one.
        diagnostic.Stage = RuntimeFailureStage.Unknown;
        return _chooseOutputDirectory(handle);
    }

    private static Task<string?> PickOutputDirectoryAsync(IntPtr handle, PlatformDiagnosticOperation diagnostic) =>
        OutputFolderPickerSteps.PickAsync(diagnostic,
            () =>
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
                picker.FileTypeFilter.Add("*");
                return picker;
            },
            picker => WinRT.Interop.InitializeWithWindow.Initialize(picker, handle),
            async picker => (await picker.PickSingleFolderAsync())?.Path);

    // Handle-bound services remain deferred until the native window exists.
    public TaskbarProgressService CreateTaskbarProgress(IntPtr handle) => new(handle);
    public WindowsShareService CreateShareSource(IntPtr handle) => new(handle);
    public TrayIconService CreateTrayIcon(IntPtr handle) =>
        new(handle, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
}

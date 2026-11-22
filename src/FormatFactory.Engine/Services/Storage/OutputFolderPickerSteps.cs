namespace FormatFactoryWinUI.Services;

/// <summary>Shares the native picker stage boundaries without observing selected folder contents.</summary>
internal static class OutputFolderPickerSteps
{
    internal static async Task<string?> PickAsync<TPicker>(PlatformDiagnosticOperation diagnostic,
        Func<TPicker> create, Action<TPicker> initialize, Func<TPicker, Task<string?>> show)
    {
        diagnostic.Stage = RuntimeFailureStage.PickerCreate;
        var picker = create();
        diagnostic.Stage = RuntimeFailureStage.PickerInitialize;
        initialize(picker);
        diagnostic.Stage = RuntimeFailureStage.PickerShow;
        var path = await show(picker);
        diagnostic.ResultCount = path is null ? 0 : 1;
        return path;
    }
}

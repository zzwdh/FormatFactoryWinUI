namespace FormatFactoryWinUI.Services;

internal static class ConversionWorkerProcess
{
    internal const string FileName = "FormatFactory.ConversionWorker.exe";
    internal const string ProtocolVersionArgument = "--protocol-v1";
    internal static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, FileName);
}

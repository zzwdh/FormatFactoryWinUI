using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FormatFactory.FilterWorker;

internal static class WorkerProtocol
{
    public const int Version = 1;
    public const int MaximumRequestBytes = 128 * 1024;
    public const int MaximumResponseBytes = 16 * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

internal sealed record WorkerRequest(int Version, string Operation, string Path);
internal sealed record WorkerResponse(int Version, bool IsAvailable, string Text, string? Failure, int? HResult = null)
{
    public static WorkerResponse Failed(string failure, int? hresult = null) =>
        new(WorkerProtocol.Version, false, string.Empty, failure, hresult);
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not ["--stdio-v1"]) return 2;
        if (OperatingSystem.IsWindows()) SetErrorMode(0x0001 | 0x0002 | 0x8000);
        WorkerResponse response;
        try
        {
            using var input = Console.OpenStandardInput();
            using var requestBytes = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (requestBytes.Length + count > WorkerProtocol.MaximumRequestBytes)
                    throw new InvalidDataException("Request exceeds protocol limit.");
                requestBytes.Write(buffer, 0, count);
            }
            var request = JsonSerializer.Deserialize<WorkerRequest>(requestBytes.ToArray());
            if (request is null || request.Version != WorkerProtocol.Version || request.Operation != "extract" ||
                string.IsNullOrWhiteSpace(request.Path) || !Path.IsPathFullyQualified(request.Path))
                response = WorkerResponse.Failed("invalid-request");
            else if (!OperatingSystem.IsWindows()) response = WorkerResponse.Failed("unsupported-platform");
            else if (!File.Exists(request.Path)) response = WorkerResponse.Failed("file-not-found");
            else response = NativeFilterExtraction.Extract(request.Path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            response = WorkerResponse.Failed("invalid-request", ex.HResult);
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, WorkerProtocol.JsonOptions);
        if (payload.Length > WorkerProtocol.MaximumResponseBytes)
            payload = JsonSerializer.SerializeToUtf8Bytes(WorkerResponse.Failed("output-limit"));
        using var output = Console.OpenStandardOutput();
        output.Write(payload);
        output.Flush();
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}

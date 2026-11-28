using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FormatFactoryWinUI.Services;

internal sealed record ConversionWorkerLaunch(ExternalProcessLifetime Lifetime, Process? Child, int ChildId) : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    public static async Task<ConversionWorkerLaunch> StartAsync(string executable, IReadOnlyList<string> arguments,
        Action<ProcessLifetimeSignal>? observe, CancellationToken token, string? workingDirectory = null,
        bool redirectStandardInput = false)
    {
        token.ThrowIfCancellationRequested();
        var request = EncodeArguments(executable, arguments);
        var id = Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream($"FluentFormatConverter-{id}", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(ConversionWorkerProcess.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { ConversionWorkerProcess.ProtocolVersionArgument, "run-engine", id }
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        if (redirectStandardInput)
        {
            start.RedirectStandardInput = true;
            start.StandardInputEncoding = new UTF8Encoding(false);
            start.ArgumentList.Add("--stdin");
        }
        var lifetime = ExternalProcessLifetime.Start(start, token, observe);
        Process? child = null;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var connected = pipe.WaitForConnectionAsync(startup.Token);
                var exited = lifetime.Exited;
                if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited && !connected.IsCompletedSuccessfully)
                {
                    await exited.ConfigureAwait(false);
                    throw new IOException("Conversion worker exited before opening the control pipe.");
                }
                await connected.ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != lifetime.Process.Id)
                    throw new IOException("Unexpected conversion worker on the control pipe.");
                // Starting the encoder only after job assignment keeps its descendants under host ownership.
                await pipe.WriteAsync(request, startup.Token).ConfigureAwait(false);
                var reply = new byte[8];
                await pipe.ReadExactlyAsync(reply, startup.Token).ConfigureAwait(false);
                var pid = BinaryPrimitives.ReadInt32LittleEndian(reply);
                var nativeError = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4));
                if (pid == 0 && nativeError != 0) throw new Win32Exception(nativeError);
                if (pid <= 0 || nativeError != 0) throw new InvalidDataException("Invalid conversion worker handshake.");
                try
                {
                    child = Process.GetProcessById(pid);
                    _ = child.Handle; // Bind the process object before the worker can release its child handle.
                    if (lifetime.Job is not null) ProcessResourcePolicy.ApplyBackgroundPolicy(child);
                }
                catch (ArgumentException) { child?.Dispose(); child = null; } // Already exited.
                catch (InvalidOperationException) when (child?.HasExited == true) { child.Dispose(); child = null; }
                await pipe.WriteAsync(new byte[] { 1 }, startup.Token).ConfigureAwait(false);
                return new(lifetime, child, pid);
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException(LocalizationService.T("GeneralServiceProcessTimeout", 30,
                    ConversionWorkerProcess.FileName), error);
            }
            finally { await startup.CancelAsync().ConfigureAwait(false); }
        }
        catch
        {
            child?.Dispose();
            lifetime.Dispose();
            throw;
        }
    }

    // Length-prefixed UTF-16 argv preserves Windows paths, embedded quotes and empty arguments.
    // Only the pipe ID appears on the worker's own command line.
    private static byte[] EncodeArguments(string executable, IReadOnlyList<string> arguments)
    {
        var values = arguments.Prepend(executable).ToArray();
        if (values.Any(value => value.Contains('\0'))) throw new ArgumentException("A conversion tool argument contains NUL.");
        var length = values.Sum(value => (long)value.Length + 1) * sizeof(char);
        if (length > 65536) throw new Win32Exception(206); // ERROR_FILENAME_EXCED_RANGE
        var request = new byte[sizeof(int) + (int)length];
        BinaryPrimitives.WriteInt32LittleEndian(request, (int)length);
        var position = sizeof(int);
        foreach (var value in values)
        {
            var bytes = MemoryMarshal.AsBytes(value.AsSpan());
            bytes.CopyTo(request.AsSpan(position));
            position += bytes.Length + sizeof(char);
        }
        return request;
    }

    public void Dispose()
    {
        Lifetime.Dispose();
        Child?.Dispose();
    }
}

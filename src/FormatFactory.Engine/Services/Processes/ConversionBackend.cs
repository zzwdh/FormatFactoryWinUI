using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace FormatFactoryWinUI.Services;

internal static class ConversionBackend
{
    internal static ExternalProcessLifetime Start(string operation, CancellationToken token)
    {
        var start = new ProcessStartInfo(ConversionWorkerProcess.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { ConversionWorkerProcess.ProtocolVersionArgument, operation }
        };
        return ExternalProcessLifetime.Start(start, token);
    }

    // Each call owns its job and pipes. Sensitive paths and payloads never enter argv.
    internal static Task<T> RequestAsync<T>(string operation, object request, CancellationToken token = default,
        JsonSerializerOptions? options = null) =>
        ExchangeAsync<T>(operation, writer => JsonSerializer.SerializeAsync(writer.BaseStream, request,
            request.GetType(), options, cancellationToken: token), token, options);

    internal static Task<T> RequestRawJsonAsync<T>(string operation, string json, CancellationToken token) =>
        ExchangeAsync<T>(operation, writer => writer.WriteAsync(json.AsMemory(), token), token);

    internal static Task<T> RequestStreamAsync<T>(string operation, Func<Stream, Task> write, CancellationToken token) =>
        ExchangeAsync<T>(operation, writer => write(writer.BaseStream), token);

    private static async Task<T> ExchangeAsync<T>(string operation, Func<StreamWriter, Task> write, CancellationToken token,
        JsonSerializerOptions? options = null)
    {
        using var lifetime = Start(operation, token);
        var process = lifetime.Process;
        var errors = process.StandardError.ReadToEndAsync();
        var response = ReceiveAsync();
        async Task<(T? Value, JsonException? Error)> ReceiveAsync()
        {
            try
            {
                var value = await JsonSerializer.DeserializeAsync<T>(process.StandardOutput.BaseStream, options,
                    cancellationToken: token).ConfigureAwait(false);
                return (value, null);
            }
            catch (JsonException malformed) { return (default, malformed); }
        }
        Exception? sendError = null;
        async Task SendAsync()
        {
            try
            {
                await write(process.StandardInput).ConfigureAwait(false);
            }
            catch (Exception error) when (error is JsonException or NotSupportedException) { sendError = error; throw; }
            finally { process.StandardInput.Close(); }
        }
        // A native failure can close stdout without a JSON response. Preserve its error code
        // even if the response decoder observes EOF first.
        try
        {
            await lifetime.WaitAndDrainAsync(Timeout.InfiniteTimeSpan, token, SendAsync(), errors, response)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!token.IsCancellationRequested && sendError is not null)
        {
            if (sendError is NotSupportedException) throw new NotSupportedException(sendError.Message, sendError);
            throw new InvalidDataException(sendError.Message, sendError);
        }
        catch (Exception) when (!token.IsCancellationRequested && errors.IsCompletedSuccessfully && errors.Result.StartsWith("error ", StringComparison.Ordinal))
        {
            throw DecodeFailure(errors.Result);
        }
        if (process.ExitCode != 0) throw DecodeFailure(await errors.ConfigureAwait(false));
        var (value, malformed) = await response.ConfigureAwait(false);
        if (malformed is not null) throw new InvalidDataException("Invalid conversion worker response.", malformed);
        return value ?? throw new InvalidDataException("Missing conversion worker response.");
    }

    internal static Exception DecodeFailure(string message)
    {
        var fields = message.Trim().Split(' ');
        if (fields is ["error", "PdfDocxNoText"]) return new InvalidDataException(LocalizationService.T("PdfDocxNoText"));
        if (fields is ["error", "document-nesting-limit-512"])
            return new InvalidDataException("The document exceeds the supported nesting depth of 512 JSON levels.");
        if (fields is ["error", "io", var code] && int.TryParse(code, out var nativeCode) && nativeCode > 0)
        {
            var description = new Win32Exception(nativeCode).Message;
            return nativeCode switch
            {
                2 => new FileNotFoundException(description),
                3 => new DirectoryNotFoundException(description),
                5 => new UnauthorizedAccessException(description),
                206 => new PathTooLongException(description),
                _ => new IOException(description, unchecked((int)0x80070000) | (nativeCode & 0xffff))
            };
        }
        if (fields is ["error", "NativeImageAnimationValidationFailed" or "NativeImageValidationFailed" or "NativeImageAnimationUnsupported"
            or "ConversionUiDocxDamagedOrEncrypted" or "ConversionUiDocxDocumentXmlMissing" or "ConversionUiDocxBodyMissing"])
            return new InvalidDataException(LocalizationService.T(fields[1]));
        return new InvalidDataException($"Conversion worker failed ({message.Trim()}).");
    }
}

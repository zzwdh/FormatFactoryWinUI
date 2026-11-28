using System.Text.Json;

namespace FormatFactoryWinUI.Services;

/// <summary>A private, cancellable exchange with the Rust planner for a single conversion.</summary>
internal sealed class MediaWorkerSession : IDisposable
{
    private readonly ExternalProcessLifetime _lifetime;
    private readonly CancellationToken _token;
    private readonly CancellationTokenRegistration _cancel;
    private readonly Task<string> _errors;

    internal MediaWorkerSession(CancellationToken token)
    {
        _token = token;
        _lifetime = ConversionBackend.Start("media-encode", token);
        _cancel = token.Register(_lifetime.Terminate);
        _errors = _lifetime.Process.StandardError.ReadToEndAsync();
    }

    internal async Task SendAsync(object value)
    {
        var writer = _lifetime.Process.StandardInput;
        _token.ThrowIfCancellationRequested();
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), _token).ConfigureAwait(false);
            await writer.FlushAsync(_token).ConfigureAwait(false);
        }
        catch (IOException) when (_token.IsCancellationRequested) { throw new OperationCanceledException(_token); }
    }

    internal async Task<T> ReadAsync<T>()
    {
        var line = await _lifetime.Process.StandardOutput.ReadLineAsync(_token).ConfigureAwait(false);
        _token.ThrowIfCancellationRequested();
        if (line is null) throw ConversionBackend.DecodeFailure(await _errors.ConfigureAwait(false));
        return JsonSerializer.Deserialize<T>(line, JsonSerializerOptions.Web) ?? throw new InvalidDataException("Invalid encoding session response.");
    }

    internal async Task CompleteAsync()
    {
        _lifetime.Process.StandardInput.Close();
        await _lifetime.WaitAndDrainAsync(Timeout.InfiniteTimeSpan, _token, _errors).ConfigureAwait(false);
        if (_lifetime.Process.ExitCode != 0) throw ConversionBackend.DecodeFailure(await _errors.ConfigureAwait(false));
    }

    public void Dispose()
    {
        _cancel.Dispose();
        _lifetime.Dispose();
    }
}

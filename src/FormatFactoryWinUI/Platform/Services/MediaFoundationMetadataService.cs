using Windows.Storage;

namespace FormatFactoryWinUI.Services;

internal static class MediaFoundationMetadataService
{
    public static async Task<MediaFoundationMetadata?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Media Foundation can be absent on Windows N. Keep the FFprobe metadata available.
        if (MediaFoundationNativeService.TryStartMediaFoundation() < 0)
        {
            return null;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            cancellationToken.ThrowIfCancellationRequested();
            var title = string.Empty;
            var artist = string.Empty;
            var album = string.Empty;
            var width = 0;
            var height = 0;
            var bitrate = 0u;
            var duration = TimeSpan.Zero;

            try
            {
                var video = await file.Properties.GetVideoPropertiesAsync();
                cancellationToken.ThrowIfCancellationRequested();
                duration = video.Duration;
                width = (int)video.Width;
                height = (int)video.Height;
                bitrate = video.Bitrate;
                title = video.Title ?? string.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.WriteApp(LocalizationService.T("OperationalLogMediaFoundationVideoPropertiesReadFailed", ex.Message));
            }

            try
            {
                var music = await file.Properties.GetMusicPropertiesAsync();
                cancellationToken.ThrowIfCancellationRequested();
                duration = duration > TimeSpan.Zero ? duration : music.Duration;
                bitrate = bitrate > 0 ? bitrate : music.Bitrate;
                title = string.IsNullOrWhiteSpace(title) ? music.Title ?? string.Empty : title;
                artist = music.Artist ?? string.Empty;
                album = music.Album ?? string.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.WriteApp(LocalizationService.T("OperationalLogMediaFoundationAudioPropertiesReadFailed", ex.Message));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new MediaFoundationMetadata(duration, bitrate, width, height, title, artist, album);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.WriteApp(LocalizationService.T("OperationalLogMediaFoundationMetadataReadFailed", ex.Message));
            return null;
        }
        finally
        {
            MediaFoundationNativeService.ShutdownMediaFoundation();
        }
    }
}

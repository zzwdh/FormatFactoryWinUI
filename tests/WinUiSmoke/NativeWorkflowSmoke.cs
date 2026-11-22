using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.ConversionEngines;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckNativeWorkflowAsync()
    {
        var fixtureRoot = Path.Combine(_runRoot, "native-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var png = Path.Combine(fixtureRoot, "transparent.png");
        await using (var file = new FileStream(png, FileMode.Create, FileAccess.ReadWrite))
        {
            using var stream = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 2, 2, 96, 96,
                new byte[] { 0,0,255,0, 0,255,0,255, 255,0,0,255, 255,255,255,255 });
            await encoder.FlushAsync();
        }
        await CheckAutomationQueueWorkflowAsync(png);
        await CheckAutomationUpgradeAsync(png);
        var opaquePng = Path.Combine(fixtureRoot, "opaque.png");
        var opaqueBmp = Path.Combine(fixtureRoot, "opaque.bmp");
        foreach (var opaque in new[] { (Path: opaquePng, Encoder: BitmapEncoder.PngEncoderId),
            (Path: opaqueBmp, Encoder: BitmapEncoder.BmpEncoderId) })
        {
            await using var file = new FileStream(opaque.Path, FileMode.CreateNew, FileAccess.ReadWrite);
            using var stream = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(opaque.Encoder, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 2, 2, 96, 96,
                new byte[] { 31,67,113,255, 211,149,79,255, 41,59,73,255, 83,97,101,255 });
            await encoder.FlushAsync();
        }
        await CheckLongImagePathsAsync(fixtureRoot, opaquePng);
        var pdf = Path.Combine(fixtureRoot, "two-pages.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            document.AddPage();
            document.AddPage();
            document.Save(pdf);
        }
        await CheckNativeAsync("Window placement restores through AppWindow and saves subsequent resize", async () =>
        {
            var appWindow = _window!.AppWindow;
            var originalPosition = appWindow.Position;
            var originalSize = appWindow.Size;
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(appWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            var expected = WindowPlacementStore.FitToWorkArea(new(area.X + 40, area.Y + 40, 860, 620),
                new(area.X, area.Y, area.Width, area.Height));
            var store = (WindowPlacementStore)Field("_windowPlacementStore")!;
            async Task CallAsync(string name) => await (Task)typeof(MainWindow)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, null)!;
            try
            {
                await store.SaveAsync(new(expected, false));
                SetField("_appWindow", appWindow);
                await CallAsync("RestoreWindowPlacementAsync");
                Require(appWindow.Position.X == expected.X && appWindow.Position.Y == expected.Y &&
                    appWindow.Size.Width == expected.Width && appWindow.Size.Height == expected.Height,
                    "Saved bounds were not applied to the real window.");
                appWindow.Resize(new(expected.Width - 20, expected.Height - 20));
                await CallAsync("PersistWindowPlacementAsync");
                var saved = await store.LoadAsync();
                Require(saved?.Bounds.Width == expected.Width - 20 && saved.Bounds.Height == expected.Height - 20,
                    "Window resize was not persisted.");
            }
            finally
            {
                Invoke("DisposeWindowPlacement");
                appWindow.MoveAndResize(new(originalPosition.X, originalPosition.Y, originalSize.Width, originalSize.Height));
            }
        });
        await CheckNativeAsync("Drag payload resolves real files and directories without touching the clipboard", async () =>
        {
            var package = new DataPackage();
            OutputTransferService.PrepareDrag(package, new[] { png, fixtureRoot });
            var view = package.GetView();
            var items = await view.GetStorageItemsAsync();
            Require(items.Count == 2 && items[0].Path == png && items[1].Path == fixtureRoot, "File/folder payload was lost.");
            Require(view.RequestedOperation == DataPackageOperation.Copy, "Output drag must copy, never move the conversion result.");
            Require(view.Properties.ContainsKey(OutputTransferService.OutputDragMarker), "Self-import prevention marker missing.");
        });
        await CheckNativeAsync("Native image conversion works without FFmpeg and preserves white alpha flattening", async () =>
        {
            var codecs = new WindowsImageCodecService();
            var probe = await codecs.ProbeAsync(png);
            Require(probe is { Width: 2, Height: 2 }, "Windows could not decode the fixture.");
            var task = new MediaTask { InputPath = png, SourceFormatId = "png", TargetFormat = "BMP", Probe = probe! };
            var settings = new AppSettings { OutputMode = "custom", CustomOutputDirectory = Path.Combine(fixtureRoot, "converted") };
            var result = await new NativeImageConversionService(codecs, new ConversionService()).ConvertAsync(task, "", settings, _ => { }, default);
            Require(result.Success && File.Exists(result.OutputPath), "Native image conversion failed: " + result.Message);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(result.OutputPath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = (await decoder.GetPixelDataAsync()).DetachPixelData();
            Require(pixels[0] >= 250 && pixels[1] >= 250 && pixels[2] >= 250, "Transparent pixel did not become white.");
        });
        await CheckNativeAsync("Native lossless conversion proves pixels and refuses transparent BMP flattening", async () =>
        {
            var codecs = new WindowsImageCodecService();
            foreach (var format in new[] { "png", "tiff" })
            {
                var directory = Path.Combine(fixtureRoot, "lossless-" + format);
                var task = new MediaTask
                {
                    InputPath = opaqueBmp, SourceFormatId = "bmp", TargetFormatId = format, ConversionMode = "lossless",
                    Probe = (await codecs.ProbeAsync(opaqueBmp))!
                };
                Require(!string.IsNullOrEmpty(task.Probe.PixelFormat), "Native probe omitted its decoded pixel format.");
                var result = await new NativeImageConversionService(codecs, new ConversionService()).ConvertAsync(task, "",
                    new AppSettings { OutputMode = "custom", CustomOutputDirectory = directory }, _ => { }, default);
                Require(result.Success, "Exact 8-bit native conversion failed: " + result.Message);
                Require(await codecs.ValidateLosslessAsync(opaqueBmp, result.OutputPath), "Committed output changed pixels.");
            }
            var rejectedDirectory = Path.Combine(fixtureRoot, "lossless-transparent-bmp");
            var transparent = new MediaTask
            {
                InputPath = png, SourceFormatId = "png", TargetFormatId = "bmp", ConversionMode = "lossless",
                Probe = (await codecs.ProbeAsync(png))!
            };
            var rejected = await new NativeImageConversionService(codecs, new ConversionService()).ConvertAsync(transparent, "",
                new AppSettings { OutputMode = "custom", CustomOutputDirectory = rejectedDirectory }, _ => { }, default);
            Require(!rejected.Success && Directory.GetFiles(rejectedDirectory).Length == 0,
                "Lossless transparent BMP silently flattened alpha or left an output.");
        });
        await CheckNativeAsync("Native TIFF conversion preserves both pages and their decoded pixels", async () =>
        {
            var source = Path.Combine(fixtureRoot, "two-pages.tiff");
            await using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                using var stream = file.AsRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, stream);
                for (var page = 0; page < 2; page++)
                {
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, 2, 1, 96, 96,
                        new byte[] { (byte)(31 + page), 67, 113, 255, 211, (byte)(149 + page), 79, 255 });
                    if (page == 0) await encoder.GoToNextFrameAsync();
                    else await encoder.FlushAsync();
                }
            }
            var codecs = new WindowsImageCodecService();
            var output = Path.Combine(fixtureRoot, "converted-two-pages.tiff");
            await codecs.TranscodeAsync(source, output, "tiff", "quality");
            Require(await codecs.ProbeAsync(output) is { PageCount: 2 }, "Native TIFF conversion discarded a page.");
            Require(await codecs.ValidateLosslessAsync(source, output), "Native TIFF conversion changed a page's pixels.");
        });
        await CheckNativeAsync("Native lossless conversion rejects real 16-bit PNG before 8-bit encoding", async () =>
        {
            var source = Path.Combine(fixtureRoot, "sixteen-bit.png");
            await using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                using var stream = file.AsRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                var samples = new ushort[] { 1001, 2003, 3007, 65535, 4009, 5011, 6013, 32769 }
                    .SelectMany(BitConverter.GetBytes).ToArray();
                encoder.SetPixelData(BitmapPixelFormat.Rgba16, BitmapAlphaMode.Straight, 2, 1, 96, 96, samples);
                await encoder.FlushAsync();
            }
            Require(WicProfilePreservingCodec.HasHighBitDepth(source), "Fixture did not retain its 16-bit channels.");
            var codecs = new WindowsImageCodecService();
            var directory = Path.Combine(fixtureRoot, "lossless-sixteen-bit");
            var task = new MediaTask
            {
                InputPath = source, SourceFormatId = "png", TargetFormatId = "tiff", ConversionMode = "lossless",
                Probe = (await codecs.ProbeAsync(source))!
            };
            var result = await new NativeImageConversionService(codecs, new ConversionService()).ConvertAsync(task, "",
                new AppSettings { OutputMode = "custom", CustomOutputDirectory = directory }, _ => { }, default);
            Require(!result.Success && Directory.GetFiles(directory).Length == 0,
                "Lossless native pipeline quantized the 16-bit source or left an output.");
        });
        await CheckNativeAsync("ICC preservation writes the original profile into PNG, JPEG and TIFF", async () =>
        {
            var profile = await File.ReadAllBytesAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "spool", "drivers", "color", "sRGB Color Space Profile.icm"));
            var tagged = Path.Combine(fixtureRoot, "profiled.png");
            await File.WriteAllBytesAsync(tagged, AddPngIccProfile(await File.ReadAllBytesAsync(png), profile));
            Require(profile.SequenceEqual(WicProfilePreservingCodec.ReadProfile(tagged) ?? []), "ICC fixture was not decoded correctly.");
            var codecs = new WindowsImageCodecService();
            foreach (var format in new[] { "png", "jpg", "tiff" })
            {
                var output = Path.Combine(fixtureRoot, "preserved." + format);
                await codecs.TranscodeWithColorPolicyAsync(tagged, output, format, "quality", "preserve");
                Require(profile.SequenceEqual(WicProfilePreservingCodec.ReadProfile(output) ?? []), format + " lost or replaced the ICC profile.");
                Require(await codecs.ProbeAsync(output) is { Width: 2, Height: 2 }, format + " could not be decoded after ICC preservation.");
            }
            var unsupported = Path.Combine(fixtureRoot, "must-not-exist.bmp");
            try
            {
                await codecs.TranscodeWithColorPolicyAsync(tagged, unsupported, "bmp", "quality", "preserve");
                throw new InvalidOperationException("Unsupported output silently accepted ICC preservation.");
            }
            catch (NotSupportedException) { Require(!File.Exists(unsupported), "Rejected output left a partial file."); }
        });
        await CheckNativeAsync("Native lossless conversion refuses ICC removal and preserves the original profile when requested", async () =>
        {
            var profile = await File.ReadAllBytesAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "spool", "drivers", "color", "sRGB Color Space Profile.icm"));
            var source = Path.Combine(fixtureRoot, "profiled-opaque.png");
            await File.WriteAllBytesAsync(source, AddPngIccProfile(await File.ReadAllBytesAsync(opaquePng), profile));
            var codecs = new WindowsImageCodecService();
            var task = new MediaTask
            {
                InputPath = source, SourceFormatId = "png", TargetFormatId = "tiff", ConversionMode = "lossless",
                Probe = (await codecs.ProbeAsync(source))!
            };
            var rejectedDirectory = Path.Combine(fixtureRoot, "lossless-icc-srgb");
            var rejected = await new NativeImageConversionService(codecs, new ConversionService()).ConvertAsync(task, "",
                new AppSettings { OutputMode = "custom", CustomOutputDirectory = rejectedDirectory }, _ => { }, default);
            Require(!rejected.Success && Directory.GetFiles(rejectedDirectory).Length == 0,
                "Lossless sRGB conversion changed or removed the source profile.");
            task.Options["imageColorPolicy"] = "preserve";
            var preserved = await new NativeImageConversionService(codecs, new ConversionService()).ConvertAsync(task, "",
                new AppSettings { OutputMode = "custom", CustomOutputDirectory = Path.Combine(fixtureRoot, "lossless-icc-preserve") },
                _ => { }, default);
            Require(preserved.Success, "Lossless ICC-preserving conversion failed: " + preserved.Message);
            Require(profile.SequenceEqual(WicProfilePreservingCodec.ReadProfile(preserved.OutputPath) ?? []) &&
                await codecs.ValidateLosslessAsync(source, preserved.OutputPath), "Lossless ICC result changed profile or pixels.");
        });
        await CheckNativeAsync("Document host rejects unverifiable lossless conversions before producing output", async () =>
        {
            var requests = new[]
            {
                new ConversionRequest { Inputs = new[] { new ConversionInput(Path.Combine(fixtureRoot, "layout.docx"), "docx") }, TargetFormatId = "txt" },
                new ConversionRequest { Inputs = new[] { new ConversionInput(opaqueBmp, "bmp"), new ConversionInput(png, "png") }, TargetFormatId = "pdf" },
                new ConversionRequest { Inputs = new[] { new ConversionInput(pdf, "pdf") }, TargetFormatId = "pdf" }
            };
            foreach (var initial in requests)
            {
                var directory = Path.Combine(fixtureRoot, "lossless-document-" + Guid.NewGuid().ToString("N"));
                var request = initial with { Mode = "lossless", OutputDirectory = directory };
                var result = await new ConversionEngines.DocumentConversionService().ConvertAsync(request, null, default);
                Require(!result.Success && !result.Cancelled && result.Artifacts.Count == 0 && !Directory.Exists(directory),
                    "Document lossless request reached normalization or left an output.");
                Require(result.Message == LocalizationService.T("ConversionUiDocumentLosslessUnsupported"),
                    "Document lossless refusal did not explain the preservation limitation.");
            }
        });
        await CheckNativeAsync("System preview loads PDF, changes pages, switches files and releases handles", async () =>
        {
            var preview = new NativeMediaPreviewWindow(new[] { new NativePreviewSource("PDF", pdf, null), new NativePreviewSource("Image", png, null) }, ElementTheme.Light);
            object Read(string name) => typeof(NativeMediaPreviewWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
            async Task WaitForImage(uint pageCount)
            {
                for (var i = 0; i < 150; i++)
                {
                    if (((Image)Read("_image")).Source is not null && (uint)Read("_pageCount") == pageCount) return;
                    await Task.Delay(100);
                }
                throw new InvalidOperationException("Preview did not finish: " + ((TextBlock)Read("_status")).Text);
            }
            try
            {
                var choice = (ComboBox)Read("_sourcePicker");
                choice.SelectedIndex = 0;
                await WaitForImage(2);
                var next = (Button)Read("_next");
                Require(next.IsEnabled, "Second PDF page is inaccessible.");
                var peer = new ButtonAutomationPeer(next);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                for (var i = 0; i < 100 && !((Button)Read("_previous")).IsEnabled; i++) await Task.Delay(50);
                Require((uint)Read("_page") == 1 && ((Button)Read("_previous")).IsEnabled, "PDF page navigation did not complete.");
                choice.SelectedIndex = 1;
                await WaitForImage(1);
                // Rapid source changes cancel the preceding PDF render.
                choice.SelectedIndex = 0;
                choice.SelectedIndex = 1;
                await WaitForImage(1);
            }
            finally { preview.Close(); }
            using var exclusive = new FileStream(pdf, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });
        await CheckNativeAsync("Native media preview opens audio without autoplay and releases the source", async () =>
        {
            var wav = Path.Combine(fixtureRoot, "silence.wav");
            using (var writer = new BinaryWriter(File.Create(wav)))
            {
                const int bytes = 48000 * 2;
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000);
                writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(bytes); writer.Write(new byte[bytes]);
            }
            var preview = new NativeMediaPreviewWindow(new[] { new NativePreviewSource("Audio", wav, null) }, ElementTheme.Light);
            object? Read(string name) => typeof(NativeMediaPreviewWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview);
            try
            {
                ((ComboBox)Read("_sourcePicker")!).SelectedIndex = 0;
                Windows.Media.Playback.MediaPlayer? player = null;
                for (var attempt = 0; attempt < 150; attempt++)
                {
                    player = Read("_player") as Windows.Media.Playback.MediaPlayer;
                    if (player?.PlaybackSession.NaturalDuration > TimeSpan.Zero) break;
                    await Task.Delay(100);
                }
                Require(player?.PlaybackSession.NaturalDuration >= TimeSpan.FromSeconds(.9),
                    "Windows did not open the audio fixture: " + ((TextBlock)Read("_status")!).Text);
                Require(!player!.AutoPlay && player.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Playing,
                    "Preview started playback without a user action.");
                Require(((MediaPlayerElement)Read("_media")!).AreTransportControlsEnabled,
                    "Native playback controls are unavailable.");
            }
            finally { preview.Close(); }
            for (var attempt = 0; ; attempt++)
            {
                try { using var exclusive = new FileStream(wav, FileMode.Open, FileAccess.ReadWrite, FileShare.None); break; }
                catch (IOException) when (attempt < 40) { await Task.Delay(100); }
            }
        });
    }

    private async Task CheckLongImagePathsAsync(string fixtureRoot, string shortSource)
    {
        // Exercise the full-path boundary while keeping every component below NTFS's limit.
        var longRoot = Enumerable.Range(0, 20).Aggregate(fixtureRoot,
            (directory, index) => Path.Combine(directory, new string((char)('a' + index), 80)));
        Directory.CreateDirectory(longRoot);
        var source = Path.Combine(longRoot, "source.png");
        File.Copy(shortSource, source);
        var original = await File.ReadAllBytesAsync(source);
        Require(source.Length > 260 && source.Split(Path.DirectorySeparatorChar).All(part => part.Length < 255),
            "Long-path fixture did not exercise a valid Windows full path above MAX_PATH.");
        var expectedPixels = new byte[] { 31,67,113,255, 211,149,79,255, 41,59,73,255, 83,97,101,255 };
        var codecs = new WindowsImageCodecService();

        async Task AssertPixelsAndReleasedAsync(string path)
        {
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using var stream = file.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                Require(decoder.PixelWidth == 2 && decoder.PixelHeight == 2 && decoder.FrameCount == 1,
                    "Long-path output dimensions or frame count changed.");
                var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb)).DetachPixelData();
                Require(pixels.SequenceEqual(expectedPixels), "Long-path conversion changed the opaque fixture pixels.");
            }
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        async Task AssertSourceUnchangedAndReleasedAsync()
        {
            var current = await File.ReadAllBytesAsync(source);
            Require(original.SequenceEqual(current), "Long-path conversion changed the source bytes.");
            using var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        await CheckNativeAsync("Native image probe accepts a real source path longer than MAX_PATH and releases it", async () =>
        {
            var probe = await codecs.ProbeAsync(source);
            Require(probe is { ContentKind: ContentKind.Image, Width: 2, Height: 2, PageCount: 1 },
                "Windows image probe did not decode the long-path source.");
            Require(probe!.FileSize == original.Length && !string.IsNullOrWhiteSpace(probe.PixelFormat),
                "Long-path probe lost the source size or decoded pixel format.");
            await AssertSourceUnchangedAndReleasedAsync();
        });
        await CheckNativeAsync("Native sRGB image transcode accepts long input and output paths with exact pixels", async () =>
        {
            var output = Path.Combine(longRoot, "direct.bmp");
            await codecs.TranscodeAsync(source, output, "bmp", "balanced");
            await AssertPixelsAndReleasedAsync(output);
            await AssertSourceUnchangedAndReleasedAsync();
        });
        await CheckNativeAsync("Native image workflow commits a validated long-path result without FFmpeg or source changes", async () =>
        {
            var outputDirectory = Path.Combine(longRoot, "workflow-output");
            var settings = new AppSettings { OutputMode = "custom", CustomOutputDirectory = outputDirectory };
            var task = new MediaTask
            {
                InputPath = source, SourceFormatId = "png", TargetFormatId = "bmp", ConversionMode = "balanced",
                Probe = (await codecs.ProbeAsync(shortSource))!
            };
            var workflow = new ConversionWorkflow(new IConversionEngine[] {
                new NativeImageConversionEngine(new NativeImageConversionService(codecs, new ConversionService()), "", settings)
            });
            var result = await workflow.ExecuteAsync(ConversionRequestFactory.Capture(task, settings), null, default);
            Require(result.Outcome == ConversionOutcome.Completed && result.Execution.Success && result.EngineId == "windows-image",
                "Long-path native conversion failed: " + result.Execution.Message);
            Require(result.OutputPath.Length > 260 && File.Exists(result.OutputPath) &&
                Directory.GetFiles(outputDirectory).Length == 1 && !Directory.EnumerateFiles(outputDirectory, "*.partial*").Any(),
                "Long-path workflow did not commit exactly one complete output.");
            await AssertPixelsAndReleasedAsync(result.OutputPath);
            await AssertSourceUnchangedAndReleasedAsync();
        });
        await CheckNativeAsync("Native image long-path reads respect cancellation and existing file-sharing restrictions", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelledOutput = Path.Combine(longRoot, "cancelled.bmp");
            async Task ExpectCancellationAsync(Func<Task> operation)
            {
                try { await operation(); }
                catch (OperationCanceledException) { return; }
                throw new InvalidOperationException("A cancelled native image read continued.");
            }
            await ExpectCancellationAsync(async () => { await codecs.ProbeAsync(source, cancellation.Token); });
            await ExpectCancellationAsync(() => codecs.TranscodeAsync(source, cancelledOutput, "bmp", "balanced", cancellation.Token));
            Require(!File.Exists(cancelledOutput), "Cancelled image conversion created an output.");
            using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try
                {
                    Require(await codecs.ProbeAsync(source) is null, "Native image probe bypassed a source read lock.");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.Runtime.InteropServices.COMException) { }
            }
            await AssertSourceUnchangedAndReleasedAsync();
        });
        var extendedSource = @"\\?\" + source;
        await CheckNativeAsync("Native image probe accepts the extended-prefix form of a real long source path", async () =>
        {
            Require(File.Exists(extendedSource), "Extended-prefix fixture is not accessible through managed file IO.");
            Require(await codecs.ProbeAsync(extendedSource) is { Width: 2, Height: 2, PageCount: 1 },
                "Native image probe did not decode the extended-prefix source.");
            await AssertSourceUnchangedAndReleasedAsync();
        });
        await CheckNativeAsync("Native image transcode accepts an extended-prefix long source without changing pixels", async () =>
        {
            var output = Path.Combine(longRoot, "extended.bmp");
            await codecs.TranscodeAsync(extendedSource, output, "bmp", "balanced");
            await AssertPixelsAndReleasedAsync(output);
            await AssertSourceUnchangedAndReleasedAsync();
        });
        await CheckNativeAsync("Native long-path image conversion retains the existing lossless and ICC preservation checks", async () =>
        {
            var lossless = Path.Combine(longRoot, "lossless.tiff");
            await codecs.TranscodeAsync(source, lossless, "tiff", "lossless");
            Require(await codecs.ValidateLosslessAsync(source, lossless), "Long-path lossless conversion failed its exact sample check.");
            await AssertPixelsAndReleasedAsync(lossless);
            var profile = await File.ReadAllBytesAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "spool", "drivers", "color", "sRGB Color Space Profile.icm"));
            var tagged = Path.Combine(longRoot, "profiled.png");
            await File.WriteAllBytesAsync(tagged, AddPngIccProfile(original, profile));
            var taggedOriginal = await File.ReadAllBytesAsync(tagged);
            var preserved = Path.Combine(longRoot, "profiled.tiff");
            await codecs.TranscodeWithColorPolicyAsync(tagged, preserved, "tiff", "lossless", "preserve");
            Require(profile.SequenceEqual(WicProfilePreservingCodec.ReadProfile(preserved) ?? []) &&
                await codecs.ValidateLosslessAsync(tagged, preserved), "Long-path preservation lost its ICC profile or changed pixels.");
            var taggedCurrent = await File.ReadAllBytesAsync(tagged);
            Require(taggedOriginal.SequenceEqual(taggedCurrent), "Long-path ICC preservation changed the source bytes.");
            await AssertPixelsAndReleasedAsync(preserved);
            using var exclusive = new FileStream(tagged, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await AssertSourceUnchangedAndReleasedAsync();
        });
    }

    private async Task CheckNativeAsync(string name, Func<Task> action)
    {
        try { await action(); _results.Add(new { name, passed = true }); }
        catch (Exception exception) { RecordFailure(name, exception); }
    }

    private static byte[] AddPngIccProfile(byte[] png, byte[] profile)
    {
        using var payload = new MemoryStream();
        payload.Write(System.Text.Encoding.ASCII.GetBytes("Test profile\0\0"));
        using (var compressed = new System.IO.Compression.ZLibStream(payload,
            System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true)) compressed.Write(profile);
        var data = payload.ToArray();
        var chunk = new byte[data.Length + 12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        System.Text.Encoding.ASCII.GetBytes("iCCP").CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        var crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(data.Length + 8), ~crc);
        using var output = new MemoryStream();
        output.Write(png.AsSpan(0, 8));
        for (var offset = 8; offset < png.Length;)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type is not ("sRGB" or "iCCP")) output.Write(png.AsSpan(offset, length + 12));
            if (type == "IHDR") output.Write(chunk);
            offset += length + 12;
        }
        return output.ToArray();
    }
}

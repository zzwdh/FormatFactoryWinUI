using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Xunit.Sdk;

namespace FormatFactoryWinUI.Tests;

public sealed class ArchiveConversionTests
{
    private static string Engine
    {
        get
        {
            var path = Path.Combine(RepositoryTestContext.Root, "work", "prepared-archive", "x64", "Engines", "7zip", "7z.exe");
            if (!File.Exists(path)) throw SkipException.ForSkip("The prepared official 7-Zip runtime is unavailable.");
            return path;
        }
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    [InlineData(ArchiveFormat.Tar)]
    [InlineData(ArchiveFormat.TarGZip)]
    [InlineData(ArchiveFormat.TarXz)]
    public async Task AllFormats_StrictRoundTripPreservesUnicodeEmptyDirectoriesAndOriginal(ArchiveFormat format)
    {
        using var directory = new TemporaryDirectory();
        var source = CreateZip(directory.Path);
        var original = await File.ReadAllBytesAsync(source);
        var service = new ArchiveConversionService(Engine);
        var phases = new List<string>();
        var converted = await service.ConvertAsync(source, directory.Path,
            new(format, StrictVerification: true), NoPassword,
            new InlineProgress(sample => phases.Add(sample.Phase)));
        Assert.EndsWith(ArchiveFormats.Extension(format), converted);
        Assert.NotEqual(source, converted);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        var roundTrip = await service.ConvertAsync(converted, directory.Path,
            new(ArchiveFormat.Zip, StrictVerification: true), NoPassword);
        using var archive = ZipFile.OpenRead(roundTrip);
        Assert.Equal(new[] { "empty/", "目录/", "目录/文件.txt", "zero.dat" }.Order(StringComparer.Ordinal),
            archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        using var reader = new StreamReader(archive.GetEntry("目录/文件.txt")!.Open(), Encoding.UTF8);
        Assert.Equal("原始内容\nhello🙂", await reader.ReadToEndAsync());
        Assert.Contains("comparing", phases);
        Assert.Equal("completed", phases[^1]);
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, ".ffarchive-*"));
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    public async Task EncryptedArchive_PromptsOnDemandAndRetriesWrongUnicodePassword(ArchiveFormat format)
    {
        using var directory = new TemporaryDirectory();
        var source = CreateZip(directory.Path);
        var service = new ArchiveConversionService(Engine);
        var secret = format == ArchiveFormat.Zip ? "fixture ASCII ! 123" : "测试密码 Ω 123";
        var encrypted = await service.ConvertAsync(source, directory.Path, new(format, true, secret), NoPassword);
        var requests = new List<ArchivePasswordRequest>();
        var converted = await service.ConvertAsync(encrypted, directory.Path, new(ArchiveFormat.Zip, true),
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult<string?>(requests.Count == 1 ? "wrong-password" : secret);
            });
        Assert.Equal(2, requests.Count);
        Assert.False(requests[0].IncorrectPassword);
        Assert.True(requests[1].IncorrectPassword);
        Assert.Equal(new[] { 1, 2 }, requests.Select(request => request.Attempt));
        using var archive = ZipFile.OpenRead(converted);
        Assert.Contains(archive.Entries, entry => entry.FullName == "目录/文件.txt");
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, ".ffarchive-*"));
    }

    [Fact]
    public async Task SkippedPassword_LeavesOriginalAndNoOutputOrScratch()
    {
        using var directory = new TemporaryDirectory();
        var source = CreateZip(directory.Path);
        var service = new ArchiveConversionService(Engine);
        var encrypted = await service.ConvertAsync(source, directory.Path, new(ArchiveFormat.SevenZip, false, "fixture-secret"), NoPassword);
        var output = Path.Combine(directory.Path, "output");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => service.ConvertAsync(encrypted, output,
            new(ArchiveFormat.Zip), (_, _) => Task.FromResult<string?>(null)));
        Assert.Equal(ArchiveFailureCode.PasswordSkipped, error.Code);
        Assert.True(File.Exists(encrypted));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Theory]
    [InlineData(ArchiveFormat.Tar)]
    [InlineData(ArchiveFormat.TarGZip)]
    [InlineData(ArchiveFormat.TarXz)]
    public async Task UnsupportedTargetEncryption_IsRejectedBeforeCreatingOutput(ArchiveFormat format)
    {
        using var directory = new TemporaryDirectory();
        var source = CreateZip(directory.Path);
        var output = Path.Combine(directory.Path, "not-created");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(source, output, new(format, false, "secret"), NoPassword));
        Assert.Equal(ArchiveFailureCode.InvalidOptions, error.Code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task NumberedVolumes_AreGroupedAndConvertedFromNonFirstSelection()
    {
        using var directory = new TemporaryDirectory();
        var data = Path.Combine(directory.Path, "random.bin");
        await File.WriteAllBytesAsync(data, System.Security.Cryptography.RandomNumberGenerator.GetBytes(64 * 1024));
        var archive = Path.Combine(directory.Path, "split.7z");
        await SevenZipAsync(["a", "-t7z", "-v8k", "-mx=0", archive, data]);
        var volumes = Directory.GetFiles(directory.Path, "split.7z.*").Order().ToArray();
        Assert.True(volumes.Length > 2);
        Assert.Single(ArchiveInput.NormalizeInputs(volumes));
        Assert.Equal(volumes[0], ArchiveInput.GetPrimaryPath(volumes[2]));
        var output = await new ArchiveConversionService(Engine).ConvertAsync(volumes[2], directory.Path,
            new(ArchiveFormat.Zip, true), NoPassword);
        Assert.EndsWith("split.zip", output);
        using var zip = ZipFile.OpenRead(output);
        using var stream = zip.GetEntry("random.bin")!.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        Assert.Equal(await File.ReadAllBytesAsync(data), memory.ToArray());
        Assert.All(volumes, volume => Assert.True(File.Exists(volume)));
    }

    [Fact]
    public async Task MissingMiddleVolume_FailsBeforeExtraction()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "split.7z.001"), [1]);
        var selected = Path.Combine(directory.Path, "split.7z.003");
        await File.WriteAllBytesAsync(selected, [3]);
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(selected, Path.Combine(directory.Path, "output"), new(ArchiveFormat.Zip), NoPassword));
        Assert.Equal(ArchiveFailureCode.MissingVolume, error.Code);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "output")));
    }

    [Fact]
    public async Task CorruptArchive_FailsWithoutPublishingPartialOutput()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "broken.zip");
        await File.WriteAllTextAsync(source, "not an archive");
        var output = Path.Combine(directory.Path, "output");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(source, output, new(ArchiveFormat.SevenZip), NoPassword));
        Assert.Equal(ArchiveFailureCode.CorruptArchive, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.Equal("not an archive", await File.ReadAllTextAsync(source));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("folder/file.txt:stream")]
    [InlineData("CON.txt")]
    public async Task UnsafeArchivePaths_AreRejectedBeforeExtraction(string name)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "unsafe.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("unsafe fixture");
        }
        var output = Path.Combine(directory.Path, "output");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(source, output, new(ArchiveFormat.SevenZip), NoPassword));
        Assert.Equal(ArchiveFailureCode.UnsafeEntry, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        Assert.False(File.Exists(Path.Combine(directory.Path, "outside.txt")));
    }

    [Fact]
    public async Task SymbolicLinkInTar_IsRejectedBeforeExtraction()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "link.tar");
        await using (var stream = File.Create(source))
        await using (var writer = new TarWriter(stream))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "unsafe-link") { LinkName = "../outside" });
        }
        var output = Path.Combine(directory.Path, "output");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(source, output, new(ArchiveFormat.Zip), NoPassword));
        Assert.Equal(ArchiveFailureCode.UnsafeEntry, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(320)]
    [InlineData(511)]
    public async Task UnixZipSymbolicLink_IsRejectedRegardlessOfItsPermissions(int permissions)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "unix-link.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = (0xA000 | permissions) << 16;
            using var writer = new StreamWriter(entry.Open());
            writer.Write("../outside");
        }
        var bytes = await File.ReadAllBytesAsync(source);
        for (var index = 0; index < bytes.Length - 5; index++)
        {
            if (bytes[index] == 0x50 && bytes[index + 1] == 0x4b && bytes[index + 2] == 1 && bytes[index + 3] == 2)
                bytes[index + 5] = 3; // ZIP creator platform: Unix, so the POSIX type bits are meaningful.
        }
        await File.WriteAllBytesAsync(source, bytes);
        var output = Path.Combine(directory.Path, "output");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(source, output, new(ArchiveFormat.SevenZip), NoPassword));
        Assert.Equal(ArchiveFailureCode.UnsafeEntry, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public async Task CaseConflictingEntries_AreRejectedWithoutLosingEitherFile()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "duplicates.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("file.txt").Open())) writer.Write("first");
            using (var writer = new StreamWriter(zip.CreateEntry("FILE.txt").Open())) writer.Write("second");
        }
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(source, directory.Path, new(ArchiveFormat.SevenZip), NoPassword));
        Assert.Equal(ArchiveFailureCode.UnsafeEntry, error.Code);
        Assert.False(File.Exists(Path.Combine(directory.Path, "duplicates.7z")));
    }

    [Fact]
    public async Task CancellationDuringCompression_RemovesScratchAndPreservesSource()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "large.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("random.bin", CompressionLevel.NoCompression).Open();
            stream.Write(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24 * 1024 * 1024));
        }
        using var cancellation = new CancellationTokenSource();
        var output = Path.Combine(directory.Path, "output");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(source, output, new(ArchiveFormat.SevenZip), NoPassword,
                new InlineProgress(sample => { if (sample.Phase == "compressing") cancellation.CancelAfter(50); }), cancellation.Token));
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public async Task StandardVerification_DoesNotRunStrictComparison()
    {
        using var directory = new TemporaryDirectory();
        var source = CreateZip(directory.Path);
        var phases = new List<string>();
        var output = await new ArchiveConversionService(Engine).ConvertAsync(source, directory.Path,
            new(ArchiveFormat.SevenZip), NoPassword, new InlineProgress(sample => phases.Add(sample.Phase)));
        Assert.True(File.Exists(output));
        Assert.Contains("verifying", phases);
        Assert.DoesNotContain("comparing", phases);
    }

    [Fact]
    public async Task EmptyArchive_CanBeConvertedWithStrictVerification()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "empty.zip");
        using (ZipFile.Open(source, ZipArchiveMode.Create)) { }
        var output = await new ArchiveConversionService(Engine).ConvertAsync(source, directory.Path,
            new(ArchiveFormat.SevenZip, true), NoPassword);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task OrdinaryTarWithDotRoot_ConvertsWithoutChangingLogicalPaths()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "ordinary.tar");
        await using (var stream = File.Create(source))
        await using (var writer = new TarWriter(stream))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "."));
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "./empty/"));
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "./file.txt")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("ordinary tar content"))
            });
        }
        var output = await new ArchiveConversionService(Engine).ConvertAsync(source, directory.Path,
            new(ArchiveFormat.Zip, true), NoPassword);
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(new[] { "empty/", "file.txt" }, zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, ".ffarchive-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyDirectories_AreCleanedAfterSuccessAndCancellation(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "readonly.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            zip.CreateEntry("readonly/").ExternalAttributes = (int)(FileAttributes.ReadOnly | FileAttributes.Directory);
            using var writer = new StreamWriter(zip.CreateEntry("readonly/file.txt").Open());
            writer.Write("preserved content");
        }
        using var cancellation = new CancellationTokenSource();
        var output = Path.Combine(directory.Path, "output");
        var conversion = new ArchiveConversionService(Engine).ConvertAsync(source, output,
            new(ArchiveFormat.SevenZip, true), NoPassword,
            new InlineProgress(sample => { if (cancel && sample.Phase == "compressing") cancellation.Cancel(); }), cancellation.Token);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion);
            Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        }
        else Assert.True(File.Exists(await conversion));
        Assert.Empty(Directory.EnumerateDirectories(output, ".ffarchive-*"));
    }

    [Fact]
    public async Task StrictComparison_DetectsSameSizeContentChangeInOtherwiseValidOutput()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("data.txt").Open(), new UTF8Encoding(false)))
            writer.Write("original");
        var output = Path.Combine(directory.Path, "output");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(source, output, new(ArchiveFormat.Zip, true), NoPassword,
                new InlineProgress(sample =>
                {
                    if (sample.Phase != "comparing") return;
                    var staged = Directory.GetFiles(output, "converted.zip", SearchOption.AllDirectories).Single();
                    using var zip = ZipFile.Open(staged, ZipArchiveMode.Update);
                    zip.GetEntry("data.txt")!.Delete();
                    using var writer = new StreamWriter(zip.CreateEntry("data.txt").Open(), new UTF8Encoding(false));
                    writer.Write("tampered");
                })));
        Assert.Equal(ArchiveFailureCode.ValidationFailed, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Theory]
    [InlineData("中文密码")]
    [InlineData("line\nbreak")]
    public async Task UnsupportedOutputPassword_IsRejectedBeforeAnyScratch(string password)
    {
        using var directory = new TemporaryDirectory();
        var source = CreateZip(directory.Path);
        var output = Path.Combine(directory.Path, "output");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(source, output, new(ArchiveFormat.Zip, false, password), NoPassword));
        Assert.Equal(ArchiveFailureCode.InvalidOptions, error.Code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ZipOutputPasswordLimit_IsExplicitAndSevenZipAllowsUnicode()
    {
        Assert.True(ArchiveFormats.IsValidOutputPassword(ArchiveFormat.Zip, new string('x', 99)));
        Assert.False(ArchiveFormats.IsValidOutputPassword(ArchiveFormat.Zip, new string('x', 100)));
        Assert.False(ArchiveFormats.IsValidOutputPassword(ArchiveFormat.Zip, "中文"));
        Assert.True(ArchiveFormats.IsValidOutputPassword(ArchiveFormat.SevenZip, "中文 Ω"));
        Assert.False(ArchiveFormats.IsValidOutputPassword(ArchiveFormat.Tar, "password"));
    }

    [Fact]
    public void PartRarAndOldRarSelections_NormalizeToFirstVolume()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "data.part01.rar");
        File.WriteAllText(first, "fixture");
        Assert.Equal(first, ArchiveInput.GetPrimaryPath(Path.Combine(directory.Path, "data.part03.rar")));
        Assert.Equal(Path.Combine(directory.Path, "old.rar"), ArchiveInput.GetPrimaryPath(Path.Combine(directory.Path, "old.r02")));
        Assert.Single(ArchiveInput.NormalizeInputs([first, Path.Combine(directory.Path, "data.part02.rar")]));
    }

    [Fact]
    public void NumberedVolumesBeyond999_StillResolveToTheThreeDigitFirstVolume()
    {
        using var directory = new TemporaryDirectory();
        var first = Path.Combine(directory.Path, "many.7z.001");
        File.WriteAllText(first, "first fixture volume");
        var thousandth = Path.Combine(directory.Path, "many.7z.1000");
        File.WriteAllText(thousandth, "later fixture volume");
        Assert.Equal(first, ArchiveInput.GetPrimaryPath(thousandth));
        Assert.Single(ArchiveInput.NormalizeInputs([first, thousandth]));
    }

    [Fact]
    public async Task CorrectPasswordWithUnsupportedEncryptedMethod_FailsWithoutPromptingAgain()
    {
        using var directory = new TemporaryDirectory();
        var data = Path.Combine(directory.Path, "data.txt");
        await File.WriteAllTextAsync(data, "synthetic encrypted test data");
        var source = Path.Combine(directory.Path, "unsupported.zip");
        const string password = "fixture-secret";
        await SevenZipAsync(["a", "-tzip", "-mem=ZipCrypto", "-p", source, data], password);
        var bytes = await File.ReadAllBytesAsync(source);
        for (var index = 0; index < bytes.Length - 12; index++)
        {
            if (bytes[index] != 0x50 || bytes[index + 1] != 0x4b) continue;
            if (bytes[index + 2] == 3 && bytes[index + 3] == 4) { bytes[index + 8] = 254; bytes[index + 9] = 0; }
            if (bytes[index + 2] == 1 && bytes[index + 3] == 2) { bytes[index + 10] = 254; bytes[index + 11] = 0; }
        }
        await File.WriteAllBytesAsync(source, bytes);
        var requests = 0;
        var output = Path.Combine(directory.Path, "output");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(source, output, new(ArchiveFormat.SevenZip), (_, _) =>
            {
                Assert.Equal(1, ++requests);
                return Task.FromResult<string?>(password);
            }));
        Assert.Equal(ArchiveFailureCode.CorruptArchive, error.Code);
        Assert.Equal(1, requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public void PublicRarFixtures_MatchTheirPinnedSourceHashes()
    {
        using var sources = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RarFixtures, "sources.json")));
        Assert.Equal("8509b99a248c52b676491a8675c3f92c7b5673bc", sources.RootElement.GetProperty("commit").GetString());
        foreach (var fixture in sources.RootElement.GetProperty("files").EnumerateArray())
        {
            var file = Path.Combine(RarFixtures, fixture.GetProperty("file").GetString()!);
            Assert.True(File.Exists(file), "A pinned public RAR fixture is missing: " + Path.GetFileName(file));
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
            Assert.Equal(fixture.GetProperty("sha256").GetString(), hash);
        }
    }

    [Theory]
    [InlineData("stored", ArchiveFormat.Zip)]
    [InlineData("stored", ArchiveFormat.SevenZip)]
    [InlineData("compressed", ArchiveFormat.Zip)]
    [InlineData("compressed", ArchiveFormat.SevenZip)]
    public async Task RealRar5_StrictlyConvertsWithoutChangingSource(string fixture, ArchiveFormat format)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(RarFixtures, "test_read_format_rar5_" + fixture + ".rar");
        var original = await File.ReadAllBytesAsync(source);
        var output = await new ArchiveConversionService(Engine).ConvertAsync(source, directory.Path,
            new(format, true), NoPassword);
        Assert.True(File.Exists(output));
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        if (fixture == "stored" && format == ArchiveFormat.Zip)
        {
            using var zip = ZipFile.OpenRead(output);
            using var reader = new StreamReader(zip.GetEntry("helloworld.txt")!.Open());
            Assert.Equal("hello libarchive test suite!\n", await reader.ReadToEndAsync());
        }
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, ".ffarchive-*"));
    }

    [Fact]
    public async Task RealRar5Volumes_SelectedMiddleVolumeProducesOneVerifiedZip()
    {
        using var directory = new TemporaryDirectory();
        var volumes = Directory.GetFiles(RarFixtures, "test_read_format_rar5_multiarchive.part*.rar").Order().ToArray();
        Assert.Equal(8, volumes.Length);
        Assert.Single(ArchiveInput.NormalizeInputs(volumes));
        var output = await new ArchiveConversionService(Engine).ConvertAsync(volumes[3], directory.Path,
            new(ArchiveFormat.Zip, true), NoPassword);
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(144608, zip.GetEntry("home/antek/temp/build/unrar5/libarchive/bin/bsdcat_test")!.Length);
        Assert.Equal(365672, zip.GetEntry("home/antek/temp/build/unrar5/libarchive/bin/bsdtar_test")!.Length);
        Assert.All(volumes, volume => Assert.True(File.Exists(volume)));
    }

    [Fact]
    public async Task RealRar5Volumes_MissingLastVolumeCannotPublishPartialOutput()
    {
        using var directory = new TemporaryDirectory();
        var volumes = Directory.GetFiles(RarFixtures, "test_read_format_rar5_multiarchive.part*.rar").Order().ToArray();
        foreach (var volume in volumes.Take(7)) File.Copy(volume, Path.Combine(directory.Path, Path.GetFileName(volume)));
        var output = Path.Combine(directory.Path, "output");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(Path.Combine(directory.Path, Path.GetFileName(volumes[2])), output, new(ArchiveFormat.Zip, true), NoPassword));
        Assert.Equal(ArchiveFailureCode.MissingVolume, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    private static string RarFixtures => Path.Combine(RepositoryTestContext.Root, "tests", "FormatFactoryWinUI.Tests", "Fixtures", "Archives");

    [Fact]
    public void PublicZipSplitFixtures_MatchTheirPinnedSourceHashes()
    {
        using var sources = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(ZipSplitFixtures, "sources.json")));
        Assert.Equal("0df4d084182c2c2662b777ee2c3b04a1c42bbc66", sources.RootElement.GetProperty("commit").GetString());
        foreach (var fixture in sources.RootElement.GetProperty("files").EnumerateArray())
        {
            var file = Path.Combine(ZipSplitFixtures, fixture.GetProperty("file").GetString()!);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
            Assert.Equal(fixture.GetProperty("sha256").GetString(), hash);
        }
    }

    [Theory]
    [InlineData(".z01")]
    [InlineData(".z02")]
    [InlineData(".zip")]
    public async Task RealZipSplit_SelectingAnyVolumeCreatesOneVerifiedArchive(string extension)
    {
        using var directory = new TemporaryDirectory();
        var stem = Path.Combine(ZipSplitFixtures, "split_zip_created_by_zip");
        var inputs = new[] { stem + ".z01", stem + ".z02", stem + ".zip" };
        var originals = inputs.ToDictionary(path => path, File.ReadAllBytes);
        Assert.All(inputs, input => Assert.True(ArchiveInput.IsSupported(input)));
        Assert.Equal(stem + ".zip", Assert.Single(ArchiveInput.NormalizeInputs(inputs)));
        var output = await new ArchiveConversionService(Engine).ConvertAsync(stem + extension, directory.Path,
            new(ArchiveFormat.Zip, true), NoPassword);
        using var zip = ZipFile.OpenRead(output);
        foreach (var (reference, entry) in new[]
        {
            ("file_to_compare_1", "commons-compress/src/main/java/org/apache/commons/compress/archivers/dump/UnsupportedCompressionAlgorithmException.java"),
            ("file_to_compare_2", "commons-compress/src/main/java/org/apache/commons/compress/compressors/deflate/DeflateParameters.java")
        })
        {
            using var extracted = new MemoryStream();
            await zip.GetEntry(entry)!.Open().CopyToAsync(extracted);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(ZipSplitFixtures, reference)), extracted.ToArray());
        }
        Assert.All(originals, original => Assert.Equal(original.Value, File.ReadAllBytes(original.Key)));
        Assert.Single(Directory.EnumerateFiles(directory.Path));
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, ".ffarchive-*"));
    }

    [Theory]
    [InlineData(".z01")]
    [InlineData(".z02")]
    [InlineData(".zip")]
    public async Task RealZipSplit_MissingAnyVolumeCannotPublishPartialOutput(string missing)
    {
        using var directory = new TemporaryDirectory();
        const string stem = "split_zip_created_by_zip";
        var extensions = new[] { ".z01", ".z02", ".zip" };
        foreach (var extension in extensions.Where(extension => extension != missing))
            File.Copy(Path.Combine(ZipSplitFixtures, stem + extension), Path.Combine(directory.Path, stem + extension));
        var selected = Path.Combine(directory.Path, stem + extensions.First(extension => extension != missing));
        var output = Path.Combine(directory.Path, "output");
        var error = await Assert.ThrowsAsync<ArchiveConversionException>(() => new ArchiveConversionService(Engine)
            .ConvertAsync(selected, output, new(ArchiveFormat.SevenZip, true), NoPassword));
        Assert.Equal(ArchiveFailureCode.MissingVolume, error.Code);
        Assert.True(!Directory.Exists(output) || !Directory.EnumerateFileSystemEntries(output).Any());
    }

    private static string ZipSplitFixtures => Path.Combine(RarFixtures, "ZipSplit");

    private static string CreateZip(string directory)
    {
        var source = Path.Combine(directory, "原包.zip");
        using var zip = ZipFile.Open(source, ZipArchiveMode.Create);
        zip.CreateEntry("empty/");
        zip.CreateEntry("zero.dat");
        using var writer = new StreamWriter(zip.CreateEntry("目录/文件.txt").Open(), new UTF8Encoding(false));
        writer.Write("原始内容\nhello🙂");
        return source;
    }

    private static Task<string?> NoPassword(ArchivePasswordRequest _, CancellationToken __) =>
        throw new XunitException("This fixture must not request an input password.");

    private static async Task SevenZipAsync(IEnumerable<string> arguments, string? password = null)
    {
        var info = new ProcessStartInfo(Engine)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = password is not null, StandardInputEncoding = password is null ? null : new UTF8Encoding(false)
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (password is not null)
        {
            await process.StandardInput.WriteLineAsync(password);
            await process.StandardInput.WriteLineAsync(password);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, (await output) + (await error));
    }

    private sealed class InlineProgress(Action<ArchiveProgress> report) : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress value) => report(value);
    }
}

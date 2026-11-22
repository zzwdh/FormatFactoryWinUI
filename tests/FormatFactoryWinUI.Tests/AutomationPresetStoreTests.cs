using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class AutomationPresetStoreTests
{
    [Fact]
    public async Task Presets_RoundTripInOrderAndDoNotShareMutableOptions()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "presets.json");
        var first = DocumentPreset("Searchable document");
        var second = new AutomationConversionPreset
        {
            Name = "Audio", TargetFormatId = "mp3", ConversionMode = "quality",
            Options = new(StringComparer.OrdinalIgnoreCase) { ["selectedAudioTrack"] = "2" }
        };
        var store = new AutomationPresetStore(path);

        var saving = store.SaveAsync([first, second]);
        first.Name = "Changed after capture";
        first.Options["ocrLanguage"] = "en-US";
        await saving;
        var restored = await new AutomationPresetStore(path).LoadAsync();

        Assert.Equal([first.Id, second.Id], restored.Select(preset => preset.Id));
        Assert.Equal("Searchable document", restored[0].Name);
        Assert.Equal("pdf", restored[0].TargetFormatId);
        Assert.Equal("balanced", restored[0].ConversionMode);
        Assert.Equal("zh-Hans", restored[0].Options["OCRLANGUAGE"]);
        Assert.Equal("2", restored[1].Options["selectedAudioTrack"]);
        restored[0].Options["ocrLanguage"] = "fr-FR";
        restored[0].Name = "Changed loaded preset";
        var reread = await store.LoadAsync();
        Assert.Equal("Searchable document", reread[0].Name);
        Assert.Equal("zh-Hans", reread[0].Options["ocrLanguage"]);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task FromRule_FiltersSourcesCredentialsInternalMetadataAndUnsupportedOptions()
    {
        using var directory = new TemporaryDirectory();
        var rule = new AutomationRule
        {
            Name = "Rule name", FolderPath = Path.Combine(directory.Path, "secret-source"),
            FolderAccessToken = "secret-access-token", OutputDirectory = Path.Combine(directory.Path, "secret-output"),
            ArchiveSource = true, ArchiveDirectory = Path.Combine(directory.Path, "secret-archive"),
            TargetFormatId = "PDF", ConversionMode = "quality"
        };
        rule.Options["PASSWORD"] = "secret-password";
        rule.Options["automation.ruleSnapshot"] = "secret-snapshot";
        rule.Options["automation.outputDirectory"] = "secret-output-option";
        rule.Options["inputPath"] = "secret-path";
        rule.Options["accessToken"] = "secret-option-token";
        rule.Options["outputPath"] = "secret-direct-output";
        rule.Options["arguments"] = "secret-command";
        rule.Options["title"] = "secret-file-title";
        rule.Options["history.capturedSettings"] = "secret-settings";
        rule.Options["ocrLanguage"] = "zh-Hans";
        rule.Options["repairDocx"] = "true";
        rule.Options["imageColorPolicy"] = "srgb"; // Inapplicable to PDF.

        var preset = AutomationConversionPreset.FromRule(rule, " PDF preset ");
        var path = Path.Combine(directory.Path, "presets.json");
        await new AutomationPresetStore(path).SaveAsync([preset]);

        Assert.NotEqual(rule.Id, preset.Id);
        Assert.Equal("PDF preset", preset.Name);
        Assert.Equal("pdf", preset.TargetFormatId);
        Assert.Equal("quality", preset.ConversionMode);
        Assert.Equal(2, preset.Options.Count);
        Assert.DoesNotContain("secret-", await File.ReadAllTextAsync(path));
        rule.Options["ocrLanguage"] = "en-US";
        Assert.Equal("zh-Hans", preset.Options["ocrLanguage"]);
    }

    [Fact]
    public async Task FromTask_CapturesAudioSelectionAndExcludesFileSpecificMetadata()
    {
        using var directory = new TemporaryDirectory();
        var task = new MediaTask
        {
            InputPath = "private-source.mkv", InputAccessToken = "private-input-token",
            SourceRoot = "private-root", SourceRootAccessToken = "private-root-token",
            OutputPath = "private-output.mp3", TargetFormatId = "mp3", ConversionMode = "quality",
            SelectedAudioTrack = 3
        };
        task.Options["password"] = "private-password";
        task.Options["automation.fingerprint"] = "private-fingerprint";
        task.Options["audioTrackPolicy"] = "compatible";
        task.Options["layoutMode"] = "fixed"; // Request-level document settings do not apply to audio.
        var preset = AutomationConversionPreset.FromTask(task, "Audio track three");
        var path = Path.Combine(directory.Path, "presets.json");

        await new AutomationPresetStore(path).SaveAsync([preset]);

        Assert.Equal("3", preset.Options["selectedAudioTrack"]);
        Assert.Equal("compatible", preset.Options["audioTrackPolicy"]);
        Assert.Equal(2, preset.Options.Count);
        Assert.DoesNotContain("private-", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Snapshot_DeepCopiesOptions()
    {
        var original = DocumentPreset("Original");
        var copy = original.Snapshot();
        copy.Name = "Copy";
        copy.Options["ocrLanguage"] = "en-US";

        Assert.Equal(original.Id, copy.Id);
        Assert.Equal("Original", original.Name);
        Assert.Equal("zh-Hans", original.Options["ocrLanguage"]);
    }

    [Theory]
    [InlineData("{broken preset bytes")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":99,\"Presets\":[]}")]
    [InlineData("{\"Version\":1,\"Presets\":null}")]
    [InlineData("{\"Version\":1,\"Presets\":[{}]}")]
    [InlineData("{\"Version\":1,\"Presets\":[],\"Unexpected\":true}")]
    public async Task CorruptDocument_IsReportedAndPreservedEvenWhenSavingWithoutLoading(string original)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "presets.json");
        await File.WriteAllTextAsync(path, original);

        await Assert.ThrowsAsync<InvalidDataException>(() => new AutomationPresetStore(path).LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => new AutomationPresetStore(path).SaveAsync([DocumentPreset("New")]));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("password", "secret")]
    [InlineData("automation.outputDirectory", "C:\\output")]
    [InlineData("arguments", "-i source -y target")]
    [InlineData("ocrLanguage", "C:\\source")]
    [InlineData("ocrLanguage", "en-US; script")]
    [InlineData("layoutMode", "arbitrary")]
    [InlineData("repairDocx", "yes")]
    [InlineData("imageColorPolicy", "srgb")]
    [InlineData("selectedAudioTrack", "1")]
    [InlineData("outputFileNameTemplate", "..\\{name}")]
    [InlineData("outputFileNameTemplate", "{unknown}")]
    [InlineData("outputFileNameTemplate", "NUL")]
    [InlineData("preserveFolderStructure", "maybe")]
    public async Task InvalidOrIncompatibleOptions_CannotReplaceValidData(string key, string value)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "presets.json");
        var store = new AutomationPresetStore(path);
        await store.SaveAsync([DocumentPreset("Before")]);
        var original = await File.ReadAllTextAsync(path);
        var invalid = DocumentPreset("Invalid");
        invalid.Options[key] = value;

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([invalid]));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("mp3", "lossless", "audioTrackPolicy", "compatible")]
    [InlineData("mp3", "balanced", "selectedAudioTrack", "-1")]
    [InlineData("mp3", "balanced", "selectedAudioTrack", "256")]
    [InlineData("mp3", "balanced", "selectedAudioTrack", "1; command")]
    [InlineData("gif", "balanced", "imageColorPolicy", "preserve")]
    [InlineData("jpg", "lossless", "imageColorPolicy", "preserve")]
    public void InvalidMediaParameters_AreRejected(string target, string mode, string key, string value)
    {
        var preset = new AutomationConversionPreset
        {
            Name = "Invalid parameters", TargetFormatId = target, ConversionMode = mode,
            Options = new(StringComparer.OrdinalIgnoreCase) { [key] = value }
        };

        Assert.Throws<InvalidDataException>(preset.Validate);
    }

    [Fact]
    public async Task InvalidIdentityTargetModeAndDuplicateIds_DoNotCreateData()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "presets.json");
        var store = new AutomationPresetStore(path);
        var valid = DocumentPreset("Valid");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([valid, valid.Snapshot()]));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([valid with { Id = Guid.Empty }]));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([valid with { Name = " " }]));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([valid with { TargetFormatId = "unknown-format" }]));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([valid with { ConversionMode = "script" }]));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([valid with { Options = null! }]));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync([null!]));

        Assert.False(File.Exists(path));
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task ConcurrentStoreInstances_AlwaysPublishOneCompleteDocument()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "presets.json");
        var collections = Enumerable.Range(0, 12)
            .Select(index => new[] { DocumentPreset($"Batch {index} A"), DocumentPreset($"Batch {index} B") })
            .ToArray();

        await Task.WhenAll(collections.Select(presets => new AutomationPresetStore(path).SaveAsync(presets)));
        var loaded = await new AutomationPresetStore(path).LoadAsync();

        Assert.Contains(collections, candidates => candidates.Select(preset => preset.Id).SequenceEqual(loaded.Select(preset => preset.Id)));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task CancelledSave_PreservesExistingDocument()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "presets.json");
        var store = new AutomationPresetStore(path);
        await store.SaveAsync([DocumentPreset("Before")]);
        var original = await File.ReadAllTextAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync([DocumentPreset("After")], cancellation.Token));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    private static AutomationConversionPreset DocumentPreset(string name) => new()
    {
        Name = name, TargetFormatId = "pdf", ConversionMode = "balanced",
        Options = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ocrLanguage"] = "zh-Hans", ["layoutMode"] = "reflow",
            ["outputFileNameTemplate"] = "{name}-{date}", ["preserveFolderStructure"] = "false"
        }
    };
}

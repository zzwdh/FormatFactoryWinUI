using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class AutomationRuleTests
{
    [Fact]
    public void Snapshot_IsolatesMutableOptions()
    {
        var rule = new AutomationRule();
        rule.Options["profile"] = "balanced";

        var snapshot = rule.Snapshot();
        rule.Name = "Later edit";
        rule.Options["profile"] = "quality";

        Assert.Equal(string.Empty, snapshot.Name);
        Assert.Equal("balanced", snapshot.Options["profile"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestinationEqualToInputOrContainingInput_IsRejected(bool archive)
    {
        using var directory = new TemporaryDirectory();
        var input = Path.Combine(directory.Path, "input");
        foreach (var destination in new[] { input, input + Path.DirectorySeparatorChar, directory.Path })
        {
            var rule = new AutomationRule
            {
                Name = "Safe route", FolderPath = input, ArchiveSource = archive,
                OutputDirectory = archive ? Path.Combine(input, "output") : destination,
                ArchiveDirectory = archive ? destination : string.Empty
            };

            Assert.Throws<InvalidDataException>(() => rule.Validate());
        }
    }

    [Fact]
    public void IndependentInputSubdirectories_AreValidDestinations()
    {
        using var directory = new TemporaryDirectory();
        var rule = new AutomationRule
        {
            Name = "Separated folders", FolderPath = directory.Path,
            OutputDirectory = Path.Combine(directory.Path, "output"),
            ArchiveSource = true, ArchiveDirectory = Path.Combine(directory.Path, "archive")
        };

        rule.Validate();
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("09:60")]
    [InlineData("morning")]
    public void InvalidDailySchedule_IsRejected(string time)
    {
        using var directory = new TemporaryDirectory();
        var rule = new AutomationRule
        {
            Name = "Timed", FolderPath = directory.Path,
            ScheduleEnabled = true, ScheduleTime = time,
            OutputDirectory = Path.Combine(directory.Path, "output")
        };

        Assert.Throws<InvalidDataException>(() => rule.Validate());
    }

    [Fact]
    public void RelativeSourcePath_IsRejected()
    {
        var rule = new AutomationRule { Name = "Relative", FolderPath = "input" };

        Assert.Throws<InvalidDataException>(() => rule.Validate());
    }
}

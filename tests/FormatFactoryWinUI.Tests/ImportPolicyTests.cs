using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class ImportPolicyTests
{
    [Theory]
    [InlineData(ContentKind.Video, true)]
    [InlineData(ContentKind.Audio, true)]
    [InlineData(ContentKind.Image, true)]
    [InlineData(ContentKind.Document, false)]
    [InlineData(ContentKind.Ebook, false)]
    [InlineData(ContentKind.Disc, false)]
    public void RequiresFfprobe_IsLimitedToExistingMediaProbePath(
        ContentKind kind,
        bool expected)
    {
        Assert.Equal(expected, ImportPolicy.RequiresFfprobe(kind));
    }

    [Fact]
    public void NaturalPathComparer_SortsNumberedPagesInHumanOrder()
    {
        string[] paths = ["page10.png", "page02.png", "page2.png", "page1.png"];

        Array.Sort(paths, ImportPolicy.NaturalPathComparer);

        Assert.Equal(["page1.png", "page2.png", "page02.png", "page10.png"], paths);
    }
}

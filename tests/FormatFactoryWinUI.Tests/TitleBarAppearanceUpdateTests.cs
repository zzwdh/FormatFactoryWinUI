using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class TitleBarAppearanceUpdateTests
{
    private const int NativeAssertion = unchecked((int)0x8007029C);

    [Fact]
    public void NativeCaptionAssertion_DoesNotEscapeActivationAndRetainsDiagnostic()
    {
        var failure = new COMException("Synthetic caption assertion.", NativeAssertion);
        var reports = new List<Exception>();
        var writes = new List<string>();

        TitleBarAppearanceUpdate.Apply(() =>
        {
            writes.Add("background");
            throw failure;
        }, reports.Add);
        writes.Add("activation-continued");

        Assert.Equal(["background", "activation-continued"], writes);
        Assert.Same(failure, Assert.Single(reports));
    }

    [Fact]
    public void NextAppearanceChange_CanSucceedAfterNativeAssertion()
    {
        var applied = false;
        var reports = new List<Exception>();
        TitleBarAppearanceUpdate.Apply(() => throw new COMException("Synthetic assertion.", NativeAssertion), reports.Add);

        TitleBarAppearanceUpdate.Apply(() => applied = true, reports.Add);

        Assert.True(applied);
        Assert.Single(reports);
    }

    [Fact]
    public void DiagnosticCallbackFailure_CannotInterruptActivation()
    {
        TitleBarAppearanceUpdate.Apply(() => throw new COMException("Synthetic assertion.", NativeAssertion),
            _ => throw new IOException("Synthetic diagnostic failure."));
    }

    [Fact]
    public void SuccessfulAppearanceUpdate_DoesNotReport()
    {
        var applied = false;
        TitleBarAppearanceUpdate.Apply(() => applied = true, _ => Assert.Fail("Successful styling must not report."));
        Assert.True(applied);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(unchecked((int)0x8000FFFF))]
    [InlineData(unchecked((int)0x80004005))]
    public void OtherComFailures_AreNotReclassifiedAsCaptionAssertion(int hresult)
    {
        var failure = new COMException("Synthetic unrelated native failure.", hresult);
        var actual = Assert.Throws<COMException>(() => TitleBarAppearanceUpdate.Apply(() => throw failure,
            _ => Assert.Fail("An unrelated native failure must propagate.")));
        Assert.Same(failure, actual);
    }

    [Fact]
    public void ManagedFailureWithSameHresult_IsNotReclassifiedAsNativeAssertion()
    {
        var failure = new InvalidOperationException("Synthetic programming failure.") { HResult = NativeAssertion };
        var actual = Assert.Throws<InvalidOperationException>(() => TitleBarAppearanceUpdate.Apply(() => throw failure,
            _ => Assert.Fail("A programming failure must propagate.")));
        Assert.Same(failure, actual);
    }
}

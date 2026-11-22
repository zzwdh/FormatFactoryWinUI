using System.Runtime.InteropServices;

namespace FormatFactoryWinUI.Services;

/// <summary>Applies optional native caption styling without interrupting window activation.</summary>
internal static class TitleBarAppearanceUpdate
{
    internal static void Apply(Action update, Action<Exception>? report = null)
    {
        try { update(); }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x8007029C))
        {
            // The native caption setter can reject an appearance update during
            // activation. Keep the current caption and its diagnostic, then let
            // the next theme/layout change try again. Other failures still escape.
            SafeDiagnostics.Report(exception, report);
        }
    }
}

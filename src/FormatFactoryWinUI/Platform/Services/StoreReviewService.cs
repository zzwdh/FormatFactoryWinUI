using System.Diagnostics;
using System.Security.Principal;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace FormatFactoryWinUI.Services;

internal enum StoreReviewResult { Succeeded, Canceled, Failed }

/// <summary>Uses Store's submission result, never a successful URI launch, to confirm a review.</summary>
internal static class StoreReviewService
{
    public static bool IsStoreInstalled
    {
        get
        {
            try
            {
                var package = Package.Current;
                return !Debugger.IsAttached && !package.IsDevelopmentMode
                    && package.SignatureKind == PackageSignatureKind.Store;
            }
            catch { return false; } // Unpackaged development builds have no Store identity.
        }
    }

    public static bool CanRequestReview
    {
        get
        {
            if (!IsStoreInstalled) return false;
            using var identity = WindowsIdentity.GetCurrent();
            // Store's review UI does not support elevated desktop applications.
            return !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static async Task<StoreReviewResult> RequestAsync(IntPtr windowHandle)
    {
        try
        {
            if (windowHandle == IntPtr.Zero || !CanRequestReview) return StoreReviewResult.Failed;
            var context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(context, windowHandle);
            var result = await context.RequestRateAndReviewAppAsync();
            return result.Status switch
            {
                StoreRateAndReviewStatus.Succeeded => StoreReviewResult.Succeeded,
                StoreRateAndReviewStatus.CanceledByUser => StoreReviewResult.Canceled,
                _ => StoreReviewResult.Failed
            };
        }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            return StoreReviewResult.Failed;
        }
    }
}

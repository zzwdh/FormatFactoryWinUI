using System.Runtime.InteropServices;
using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.Tests;

public sealed class StorageTokenDiagnosticTests
{
    private const string Private = "C:\\SyntheticPrivate\\personal.docx token=synthetic-secret https://private.invalid/user";

    [Fact]
    public void FailureFacts_KeepActionStageKindAndTokenPresenceWithoutSensitiveValues()
    {
        var cases = new[]
        {
            (StorageTokenDiagnosticAction.Remember, StorageTokenDiagnosticKind.File, StorageTokenDiagnosticStage.FutureAccessAdd, false),
            (StorageTokenDiagnosticAction.Resolve, StorageTokenDiagnosticKind.Folder, StorageTokenDiagnosticStage.RestoreItem, true),
            (StorageTokenDiagnosticAction.Recover, StorageTokenDiagnosticKind.Unknown, StorageTokenDiagnosticStage.RegistryRead, false),
            (StorageTokenDiagnosticAction.Release, StorageTokenDiagnosticKind.File, StorageTokenDiagnosticStage.FutureAccessRemove, true)
        };
        foreach (var (action, kind, stage, tokenPresent) in cases)
        {
            var diagnostic = new StorageTokenDiagnosticOperation(action, kind, tokenPresent) { Stage = stage };
            var error = new COMException(Private, unchecked((int)0x80070005));
            error.Data["token"] = Private;
            var safe = diagnostic.Format(error);
            Assert.NotEqual(Guid.Empty, diagnostic.Id);
            Assert.Contains($"operation={diagnostic.Id:N}", safe);
            Assert.Contains($"action={action} stage={stage} kind={kind} token_present={tokenPresent} outcome=failed elapsed_ms=", safe);
            Assert.Contains("exception=COMException hresult=0x80070005 native=5", safe);
            Assert.DoesNotContain("SyntheticPrivate", safe);
            Assert.DoesNotContain("synthetic-secret", safe);
            Assert.DoesNotContain("private.invalid", safe);
            Assert.Equal(Private, error.Message);
            Assert.Equal(Private, error.Data["token"]);
        }
    }

    [Fact]
    public void Facts_NormalizeEnumsAndKeepCancellationDistinctFromFailure()
    {
        var diagnostic = new StorageTokenDiagnosticOperation((StorageTokenDiagnosticAction)999,
            (StorageTokenDiagnosticKind)999, tokenPresent: true) { Stage = (StorageTokenDiagnosticStage)999 };
        var original = new OperationCanceledException(Private);
        var safe = diagnostic.Format(original);
        Assert.Contains("action=Unknown stage=Unknown kind=Unknown token_present=True outcome=cancelled", safe);
        Assert.Contains("exception=OperationCanceledException", safe);
        Assert.DoesNotContain(Private, safe);
        Assert.Equal(Private, original.Message);
    }
}

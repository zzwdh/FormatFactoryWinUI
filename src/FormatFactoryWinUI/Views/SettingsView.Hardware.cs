using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.ViewModels;
using Microsoft.UI.Xaml;

namespace FormatFactoryWinUI.Views;

public sealed partial class SettingsView
{
    internal void RenderHardware(HardwareSettingsState state)
    {
        if (!ViewModel.IsInitialized) return;
        HardwareDecodeTestButton.IsEnabled = !state.TestRunning;
        HardwareDecodeStatusText.Text = state.TestRunning
            ? LocalizationService.T("HardwareSettingsTesting")
            : state.NeedsFfmpeg
                ? LocalizationService.T("HardwareSettingsFfmpegRequired")
                : !string.IsNullOrWhiteSpace(state.TestError)
                    ? LocalizationService.T("HardwareSettingsTestFailed", state.TestError)
                    : state.Report is null
                        ? LocalizationService.T("HardwareSettingsNotTested")
                        : LocalizationService.T(
                            "HardwareSettingsLastChecked",
                            state.Report.TestedAt
                                .ToLocalTime()
                                .ToString("g", LocalizationService.CurrentCulture));

        if (state.Inventory is null)
        {
            HardwareCardsLayout.MaximumRowsOrColumns = 1;
            HardwareCardsRepeater.ItemsSource = Array.Empty<HardwareCardPresentation>();
            HardwareInventoryNoticeText.Visibility = Visibility.Visible;
            HardwareInventoryNoticeText.Text = state.InventoryLoading
                ? LocalizationService.T("HardwareSettingsDetectingHardware")
                : LocalizationService.T(
                    "HardwareSettingsInventoryFailed",
                    state.InventoryError ?? string.Empty);
            return;
        }

        var reportForCards = state.TestRunning ? null : state.Report;
        var cards = HardwareDecodeGrouping
            .Group(state.Inventory, reportForCards)
            .Select(group => CreateHardwareCardPresentation(group, state.Inventory))
            .ToArray();
        HardwareCardsLayout.MaximumRowsOrColumns = Math.Max(1, cards.Length);
        HardwareCardsRepeater.ItemsSource = cards;

        if (state.Inventory.Gpus.Count == 0)
        {
            HardwareInventoryNoticeText.Text = LocalizationService.T("HardwareSettingsNoGpu");
            HardwareInventoryNoticeText.Visibility = Visibility.Visible;
        }
        else
        {
            HardwareInventoryNoticeText.Text = string.Empty;
            HardwareInventoryNoticeText.Visibility = Visibility.Collapsed;
        }
    }

    private static HardwareCardPresentation CreateHardwareCardPresentation(
        HardwareDecodeGroup hardwareGroup,
        HardwareInventory inventory)
    {
        var isCpu = hardwareGroup.Kind == HardwareDecodeGroupKind.Cpu;
        var cpu = hardwareGroup.Cpu;
        var gpu = hardwareGroup.Gpu;
        var kindLabel = isCpu
            ? LocalizationService.T("HardwareSettingsCpuLabel")
            : LocalizationService.T("HardwareSettingsGpuLabel", (gpu?.Index ?? 0) + 1);
        var name = string.IsNullOrWhiteSpace(hardwareGroup.Name)
            ? LocalizationService.T(isCpu ? "HardwareSettingsUnknownCpu" : "HardwareSettingsUnknownGpu")
            : hardwareGroup.Name.Trim();
        var details = isCpu && cpu is not null
            ? LocalizationService.T(
                "HardwareSettingsCpuDetails",
                cpu.ProcessArchitecture,
                cpu.SystemArchitecture,
                cpu.LogicalProcessorCount)
            : gpu is not null
                ? LocalizationService.T(
                    "HardwareSettingsGpuDetails",
                    $"0x{gpu.VendorId:X4}",
                    gpu.DedicatedVideoMemory / 1024d / 1024d / 1024d)
                : string.Empty;
        var isDefaultGpu = gpu is not null && inventory.DefaultGpu?.Index == gpu.Index;
        var accelerators = hardwareGroup.Decoders
            .GroupBy(assignment => assignment.BackendId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(backendGroup => BackendDisplayOrder(backendGroup.Key))
            .Select(backendGroup => new HardwareAcceleratorPresentation
            {
                Name = BackendDisplayName(backendGroup.Key),
                Codecs = backendGroup.Select(CreateHardwareCodecPresentation).ToArray()
            })
            .ToArray();

        return new HardwareCardPresentation
        {
            KindLabel = kindLabel,
            Name = name,
            Details = details,
            DefaultBadgeText = LocalizationService.T("HardwareSettingsDefaultGpu"),
            DefaultBadgeVisibility = isDefaultGpu ? Visibility.Visible : Visibility.Collapsed,
            DecoderSectionTitle = LocalizationService.T("HardwareSettingsDecoderSection"),
            Accelerators = accelerators,
            EmptyText = LocalizationService.T("HardwareSettingsNoAssignedBackend"),
            EmptyVisibility = accelerators.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
            AutomationName = LocalizationService.T("HardwareSettingsCardAutomationName", kindLabel, name)
        };
    }

    private static HardwareCodecPresentation CreateHardwareCodecPresentation(
        HardwareDecodeAssignment assignment) => assignment.Supported switch
        {
            true => new HardwareCodecPresentation
            {
                DisplayName = assignment.DisplayName,
                StatusGlyph = "✓",
                StatusText = LocalizationService.T("HardwareSettingsSupported"),
                StatusOpacity = 1
            },
            false => new HardwareCodecPresentation
            {
                DisplayName = assignment.DisplayName,
                StatusGlyph = "×",
                StatusText = LocalizationService.T("HardwareSettingsUnsupported"),
                StatusOpacity = 1
            },
            null => new HardwareCodecPresentation
            {
                DisplayName = assignment.DisplayName,
                StatusGlyph = "·",
                StatusText = LocalizationService.T("HardwareSettingsPending"),
                StatusOpacity = 0.68
            }
        };

    private static string BackendDisplayName(string backendId) => backendId.ToLowerInvariant() switch
    {
        HardwareDecodeGrouping.SoftwareBackendId => LocalizationService.T("HardwareSettingsSoftwareBackend"),
        HardwareDecodeGrouping.CudaBackendId => LocalizationService.T("HardwareSettingsCudaBackend"),
        HardwareDecodeGrouping.QuickSyncBackendId => LocalizationService.T("HardwareSettingsQsvBackend"),
        HardwareDecodeGrouping.Direct3D11BackendId => LocalizationService.T("HardwareSettingsD3d11vaBackend"),
        HardwareDecodeGrouping.Dxva2BackendId => LocalizationService.T("HardwareSettingsDxva2Backend"),
        _ => backendId.ToUpperInvariant()
    };

    private static int BackendDisplayOrder(string backendId) => backendId.ToLowerInvariant() switch
    {
        HardwareDecodeGrouping.SoftwareBackendId => 0,
        HardwareDecodeGrouping.CudaBackendId => 1,
        HardwareDecodeGrouping.QuickSyncBackendId => 2,
        HardwareDecodeGrouping.Direct3D11BackendId => 3,
        HardwareDecodeGrouping.Dxva2BackendId => 4,
        _ => int.MaxValue
    };

    private sealed class HardwareCardPresentation
    {
        public required string KindLabel { get; init; }
        public required string Name { get; init; }
        public required string Details { get; init; }
        public required string DefaultBadgeText { get; init; }
        public required Visibility DefaultBadgeVisibility { get; init; }
        public required string DecoderSectionTitle { get; init; }
        public required IReadOnlyList<HardwareAcceleratorPresentation> Accelerators { get; init; }
        public required string EmptyText { get; init; }
        public required Visibility EmptyVisibility { get; init; }
        public required string AutomationName { get; init; }
    }

    private sealed class HardwareAcceleratorPresentation
    {
        public required string Name { get; init; }
        public required IReadOnlyList<HardwareCodecPresentation> Codecs { get; init; }
    }

    private sealed class HardwareCodecPresentation
    {
        public required string DisplayName { get; init; }
        public required string StatusGlyph { get; init; }
        public required string StatusText { get; init; }
        public required double StatusOpacity { get; init; }
    }

}

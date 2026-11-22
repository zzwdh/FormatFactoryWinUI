using FormatFactoryWinUI.Services;

namespace FormatFactoryWinUI.ViewModels;

public sealed record HardwareSettingsState(
    HardwareInventory? Inventory,
    HardwareDecodeReport? Report,
    bool InventoryLoading,
    string? InventoryError,
    bool TestRunning,
    bool NeedsFfmpeg,
    string? TestError);

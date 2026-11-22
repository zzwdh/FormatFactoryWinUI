namespace FormatFactoryWinUI.Services;

/// <summary>Presentation-only rules; these never decide conversion engine availability.</summary>
public static class WindowsNativeCapabilityPresentation
{
    public static bool NeedsAttention(WindowsNativeCapabilityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Status is NativeCapabilityStatus.Failed
            or NativeCapabilityStatus.Unavailable
            or NativeCapabilityStatus.NotSupported
            || entry.IsAvailable && entry.ItemCount == 0 && IsDeviceCapability(entry.Id);
    }

    public static IReadOnlyList<WindowsNativeCapabilityEntry> GetVisibleEntries(
        WindowsNativeCapabilitySnapshot snapshot, bool attentionOnly)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return attentionOnly
            ? snapshot.Capabilities.Where(NeedsAttention).ToArray()
            : snapshot.Capabilities;
    }

    public static WindowsNativeCapabilityEntry? FindNextAttention(
        WindowsNativeCapabilitySnapshot snapshot, string? currentId)
    {
        var entries = GetVisibleEntries(snapshot, attentionOnly: true);
        if (entries.Count == 0)
        {
            return null;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            if (string.Equals(entries[index].Id, currentId, StringComparison.OrdinalIgnoreCase))
            {
                return entries[(index + 1) % entries.Count];
            }
        }

        return entries[0];
    }

    public static string? GetDeviceHintKey(WindowsNativeCapabilitySnapshot snapshot, int opticalDriveCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var devices = snapshot.Capabilities.Where(entry => IsDeviceCapability(entry.Id)).ToArray();
        if (devices.Any(entry => entry.Status == NativeCapabilityStatus.Failed))
        {
            return "NativeUiWorkflowDeviceCheckFailed";
        }

        if (devices.Any(entry => !entry.IsAvailable))
        {
            return "NativeUiWorkflowDeviceUnavailable";
        }

        return devices.Any(entry => entry.ItemCount == 0) || opticalDriveCount == 0
            ? "NativeUiWorkflowConnectDevice"
            : null;
    }

    private static bool IsDeviceCapability(string id) =>
        string.Equals(id, WindowsNativeCapabilityIds.Scanner, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, WindowsNativeCapabilityIds.Printer, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id, WindowsNativeCapabilityIds.OpticalMedia, StringComparison.OrdinalIgnoreCase);
}

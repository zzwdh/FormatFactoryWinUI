using System.Runtime.InteropServices;
using System.Text;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private static readonly (uint Command, string Target, string Label)[] TrayDestinations =
    [
        (1010, "all", "QueueTitle"),
        (1101, "video", "NavVideo"), (1102, "audio", "NavAudio"),
        (1103, "image", "NavImage"), (1104, "document", "NavDocument"),
        (1105, "ebook", "NavEbook"), (1106, "disc", "NavDisc"),
        (1107, "archive", "ArchiveTitle"),
        (1020, "history", "NavHistory"), (1021, "automation", "AutomationTitle"),
        (1022, "settings", "NavSettings")
    ];

    private async Task CheckTrayNavigationAsync()
    {
        var previousLanguage = LocalizationService.CurrentLanguageCode;
        Check("Native tray navigation groups conversion pages and localizes actions in every language", () =>
        {
            try
            {
                foreach (var language in LocalizationService.LanguageOptions.Where(option => option.Key != "auto").Select(option => option.Key))
                {
                    LocalizationService.SetLanguage(language);
                    var menu = TrayIconService.CreateContextMenu();
                    Require(menu != IntPtr.Zero, $"The {language} tray menu was not created.");
                    try
                    {
                        Require(TrayMenuNative.GetMenuItemCount(menu) == 10,
                            $"The {language} tray menu lost its compact top-level grouping.");
                        Require(TrayMenuNative.GetMenuDefaultItem(menu, false, 0) == 1001,
                            "Opening the main window is not the default tray action.");
                        foreach (var position in new[] { 1, 4, 8 })
                            Require((TrayMenuNative.GetMenuState(menu, (uint)position, TrayMenuNative.ByPosition) & TrayMenuNative.Separator) != 0,
                                $"The tray menu lost its separator at position {position}.");
                        RequireTrayMenuLabel(menu, 0, "TrayOpenWindow");
                        RequireTrayMenuLabel(menu, 3, "TrayConversions");
                        RequireTrayMenuLabel(menu, 9, "TrayExit");
                        Require(TrayMenuNative.GetMenuItemID(menu, 0) == 1001 && TrayMenuNative.GetMenuItemID(menu, 9) == 1002,
                            "The open-window and exit actions no longer occupy the menu boundaries.");
                        Require(ReadTrayMenuLabel(menu, 9) != LocalizationService.T("StateCancelled"),
                            "The exit action is still mislabeled as a cancelled task.");

                        var conversionMenu = TrayMenuNative.GetSubMenu(menu, 3);
                        Require(conversionMenu != IntPtr.Zero && TrayMenuNative.GetMenuItemCount(conversionMenu) == 7,
                            "Conversion categories are not grouped in one seven-item submenu.");
                        var observed = ReadTrayMenuCommands(menu).ToArray();
                        Require(observed.Select(item => item.Command).Distinct().Count() == observed.Length,
                            "Two tray actions share a command identifier.");
                        Require(observed.Select(item => item.Command).Order()
                            .SequenceEqual(TrayDestinations.Select(item => item.Command).Append(1001u).Append(1002u).Order()),
                            "The native tray menu omitted a destination or exposed an unexpected action.");
                        foreach (var (command, target, label) in TrayDestinations)
                        {
                            var item = observed.Single(item => item.Command == command);
                            Require(item.Text == LocalizationService.T(label).Replace("&", "&&", StringComparison.Ordinal),
                                $"The {language} tray label for {target} is incorrect.");
                            Require(TrayIconService.NavigationTargetForCommand(command) == target,
                                $"The {target} tray command points to another page.");
                            Require(item.IsNested == (command is >= 1101 and <= 1107),
                                $"The {target} tray destination is in the wrong menu group.");
                        }
                        foreach (var command in new uint[] { 0, 42, 1001, 1002, uint.MaxValue })
                            Require(TrayIconService.NavigationTargetForCommand(command) is null,
                                $"Non-navigation command {command} unexpectedly routes to a page.");
                    }
                    finally { TrayMenuNative.DestroyMenu(menu); }
                }
            }
            finally
            {
                LocalizationService.SetLanguage(previousLanguage);
                Invoke("ApplyLocalization");
            }
        });

        var navigation = (NavigationView)Field("NavView")!;
        var previousPage = (navigation.SelectedItem as NavigationViewItem)?.Tag as string ?? "all";
        var taskIds = _window!.Tasks.Select(task => task.Id).ToArray();
        // Production startup is detached by the harness. Its isolated UI is ready.
        ((TaskCompletionSource<bool>)Field("_activationReady")!).TrySetResult(true);
        try
        {
            foreach (var (_, target, _) in TrayDestinations)
            {
                await CheckAsync($"Tray navigation reaches {target} without changing conversion tasks", async () =>
                {
                    await ((Task)Invoke("NavigateFromTrayAsync", target)!).WaitAsync(TimeSpan.FromSeconds(5));
                    Require(navigation.SelectedItem is NavigationViewItem item && Equals(item.Tag, target),
                        $"The tray shortcut did not select {target}.");
                    RequireNavigation(target);
                    Require(taskIds.SequenceEqual(_window.Tasks.Select(task => task.Id)),
                        "Tray navigation changed or imported conversion tasks.");
                });
            }
        }
        finally
        {
            SelectNavigation(previousPage);
            await AwaitNavigationAsync();
        }
    }

    private static void RequireTrayMenuLabel(IntPtr menu, uint position, string key) =>
        Require(ReadTrayMenuLabel(menu, position) == LocalizationService.T(key).Replace("&", "&&", StringComparison.Ordinal),
            $"The tray action {key} is not localized correctly.");

    private static string ReadTrayMenuLabel(IntPtr menu, uint position)
    {
        var text = new StringBuilder(1024);
        TrayMenuNative.GetMenuString(menu, position, text, text.Capacity, TrayMenuNative.ByPosition);
        return text.ToString();
    }

    private static IEnumerable<(uint Command, string Text, bool IsNested)> ReadTrayMenuCommands(IntPtr menu, bool nested = false)
    {
        for (uint position = 0; position < TrayMenuNative.GetMenuItemCount(menu); position++)
        {
            if ((TrayMenuNative.GetMenuState(menu, position, TrayMenuNative.ByPosition) & TrayMenuNative.Separator) != 0) continue;
            var child = TrayMenuNative.GetSubMenu(menu, (int)position);
            if (child != IntPtr.Zero)
            {
                foreach (var item in ReadTrayMenuCommands(child, true)) yield return item;
            }
            else yield return (TrayMenuNative.GetMenuItemID(menu, (int)position), ReadTrayMenuLabel(menu, position), nested);
        }
    }

    private static class TrayMenuNative
    {
        internal const uint ByPosition = 0x400;
        internal const uint Separator = 0x800;

        [DllImport("user32.dll")]
        internal static extern int GetMenuItemCount(IntPtr menu);
        [DllImport("user32.dll")]
        internal static extern uint GetMenuItemID(IntPtr menu, int position);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetSubMenu(IntPtr menu, int position);
        [DllImport("user32.dll")]
        internal static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
        [DllImport("user32.dll")]
        internal static extern uint GetMenuDefaultItem(IntPtr menu, [MarshalAs(UnmanagedType.Bool)] bool byPosition, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int capacity, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(IntPtr menu);
    }
}

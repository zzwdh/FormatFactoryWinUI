using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using FormatFactoryWinUI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public partial class App
{
    private async Task CheckArchiveConversionAsync()
    {
        var language = LocalizationService.CurrentLanguageCode;
        var fixture = Path.Combine(_runRoot, "archive-conversion", Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(fixture, "source");
        var outputDirectory = Path.Combine(fixture, "output");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(outputDirectory);
        var engine = FindArchiveSmokeEngine();
        var good = Path.Combine(sourceDirectory, "archive-one.zip");
        var second = Path.Combine(sourceDirectory, "archive-two.zip");
        var broken = Path.Combine(sourceDirectory, "broken.zip");
        CreateArchiveSmokeZip(good);
        CreateArchiveSmokeZip(second);
        await File.WriteAllTextAsync(broken, "Synthetic damaged archive; no user data.");
        var originalGood = await File.ReadAllBytesAsync(good);
        var view = new ArchiveConversionView { VerticalAlignment = VerticalAlignment.Top };
        var host = new Grid { Padding = new Thickness(18), RequestedTheme = ElementTheme.Light };
        host.Children.Add(view);
        var shell = new Window { Content = host };
        Func<ContentDialog, Task>? dialogAction = null;
        Exception? dialogError = null;
        var dialogCount = 0;
        try
        {
            shell.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            shell.AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 1040));
            shell.AppWindow.Show(false);
            await ArchiveSmokeWaitAsync(() => host.IsLoaded && host.XamlRoot is not null);
            var scale = host.XamlRoot!.RasterizationScale;
            shell.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(1060 * scale), (int)(1400 * scale)));
            var dispatcher = view.DispatcherQueue;
            view.Configure(() => WinRT.Interop.WindowNative.GetWindowHandle(shell), async (dialog, cancellationToken) =>
            {
                dialogCount++;
                var action = dialogAction;
                dialog.Opened += (_, _) => dialog.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        dialog.UpdateLayout();
                        if (action is not null) await action(dialog);
                    }
                    catch (Exception exception)
                    {
                        dialogError = exception;
                        dialog.Hide();
                    }
                });
                using var cancellation = cancellationToken.Register(() => dispatcher.TryEnqueue(() => dialog.Hide()));
                return await dialog.ShowAsync();
            }, new ArchiveConversionService(engine));
            view.OutputFolderBox.Text = outputDirectory;

            await CheckAsync("Archive conversion entry and real controls localize without changing defaults", async () =>
            {
                var archivePage = (ScrollViewer)Field("ArchivePage")!;
                Require(Field("ArchiveView") is ArchiveConversionView converter && ReferenceEquals(archivePage.Content, converter),
                    "The archive navigation page does not expose its converter directly.");
                foreach (var code in new[] { "en-US", "zh-Hans", "zh-Hant", "da-DK" })
                {
                    LocalizationService.SetLanguage(code);
                    view.ApplyLocalization();
                    Require(((TextBlock)view.FindName("TitleText")).Text == LocalizationService.T("ArchiveTitle") &&
                        Equals(ArchiveSmokeButton(view, "AddButton").Content, LocalizationService.T("ArchiveAdd")) &&
                        !string.IsNullOrWhiteSpace((string)view.OutputFolderBox.Header),
                        "Archive controls did not resolve the selected language.");
                }
                LocalizationService.SetLanguage("zh-Hans");
                view.ApplyLocalization();
                var shown = dialogCount;
                dialogAction = dialog =>
                {
                    var panel = (StackPanel)dialog.Content;
                    var format = panel.Children.OfType<ComboBox>().Single();
                    var flags = panel.Children.OfType<CheckBox>().ToArray();
                    Require((ArchiveFormat)((ComboBoxItem)format.SelectedItem).Tag == ArchiveFormat.Zip &&
                        flags.All(flag => flag.IsChecked == false) &&
                        panel.Children.OfType<PasswordBox>().All(box => box.Password.Length == 0),
                        "A new archive batch did not default to ZIP, standard verification and no output encryption.");
                    dialog.Hide();
                    return Task.CompletedTask;
                };
                ArchiveSmokeInvoke(ArchiveSmokeButton(view, "BatchSettingsButton"));
                await ArchiveSmokeWaitAsync(() => dialogCount > shown && !ArchiveSmokeEditing(view));
                Require(dialogError is null, dialogError?.ToString() ?? "Archive default settings failed.");
            });

            await CheckAsync("Archive batch settings apply once and the task context menu edits only its archive", async () =>
            {
                view.AddInputs(new[] { good, second, good }, new(ArchiveFormat.Zip));
                Require(view.Tasks.Count == 2, "Adding a duplicate source created another archive task.");
                var shown = dialogCount;
                dialogAction = dialog =>
                {
                    var panel = (StackPanel)dialog.Content;
                    var format = panel.Children.OfType<ComboBox>().Single();
                    format.SelectedIndex = (int)ArchiveFormat.SevenZip;
                    panel.Children.OfType<CheckBox>().First().IsChecked = true;
                    panel.Children.OfType<CheckBox>().Last().IsChecked = true;
                    foreach (var box in panel.Children.OfType<PasswordBox>()) box.Password = "synthetic-output-password";
                    ArchiveSmokeInvoke(FindFeedbackPrimaryButton(dialog));
                    return Task.CompletedTask;
                };
                ArchiveSmokeInvoke(ArchiveSmokeButton(view, "BatchSettingsButton"));
                await ArchiveSmokeWaitAsync(() => dialogCount > shown && !ArchiveSmokeEditing(view));
                Require(dialogError is null && view.Tasks.All(task => task.Options.TargetFormat == ArchiveFormat.SevenZip &&
                    task.Options.StrictVerification && task.Options.OutputPassword == "synthetic-output-password"),
                    dialogError?.ToString() ?? "Batch settings were not applied to every waiting task.");
                shown = dialogCount;
                dialogAction = dialog =>
                {
                    Require(Equals(dialog.Title, LocalizationService.T("ArchiveTaskSettings")),
                        "Editing a single task showed the batch settings title.");
                    var panel = (StackPanel)dialog.Content;
                    panel.Children.OfType<CheckBox>().Last().IsChecked = false;
                    panel.Children.OfType<ComboBox>().Single().SelectedIndex = (int)ArchiveFormat.TarGZip;
                    panel.Children.OfType<CheckBox>().First().IsChecked = false;
                    ArchiveSmokeInvoke(FindFeedbackPrimaryButton(dialog));
                    return Task.CompletedTask;
                };
                host.UpdateLayout();
                await Task.Delay(50);
                var task = view.Tasks[0];
                var container = (ListViewItem)view.TaskList.ContainerFromItem(task);
                var card = FindDescendants<StackPanel>(container).Single(panel => panel.ContextFlyout is MenuFlyout);
                var menu = await OpenTaskContextMenuAsync(card);
                var edit = menu.Items.OfType<MenuFlyoutItem>().Single(action => action.Text == LocalizationService.T("ArchiveTaskSettings"));
                Require(edit.IsEnabled && ReferenceEquals(edit.Tag, task) &&
                    !menu.Items.OfType<MenuFlyoutItem>().Single(action => action.Text == LocalizationService.T("ArchiveOpenOutput")).IsEnabled,
                    "The archive menu did not use the waiting task's action state.");
                InvokeTaskContextMenuItem(edit);
                await ArchiveSmokeWaitAsync(() => dialogCount > shown && !ArchiveSmokeEditing(view));
                Require(dialogError is null && view.Tasks[0].Options == new ArchiveConversionOptions(ArchiveFormat.TarGZip) &&
                    view.Tasks[1].Options.TargetFormat == ArchiveFormat.SevenZip && view.Tasks[1].HasEncryption,
                    dialogError?.ToString() ?? "Editing one archive changed another task's settings.");
            });

            await CheckAsync("Unsupported encrypted output is blocked until the user explicitly disables encryption", async () =>
            {
                var shown = dialogCount;
                dialogAction = async dialog =>
                {
                    var panel = (StackPanel)dialog.Content;
                    var encryption = panel.Children.OfType<CheckBox>().Last();
                    Require(encryption.IsChecked == true, "The encrypted task lost its output encryption setting.");
                    panel.Children.OfType<ComboBox>().Single().SelectedIndex = (int)ArchiveFormat.Tar;
                    Require(encryption.IsChecked == true,
                        "Selecting a format without encryption silently switched encryption off.");
                    ArchiveSmokeInvoke(FindFeedbackPrimaryButton(dialog));
                    await Task.Delay(60);
                    Require(dialog.IsLoaded && ((TextBlock)panel.Children.Last()).Text == LocalizationService.T("ArchiveEncryptionUnavailable"),
                        "An unsupported encryption setting was accepted or lacked a readable explanation.");
                    encryption.IsChecked = false;
                    ArchiveSmokeInvoke(FindFeedbackPrimaryButton(dialog));
                };
                host.UpdateLayout();
                ArchiveSmokeInvoke(ArchiveSmokeTaskButton(view, view.Tasks[1], "ArchiveTaskSettings"));
                await ArchiveSmokeWaitAsync(() => dialogCount > shown && !ArchiveSmokeEditing(view));
                Require(dialogError is null && view.Tasks[1].Options.TargetFormat == ArchiveFormat.Tar && !view.Tasks[1].HasEncryption,
                    dialogError?.ToString() ?? "Explicitly disabling encryption did not allow TAR output.");
                view.Session.Tasks.Clear();
            });

            await CheckAsync("ZIP output password restrictions are visible and Chinese output passwords remain available through 7Z", async () =>
            {
                view.AddInputs(new[] { good }, new(ArchiveFormat.Zip));
                var shown = dialogCount;
                dialogAction = async dialog =>
                {
                    var panel = (StackPanel)dialog.Content;
                    panel.Children.OfType<CheckBox>().Last().IsChecked = true;
                    foreach (var box in panel.Children.OfType<PasswordBox>()) box.Password = "\u6d4b\u8bd5\u5bc6\u7801";
                    ArchiveSmokeInvoke(FindFeedbackPrimaryButton(dialog));
                    await Task.Delay(60);
                    Require(dialog.IsLoaded && ((TextBlock)panel.Children.Last()).Text == LocalizationService.T("ArchiveZipPasswordRules"),
                        "ZIP accepted an unsupported output password or failed to explain how to use a Chinese password.");
                    panel.Children.OfType<ComboBox>().Single().SelectedIndex = (int)ArchiveFormat.SevenZip;
                    ArchiveSmokeInvoke(FindFeedbackPrimaryButton(dialog));
                };
                host.UpdateLayout();
                await Task.Delay(30);
                ArchiveSmokeInvoke(ArchiveSmokeTaskButton(view, view.Tasks.Single(), "ArchiveTaskSettings"));
                await ArchiveSmokeWaitAsync(() => dialogCount > shown && !ArchiveSmokeEditing(view));
                Require(dialogError is null && view.Tasks.Single().Options.TargetFormat == ArchiveFormat.SevenZip &&
                    view.Tasks.Single().Options.OutputPassword == "\u6d4b\u8bd5\u5bc6\u7801",
                    dialogError?.ToString() ?? "7Z did not retain the explicitly chosen Chinese output password.");
                view.Session.Tasks.Clear();
            });

            await CheckAsync("Selecting several real archive volumes imports exactly one logical task", async () =>
            {
                var payload = Path.Combine(sourceDirectory, "volume-payload.bin");
                await File.WriteAllBytesAsync(payload, System.Security.Cryptography.RandomNumberGenerator.GetBytes(240 * 1024));
                var split = Path.Combine(sourceDirectory, "volumes.7z");
                await RunArchiveSmokeEngineAsync(engine, sourceDirectory, "a", "-t7z", "-v64k", "-mx=0", "-y", split, payload);
                var volumes = Directory.GetFiles(sourceDirectory, "volumes.7z.*").Order(StringComparer.OrdinalIgnoreCase).ToArray();
                Require(volumes.Length > 1, "The synthetic multivolume fixture was not split.");
                view.AddInputs(volumes.Reverse().Concat(new[] { volumes[0] }), new(ArchiveFormat.Zip));
                Require(view.Tasks.Count == 1 && view.Tasks[0].SourcePath.EndsWith(".7z.001", StringComparison.OrdinalIgnoreCase),
                    "Volumes were not normalized to the first volume and deduplicated.");
                await view.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(view.Tasks[0].State == ArchiveTaskState.Succeeded && File.Exists(view.Tasks[0].OutputPath),
                    "The complete volume set did not become one verified output archive.");
                await ArchiveSmokeWaitAsync(() => ArchiveSmokeButton(view, "ClearButton").IsEnabled);
                ArchiveSmokeInvoke(ArchiveSmokeButton(view, "ClearButton"));
                Require(view.Tasks.Count == 0 && volumes.All(File.Exists),
                    "Clearing completed work removed source volumes or retained the finished row.");
            });

            await CheckAsync("A bad archive fails while the next real archive still converts and keeps its source bytes", async () =>
            {
                view.AddInputs(new[] { broken, good }, new(ArchiveFormat.SevenZip, StrictVerification: true));
                await view.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(view.Tasks.Count == 2 && view.Tasks[0].State == ArchiveTaskState.Failed &&
                    view.Tasks[1].State == ArchiveTaskState.Succeeded && File.Exists(view.Tasks[1].OutputPath),
                    "One damaged archive interrupted the rest of its batch or was reported as successful.");
                Require((await File.ReadAllBytesAsync(good)).SequenceEqual(originalGood) && File.Exists(broken) &&
                    view.Tasks[0].Status != "ArchiveErrorCorruptArchive" && !view.IsRunning,
                    "Archive conversion modified its source, exposed a resource key, or stayed busy after finishing.");
                await ArchiveSmokeWaitAsync(() => ArchiveSmokeButton(view, "ClearButton").IsEnabled);
                if (_capture)
                {
                    await Task.Delay(500); // Let the native progress animation reach its completed value.
                    await SaveResultSummaryCaptureAsync(view, "archive-conversion-results");
                }
                ArchiveSmokeInvoke(ArchiveSmokeButton(view, "ClearButton"));
                Require(view.Tasks.Count == 0, "Finished archive tasks were not cleared.");
            });

            var encrypted = Path.Combine(sourceDirectory, "encrypted.7z");
            const string fixturePassword = "synthetic-source-password";
            await RunArchiveSmokeEngineAsync(engine, sourceDirectory, "a", "-t7z", "-mhe=on", "-p" + fixturePassword,
                "-y", encrypted, Path.GetFileName(good));

            await CheckAsync("A real encrypted archive opens the password controls and allows a wrong-password retry", async () =>
            {
                var requests = 0;
                var passwordBoxes = new List<PasswordBox>();
                dialogAction = dialog =>
                {
                    requests++;
                    var panel = (StackPanel)dialog.Content;
                    Require(Equals(dialog.Title, LocalizationService.T("ArchiveSourcePasswordTitle")),
                        "The encrypted source did not show the password dialog.");
                    if (requests > 1)
                        Require(panel.Children.OfType<TextBlock>().Any(text => text.Text == LocalizationService.T("ArchiveWrongPassword")),
                            "The repeated password prompt did not explain the wrong password.");
                    var password = panel.Children.OfType<PasswordBox>().Single();
                    passwordBoxes.Add(password);
                    password.Password = requests == 1 ? "synthetic-wrong-password" : fixturePassword;
                    ArchiveSmokeInvoke(FindFeedbackPrimaryButton(dialog));
                    return Task.CompletedTask;
                };
                view.AddInputs(new[] { encrypted }, new(ArchiveFormat.Zip));
                await view.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(dialogError is null && requests >= 2 && view.Tasks.Single().State == ArchiveTaskState.Succeeded &&
                    passwordBoxes.All(box => box.Password.Length == 0),
                    dialogError?.ToString() ?? "Password retry failed or the real password control retained its secret.");
                await ArchiveSmokeWaitAsync(() => ArchiveSmokeButton(view, "ClearButton").IsEnabled);
                ArchiveSmokeInvoke(ArchiveSmokeButton(view, "ClearButton"));
            });

            await CheckAsync("Cancelling while waiting for a password closes the dialog and leaves the tool ready for more work", async () =>
            {
                var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                PasswordBox? passwordBox = null;
                dialogAction = dialog =>
                {
                    passwordBox = ((StackPanel)dialog.Content).Children.OfType<PasswordBox>().Single();
                    passwordBox.Password = "synthetic-cancelled-password";
                    opened.TrySetResult(true);
                    return Task.CompletedTask;
                };
                view.AddInputs(new[] { encrypted, second },
                    new(ArchiveFormat.SevenZip, OutputPassword: "synthetic-pending-output-password"));
                var run = view.StartAsync();
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(view.Tasks[0].State == ArchiveTaskState.AwaitingPassword &&
                    view.Tasks[1].State == ArchiveTaskState.Waiting && view.IsRunning,
                    "The task did not enter the visible password-wait state.");
                await view.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await run;
                await Task.Delay(100);
                Require(view.Tasks.All(task => task.State == ArchiveTaskState.Cancelled &&
                    task.Options.OutputPassword is null && task.HasEncryption) && !view.IsRunning &&
                    passwordBox?.Password.Length == 0 && ArchiveSmokeButton(view, "AddButton").IsEnabled,
                    "Cancellation retained an active or pending password, lost the encryption marker, disabled controls, or a late callback restored Running state.");
                var shown = dialogCount;
                dialogAction = async dialog =>
                {
                    var panel = (StackPanel)dialog.Content;
                    Require(panel.Children.OfType<CheckBox>().Last().IsChecked == true &&
                        panel.Children.OfType<PasswordBox>().All(box => box.Password.Length == 0),
                        "Retry did not retain encryption while requiring a fresh output password.");
                    ArchiveSmokeInvoke(FindFeedbackPrimaryButton(dialog));
                    await Task.Delay(60);
                    Require(dialog.IsLoaded && ((TextBlock)panel.Children.Last()).Text == LocalizationService.T("ArchivePasswordEmpty"),
                        "An encrypted retry accepted an empty output password after cancellation.");
                    dialog.Hide();
                };
                host.UpdateLayout();
                ArchiveSmokeInvoke(ArchiveSmokeTaskButton(view, view.Tasks[0], "ArchiveRetry"));
                await ArchiveSmokeWaitAsync(() => dialogCount > shown && !ArchiveSmokeEditing(view));
                Require(dialogError is null && view.Tasks.All(task => task.State == ArchiveTaskState.Cancelled),
                    dialogError?.ToString() ?? "Dismissing retry settings unexpectedly resumed cancelled tasks.");
                ArchiveSmokeInvoke(ArchiveSmokeButton(view, "ClearButton"));
                view.AddInputs(new[] { second }, new(ArchiveFormat.TarGZip));
                await view.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Require(view.Tasks.Single().State == ArchiveTaskState.Succeeded,
                    "The archive tool could not convert another archive after cancelling a password request.");
                await ArchiveSmokeWaitAsync(() => ArchiveSmokeButton(view, "ClearButton").IsEnabled);
                if (_capture)
                {
                    await Task.Delay(500);
                    await SaveResultSummaryCaptureAsync(view, "archive-conversion-ready");
                }
            });

        }
        finally
        {
            await view.StopAsync();
            view.Dispose();
            shell.Close();
            LocalizationService.SetLanguage(language);
        }
    }

    private static string FindArchiveSmokeEngine()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var engine = Path.Combine(root.FullName, "work", "prepared-archive", "x64", "Engines", "7zip", "7z.exe");
            if (File.Exists(engine)) return engine;
            root = root.Parent;
        }
        throw new FileNotFoundException("The archive smoke test requires the prepared x64 7-Zip engine.");
    }

    private static void CreateArchiveSmokeZip(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        archive.CreateEntry("empty-directory/");
        using var writer = new StreamWriter(archive.CreateEntry("nested/content.txt").Open());
        writer.Write("Synthetic archive conversion smoke fixture.");
    }

    private static async Task RunArchiveSmokeEngineAsync(string executable, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the archive fixture engine.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await output;
        await error;
        Require(process.ExitCode == 0, "The archive fixture engine failed.");
    }

    private static Button ArchiveSmokeButton(ArchiveConversionView view, string name) => (Button)view.FindName(name);

    private static Button ArchiveSmokeTaskButton(ArchiveConversionView view, ArchiveTaskItem task, string labelKey)
    {
        var row = view.TaskList.ContainerFromItem(task) as DependencyObject
            ?? throw new InvalidOperationException("The archive task row was not realized.");
        return FindDescendants<Button>(row).Single(button => Equals(button.Content, LocalizationService.T(labelKey)));
    }

    private static bool ArchiveSmokeEditing(ArchiveConversionView view) =>
        (bool)typeof(ArchiveConversionView).GetField("_editing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static void ArchiveSmokeInvoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task<MenuFlyout> OpenTaskContextMenuAsync(FrameworkElement card)
    {
        var menu = card.ContextFlyout as MenuFlyout ?? throw new InvalidOperationException("The card has no context menu.");
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(object? sender, object args) => opened.TrySetResult();
        menu.Opened += OnOpened;
        try
        {
            menu.ShowAt(card);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return menu;
        }
        finally { menu.Opened -= OnOpened; }
    }

    private static void InvokeTaskContextMenuItem(MenuFlyoutItem item) =>
        ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task ArchiveSmokeWaitAsync(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!condition() && DateTimeOffset.UtcNow < until) await Task.Delay(25);
        Require(condition(), "Timed out waiting for the live archive controls.");
    }
}

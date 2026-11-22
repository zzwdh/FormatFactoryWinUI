using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private async void OutputSettings_Click(object sender, RoutedEventArgs args)
    {
        if (_windowClosed || _initializing || _closeSaveInProgress || _desktopCommandInProgress
            || _settingsResetInProgress || _queueSession.IsStopping || _reviewBusinessDialogs > 0) return;
        try { await ShowContentDialogAsync(CreateOutputSettingsDialog()); }
        catch (Exception exception)
        {
            SafeDiagnostics.Report(exception);
            if (!_windowClosed) ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
    }

    private ContentDialog CreateOutputSettingsDialog()
    {
        var preserve = new ToggleSwitch
        {
            Header = LocalizationService.T("DesktopUpgradePreserveFolders"),
            OnContent = LocalizationService.T("HardwareOn"),
            OffContent = LocalizationService.T("HardwareOff"),
            IsOn = _settings.PreserveFolderStructure
        };
        var template = new TextBox
        {
            Header = LocalizationService.T("DesktopUpgradeNamingTemplate"),
            Text = OutputPathService.NormalizeFileNameTemplate(_settings.OutputFileNameTemplate),
            MaxLength = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(preserve, LocalizationService.T("DesktopUpgradePreserveFolders"));
        AutomationProperties.SetName(template, LocalizationService.T("DesktopUpgradeNamingTemplate"));
        var patterns = new ComboBox
        {
            Header = LocalizationService.T("NamingPatterns"),
            PlaceholderText = LocalizationService.T("NamingPatterns"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var pattern in new[] { "{name}", "{name}_{preset}_{format}", "{name}_{width}x{height}", "{date}_{name}" })
            patterns.Items.Add(pattern);
        patterns.SelectionChanged += (_, _) =>
        {
            if (patterns.SelectedItem is string pattern) template.Text = pattern;
        };
        var tokens = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var token in new[] { "{name}", "{format}", "{date}", "{preset}", "{width}", "{height}" })
        {
            var button = new Button { Content = token, Padding = new Thickness(6, 4, 6, 4) };
            AutomationProperties.SetName(button, LocalizationService.T("NamingInsertToken", token));
            button.Click += (_, _) =>
            {
                var start = template.SelectionStart;
                template.SelectedText = token;
                template.Select(Math.Min(start + token.Length, template.Text.Length), 0);
                template.Focus(FocusState.Programmatic);
            };
            tokens.Children.Add(button);
        }
        var selected = ConverterView.SelectedTasks.FirstOrDefault()?.Model ?? _queueSession.Tasks.FirstOrDefault();
        var previewSources = selected?.OutputGroupId is { } groupId
            ? _queueSession.Tasks.Where(task => task.OutputGroupId == groupId).OrderBy(task => task.OutputOrder).Take(3).ToArray()
            : selected is null ? [] : new[] { selected };
        var sourceText = new TextBlock
        {
            Text = selected is null ? LocalizationService.T("NamingSamplePreview")
                : LocalizationService.T("NamingSourcePreview", selected.FileName),
            TextWrapping = TextWrapping.Wrap
        };
        var preview = new TextBlock
        {
            Name = "NamingPathPreview", TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, FlowDirection = FlowDirection.LeftToRight
        };
        AutomationProperties.SetName(preview, LocalizationService.T("NamingPathPreview"));
        AutomationProperties.SetLiveSetting(preview, AutomationLiveSetting.Polite);
        var feedback = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Warning };
        var content = new StackPanel { Spacing = 10, MaxWidth = 520 };
        content.Children.Add(preserve);
        content.Children.Add(patterns);
        content.Children.Add(template);
        content.Children.Add(tokens);
        content.Children.Add(new TextBlock
        {
            Text = LocalizationService.T("NamingTemplateHelp"),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(sourceText);
        content.Children.Add(new ScrollViewer { Content = preview, MaxHeight = 112,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        content.Children.Add(new TextBlock { Text = LocalizationService.T("NamingPreviewNote"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(feedback);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            FlowDirection = RootGrid.FlowDirection,
            Title = LocalizationService.T("DesktopUpgradeOutputSettings"),
            Content = content,
            PrimaryButtonText = LocalizationService.T("SaveSettings"),
            CloseButtonText = LocalizationService.T("NativeUiCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        CancellationTokenSource? pendingPreview = null;
        var closed = false;
        async void UpdatePreview()
        {
            if (closed) return;
            pendingPreview?.Cancel();
            pendingPreview?.Dispose();
            pendingPreview = new CancellationTokenSource();
            var token = pendingPreview.Token;
            if (!dialog.IsPrimaryButtonEnabled) { preview.Text = string.Empty; return; }
            var settings = _settings.Snapshot();
            settings.OutputFileNameTemplate = template.Text;
            settings.PreserveFolderStructure = preserve.IsOn;
            var sources = previewSources.Length > 0 ? previewSources.Select(task => new MediaTask
            {
                InputPath = task.InputPath, SourceRoot = task.SourceRoot, ContentProbe = task.ContentProbe,
                ContentKind = task.ContentKind, SourceFormatId = task.SourceFormatId,
                TargetFormatId = task.TargetFormatId, ConversionMode = task.ConversionMode, OutputLabel = task.OutputLabel
            }).ToArray() : [new MediaTask
            {
                InputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "example.mov"),
                Probe = new MediaProbeResult { Width = 1920, Height = 1080 }, ContentKind = ContentKind.Video,
                TargetFormatId = "mp4"
            }];
            preview.Text = LocalizationService.T("NamingPreviewLoading");
            try
            {
                // Network destinations can be slow. Debounce typing and inspect paths away from the UI thread.
                await Task.Delay(180, token);
                var paths = await Task.Run(() => new OutputPathService().PreviewMany(sources, settings, token), token);
                if (closed || token.IsCancellationRequested) return;
                preview.Text = string.Join(Environment.NewLine, paths.Select(path => path.FinalPath));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                if (!closed && !token.IsCancellationRequested) preview.Text = LocalizationService.T("NamingPreviewUnavailable");
            }
        }
        void Validate()
        {
            var error = OutputPathService.ValidateFileNameTemplate(template.Text);
            dialog.IsPrimaryButtonEnabled = error is null;
            feedback.Message = error;
            feedback.IsOpen = error is not null;
            UpdatePreview();
        }
        template.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => Validate());
        preserve.Toggled += (_, _) => Validate();
        dialog.Closed += (_, _) => { closed = true; pendingPreview?.Cancel(); pendingPreview?.Dispose(); };
        dialog.PrimaryButtonClick += async (_, click) =>
        {
            var deferral = click.GetDeferral();
            try
            {
                Validate();
                if (!dialog.IsPrimaryButtonEnabled) { click.Cancel = true; return; }
                var name = OutputPathService.NormalizeFileNameTemplate(template.Text);
                var keepFolders = preserve.IsOn;
                if (!await CommitImmediateSettingAsync(settings =>
                    {
                        settings.OutputFileNameTemplate = name;
                        settings.PreserveFolderStructure = keepFolders;
                    }))
                {
                    click.Cancel = true;
                    feedback.Message = SettingsView.ViewModel.SaveMessage;
                    feedback.IsOpen = true;
                    return;
                }
                if (!_windowClosed) ShowInfo(LocalizationService.T("DesktopUpgradeOutputSaved"), InfoBarSeverity.Success);
            }
            catch (Exception exception)
            {
                click.Cancel = true;
                SafeDiagnostics.Report(exception);
                feedback.Message = exception.Message;
                feedback.IsOpen = true;
            }
            finally { deferral.Complete(); }
        };
        Validate();
        return dialog;
    }
}

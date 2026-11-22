using System.Collections.ObjectModel;
using FormatFactoryWinUI.ApplicationServices;
using FormatFactoryWinUI.Models;
using FormatFactoryWinUI.Motion;
using FormatFactoryWinUI.Services;
using Microsoft.UI.Dispatching;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.System;

namespace FormatFactoryWinUI.Views;

public sealed partial class ArchiveConversionView : UserControl, IDisposable
{
    private Func<IntPtr>? _windowHandle;
    private Func<ContentDialog, CancellationToken, Task<ContentDialogResult>>? _showDialog;
    private Func<Task<string?>>? _chooseOutputFolder;
    private ArchiveSession _session = new();
    private ArchiveConversionOptions _defaults
    { get => _session.Defaults; set => _session.SetDefaults(value, _session.DefaultEncryptOutput); }
    private bool _defaultsEncrypted
    { get => _session.DefaultEncryptOutput; set => _session.SetDefaults(_session.Defaults, value); }
    private bool _restoring => _session.IsRestoring;
    private bool _persistenceReady => _session.CanPersist;
    private ContentDialog? _dialog;
    private readonly DispatcherQueue _dispatcher;
    private bool _editing;
    private bool _disposed;
    private long _statusMotionVersion;
    private readonly ObservableCollection<ArchiveTaskItem> _tasks = [];
    internal ReadOnlyObservableCollection<ArchiveTaskItem> Tasks { get; }
    internal ArchiveSession Session => _session;
    internal bool IsRunning => _session.IsRunning;

    public ArchiveConversionView()
    {
        Tasks = new(_tasks);
        InitializeComponent();
        _dispatcher = DispatcherQueue;
        _session.Dispose();
        _session = new ArchiveSession(dispatch: DispatchAsync);
        TaskList.ItemsSource = Tasks;
        AttachSession();
        ApplyLocalization();
    }

    internal void Configure(Func<IntPtr> windowHandle,
        Func<ContentDialog, CancellationToken, Task<ContentDialogResult>> showDialog, ArchiveConversionService? converter = null,
        ArchiveTaskStore? taskStore = null, Func<ConversionHistoryItem, CancellationToken, Task>? recordHistory = null,
        Func<Task<string?>>? chooseOutputFolder = null, ArchiveCommitReceiptStore? commitReceipts = null)
    {
        _windowHandle = windowHandle;
        _showDialog = showDialog;
        DetachSession();
        _session.Dispose();
        _session = new ArchiveSession(converter, taskStore, recordHistory, commitReceipts, DispatchAsync);
        _restoreTask = null;
        _usesPersistence = taskStore is not null;
        AttachSession();
        _chooseOutputFolder = chooseOutputFolder;
        RefreshControls();
    }

    internal void ApplyLocalization()
    {
        TitleText.Text = T("ArchiveTitle");
        DescriptionText.Text = T("ArchiveDescription");
        OutputFolderBox.Header = T("ArchiveOutputFolder");
        BrowseButton.Content = T("ArchiveBrowse");
        AddButton.Content = T("ArchiveAdd");
        BatchSettingsButton.Content = T("ArchiveBatchSettings");
        StartButton.Content = T("ArchiveStart");
        CancelButton.Content = T("ArchiveCancel");
        ClearButton.Content = T("ArchiveClear");
        VolumeHintText.Text = T("ArchiveVolumeHint");
        EmptyText.Text = T("ArchiveEmpty");
        AutomationProperties.SetName(TaskList, T("ArchiveTitle"));
        foreach (var task in Tasks) task.Refresh();
        RefreshControls();
    }

    internal void AddInputs(IEnumerable<string> paths, ArchiveConversionOptions options) =>
        _session.AddInputs(paths, options);

    private async void Add_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || IsRunning || _editing || _windowHandle is null) return;
        _editing = true;
        RefreshControls();
        try
        {
            var picker = new FileOpenPicker();
            // Include all volume suffixes; the engine validates and normalizes each selection.
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
            var files = await picker.PickMultipleFilesAsync();
            if (_disposed || files.Count == 0) return;
            var options = await EditSettingsAsync(_defaults, _defaultsEncrypted);
            if (_disposed || options is null) return;
            _defaults = options;
            _defaultsEncrypted = !string.IsNullOrEmpty(options.OutputPassword);
            AddInputs(files.Select(file => file.Path), options);
            if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
                OutputFolderBox.Text = Path.GetDirectoryName(files[0].Path) ?? string.Empty;
        }
        catch (Exception) { ShowStatus(T("ArchiveUnsupportedFiles"), InfoBarSeverity.Error); }
        finally { _editing = false; RefreshControls(); }
    }

    private async void Browse_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || IsRunning || _editing || _windowHandle is null) return;
        _editing = true;
        RefreshControls();
        try
        {
            var folder = await PickOutputFolderAsync();
            if (!_disposed && folder is not null) OutputFolderBox.Text = folder;
        }
        catch (Exception) { ShowStatus(T("ArchiveOutputFolderRequired"), InfoBarSeverity.Error); }
        finally { _editing = false; RefreshControls(); }
    }

    private async Task<string?> PickOutputFolderAsync()
    {
        if (_chooseOutputFolder is not null) return await _chooseOutputFolder();
        if (_windowHandle is null) return null;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void ChooseTaskOutput_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || IsRunning || _editing || _restoring ||
            (sender as FrameworkElement)?.Tag is not ArchiveTaskItem { CanChooseOutputFolder: true } task) return;
        _editing = true;
        RefreshControls();
        try
        {
            var folder = await PickOutputFolderAsync();
            if (_disposed || string.IsNullOrWhiteSpace(folder) || !Tasks.Contains(task)) return;
            OutputFolderBox.Text = folder;
            task.PrepareRetryInFolder(folder);
            ShowStatus(T("ArchiveOutputChangedReady"), InfoBarSeverity.Informational);
            QueueCheckpoint();
        }
        catch (Exception exception)
        { SafeDiagnostics.Report(exception); ShowStatus(T("ArchiveOutputFolderRequired"), InfoBarSeverity.Warning); }
        finally { _editing = false; RefreshControls(); }
    }

    private async void BatchSettings_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || IsRunning || _editing) return;
        _editing = true;
        RefreshControls();
        try
        {
            var options = await EditSettingsAsync(_defaults, _defaultsEncrypted);
            if (_disposed || options is null) return;
            _defaults = options;
            _defaultsEncrypted = !string.IsNullOrEmpty(options.OutputPassword);
            foreach (var task in Tasks.Where(task => task.State != ArchiveTaskState.Succeeded)) task.SetOptions(options);
            ShowStatus(T("ArchiveBatchApplied"), InfoBarSeverity.Informational);
            QueueCheckpoint();
        }
        finally { _editing = false; RefreshControls(); }
    }

    private async void TaskSettings_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || _editing || IsRunning || (sender as FrameworkElement)?.Tag is not ArchiveTaskItem task) return;
        _editing = true;
        RefreshControls();
        try
        {
            var options = await EditSettingsAsync(task.Options, task.HasEncryption, singleTask: true);
            if (!_disposed && options is not null) { task.SetOptions(options); QueueCheckpoint(); }
        }
        finally { _editing = false; RefreshControls(); }
    }

    private async Task<ArchiveConversionOptions?> EditSettingsAsync(ArchiveConversionOptions current, bool encrypted = false, bool singleTask = false)
    {
        if (_disposed || XamlRoot is null) return null;
        var format = new ComboBox { Header = T("ArchiveTargetFormat"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in Enum.GetValues<ArchiveFormat>())
            format.Items.Add(new ComboBoxItem { Content = ArchiveFormats.DisplayName(value), Tag = value });
        format.SelectedIndex = (int)current.TargetFormat;
        var strict = new CheckBox { Content = T("ArchiveStrictVerification"), IsChecked = current.StrictVerification };
        var encryption = new CheckBox { Content = T("ArchiveEncryptOutput"), IsChecked = encrypted || !string.IsNullOrEmpty(current.OutputPassword) };
        var password = new PasswordBox { Header = T("ArchiveOutputPassword"), Password = current.OutputPassword ?? string.Empty, MaxLength = 1024 };
        var confirm = new PasswordBox { Header = T("ArchiveConfirmPassword"), Password = current.OutputPassword ?? string.Empty, MaxLength = 1024 };
        var encryptionNote = new TextBlock { Text = T("ArchiveEncryptionUnavailable"), TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12, MinWidth = 260, MaxWidth = 480 };
        panel.Children.Add(format);
        panel.Children.Add(strict);
        panel.Children.Add(new TextBlock { Text = T("ArchiveStrictDescription"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(encryption);
        panel.Children.Add(encryptionNote);
        panel.Children.Add(password);
        panel.Children.Add(confirm);
        panel.Children.Add(error);
        ArchiveFormat SelectedFormat() => (ArchiveFormat)((ComboBoxItem)format.SelectedItem).Tag;
        void RefreshEncryption()
        {
            var supported = ArchiveFormats.SupportsEncryption(SelectedFormat());
            // A format change must never silently turn an encrypted output into plaintext.
            encryption.IsEnabled = supported || encryption.IsChecked == true;
            encryptionNote.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
            password.Visibility = confirm.Visibility = encryption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (encryption.IsChecked != true) password.Password = confirm.Password = string.Empty;
        }
        format.SelectionChanged += (_, _) => RefreshEncryption();
        encryption.Checked += (_, _) => RefreshEncryption();
        encryption.Unchecked += (_, _) => RefreshEncryption();
        RefreshEncryption();
        var dialog = new ContentDialog
        {
            Title = T(singleTask ? "ArchiveTaskSettings" : "ArchiveBatchSettings"), Content = panel, XamlRoot = XamlRoot,
            PrimaryButtonText = T("ArchiveSave"), CloseButtonText = T("ArchiveClose"),
            DefaultButton = ContentDialogButton.Primary
        };
        ArchiveConversionOptions? result = null;
        dialog.PrimaryButtonClick += (_, click) =>
        {
            if (encryption.IsChecked == true && !ArchiveFormats.SupportsEncryption(SelectedFormat()))
            { error.Text = T("ArchiveEncryptionUnavailable"); click.Cancel = true; return; }
            if (encryption.IsChecked == true && string.IsNullOrEmpty(password.Password))
            { error.Text = T("ArchivePasswordEmpty"); click.Cancel = true; return; }
            if (encryption.IsChecked == true && password.Password != confirm.Password)
            { error.Text = T("ArchivePasswordMismatch"); click.Cancel = true; return; }
            if (encryption.IsChecked == true && !ArchiveFormats.IsValidOutputPassword(SelectedFormat(), password.Password))
            { error.Text = T(SelectedFormat() == ArchiveFormat.Zip ? "ArchiveZipPasswordRules" : "ArchiveErrorInvalidOptions"); click.Cancel = true; return; }
            result = new ArchiveConversionOptions(SelectedFormat(), strict.IsChecked == true,
                encryption.IsChecked == true ? password.Password : null);
        };
        try { return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? result : null; }
        finally { password.Password = confirm.Password = string.Empty; }
    }

    private async void Start_Click(object sender, RoutedEventArgs args)
    {
        try { await StartAsync(); }
        catch (Exception) { ShowStatus(T("ArchiveErrorEngineFailed"), InfoBarSeverity.Error); }
    }

    internal async Task StartAsync()
    {
        if (_disposed || IsRunning || _editing || _restoring || !_persistenceReady)
        { if (IsRunning) await _session.StartAsync(RequestPasswordAsync); return; }
        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
        { ShowStatus(T("ArchiveOutputFolderRequired"), InfoBarSeverity.Warning); return; }
        var pending = Tasks.Where(task => task.State == ArchiveTaskState.Waiting).ToArray();
        if (pending.Length == 0) return;
        _editing = true;
        RefreshControls();
        try
        {
            foreach (var task in pending.Where(task => task.HasEncryption && string.IsNullOrEmpty(task.Options.OutputPassword)))
            {
                var options = await EditSettingsAsync(task.Options, encrypted: true, singleTask: true);
                if (_disposed || options is null) return;
                task.SetOptions(options);
            }
        }
        finally { _editing = false; RefreshControls(); }
        StatusBar.IsOpen = false;
        _session.OutputDirectory = OutputFolderBox.Text.Trim();
        await _session.StartAsync(RequestPasswordAsync);
    }

    private Task<string?> RequestPasswordAsync(ArchiveTask task, ArchivePasswordRequest request, CancellationToken token)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (_disposed) { completion.TrySetCanceled(token); return; }
                var password = new PasswordBox { Header = T("ArchiveSourcePassword") };
                var panel = new StackPanel { Spacing = 12, MinWidth = 260, MaxWidth = 480 };
                panel.Children.Add(new TextBlock { Text = Path.GetFileName(request.SourcePath), TextWrapping = TextWrapping.Wrap });
                if (request.IncorrectPassword)
                    panel.Children.Add(new TextBlock { Text = T("ArchiveWrongPassword"), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(password);
                var dialog = new ContentDialog
                {
                    Title = T("ArchiveSourcePasswordTitle"), Content = panel, XamlRoot = XamlRoot,
                    PrimaryButtonText = T("ArchiveSave"), CloseButtonText = T("ArchiveSkip"),
                    DefaultButton = ContentDialogButton.Primary
                };
                dialog.PrimaryButtonClick += (_, click) => { if (password.Password.Length == 0) click.Cancel = true; };
                try
                {
                    var choice = await ShowDialogAsync(dialog, token);
                    token.ThrowIfCancellationRequested();
                    completion.TrySetResult(choice == ContentDialogResult.Primary ? password.Password : null);
                }
                finally
                {
                    password.Password = string.Empty;
                }
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetException(new InvalidOperationException("Archive password dispatcher unavailable."));
        return completion.Task;
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, CancellationToken cancellationToken = default)
    {
        if (_disposed) return ContentDialogResult.None;
        _dialog = dialog;
        RefreshControls();
        try { return _showDialog is null ? await dialog.ShowAsync() : await _showDialog(dialog, cancellationToken); }
        finally { if (ReferenceEquals(_dialog, dialog)) _dialog = null; RefreshControls(); }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs args) => await StopAsync();

    internal async Task StopAsync(bool preserveForRestart = false)
    {
        await RestoreAsync();
        _session.OutputDirectory = OutputFolderBox.Text;
        var stopping = _session.StopAsync(preserveForRestart);
        _dialog?.Hide();
        await stopping;
    }

    private void ArchiveTaskActions_Opening(object sender, object args)
    {
        if (sender is not MenuFlyout { Target: FrameworkElement row } menu) return;
        foreach (var action in menu.Items) action.DataContext = row.DataContext;
    }

    private void Remove_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not ArchiveTaskItem task) return;
        var active = task.State is ArchiveTaskState.Running or ArchiveTaskState.AwaitingPassword;
        _session.CancelOrRemove(task.Model);
        if (active) _dialog?.Hide();
        RefreshControls();
    }

    private async void Retry_Click(object sender, RoutedEventArgs args)
    {
        if (IsRunning || _editing || (sender as FrameworkElement)?.Tag is not ArchiveTaskItem task) return;
        _editing = true;
        RefreshControls();
        try
        {
            var options = await EditSettingsAsync(task.Options, task.HasEncryption, singleTask: true);
            if (!_disposed && options is not null) { task.SetOptions(options); QueueCheckpoint(); }
        }
        finally { _editing = false; RefreshControls(); }
    }

    private async void OpenOutput_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not ArchiveTaskItem { CanOpen: true } task) return;
        try
        {
            if (!File.Exists(task.OutputPath)) return;
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(task.OutputPath)!);
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception) { ShowStatus(T("ArchiveErrorEngineFailed"), InfoBarSeverity.Warning); }
    }

    private void Clear_Click(object sender, RoutedEventArgs args)
    {
        if (IsRunning || _editing) return;
        _session.ClearFinished();
        RefreshControls();
    }

    private void OutputFolder_Changed(object sender, TextChangedEventArgs args)
    { if (StartButton is not null) { _session.OutputDirectory = OutputFolderBox.Text; RefreshControls(); } }

    private void RefreshControls()
    {
        if (_disposed) return;
        var busy = IsRunning || _restoring || !_persistenceReady;
        AddButton.IsEnabled = BrowseButton.IsEnabled = BatchSettingsButton.IsEnabled = !busy && !_editing;
        OutputFolderBox.IsEnabled = !busy && !_editing;
        StartButton.IsEnabled = !busy && !_editing && Tasks.Any(task => task.State == ArchiveTaskState.Waiting);
        CancelButton.IsEnabled = IsRunning;
        ClearButton.IsEnabled = !busy && !_editing && Tasks.Any(task => task.State is ArchiveTaskState.Succeeded or ArchiveTaskState.Failed or ArchiveTaskState.Cancelled);
        SummaryText.Text = LocalizationService.T("ArchiveTaskSummary", Tasks.Count,
            Tasks.Count(task => task.State == ArchiveTaskState.Succeeded), Tasks.Count(task => task.State == ArchiveTaskState.Failed));
        BatchResultText.Text = FormatBatchSummary();
        var hasTasks = Tasks.Count > 0;
        var emptyVisibility = hasTasks ? Visibility.Collapsed : Visibility.Visible;
        var contentChanged = EmptyText.Visibility != emptyVisibility;
        EmptyText.Visibility = emptyVisibility;
        if (contentChanged)
        {
            FluentMotion.Reset(EmptyText);
            FluentMotion.Reset(TaskList);
            if (IsLoaded && Visibility == Visibility.Visible)
                FluentMotion.Entrance(hasTasks ? TaskList : EmptyText, FluentMotion.ContentDurationMs, 6);
        }
        foreach (var task in Tasks) task.SetBatchBusy(busy || _editing);
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (_disposed) return;
        var wasOpen = StatusBar.IsOpen;
        StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true;
        if (wasOpen) return;
        var version = ++_statusMotionVersion;
        if (!IsLoaded || Visibility != Visibility.Visible || !FluentMotion.IsEnabled) return;
        _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_disposed && version == _statusMotionVersion && IsLoaded && Visibility == Visibility.Visible && StatusBar.IsOpen)
                FluentMotion.Entrance(StatusBar, FluentMotion.ContentDurationMs, 4);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
        _dialog?.Hide();
        DetachSession();
    }

    private static string T(string key) => LocalizationService.T(key);

}

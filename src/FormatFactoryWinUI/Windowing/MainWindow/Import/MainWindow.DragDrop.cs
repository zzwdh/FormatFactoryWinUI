using FormatFactoryWinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FormatFactoryWinUI;

public sealed partial class MainWindow
{
    private DispatcherTimer? _dropLeaveTimer;
    private bool _dropImportInProgress;

    private void InitializeFileDrop()
    {
        // Lists and editors can handle routed drag events themselves. Listen once at
        // the window content root so cards, controls and blank space share one import.
        RootGrid.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(Window_DragOver), true);
        RootGrid.AddHandler(UIElement.DragOverEvent, new DragEventHandler(Window_DragOver), true);
        RootGrid.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(Window_DragLeave), true);
        RootGrid.AddHandler(UIElement.DropEvent, new DragEventHandler(Window_Drop), true);
        RootGrid.Unloaded += (_, _) => ResetDropFeedback();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                ResetDropFeedback();
            }
        };
        _dropLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _dropLeaveTimer.Tick += (_, _) => ResetDropFeedback();
    }

    private bool CanAcceptFileDrop(DragEventArgs e) =>
        !_windowClosed && !_initializing && !_dropImportInProgress && _activeImport is null &&
        !e.DataView.Properties.ContainsKey(OutputTransferService.OutputDragMarker) &&
        e.DataView.Contains(StandardDataFormats.StorageItems);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!CanAcceptFileDrop(e))
        {
            // Preserve native text dragging inside editable controls. Only reject
            // file drops here when startup or another import is still in progress.
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                e.AcceptedOperation = DataPackageOperation.None;
            }
            ResetDropFeedback();
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = LocalizationService.T("DragCaption");
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        _dropLeaveTimer?.Stop();
        ShowDropFeedback();
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        // Moving between child controls also raises DragLeave. A following Enter/Over
        // cancels this brief delay, preventing flicker while still clearing on Escape.
        if (DropFeedbackOverlay.Visibility == Visibility.Visible)
        {
            _dropLeaveTimer?.Stop();
            _dropLeaveTimer?.Start();
        }
    }

    private void ResetDropFeedback()
    {
        _dropLeaveTimer?.Stop();
        if (DropFeedbackOverlay is not null)
        {
            _ = HideDropFeedbackAsync();
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        ResetDropFeedback();
        if (!CanAcceptFileDrop(e))
        {
            // Preserve native text dragging inside editable controls. Only reject
            // file drops here when startup or another import is still in progress.
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                e.AcceptedOperation = DataPackageOperation.None;
            }
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Copy;
        _dropImportInProgress = true;
        try
        {
            IReadOnlyList<IStorageItem> items;
            var deferral = e.GetDeferral();
            try
            {
                items = await e.DataView.GetStorageItemsAsync();
            }
            finally
            {
                // Release Explorer after reading the data; media analysis can take longer.
                deferral.Complete();
            }

            if (_windowClosed || items.Count == 0)
            {
                return;
            }

            // A category filter or another page must not hide the newly imported tasks.
            NavView.SelectedItem = NavAllItem;
            var droppedFiles = new List<string>();
            var cancellationVersion = _importCancellationVersion;
            foreach (var item in items)
            {
                if (_windowClosed) return;
                if (item is StorageFolder folder)
                {
                    var accessToken = await StorageAccessService.RememberFolderAsync(folder.Path);
                    if (_windowClosed) return;
                    await AddFolderAsync(folder.Path, accessToken);
                    if (_importCancellationVersion != cancellationVersion) return;
                }
                else if (item is StorageFile file)
                {
                    droppedFiles.Add(file.Path);
                }
            }
            if (!_windowClosed && droppedFiles.Count > 0)
            {
                await AddIncomingPathsAsync(droppedFiles, null);
            }
        }
        catch (Exception ex)
        {
            LogService.WriteAppError(ex);
            ShowInfo(LocalizationService.T("ConversionUiDropReadFailed", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _dropImportInProgress = false;
            ResetDropFeedback();
        }
    }
}

using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private Button? _addSelectedToEncodeQueueButton;
    private Label? _queueHandoffStatusLabel;
    private bool _queueHandoffInProgress;

    private async void AddSelectedFilesToEncodeQueue_Click(object? sender, EventArgs e)
    {
        if (!CanUseFormUi || _queueHandoffInProgress)
            return;

        LibraryFileViewRecord[] selectedFiles = SelectedVisibleQueueFiles();
        LibraryFileQueueResult prepared = LibraryFileQueueSelection.PreparePresentCatalogSelection(selectedFiles);
        if (prepared.AvailablePaths.Count == 0)
        {
            if (prepared.UnavailableCount > 0)
                SetQueueHandoffStatus("No available selected files to send.");
            return;
        }

        Func<IReadOnlyList<string>, Task>? addToEncodeQueue = _reviewOptions.AddToEncodeQueueAsync;
        if (addToEncodeQueue == null)
            return;

        _queueHandoffInProgress = true;
        UpdateAnalyzerActionState();
        SetQueueHandoffStatus($"Sending {prepared.AvailablePaths.Count:N0} file(s) to Encode Queue…");
        try
        {
            LibraryFileQueueResult result = await LibraryFileQueueSelection.DispatchPresentCatalogSelectionAsync(
                selectedFiles,
                addToEncodeQueue);
            if (CanUseFormUi && result.Dispatched)
            {
                string unavailable = result.UnavailableCount > 0
                    ? $"; {result.UnavailableCount:N0} unavailable or invalid selection(s) skipped"
                    : "";
                SetQueueHandoffStatus(
                    $"{result.AvailablePaths.Count:N0} file(s) sent to Encode Queue{unavailable}.");
            }
        }
        catch (OperationCanceledException)
        {
            if (CanUseFormUi)
                SetQueueHandoffStatus("The Encode queue import was canceled.");
        }
        catch (Exception exception)
        {
            ShowError("The selected files could not be sent to the Encode queue.", exception);
        }
        finally
        {
            _queueHandoffInProgress = false;
            if (CanUseFormUi)
                UpdateAnalyzerActionState();
        }
    }

    private void SetQueueHandoffStatus(string status)
    {
        if (!CanUseFormUi || _queueHandoffStatusLabel == null)
            return;
        _queueHandoffStatusLabel.Text = status;
        _queueHandoffStatusLabel.Visible = !string.IsNullOrWhiteSpace(status);
    }

    private LibraryFileViewRecord[] SelectedVisibleQueueFiles() => _filesGrid.SelectedRows
        .Cast<DataGridViewRow>()
        .Where(row => row.Visible)
        .Select(row => row.Tag)
        .OfType<LibraryFileViewRecord>()
        .ToArray();
}

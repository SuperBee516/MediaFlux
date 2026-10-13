using System.Globalization;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private sealed record SavedSearchPickerItem(LibrarySavedSearch? Search, bool IsDirty = false)
    {
        public override string ToString() => Search == null ? "Saved searches" : Search.Name + (IsDirty ? " *" : "");
    }

    private readonly ComboBox _savedSearchPicker = new()
    {
        Name = "SavedSearchPicker",
        Width = 180,
        DropDownStyle = ComboBoxStyle.DropDownList,
        AccessibleName = "Load a saved Advanced Search"
    };
    private readonly Button _saveSavedSearchButton = new()
    {
        Name = "SaveAdvancedSearch",
        Text = "Save…",
        AutoSize = true,
        AccessibleName = "Save current Advanced Search"
    };
    private readonly Button _manageSavedSearchesButton = new()
    {
        Name = "ManageAdvancedSearches",
        Text = "More ▾",
        AutoSize = true,
        AccessibleName = "Update, rename, or delete the selected saved search"
    };
    private readonly ContextMenuStrip _savedSearchMenu = new();
    private readonly ToolStripMenuItem _updateSavedSearchMenuItem = new("Update selected search…");
    private readonly ToolStripMenuItem _renameSavedSearchMenuItem = new("Rename selected search…");
    private readonly ToolStripMenuItem _deleteSavedSearchMenuItem = new("Delete selected search…");
    private IReadOnlyList<LibrarySavedSearch> _savedSearches = Array.Empty<LibrarySavedSearch>();
    private string? _selectedSavedSearchName;
    private CatalogSearchDefinition? _selectedSavedSearchDefinition;
    private CatalogSearchDefinition? _activeSavedSearchTemplate;
    private bool _selectedSavedSearchIsDirty;
    private bool _suppressSavedSearchPickerEvents;

    private void InitializeSavedSearchUi()
    {
        _savedSearches = _advancedSearchStore.Load();
        PopulateSavedSearchPicker(selectedName: null);

        AnalyzerUi.StyleSecondary(_saveSavedSearchButton);
        AnalyzerUi.StyleSecondary(_manageSavedSearchesButton);
        _savedSearchPicker.SelectedIndexChanged += (_, _) => SavedSearchPicker_SelectionChanged();
        _saveSavedSearchButton.Click += (_, _) => SaveSavedSearchFromDialog();
        _manageSavedSearchesButton.Click += (_, _) =>
            _savedSearchMenu.Show(_manageSavedSearchesButton, new Point(0, _manageSavedSearchesButton.Height));

        _savedSearchMenu.Items.AddRange(new ToolStripItem[]
        {
            _updateSavedSearchMenuItem,
            _renameSavedSearchMenuItem,
            _deleteSavedSearchMenuItem
        });
        _updateSavedSearchMenuItem.Click += (_, _) => UpdateSavedSearchFromConfirmation();
        _renameSavedSearchMenuItem.Click += (_, _) => RenameSavedSearchFromDialog();
        _deleteSavedSearchMenuItem.Click += (_, _) => DeleteSavedSearchFromConfirmation();
        UpdateSavedSearchMenuState();
    }

    private void PopulateSavedSearchPicker(string? selectedName)
    {
        _suppressSavedSearchPickerEvents = true;
        try
        {
            _savedSearchPicker.Items.Clear();
            _savedSearchPicker.Items.Add(new SavedSearchPickerItem(null));
            foreach (LibrarySavedSearch search in _savedSearches)
                _savedSearchPicker.Items.Add(new SavedSearchPickerItem(search,
                    _selectedSavedSearchIsDirty && search.Name.Equals(_selectedSavedSearchName, StringComparison.OrdinalIgnoreCase)));

            int selectedIndex = 0;
            if (selectedName != null)
            {
                for (int i = 1; i < _savedSearchPicker.Items.Count; i++)
                {
                    if (_savedSearchPicker.Items[i] is SavedSearchPickerItem item &&
                        item.Search?.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }
            _savedSearchPicker.SelectedIndex = selectedIndex;
        }
        finally { _suppressSavedSearchPickerEvents = false; }
        UpdateSavedSearchMenuState();
    }

    private void SavedSearchPicker_SelectionChanged()
    {
        if (_suppressSavedSearchPickerEvents) return;
        if (_savedSearchPicker.SelectedItem is not SavedSearchPickerItem { Search: { } search })
        {
            _selectedSavedSearchName = null;
            _selectedSavedSearchDefinition = null;
            _selectedSavedSearchIsDirty = false;
            UpdateSavedSearchMenuState();
            return;
        }

        LoadSavedSearch(search.Name);
    }

    private void LoadSavedSearch(string name)
    {
        LibrarySavedSearch? search = _savedSearches.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (search == null)
        {
            SetAdvancedSearchValidation("That saved search is no longer available. Reload the Library Analyzer.");
            return;
        }

        try
        {
            CatalogSearchDefinitionValidator.Validate(search.Definition);
            ApplySavedSearchDefinition(search);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            LogSavedSearchFailure("Load saved search", exception);
            SetAdvancedSearchValidation("This saved search is invalid and was not applied. See the error log.");
            ClearSavedSearchAssociation(selectPlaceholder: false);
            ReloadSavedSearches(selectedName: null);
        }
    }

    private void ApplySavedSearchDefinition(LibrarySavedSearch saved)
    {
        CatalogSearchDefinition definition = saved.Definition;
        HashSet<int> quickConditions = FindQuickConditionIndexes(definition.Conditions);
        var preparedRows = new List<(int Index, LibraryAnalyzerSearchConditionRow Row)>();
        try
        {
            for (int i = 0; i < definition.Conditions.Count; i++)
            {
                if (quickConditions.Contains(i)) continue;
                CatalogSearchCondition condition = definition.Conditions[i];
                var row = new LibraryAnalyzerSearchConditionRow(condition.PropertyId);
                try { row.ApplyCondition(condition); }
                catch { row.Dispose(); throw; }
                preparedRows.Add((i, row));
            }
        }
        catch
        {
            foreach ((int _, LibraryAnalyzerSearchConditionRow row) in preparedRows) row.Dispose();
            throw;
        }

        _suppressAdvancedSearchEvents = true;
        try
        {
            foreach (ComboBox choice in new[] { _quickCodec, _quickResolution, _quickBitDepth, _quickScanType, _quickDynamicRange })
                choice.SelectedIndex = 0;
            foreach (TextBox editor in new[] { _quickFpsMin, _quickFpsMax, _quickBitrateMin, _quickBitrateMax })
                editor.Clear();
            foreach (LibraryAnalyzerSearchConditionRow row in _advancedRows.ToArray())
            {
                _advancedConditionRows.Controls.Remove(row);
                row.Dispose();
            }
            _advancedRows.Clear();

            var preparedByIndex = preparedRows.ToDictionary(item => item.Index, item => item.Row);
            for (int i = 0; i < definition.Conditions.Count; i++)
            {
                if (quickConditions.Contains(i))
                {
                    if (!TryApplyQuickCondition(definition.Conditions[i]))
                        throw new InvalidOperationException("A saved quick-filter condition could not be restored exactly.");
                    continue;
                }
                AttachAdvancedConditionRow(preparedByIndex[i]);
            }

            _activeSavedSearchTemplate = definition with
            {
                Conditions = Array.Empty<CatalogSearchCondition>()
            };
            _selectedSavedSearchName = saved.Name;
            _selectedSavedSearchDefinition = definition;
            _selectedSavedSearchIsDirty = false;
        }
        finally
        {
            _suppressAdvancedSearchEvents = false;
            foreach ((int _, LibraryAnalyzerSearchConditionRow row) in preparedRows)
                if (!_advancedRows.Contains(row)) row.Dispose();
        }

        SetAdvancedSearchValidation("");
        PopulateSavedSearchPicker(saved.Name);
        UpdateAdvancedFilterCount();
        ScheduleAdvancedSearchRefresh(immediate: true);
    }

    private static HashSet<int> FindQuickConditionIndexes(IReadOnlyList<CatalogSearchCondition> conditions)
    {
        var result = new HashSet<int>();
        string[] quickProperties =
        {
            "video.codec", "video.resolution_class", "video.fps", "video.bitrate",
            "video.bit_depth", "video.scan_type", "video.dynamic_range"
        };
        foreach (string propertyId in quickProperties)
        {
            int[] candidates = conditions.Select((condition, index) => (condition, index))
                .Where(pair => pair.condition.PropertyId == propertyId)
                .Select(pair => pair.index).ToArray();
            if (candidates.Length == 1 && IsQuickConditionMappable(conditions[candidates[0]]))
                result.Add(candidates[0]);
        }
        return result;
    }

    private static bool IsQuickConditionMappable(CatalogSearchCondition condition)
    {
        if (condition.Operator == CatalogSearchOperator.Is && condition.Value?.Text is { } text)
        {
            if (condition.PropertyId == "video.codec")
                return text is "h264" or "hevc" or "av1" or "vp9";
            if (condition.PropertyId is "video.resolution_class" or "video.scan_type" or "video.dynamic_range")
                return CatalogSearchRegistry.TryGet(condition.PropertyId, out CatalogSearchPropertyInfo? property) &&
                       property?.Choices.Contains(text, StringComparer.Ordinal) == true;
        }

        if (condition.PropertyId == "video.bit_depth" && condition.Operator == CatalogSearchOperator.Equal &&
            condition.Value?.Number is { } depth && (condition.Value.Unit == null || condition.Value.Unit == "count") &&
            depth == decimal.Truncate(depth) && depth is 8 or 10 or 12)
            return true;

        return condition.PropertyId == "video.fps" && IsQuickRange(condition, "fps") ||
               condition.PropertyId == "video.bitrate" && IsQuickRange(condition, "mbps");
    }

    private bool TryApplyQuickCondition(CatalogSearchCondition condition)
    {
        if (condition.Operator == CatalogSearchOperator.Is && condition.Value?.Text is { } text)
        {
            ComboBox? target = condition.PropertyId switch
            {
                "video.codec" => _quickCodec,
                "video.resolution_class" => _quickResolution,
                "video.scan_type" => _quickScanType,
                "video.dynamic_range" => _quickDynamicRange,
                _ => null
            };
            if (target != null && target.Items.Cast<QuickChoice>().FirstOrDefault(item =>
                    string.Equals(item.Value, text, StringComparison.Ordinal)) is { } selected)
            {
                target.SelectedItem = selected;
                return true;
            }
        }

        if (condition.PropertyId == "video.bit_depth" && condition.Operator == CatalogSearchOperator.Equal &&
            condition.Value?.Number is { } depth && (condition.Value.Unit == null || condition.Value.Unit == "count") &&
            depth == decimal.Truncate(depth) && depth is 8 or 10 or 12)
        {
            string label = depth.ToString(CultureInfo.InvariantCulture) + "-bit";
            _quickBitDepth.SelectedItem = _quickBitDepth.Items.Cast<QuickChoice>()
                .First(item => item.Label == label);
            return true;
        }

        if (condition.PropertyId == "video.fps" && IsQuickRange(condition, "fps"))
        {
            SetQuickRange(condition, _quickFpsMin, _quickFpsMax);
            return true;
        }

        if (condition.PropertyId == "video.bitrate" && IsQuickRange(condition, "mbps"))
        {
            SetQuickRange(condition, _quickBitrateMin, _quickBitrateMax);
            return true;
        }

        return false;
    }

    private static bool IsQuickRange(CatalogSearchCondition condition, string unit)
    {
        bool supportedOperator = condition.Operator switch
        {
            CatalogSearchOperator.Between => condition.Value?.Number != null && condition.UpperValue?.Number != null &&
                                            condition.Value.Unit == unit && condition.UpperValue.Unit == unit,
            CatalogSearchOperator.GreaterThanOrEqual => condition.Value?.Number != null && condition.UpperValue == null &&
                                                        condition.Value.Unit == unit,
            CatalogSearchOperator.LessThanOrEqual => condition.Value?.Number != null && condition.UpperValue == null &&
                                                     condition.Value.Unit == unit,
            _ => false
        };
        return supportedOperator && (condition.PropertyId is "video.fps" or "video.bitrate");
    }

    private static void SetQuickRange(CatalogSearchCondition condition, TextBox minimum, TextBox maximum)
    {
        switch (condition.Operator)
        {
            case CatalogSearchOperator.Between:
                minimum.Text = condition.Value!.Number!.Value.ToString(CultureInfo.InvariantCulture);
                maximum.Text = condition.UpperValue!.Number!.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case CatalogSearchOperator.GreaterThanOrEqual:
                minimum.Text = condition.Value!.Number!.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case CatalogSearchOperator.LessThanOrEqual:
                maximum.Text = condition.Value!.Number!.Value.ToString(CultureInfo.InvariantCulture);
                break;
        }
    }

    private void SaveSavedSearchFromDialog()
    {
        using var dialog = new SavedSearchNameDialog("Save Advanced Search", "Enter a name for this search.");
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SaveCurrentSearchAs(dialog.SearchName);
    }

    private void SaveCurrentSearchAs(string name)
    {
        try
        {
            CatalogSearchDefinition definition = CurrentDefinitionForSave();
            LibrarySavedSearch saved = _advancedSearchStore.SaveNew(name, definition);
            _selectedSavedSearchName = saved.Name;
            _selectedSavedSearchDefinition = saved.Definition;
            _activeSavedSearchTemplate = saved.Definition with { Conditions = Array.Empty<CatalogSearchCondition>() };
            _selectedSavedSearchIsDirty = false;
            SetAdvancedSearchValidation("");
            ReloadSavedSearches(saved.Name);
            UpdateAdvancedFilterCount();
        }
        catch (ArgumentException exception)
        {
            SetAdvancedSearchValidation(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            SetAdvancedSearchValidation(exception.Message);
        }
        catch (Exception exception)
        {
            LogSavedSearchFailure("Save new search", exception);
            SetAdvancedSearchValidation("The saved search could not be written. See the error log.");
        }
    }

    private void UpdateSavedSearchFromConfirmation()
    {
        if (_selectedSavedSearchName == null || !_selectedSavedSearchIsDirty) return;
        if (MessageBox.Show(this, $"Replace the saved definition for '{_selectedSavedSearchName}' with the current Advanced Search?",
                "Update saved search", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        UpdateSelectedSavedSearch();
    }

    private void UpdateSelectedSavedSearch()
    {
        if (_selectedSavedSearchName == null) return;
        try
        {
            CatalogSearchDefinition definition = CurrentDefinitionForSave();
            LibrarySavedSearch saved = _advancedSearchStore.Update(_selectedSavedSearchName, definition);
            _selectedSavedSearchName = saved.Name;
            _selectedSavedSearchDefinition = saved.Definition;
            _activeSavedSearchTemplate = saved.Definition with { Conditions = Array.Empty<CatalogSearchCondition>() };
            _selectedSavedSearchIsDirty = false;
            SetAdvancedSearchValidation("");
            ReloadSavedSearches(saved.Name);
            UpdateAdvancedFilterCount();
        }
        catch (ArgumentException exception) { SetAdvancedSearchValidation(exception.Message); }
        catch (InvalidOperationException exception) { SetAdvancedSearchValidation(exception.Message); }
        catch (Exception exception)
        {
            LogSavedSearchFailure("Update saved search", exception);
            SetAdvancedSearchValidation("The saved search could not be updated. See the error log.");
        }
    }

    private void RenameSavedSearchFromDialog()
    {
        if (_selectedSavedSearchName == null) return;
        using var dialog = new SavedSearchNameDialog("Rename Saved Search", "Enter the new saved-search name.",
            _selectedSavedSearchName, "Rename");
        if (dialog.ShowDialog(this) == DialogResult.OK)
            RenameSelectedSavedSearch(dialog.SearchName);
    }

    private void RenameSelectedSavedSearch(string newName)
    {
        if (_selectedSavedSearchName == null) return;
        try
        {
            LibrarySavedSearch renamed = _advancedSearchStore.Rename(_selectedSavedSearchName, newName);
            _selectedSavedSearchName = renamed.Name;
            _selectedSavedSearchDefinition = renamed.Definition;
            _selectedSavedSearchIsDirty = false;
            SetAdvancedSearchValidation("");
            ReloadSavedSearches(renamed.Name);
        }
        catch (ArgumentException exception) { SetAdvancedSearchValidation(exception.Message); }
        catch (InvalidOperationException exception) { SetAdvancedSearchValidation(exception.Message); }
        catch (Exception exception)
        {
            LogSavedSearchFailure("Rename saved search", exception);
            SetAdvancedSearchValidation("The saved search could not be renamed. See the error log.");
        }
    }

    private void DeleteSavedSearchFromConfirmation()
    {
        if (_selectedSavedSearchName == null) return;
        if (MessageBox.Show(this, $"Delete the saved search '{_selectedSavedSearchName}'? The current search conditions will remain active.",
                "Delete saved search", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        DeleteSelectedSavedSearch();
    }

    private void DeleteSelectedSavedSearch()
    {
        if (_selectedSavedSearchName == null) return;
        try
        {
            _advancedSearchStore.Delete(_selectedSavedSearchName);
            _selectedSavedSearchName = null;
            _selectedSavedSearchDefinition = null;
            _selectedSavedSearchIsDirty = false;
            SetAdvancedSearchValidation("");
            ReloadSavedSearches(selectedName: null);
            UpdateAdvancedFilterCount();
        }
        catch (Exception exception)
        {
            LogSavedSearchFailure("Delete saved search", exception);
            SetAdvancedSearchValidation("The saved search could not be deleted. See the error log.");
        }
    }

    private void ReloadSavedSearches(string? selectedName)
    {
        _savedSearches = _advancedSearchStore.Load();
        PopulateSavedSearchPicker(selectedName);
    }

    private void ClearSavedSearchAssociation(bool selectPlaceholder)
    {
        _selectedSavedSearchName = null;
        _selectedSavedSearchDefinition = null;
        _selectedSavedSearchIsDirty = false;
        if (selectPlaceholder)
        {
            _suppressSavedSearchPickerEvents = true;
            try { if (_savedSearchPicker.Items.Count > 0) _savedSearchPicker.SelectedIndex = 0; }
            finally { _suppressSavedSearchPickerEvents = false; }
        }
        UpdateSavedSearchMenuState();
    }

    private CatalogSearchDefinition CurrentDefinitionForSave()
    {
        CatalogSearchDefinition definition = BuildAdvancedSearchDefinition() ??
            new CatalogSearchDefinition(CatalogSearchDefinition.CurrentVersion, Array.Empty<CatalogSearchCondition>());
        CatalogSearchDefinitionValidator.Validate(definition);
        return definition;
    }

    private int SavedSearchTemplateFilterCount()
    {
        CatalogSearchDefinition? template = _activeSavedSearchTemplate;
        if (template == null) return 0;
        return (string.IsNullOrWhiteSpace(template.Search) ? 0 : 1) +
               (template.LocationId.HasValue ? 1 : 0) +
               (template.Availability.HasValue ? 1 : 0) +
               (template.ProbeStatus.HasValue ? 1 : 0) +
               (template.Sort != null ? 1 : 0);
    }

    private void RefreshSavedSearchDirtyState()
    {
        bool dirty = false;
        if (_selectedSavedSearchDefinition != null)
        {
            try
            {
                CatalogSearchDefinition current = BuildAdvancedSearchDefinition() ??
                    new CatalogSearchDefinition(CatalogSearchDefinition.CurrentVersion, Array.Empty<CatalogSearchCondition>());
                dirty = !CatalogSearchDefinitionEquivalence.AreEquivalent(current, _selectedSavedSearchDefinition);
            }
            catch (ArgumentException) { dirty = true; }
        }

        if (dirty != _selectedSavedSearchIsDirty)
        {
            _selectedSavedSearchIsDirty = dirty;
            for (int i = 1; i < _savedSearchPicker.Items.Count; i++)
            {
                if (_savedSearchPicker.Items[i] is not SavedSearchPickerItem item ||
                    item.Search?.Name.Equals(_selectedSavedSearchName, StringComparison.OrdinalIgnoreCase) != true)
                    continue;
                _suppressSavedSearchPickerEvents = true;
                try
                {
                    _savedSearchPicker.Items[i] = item with { IsDirty = dirty };
                    _savedSearchPicker.SelectedIndex = i;
                }
                finally { _suppressSavedSearchPickerEvents = false; }
                break;
            }
        }
        UpdateSavedSearchMenuState();
    }

    private void UpdateSavedSearchMenuState()
    {
        bool selected = _selectedSavedSearchName != null;
        _updateSavedSearchMenuItem.Enabled = selected && _selectedSavedSearchIsDirty;
        _renameSavedSearchMenuItem.Enabled = selected;
        _deleteSavedSearchMenuItem.Enabled = selected;
    }

    private void LogSavedSearchFailure(string action, Exception exception) =>
        ErrorLogService.Append(AppPaths.UserDataDirectory, $"Library Analyzer {action} failed",
            _advancedSearchStore.StoragePath, exception);

    private sealed class SavedSearchNameDialog : Form
    {
        private readonly TextBox _name = new()
        {
            Dock = DockStyle.Top,
            MaxLength = LibraryAdvancedSearchStore.MaximumNameLength,
            AccessibleName = "Saved search name"
        };
        private readonly Label _error = new()
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 24,
            ForeColor = AnalyzerUi.AttentionTextColor
        };

        internal SavedSearchNameDialog(string title, string prompt, string? initialName = null,
            string acceptLabel = "Save")
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(430, 132);
            Font = new Font("Segoe UI", 9F);
            _name.Text = initialName ?? "";

            var label = new Label { Text = prompt, Dock = DockStyle.Top, AutoSize = false, Height = 25 };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            var ok = new Button { Text = acceptLabel, AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_name.Text))
                {
                    _error.Text = "Enter a name.";
                    _name.Focus();
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            buttons.Controls.AddRange(new Control[] { cancel, ok });
            Controls.Add(buttons);
            Controls.Add(_error);
            Controls.Add(_name);
            Controls.Add(label);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        internal string SearchName => _name.Text.Trim();
    }

    private void CleanupSavedSearchUi()
    {
        _savedSearchMenu.Dispose();
    }
}

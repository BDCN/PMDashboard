using System.IO;
using System.Windows;
using System.Windows.Controls;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class ConfigPage : UserControl
{
    private ConfigViewModel? Vm => DataContext as ConfigViewModel;

    public ConfigPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Vm is not { } vm) return;
            // Restore saved password into PasswordBox (PasswordBox doesn't support data binding)
            if (!string.IsNullOrEmpty(vm.CloudPassword))
                TxtCloudPassword.Password = vm.CloudPassword;
            // Populate DDL text area if Cloud mode was already active when the page opens
            if (vm.IsDbCloud)
                TxtDdl.Text = vm.GetDdlScript();
            try { UpdateColumnMappingStatus(); } catch { /* status line only */ }

            // Opened from Coverage Planner's "Model settings" button — jump to that card. Deferred
            // to after layout so the card has a position to scroll to.
            if (vm.PendingScrollTarget == "Coverage")
            {
                vm.PendingScrollTarget = null;
                Dispatcher.InvokeAsync(() => CoverageCard.BringIntoView(),
                                       System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };
    }

    // ── Percentage Calculation ──

    private void BtnPctSites_Click(object sender, RoutedEventArgs e) =>
        Vm?.SavePercentageCalculation("Sites");

    private void BtnPctScore_Click(object sender, RoutedEventArgs e) =>
        Vm?.SavePercentageCalculation("Score");

    private void BtnPctCount_Click(object sender, RoutedEventArgs e) =>
        Vm?.SavePercentageCalculation("Count");

    // ── Workload Scoring Parameters ──

    private void BtnSaveWorkloadParams_Click(object sender, RoutedEventArgs e) =>
        Vm?.SaveWorkloadParams();

    private void BtnSaveScheduleConflict_Click(object sender, RoutedEventArgs e) =>
        Vm?.SaveScheduleConflictSettings();

    private void BtnSaveCoverage_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.SaveCoverageSettings();
        MessageBox.Show("Coverage planning settings saved. The Coverage Planner recalculates the next time you open it.",
            "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnResetCoverage_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var confirm = MessageBox.Show(
            "Put every Coverage Planning variable back to its default and save?\n\n" + vm.CoverageDefaultsText +
            "\n\nThe CX window is shared with Forecast Schedule Conflicts, so that card changes too.",
            "Reset to Defaults", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        vm.ResetCoverageSettings();
        MessageBox.Show("Coverage planning settings reset to defaults and saved.",
            "Reset", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSyncEquipment_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        TxtSyncStatus.Text = "Syncing…";
        // Run on background thread so UI doesn't freeze on large datasets
        System.Threading.Tasks.Task.Run(() =>
        {
            var msg = vm.SyncEquipmentFromOrders();
            Dispatcher.Invoke(() => TxtSyncStatus.Text = msg);
        });
    }

    // ── Source Data type ──

    private void BtnVadata_Click(object sender, RoutedEventArgs e)    => TryChangeSourceType("VADATA");
    private void BtnTableau_Click(object sender, RoutedEventArgs e)   => TryChangeSourceType("Tableau");
    private void BtnOpenLines_Click(object sender, RoutedEventArgs e) => TryChangeSourceType("OpenLines");

    private void TryChangeSourceType(string newType)
    {
        if (Vm is not { } vm) return;
        if (vm.SourceDataType == newType) return; // no change — radio button will revert via OneWay binding

        vm.SaveSourceDataType(newType);
        vm.StatusMessage = $"Source data type changed to {newType}.";
        UpdateColumnMappingStatus();
    }

    // ── Column mapping (Dialogs/OrderColumnMappingDialog) ──

    private void BtnColumnMapping_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new Dialogs.OrderColumnMappingDialog(vm.SourceDataType) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.StatusMessage = "Column mapping saved.";
        UpdateColumnMappingStatus();
    }

    private void UpdateColumnMappingStatus()
    {
        if (Vm is not { } vm) return;
        var type = vm.SourceDataType;
        var map  = OrderColumnMapping.Load(type);
        TxtColumnMappingStatus.Text =
            (OrderColumnMapping.IsCustomised(type)
                ? $"{OrderColumnMapping.SourceLabel(type)}: custom mapping. "
                : $"{OrderColumnMapping.SourceLabel(type)}: built-in column positions. ") +
            $"Job Name ← {map["JobName"]}, Customer Order ← {map["CustomerOrder"]}, Order Line ← {map["OrdLine"]}.";
    }

    // ── Upload method ──

    private void BtnManual_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsSemiAutomatic = false;
    }

    private void BtnSemiAuto_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsSemiAutomatic = true;
    }

    private void BtnSaveMethod_Click(object sender, RoutedEventArgs e)
    {
        Vm?.SaveUploadMethod();
    }

    // ── Folder selection ──

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "Select the import folder to watch for new Excel files",
            Multiselect      = false,
            InitialDirectory = Directory.Exists(vm.ImportFolder) ? vm.ImportFolder : ""
        };
        if (dlg.ShowDialog() == true)
            vm.ImportFolder = dlg.FolderName;
    }

    private void BtnNewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!Directory.Exists(vm.ImportFolder))
        {
            MessageBox.Show("Please select an existing parent folder first using Browse.",
                "No Parent Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        NewFolderPanel.Visibility = Visibility.Visible;
        TxtNewFolderName.Clear();
        TxtNewFolderName.Focus();
    }

    private void BtnCreateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var name = TxtNewFolderName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Please enter a folder name.", "Invalid Name",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var newPath = Path.Combine(vm.ImportFolder, name);
            Directory.CreateDirectory(newPath);
            vm.ImportFolder = newPath;
            NewFolderPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create folder:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnCancelNewFolder_Click(object sender, RoutedEventArgs e)
    {
        NewFolderPanel.Visibility = Visibility.Collapsed;
    }

    // ── Import settings ──

    private void BtnSaveImportSettings_Click(object sender, RoutedEventArgs e)
    {
        Vm?.SaveImportSettings();
    }

    // ── Assignment Rules ──

    private void RulesGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not AssignmentRule rule) return;
        Dispatcher.BeginInvoke(() => vm.SaveRule(rule),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BtnRuleUp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is AssignmentRule rule) vm.MoveRuleUp(rule);
    }

    private void BtnRuleDown_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is AssignmentRule rule) vm.MoveRuleDown(rule);
    }

    private void BtnDeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not AssignmentRule rule || rule.IsBuiltIn) return;
        var confirm = MessageBox.Show($"Delete rule \"{rule.Name}\"?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes) vm.DeleteRule(rule);
    }

    private void BtnAddRule_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new AddEditRuleDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            vm.AddCustomRule(dlg.RuleName, dlg.RuleDescription, dlg.RuleWeight);
    }

    // ── ROMP Status management ──

    private void StatusGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not RompStatus status) return;
        Dispatcher.BeginInvoke(() =>
            {
                var error = vm.SaveRompStatus(status);
                if (error != null)
                    MessageBox.Show(error, "Can't Rename Status", MessageBoxButton.OK, MessageBoxImage.Warning);
            },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ChkStatusComplete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is CheckBox chk && chk.DataContext is RompStatus status)
        {
            status.IsComplete = chk.IsChecked == true;
            // If marked complete, it can't also count as active
            if (status.IsComplete) status.CountsAsActive = false;
            vm.SaveRompStatus(status);
        }
    }

    private void ChkStatusActive_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is CheckBox chk && chk.DataContext is RompStatus status)
        {
            status.CountsAsActive = chk.IsChecked == true;
            vm.SaveRompStatus(status);
        }
    }

    private void ChkStatusDisabled_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is CheckBox chk && chk.DataContext is RompStatus status)
        {
            status.IsDisabled = chk.IsChecked == true;
            vm.SaveRompStatus(status);
        }
    }

    /// <summary>Opens the native Windows color picker for a status's indicator-dot color. Uses
    /// System.Windows.Forms.ColorDialog — WPF has no first-party equivalent — so the conversion
    /// between System.Drawing.Color (dialog) and the "#RRGGBB" string (RompStatus.Color) happens
    /// right here rather than spreading WinForms types through the rest of the app.</summary>
    private void StatusColorSwatch_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((FrameworkElement)sender).Tag is not RompStatus status) return;

        var current = System.Drawing.ColorTranslator.FromHtml(
            string.IsNullOrWhiteSpace(status.Color) ? "#94A3B8" : status.Color);

        using var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = current,
            FullOpen = true,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        status.Color = System.Drawing.ColorTranslator.ToHtml(dlg.Color);
        vm.SaveRompStatus(status);
    }

    private void BtnStatusUp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is RompStatus status) vm.MoveRompStatusUp(status);
    }

    private void BtnStatusDown_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is RompStatus status) vm.MoveRompStatusDown(status);
    }

    private void BtnDeleteStatus_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not RompStatus status) return;
        var confirm = MessageBox.Show($"Delete status \"{status.Name}\"?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var error = vm.DeleteRompStatus(status);
        if (error != null)
            MessageBox.Show(error, "Can't Delete Status", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BtnAddStatus_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new AddEditStatusDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            vm.AddCustomStatus(dlg.StatusName, dlg.IsComplete, dlg.CountsAsActive);
    }

    // ── Equipment Score Categories ──

    private void EquipGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not ConfigViewModel.EquipmentCategoryVm cat) return;
        Dispatcher.BeginInvoke(() => vm.SaveEquipmentCategory(cat),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ChkEquipExcluded_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is CheckBox chk && chk.DataContext is ConfigViewModel.EquipmentCategoryVm cat)
        {
            cat.IsExcluded = chk.IsChecked == true;
            vm.SaveEquipmentCategory(cat);
        }
    }

    private void BtnEquipUp_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is ConfigViewModel.EquipmentCategoryVm cat) vm.MoveEquipmentCategoryUp(cat);
    }

    private void BtnEquipDown_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is ConfigViewModel.EquipmentCategoryVm cat) vm.MoveEquipmentCategoryDown(cat);
    }

    private void BtnDeleteEquip_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not ConfigViewModel.EquipmentCategoryVm cat || cat.IsBuiltIn) return;
        var confirm = MessageBox.Show($"Delete category \"{cat.Name}\"?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes) vm.DeleteEquipmentCategory(cat);
    }

    private void BtnAddEquip_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new AddEquipmentWeightDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            vm.AddEquipmentCategory(dlg.EwName, dlg.EwKeyword, dlg.EwWeight, dlg.EwExcluded);
    }

    private void BtnSaveItemPrefix_Click(object sender, RoutedEventArgs e)
    {
        Vm?.SaveItemFilterPrefix();
    }

    // ── Slider drag-release handlers (save only when user releases slider thumb) ─

    private void StatusScoreSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((System.Windows.Controls.Slider)sender).DataContext is PMDashboard.Data.Models.RompStatus status)
            vm.SaveRompStatus(status);
    }

    private void EquipScoreSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((System.Windows.Controls.Slider)sender).DataContext is ConfigViewModel.EquipmentCategoryVm cat)
            vm.SaveEquipmentCategory(cat);
    }

    // ── Database mode ──────────────────────────────────────────────────────────

    private void BtnDbLocal_Click(object sender, RoutedEventArgs e) =>
        Vm?.ApplyLocalMode();

    private void BtnDbNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.DbMode = DatabaseMode.Network;
    }

    private void BtnDbCloud_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.DbMode = DatabaseMode.Cloud;
        TxtDdl.Text = vm.GetDdlScript();
    }

    private void BtnBrowseNetworkDb_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select the shared network database file",
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            CheckFileExists = false,
        };
        if (dlg.ShowDialog() == true) vm.NetworkDbPath = dlg.FileName;
    }

    private void BtnApplyNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.ApplyNetworkMode(vm.NetworkDbPath);
    }

    private void BtnCreateNetworkDb_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (string.IsNullOrWhiteSpace(vm.NetworkDbPath))
        {
            MessageBox.Show("Enter the network path first.", "No Path", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = vm.CreateNetworkDatabase(vm.NetworkDbPath);
        MessageBox.Show(result, "Create Network Database", MessageBoxButton.OK,
            result.StartsWith("Network database created") ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void BtnSyncBefore_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!DatabaseConfig.IsNetwork)
        {
            MessageBox.Show("Travel Sync needs Network mode to be applied (and SAT restarted) first.",
                "Sync Before Travel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Dialogs.TravelSyncFlow.GoOffline(Window.GetWindow(this));   // same flow as the sidebar control
        vm.LoadDatabaseSettings();
    }

    private void BtnSyncAfter_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        Dialogs.TravelSyncFlow.SyncBack(Window.GetWindow(this));
        vm.LoadDatabaseSettings();
    }

    private void CloudPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.CloudPassword = ((System.Windows.Controls.PasswordBox)sender).Password;
    }

    private void BtnTestCloud_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.DbStatusMessage = "Testing connection…";
        System.Threading.Tasks.Task.Run(async () =>
        {
            var msg = await vm.TestCloudConnectionAsync();
            Dispatcher.Invoke(() => vm.DbStatusMessage = msg);
        });
    }

    private void BtnApplyCloud_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.ApplyCloudMode();
    }

    private void BtnRefreshSyncStatus_Click(object sender, RoutedEventArgs e) =>
        Vm?.RefreshCloudSyncStatus();

    // ── Danger zone — delete this machine's local data ───────────────────────

    private void BtnDeleteLocalData_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var counts = vm.GetLocalDataCounts();
        int total  = counts.Values.Sum();

        if (total == 0)
        {
            ShowDeleteLocalStatus("Local database is already empty — nothing to delete.");
            return;
        }

        var breakdown = string.Join(
            Environment.NewLine,
            counts.Where(c => c.Value > 0).Select(c => $"    {c.Key}: {c.Value:N0}"));

        var mode = DatabaseConfig.IsCloud
            ? "You are in Cloud mode, so the next sync will pull the cloud's copy back down. Nothing is deleted from the cloud."
            : "You are in Local mode, so this data is NOT stored anywhere else. It cannot be recovered.";

        var first = MessageBox.Show(
            $"Delete all data from this computer's local database?\n\n{breakdown}\n\n" +
            $"Total: {total:N0} record(s).\n\n" +
            $"Your PM roster, settings, statuses, equipment categories and the admin password are kept.\n\n{mode}",
            "Delete Local Data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (first != MessageBoxResult.Yes) return;

        // Second gate: irreversible, so require the word to be typed rather than a stray double-click.
        var dlg = new TextInputDialog(
            "Confirm Delete",
            $"This permanently deletes {total:N0} record(s) from this computer and cannot be undone.\n\nType DELETE to confirm:")
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() != true) return;
        if (!string.Equals(dlg.Value, "DELETE", StringComparison.Ordinal))
        {
            ShowDeleteLocalStatus("Cancelled — confirmation text did not match.");
            return;
        }

        var result = vm.DeleteLocalData();
        ShowDeleteLocalStatus(result + "  Restart the app to reload from an empty state.");
        vm.RefreshCloudSyncStatus();
    }

    private void ShowDeleteLocalStatus(string message)
    {
        TxtDeleteLocalStatus.Text       = message;
        TxtDeleteLocalStatus.Visibility = Visibility.Visible;
    }

    // ── Danger zone — clear every PM assignment ──────────────────────────────

    private void BtnClearAssignments_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!CurrentUser.IsSuperAdmin)
        {
            MessageBox.Show("Only a Super Admin can clear all assignments.", "Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (sites, campuses, romps, clusters) = vm.GetAssignmentCounts();
        if (sites + campuses + romps + clusters == 0)
        {
            ShowClearAssignmentsStatus("There are no assignments to clear.");
            return;
        }

        var scope = DatabaseConfig.IsCloud
            ? "You are in Cloud mode, so this is sent to the cloud and every other computer loses these assignments too."
            : "This can't be undone, except by re-importing an assignments file.";
        var first = MessageBox.Show(
            $"Remove every PM assignment?\n\n    Sites: {sites:N0}\n    Campuses: {campuses:N0}\n    ROMP overrides: {romps:N0}\n    Clusters (direct): {clusters:N0}\n\n" +
            $"PMs, teams, clusters, sites, ROMPs and assignment history are kept.\n\n{scope}\n\n" +
            "Tip: Assignment page → Download Assignments Template first, to keep a copy.",
            "Clear All Assignments", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (first != MessageBoxResult.Yes) return;

        var dlg = new TextInputDialog("Confirm Clear Assignments", "This unassigns every PM from everything.\n\nType CLEAR to confirm:")
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (!string.Equals(dlg.Value, "CLEAR", StringComparison.Ordinal))
        {
            ShowClearAssignmentsStatus("Cancelled — confirmation text did not match.");
            return;
        }

        ShowClearAssignmentsStatus(vm.ClearAllAssignments());
    }

    private void ShowClearAssignmentsStatus(string message)
    {
        TxtClearAssignmentsStatus.Text       = message;
        TxtClearAssignmentsStatus.Visibility = Visibility.Visible;
    }

    // ── Danger zone — clear EVERYTHING, cloud included ───────────────────────

    private void BtnClearAllData_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // Belt-and-suspenders: the button only exists inside the SuperAdmin-only Database card,
        // but this is destructive enough to check again rather than trust the UI alone.
        if (!CurrentUser.IsSuperAdmin)
        {
            MessageBox.Show("Only a Super Admin can clear all data.",
                "Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var counts = vm.GetAllDataCounts();
        int total  = counts.Values.Sum();

        if (total == 0)
        {
            ShowClearAllStatus("Database is already empty — nothing to clear.");
            return;
        }

        var breakdown = string.Join(
            Environment.NewLine,
            counts.Where(c => c.Value > 0).Select(c => $"    {c.Key}: {c.Value:N0}"));

        var scope = DatabaseConfig.IsCloud
            ? "You are in Cloud mode: this deletes from the SHARED CLOUD DATABASE too, not just this computer. Every other machine and every user account — including your own — is affected. There is no cloud copy left afterward to recover from."
            : "You are in Local mode. Once this runs, this data cannot be recovered.";

        var first = MessageBox.Show(
            $"Clear ALL data — every PM, Cluster, Site, ROMP, Order, user account, and setting?\n\n{breakdown}\n\n" +
            $"Total: {total:N0} record(s).\n\n{scope}\n\nThis is permanent.",
            "Clear ALL Data", MessageBoxButton.YesNo, MessageBoxImage.Stop);
        if (first != MessageBoxResult.Yes) return;

        // Second gate: a longer, more deliberate phrase than Delete Local Data's "DELETE" —
        // this is strictly more destructive (cloud-wide, includes every account) and should be
        // harder to trigger by a stray double-click or muscle memory from the other button.
        var dlg = new TextInputDialog(
            "Confirm Clear ALL Data",
            $"This permanently deletes {total:N0} record(s) — everywhere, including the cloud — and cannot be undone.\n\nType CLEAR ALL DATA to confirm:")
        { Owner = Window.GetWindow(this) };

        if (dlg.ShowDialog() != true) return;
        if (!string.Equals(dlg.Value, "CLEAR ALL DATA", StringComparison.Ordinal))
        {
            ShowClearAllStatus("Cancelled — confirmation text did not match.");
            return;
        }

        var result = vm.ClearAllData();
        AuditService.Log("ClearAll", "Database", details: result);

        MessageBox.Show(
            result + "\n\nThe application will now close. Reopen it to continue — you'll be prompted to create a new Super Admin account.",
            "Clear ALL Data Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        Application.Current.Shutdown();
    }

    private void ShowClearAllStatus(string message)
    {
        TxtClearAllStatus.Text       = message;
        TxtClearAllStatus.Visibility = Visibility.Visible;
    }

    private void BtnForcePush_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "This will overwrite every record in the cloud database with the data on this computer.\n\n" +
            "Use this after a schema change (new column added) or when sync is stuck.\n\n" +
            "Continue?",
            "Force Push All Data", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
        {
            App.CloudSync.TriggerForcePush();
            Vm?.RefreshCloudSyncStatus();
        }
    }

    private void BtnCopyDdl_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        Clipboard.SetText(vm.GetDdlScript());
        vm.DbStatusMessage = "DDL script copied to clipboard.";
    }
}

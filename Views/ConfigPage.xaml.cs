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
        Dispatcher.BeginInvoke(() => vm.SaveRompStatus(status),
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
        if (((Button)sender).Tag is not RompStatus status || status.IsBuiltIn) return;
        var confirm = MessageBox.Show($"Delete status \"{status.Name}\"?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes) vm.DeleteRompStatus(status);
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
        var result = vm.SyncBeforeTravel();
        vm.DbStatusMessage = result;
        MessageBox.Show(result, "Sync Before Travel", MessageBoxButton.OK,
            vm.IsTravelMode ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void BtnSyncAfter_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var confirm = MessageBox.Show(
            "This will merge both databases using LastModified timestamps.\n\n" +
            "• Most recently modified version of each record wins.\n" +
            "• New records from either side are added to both.\n" +
            "• Record deletions made offline are not propagated.\n\n" +
            "Continue?",
            "Sync After Travel", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var result = vm.SyncAfterTravel();
        vm.DbStatusMessage = result;
        MessageBox.Show(result, "Sync After Travel", MessageBoxButton.OK,
            result.StartsWith("Sync complete") ? MessageBoxImage.Information : MessageBoxImage.Warning);
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

    // ── Manage users ─────────────────────────────────────────────────────────

    private void BtnAddUser_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new AddEditUserDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) vm.LoadUsers();
    }

    private void BtnResetUserPassword_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not FrameworkElement { DataContext: User user }) return;

        var dlg = new ResetUserPasswordDialog(user.Id, user.DisplayName) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private void BtnToggleUserActive_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not FrameworkElement { DataContext: User user }) return;

        if (user.IsActive)
        {
            var confirm = MessageBox.Show(
                $"Deactivate {user.DisplayName}? They will no longer be able to sign in.",
                "Deactivate User", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        vm.SetUserActive(user, !user.IsActive);
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

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class ForecastPlanningPage : UserControl
{
    private ForecastPlanningViewModel? Vm => DataContext as ForecastPlanningViewModel;

    public ForecastPlanningPage()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdatePageInfo();
    }

    // ── Types ────────────────────────────────────────────────────────────────

    private void BtnSelectType_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Button { Tag: ForecastType type }) return;
        vm.SelectedType = type;
        UpdatePageInfo();
    }

    private void BtnAddType_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var error = vm.AddType();
        if (error != null)
            MessageBox.Show(error, "Add Type", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ── Fields ───────────────────────────────────────────────────────────────

    private void FieldsGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not ForecastField field) return;
        // The grid's own binding already pushed the edited Name into the (untracked) display
        // object; re-save it via the VM so it's written through a tracked context.
        Dispatcher.InvokeAsync(() => vm.RenameField(field, field.Name));
    }

    private void ChkSiteKey_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not CheckBox { Tag: ForecastField field } chk) return;
        vm.SetFieldIsSiteKey(field, chk.IsChecked == true);
    }

    private void ChkCampus_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not CheckBox { Tag: ForecastField field } chk) return;
        var res = vm.SetFieldIsCampus(field, chk.IsChecked == true);
        ShowUploadStatus($"Campus column {(chk.IsChecked == true ? "set" : "cleared")}: " + ArrangementText(res));
    }

    private static string ArrangementText(CampusArrangementService.Result r) =>
        $"{r.SitesMoved:N0} site(s) moved, {r.CampusesCreated:N0} campus(es) created, " +
        $"{r.MappedSites:N0} site(s) have a campus in the forecast.";

    private void ChkIgnored_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not CheckBox { Tag: ForecastField field } chk) return;
        vm.SetFieldIgnored(field, chk.IsChecked == true);
    }

    // ── Upload ───────────────────────────────────────────────────────────────

    private void BtnUpload_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var dlg = new OpenFileDialog
        {
            Title  = "Select Forecast Excel File",
            Filter = "Excel Files|*.xlsx;*.xls|All Files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        ShowUploadStatus("");

        List<string> headers;
        ForecastType? bestGuess;
        double bestScore;
        try
        {
            (headers, bestGuess, bestScore) = vm.DetectUpload(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read that file: {ex.Message}", "Upload Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (headers.Count == 0)
        {
            MessageBox.Show("No columns found in the file's first row.", "Upload",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ForecastType? chosenType;
        if (bestGuess != null && bestScore >= 0.85)
        {
            // Confident auto-match — proceed without interrupting, same philosophy as the
            // Orders auto-detect-with-override discussed for this feature.
            chosenType = bestGuess;
        }
        else
        {
            var pickDlg = new SelectForecastTypeDialog(vm.Types.ToList(), bestGuess, bestScore, System.IO.Path.GetFileName(dlg.FileName))
                { Owner = Window.GetWindow(this) };
            if (pickDlg.ShowDialog() != true) return;
            chosenType = pickDlg.Selected;
        }
        if (chosenType == null) return;

        try
        {
            var result = vm.Import(dlg.FileName, chosenType.Id);
            var status =
                $"Imported \"{result.FileName}\" as {chosenType.Name}: {result.RowsImported:N0} row(s), " +
                $"{result.NewFieldsCreated} new field(s) created, {result.SitesMatched:N0} site(s) matched" +
                (result.SitesUnmatched > 0 ? $", {result.SitesUnmatched:N0} unmatched." : ".");
            // 0 rows almost always means the wrong worksheet was read (common on exports with a
            // summary/pivot tab first) — surface the diagnostics instead of a bare "0 row(s)".
            if (result.RowsImported == 0)
                status += $" Read sheet \"{result.SheetName}\" (1 of {result.SheetCount} worksheet(s)), " +
                          $"sized {result.LastRow:N0} row(s) x {result.LastCol} column(s) — " +
                          "if the real data is on a different tab, that's why nothing imported.";
            if (vm.LastArrangement is { } arr && (arr.SitesMoved > 0 || arr.CampusesCreated > 0))
                status += " Campuses: " + ArrangementText(arr);
            ShowUploadStatus(status);
            UpdatePageInfo();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed: {ex.Message}", "Upload Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowUploadStatus(string message)
    {
        TxtUploadStatus.Text       = message;
        TxtUploadStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Browse paging ────────────────────────────────────────────────────────

    private void BtnPrevPage_Click(object sender, RoutedEventArgs e)
    {
        Vm?.PrevPage();
        UpdatePageInfo();
    }

    private void BtnNextPage_Click(object sender, RoutedEventArgs e)
    {
        Vm?.NextPage();
        UpdatePageInfo();
    }

    private void UpdatePageInfo()
    {
        if (Vm is not { } vm) return;
        TxtPageInfo.Text    = $"Page {vm.BrowsePageIndex + 1} of {vm.BrowseTotalPages}";
        TxtBrowseCount.Text = $"{vm.BrowseTotalRows:N0} row(s)";
    }
}
